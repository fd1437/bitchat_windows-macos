using System.Security.Cryptography;

namespace BitChat.Core;

public static class BridgeDrop
{
    public const int Kind=1401,MaxEnvelopeBytes=20*1024;
    public static string SenderKey(string messageId,byte[] recipient)
    {
        byte[] id=Bytes.Utf8(messageId);
        return "v2:"+Bytes.Hex(Crypto.Hash(Bytes.Join(Bytes.Utf8("bitchat-bridge-drop-dedup-v2"),Bytes.U32((uint)id.Length),id,Bytes.U32((uint)recipient.Length),recipient)));
    }
    public static string[] CandidateTags(byte[] noiseKey,long now)
    {
        if(noiseKey.Length!=32)throw new ArgumentException("A Noise static public key must be 32 bytes");
        return Enumerable.Range(-1,3).Select(offset=>Bytes.Hex(CourierEnvelope.RecipientTag(noiseKey,now/86400000+offset))).ToArray();
    }
    public static NostrEvent Create(CourierEnvelope envelope,long now)
    {
        var encoded=envelope.Encode();
        if(!envelope.ValidAt(now)||encoded.Length>MaxEnvelopeBytes)throw new InvalidDataException("Invalid bridge courier envelope");
        byte[] key=Schnorr.GenerateKey();
        try{return NostrEvent.Create(key,Kind,Convert.ToBase64String(encoded),[["x",Bytes.Hex(envelope.Tag)],["expiration",(envelope.Expires/1000).ToString(System.Globalization.CultureInfo.InvariantCulture)]],now/1000);}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    public static CourierEnvelope Read(NostrEvent message,long now)
    {
        if(message.kind!=Kind||!message.Valid()||message.content.Length>((MaxEnvelopeBytes+2)/3)*4)throw new InvalidDataException("Invalid courier drop event");
        byte[] encoded=Convert.FromBase64String(message.content);
        if(encoded.Length>MaxEnvelopeBytes)throw new InvalidDataException("Courier drop exceeds size limit");
        var envelope=CourierEnvelope.Decode(encoded,now);
        if(!message.tags.Any(t=>t.Length>=2&&t[0]=="x"&&t[1]==Bytes.Hex(envelope.Tag)))throw new InvalidDataException("Mislabeled courier recipient tag");
        return envelope;
    }
    public static Dictionary<string,object> Filter(IEnumerable<string> tags,long now)=>new()
    {{"kinds",new[]{Kind}},{"#x",tags.Distinct().Order().ToArray()},{"since",now/1000-86400},{"limit",100}};
}

public sealed record BridgeDropSnapshot(Dictionary<string,long> Published,Dictionary<string,long> Seen,long Revision=0)
{
    public static BridgeDropSnapshot Empty()=>new([],[]);
}
public sealed record BridgeWatchPeer(string Id,byte[] NoiseKey);

public sealed class BridgeCourierEngine
{
    private sealed class Operation(string key,Guid epoch,bool held)
    {
        public string Key=key;public Guid Epoch=epoch;public bool Held=held;public CourierEnvelope? Envelope;
        public TaskCompletionSource<bool> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object sync=new();
    private readonly Func<long> now;
    private readonly Action<BridgeDropSnapshot> persist;
    private readonly Dictionary<string,long> published,seen,rejected=[];
    private readonly Dictionary<string,Operation> active=[];
    private readonly LinkedList<string> pending=[];
    private readonly Dictionary<string,Guid> inbound=[];
    private HashSet<string> ownTags=[],subscribedTags=[];
    private Dictionary<string,string> watchedTags=[];
    private readonly SemaphoreSlim refreshing=new(1);
    private bool enabled,subscriptionOpen;
    private Guid epoch=Guid.NewGuid();private long revision;
    public Func<bool> Connected{get;set;}=()=>false;
    public Func<NostrEvent,Task<bool>> Publish{get;set;}=_=>Task.FromResult(false);
    public Func<string[],Task> Subscribe{get;set;}=_=>Task.CompletedTask;
    public Func<Task> Unsubscribe{get;set;}=()=>Task.CompletedTask;
    public Func<CourierEnvelope,Task<bool>> OpenOwn{get;set;}=_=>Task.FromResult(false);
    public Func<CourierEnvelope,string,Task<bool>> Deliver{get;set;}=(_,_)=>Task.FromResult(false);
    public event Action<string>? Status;
    public BridgeCourierEngine(BridgeDropSnapshot snapshot,Action<BridgeDropSnapshot> persist,Func<long>? clock=null)
    {now=clock??(()=>Bytes.Now);this.persist=persist;published=new(snapshot.Published);seen=new(snapshot.Seen);revision=snapshot.Revision;Prune(published);Prune(seen);}
    private void Prune(Dictionary<string,long> entries)
    {
        foreach(string id in entries.Where(p=>now()-p.Value>86400000).Select(p=>p.Key).ToArray())entries.Remove(id);
        foreach(string id in entries.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).Skip(512).Select(p=>p.Key).ToArray())entries.Remove(id);
    }
    private bool Contains(Dictionary<string,long> entries,string id)=>entries.TryGetValue(id,out long at)&&now()-at<=86400000;
    private void Persist()
    {
        Prune(published);Prune(seen);
        try{persist(new(new(published),new(seen),++revision));}catch(Exception e){Status?.Invoke("Courier dedup persistence: "+e.Message);}
    }
    public bool WasPublished(string messageId,byte[] recipient)
    {lock(sync)return Contains(published,BridgeDrop.SenderKey(messageId,recipient))||Contains(published,messageId);}
    public void SetEnabled(bool value)
    {
        lock(sync)
        {
            if(enabled==value)return;enabled=value;
            if(value)return;
            epoch=Guid.NewGuid();var old=active.Values.ToArray();active.Clear();pending.Clear();inbound.Clear();
            foreach(var op in old)op.Completion.TrySetResult(false);
        }
    }
    public void Wipe()
    {SetEnabled(false);lock(sync){published.Clear();seen.Clear();rejected.Clear();Persist();}}

    public Task<bool> Deposit(string messageId,byte[] recipient,Func<CourierEnvelope> seal)
    {
        string key=BridgeDrop.SenderKey(messageId,recipient);Operation operation;bool deterministicRejection=false;
        lock(sync)
        {
            if(!enabled||WasPublished(messageId,recipient)||Contains(rejected,key)||active.ContainsKey(key))return Task.FromResult(false);
            operation=new(key,epoch,false);active[key]=operation;
        }
        try
        {
            var envelope=seal();
            if(!envelope.ValidAt(now())||envelope.Encode().Length>BridgeDrop.MaxEnvelopeBytes){deterministicRejection=true;throw new InvalidDataException("Invalid bridge drop size/expiry");}
            lock(sync)
            {
                if(!Current(operation))return operation.Completion.Task;
                operation.Envelope=envelope;
                if(!Connected())
                {
                    pending.AddLast(key);
                    while(pending.Count>20){string evicted=pending.First!.Value;pending.RemoveFirst();if(active.TryGetValue(evicted,out var old))Finish(old,false);}
                    return operation.Completion.Task;
                }
            }
            _=PublishOperation(operation);
        }
        catch(Exception e)
        {
            lock(sync){if(Current(operation)){if(deterministicRejection){rejected[key]=now();Prune(rejected);}Finish(operation,false);}}
            Status?.Invoke("Courier drop rejected: "+e.Message);
        }
        return operation.Completion.Task;
    }
    public Task<bool> PublishHeld(CourierEnvelope envelope)
    {
        Operation operation;string key="held:"+envelope.Id;
        lock(sync)
        {
            if(!enabled||!Connected()||active.ContainsKey(key)||!envelope.ValidAt(now()))return Task.FromResult(false);
            active[key]=operation=new(key,epoch,true){Envelope=envelope};
        }
        _=PublishOperation(operation);return operation.Completion.Task;
    }
    private bool Current(Operation op)=>enabled&&epoch==op.Epoch&&active.GetValueOrDefault(op.Key)==op;
    private void Finish(Operation op,bool accepted)
    {
        if(!Current(op))return;
        active.Remove(op.Key);
        if(accepted&&!op.Held){published[op.Key]=now();Persist();}
        op.Completion.TrySetResult(accepted);
    }
    private async Task PublishOperation(Operation op)
    {
        bool accepted=false;
        try
        {
            lock(sync)if(!Current(op))return;
            var message=BridgeDrop.Create(op.Envelope!,now());
            accepted=await Publish(message);
        }
        catch(Exception e){Status?.Invoke("Courier relay publish: "+e.Message);}
        lock(sync)Finish(op,accepted);
    }
    public async Task Refresh(byte[] myNoiseKey,IReadOnlyList<BridgeWatchPeer> peers)
    {
        await refreshing.WaitAsync();
        try
        {
            bool close=false;string[]? open=null;Guid lease;
            lock(sync)
            {
                lease=epoch;
                if(!enabled||!Connected()){close=subscriptionOpen;subscriptionOpen=false;subscribedTags=[];}
                else
                {
                    ownTags=BridgeDrop.CandidateTags(myNoiseKey,now()).ToHashSet();watchedTags=[];
                    foreach(var peer in peers.Take(16))foreach(string tag in BridgeDrop.CandidateTags(peer.NoiseKey,now()))watchedTags.TryAdd(tag,peer.Id);
                    var all=ownTags.Concat(watchedTags.Keys).ToHashSet();
                    if(!subscriptionOpen||!all.SetEquals(subscribedTags)){open=all.Order().ToArray();subscribedTags=all;subscriptionOpen=true;}
                }
            }
            if(close)await Unsubscribe();
            if(open!=null)
            {
                try{await Subscribe(open);}
                catch{lock(sync){if(epoch==lease){subscriptionOpen=false;subscribedTags=[];}}throw;}
            }
            Operation[] queued;
            lock(sync)
            {
                if(!enabled||epoch!=lease||!Connected())return;
                queued=pending.Select(k=>active.GetValueOrDefault(k)).Where(op=>op!=null).Cast<Operation>().ToArray();pending.Clear();
            }
            foreach(var op in queued)_=PublishOperation(op);
        }
        finally{refreshing.Release();}
    }
    public async Task<bool> Receive(NostrEvent message)
    {
        Guid lease;
        lock(sync){if(!enabled||Contains(seen,message.id)||inbound.ContainsKey(message.id))return false;lease=epoch;inbound[message.id]=lease;}
        bool accepted=false;
        try
        {
            var envelope=BridgeDrop.Read(message,now());string tag=Bytes.Hex(envelope.Tag);bool own;string? peer;
            lock(sync){if(!enabled||epoch!=lease)return false;own=ownTags.Contains(tag);peer=watchedTags.GetValueOrDefault(tag);}
            if(own)accepted=await OpenOwn(envelope);else if(peer!=null)accepted=await Deliver(envelope,peer);
            lock(sync)if(accepted&&enabled&&epoch==lease){seen[message.id]=now();Persist();return true;}
        }
        catch(Exception e)when(e is InvalidDataException or FormatException or CryptographicException or ArgumentException or IOException){Status?.Invoke("Courier drop not consumed: "+e.Message);}
        finally{lock(sync)if(inbound.GetValueOrDefault(message.id)==lease)inbound.Remove(message.id);}
        return false;
    }
}

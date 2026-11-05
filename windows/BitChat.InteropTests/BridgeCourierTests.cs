using BitChat.Core;
using System.Text.Json;
using System.Security.Cryptography;

internal static class BridgeCourierTests
{
    public static async Task Run(Action<bool,string> check,JsonElement reference)
    {
        var fixture=reference.GetProperty("bridge_drop");long now=fixture.GetProperty("now_ms").GetInt64();
        byte[] recipient=Convert.FromBase64String(fixture.GetProperty("recipient_public").GetString()!);
        var message=NostrEvent.Parse(fixture.GetProperty("event").GetString()!);
        var envelope=BridgeDrop.Read(message,now);
        check(Convert.ToBase64String(envelope.Encode())==fixture.GetProperty("envelope").GetString(),"Actual Swift signed kind-1401 courier envelope");
        check(BridgeDrop.SenderKey("bridge-été-001",recipient)==fixture.GetProperty("sender_key").GetString(),"Actual Swift recipient-scoped bridge dedup hash");
        var emitted=BridgeDrop.Create(envelope,now);var again=BridgeDrop.Create(envelope,now);
        check(emitted.Valid()&&emitted.pubkey!=again.pubkey&&emitted.kind==1401,"Bridge drops use fresh valid throwaway Schnorr identities");
        BridgeDropSnapshot saved=BridgeDropSnapshot.Empty();bool connected=false;int sends=0;bool accept=false;
        BridgeCourierEngine Engine()=>new(saved,s=>saved=s,()=>now);
        var engine=Engine();engine.SetEnabled(true);engine.Connected=()=>connected;engine.Publish=_=>{sends++;return Task.FromResult(accept);};
        var queued=Enumerable.Range(0,21).Select(i=>engine.Deposit("queued-"+i,recipient,()=>envelope)).ToArray();
        check(queued[0].IsCompleted&&!await queued[0]&&queued.Skip(1).All(t=>!t.IsCompleted),"Offline bridge queue caps at twenty and releases evicted operation");
        connected=true;await engine.Refresh(recipient,[]);await Task.WhenAll(queued);
        check(sends==20&&saved.Published.Count==0,"Relay refusal never commits published dedup");
        accept=true;check(await engine.Deposit("queued-0",recipient,()=>envelope)&&engine.WasPublished("queued-0",recipient),"Evicted or refused bridge message remains retryable");
        var restarted=Engine();check(restarted.WasPublished("queued-0",recipient)&&!restarted.WasPublished("queued-0",RandomNumberGenerator.GetBytes(32)),"Published bridge dedup survives restart and is recipient scoped");
        int seals=0;check(!await engine.Deposit("transient",recipient,()=>{seals++;throw new IOException("disk unavailable");})&&await engine.Deposit("transient",recipient,()=>{seals++;return envelope;})&&seals==2,"Transient envelope storage error does not blacklist retry");
        int subscriptions=0;engine.Subscribe=_=>{subscriptions++;return Task.CompletedTask;};await engine.Refresh(recipient,[]);await engine.Refresh(recipient,[]);
        check(subscriptions==0,"Unchanged bridge watch tags do not rebuild subscription");
        int opens=0;bool canOpen=false;engine.OpenOwn=_=>{opens++;return Task.FromResult(canOpen);};
        check(!await engine.Receive(message)&&!saved.Seen.ContainsKey(message.id),"Missing prekey or transient inbox failure remains unconsumed");
        canOpen=true;check(await engine.Receive(message)&&saved.Seen.ContainsKey(message.id)&&!await engine.Receive(message)&&opens==2,"Successful inbox commit consumes bridge event exactly once");
        var ownRestart=Engine();ownRestart.SetEnabled(true);ownRestart.Connected=()=>true;await ownRestart.Refresh(recipient,[]);int restartOpens=0;ownRestart.OpenOwn=_=>{restartOpens++;return Task.FromResult(true);};
        check(!await ownRestart.Receive(message)&&restartOpens==0,"Consumed bridge event survives restart without another receipt");
        var delayed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);engine.Publish=_=>delayed.Task;
        var old=engine.Deposit("epoch",recipient,()=>envelope);engine.SetEnabled(false);engine.SetEnabled(true);
        var replacement=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);engine.Publish=_=>replacement.Task;var fresh=engine.Deposit("epoch",recipient,()=>envelope);
        delayed.SetResult(true);await Task.Yield();check(!await old&&!engine.WasPublished("epoch",recipient)&&!fresh.IsCompleted,"Late relay ACK cannot commit or release replacement operation");replacement.SetResult(true);check(await fresh,"Replacement bridge publish completes with its own ACK");
        var failing=Engine();failing.SetEnabled(true);failing.Connected=()=>true;int attempts=0;failing.Subscribe=_=>++attempts==1?Task.FromException(new IOException("relay reset")):Task.CompletedTask;
        try{await failing.Refresh(recipient,[]);}catch(IOException){}await failing.Refresh(recipient,[]);check(attempts==2,"Failed watch subscription retries on next refresh");
        var links=new AuthenticatedLinks();Guid epoch=links.Open("radio"),generation=Guid.NewGuid();check(links.Authenticate("radio",epoch,"peer",generation),"Physical link accepts current authenticated Noise session");var lease=links.For("peer").Single();
        Guid next=links.Open("radio");links.Close("radio",epoch);check(!links.Current(lease)&&links.Epoch("radio")==next&&!links.Authenticate("radio",epoch,"peer",generation),"Reconnected physical link rejects stale authentication and late disconnect");
        links.Authenticate("radio",next,"peer",Guid.NewGuid());check(links.For("peer",generation).Length==0,"Direct courier lease binds exact Noise generation");
        await Direct(check);
        now+=86400001;check(!engine.WasPublished("queued-0",recipient),"Bridge published dedup expires after twenty-four hours");
    }
    private static async Task Direct(Action<bool,string> check)
    {
        var transit=new Queue<(bool ToB,byte[] Bytes)>();
        Task<int> Send(bool toB,MeshPacket packet){foreach(var part in FragmentAssembler.Split(packet,128))transit.Enqueue((toB,part.Encode()));return Task.FromResult(1);}
        using var a=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"A",p=>Send(true,p));
        using var b=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"B",p=>Send(false,p));
        var aLinks=new AuthenticatedLinks();var bLinks=new AuthenticatedLinks();Guid ae=aLinks.Open("B"),be=bLinks.Open("A");
        a.LinkAuthenticated+=(peer,ingress,epoch,generation)=>aLinks.Authenticate(ingress,epoch,peer,generation);
        b.LinkAuthenticated+=(peer,ingress,epoch,generation)=>bLinks.Authenticate(ingress,epoch,peer,generation);
        async Task Pump(){int budget=1000;while(transit.Count>0){if(--budget==0)throw new Exception("direct handshake loop");var item=transit.Dequeue();await(item.ToB?b:a).Receive(item.Bytes,item.ToB?"A":"B",item.ToB?be:ae);}}
        await a.Announce();await b.Announce();await Pump();await a.ProveDirectLink(b.Id,ae);await Pump();
        check(aLinks.For(b.Id).Length==1&&bLinks.For(a.Id).Length==1,"Fragmented Noise handshake authenticates both exact physical connections");
        var lease=aLinks.For(b.Id).Single();int direct=0;bool received=false;
        b.CourierReceived+=(_,_,verified)=>received=verified;
        var envelope=new CourierEnvelope(new byte[16],(ulong)(Bytes.Now+100000),[1,2,3]);
        async Task<int> DirectSend(MeshPacket packet,AuthenticatedLink bound){if(!aLinks.Current(bound))return 0;direct++;await b.Receive(packet.Encode(),"A",be);return 1;}
        check(await a.DeliverCourierDirect(envelope,lease,DirectSend)&&direct==1&&received,"Courier handoff requires authenticated session and directed physical acceptance");
        aLinks.Open("B");check(!await a.DeliverCourierDirect(envelope,lease,DirectSend)&&direct==1,"Stale physical courier lease cannot report delivery");
        var fragmented=new MeshPacket(0x10,a.PeerId,RandomNumberGenerator.GetBytes(300),b.PeerId,Timestamp:(ulong)Bytes.Now);
        var fragments=FragmentAssembler.Split(fragmented,128).ToArray();var assembler=new FragmentAssembler();bool directProvenance=true;
        for(int i=0;i<fragments.Length;i++)assembler.Add(fragments[i],"A",i==0?be:Guid.NewGuid(),out directProvenance);
        check(!directProvenance,"Fragments from different connection epochs cannot authenticate a direct link");
        assembler=new();for(int i=0;i<fragments.Length;i++)assembler.Add(fragments[i] with{Ttl=(byte)(i==0?6:7)},"A",be,out directProvenance);
        check(!directProvenance,"Relayed fragment cannot recover direct provenance from inner TTL");
    }
}

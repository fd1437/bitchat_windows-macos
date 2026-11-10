using System.IO;
using System.Security.Cryptography;
using BitChat.Core;

namespace BitChat.Windows;

public sealed partial class AppController
{
    public sealed record FileSendProgress(string Conversation,string Id,string Name,int Percent);
    private sealed class TrackedSend(string conversation,string id,string name)
    {public string Conversation=conversation,Id=id,Name=name;public int Percent=-1;public long LastReport;}
    private readonly System.Collections.Concurrent.ConcurrentDictionary<MeshPacket,TrackedSend> trackedSends=new(ReferenceEqualityComparer.Instance);
    public event Action<FileSendProgress>? FileProgress;
    private void TrackMediaSend(string? peer,string name,MeshPacket packet)
    {
        string conversation=peer==null?"mesh":"mesh:"+peer;
        StoredMessage? message;lock(Store.State.Messages)message=Store.State.Messages.LastOrDefault(m=>m.Own&&m.Conversation==conversation&&m.Text==name&&m.Attachment!=null);
        if(message!=null)trackedSends[packet]=new(conversation,message.Id,name);
    }
    private void ReportMediaSend(MeshPacket packet,long bytes,long total)
    {
        if(!trackedSends.TryGetValue(packet,out var item)||total<=0)return;
        int percent=(int)Math.Min(99,bytes*100/total);
        lock(item)
        {
            if(percent<=item.Percent||percent<99&&Bytes.Now-item.LastReport<150)return;
            item.Percent=percent;item.LastReport=Bytes.Now;
        }
        Post(()=>{if(trackedSends.ContainsKey(packet))FileProgress?.Invoke(new(item.Conversation,item.Id,item.Name,percent));});
    }
    private readonly PrivateMediaLedger mediaLedger;
    private long lastMediaRetentionSweep;
    public Func<string,Task<bool>>? ConfirmLegacyMedia{get;set;}
    private static byte[] ProtectMediaDecision(byte[] data)=>ProtectedData.Protect(data,null,DataProtectionScope.CurrentUser);
    private static byte[] UnprotectMediaDecision(byte[] data)=>ProtectedData.Unprotect(data,null,DataProtectionScope.CurrentUser);

    private async Task<bool> AcceptMedia(string peer,string id,MediaPacket media)
    {
        if(disposed)return false;
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation=stop.Token.Register(()=>completion.TrySetResult(false));
        Dispatch(()=>
        {
            if(disposed||Store.State.Blocked.Contains(peer)){completion.TrySetResult(false);return;}
            try
            {
                var decision=mediaLedger.Accept(id,media,Store.SaveMedia);
                if(decision.Kind==MediaDecisionKind.Deleted){completion.TrySetResult(true);return;}
                if(decision.Kind!=MediaDecisionKind.Accepted){completion.TrySetResult(false);return;}
                DeliverStoredMedia(peer,"mesh:"+peer,id,decision.Path!,decision.Name??media.Name,decision.Mime??media.Mime);
                completion.TrySetResult(true);
            }
            catch(Exception e){Status?.Invoke("Private media was not acknowledged: "+e.Message);completion.TrySetResult(false);}
        });
        return await completion.Task;
    }
    private void DeliverStoredMedia(string peer,string conversation,string id,string saved,string name,string mime)
    {
        StoredMessage? message=null,previous=null;
        lock(Store.State.Messages)
        {
            if(!Store.State.Messages.Any(m=>m.Conversation==conversation&&m.Id==id))
            {message=new(conversation,id,Peers.GetValueOrDefault(peer)?.Nickname??peer,name,Bytes.Now,Attachment:saved,Mime:mime,Peer:peer);Store.State.Messages.Add(message);}
            else
            {
                int i=Store.State.Messages.FindIndex(m=>m.Conversation==conversation&&m.Id==id&&!m.Own);
                if(i>=0&&Store.State.Messages[i].Attachment!=saved)
                {previous=Store.State.Messages[i];Store.State.Messages[i]=previous with{Attachment=saved,Mime=mime,Text=name,Status="received"};}
            }
        }
        try{Store.Save();}
        catch{if(message!=null)Store.State.Messages.Remove(message);if(previous!=null){int i=Store.State.Messages.FindIndex(m=>m.Conversation==conversation&&m.Id==id);if(i>=0)Store.State.Messages[i]=previous;}throw;}
        if(message!=null)Message?.Invoke(message);
        if(previous!=null){if(previous.Attachment!=null)try{Store.RemoveMedia(previous.Attachment);}catch{}TimelineChanged?.Invoke();}
    }
    public async Task MarkMeshRead(StoredMessage message)
    {
        if(message.Own||!message.Conversation.StartsWith("mesh:"))return;
        if(PrivateMediaIdentity.IsStable(message.Id))
        {
            var decision=mediaLedger.Get(message.Id);
            if(decision.Kind!=MediaDecisionKind.Accepted||decision.Path!=message.Attachment)return;
        }
        await Mesh.SendTyped(message.Conversation[5..],Bytes.Join([2],Bytes.Utf8(message.Id)));
        int index=Store.State.Messages.FindIndex(m=>m.Conversation==message.Conversation&&m.Id==message.Id&&!m.Own);
        if(index>=0){Store.State.Messages[index]=Store.State.Messages[index] with{Status="read"};Store.Save();}
    }
    private static string? VoiceBurstId(string name)
    {
        if(name.StartsWith("voice_",StringComparison.Ordinal)&&name.EndsWith(".m4a",StringComparison.OrdinalIgnoreCase))
        {string id=name[6..^4];if(id.Length==16&&id.All(Uri.IsHexDigit))return id.ToLowerInvariant();}
        return null;
    }
    private void ReceiveRawMedia(string peer,MediaPacket media,bool isPrivate)
    {
        string conversation=isPrivate?"mesh:"+peer:"mesh";
        string id=VoiceBurstId(media.Name)??Guid.NewGuid().ToString();
        if(VoiceBurstId(media.Name)==null&&Store.State.Messages.Any(m=>m.Id==id&&m.Conversation==conversation))return;
        DeliverStoredMedia(peer,conversation,id,Store.SaveMedia(media),media.Name,media.Mime);
    }
    private void ReceiveLiveMedia(string peer,string conversation,string id,MediaPacket media)
    {
        var decision=mediaLedger.Get(id);
        if(decision.Kind is MediaDecisionKind.Deleted or MediaDecisionKind.Unavailable)return;
        if(decision.Kind==MediaDecisionKind.Accepted)
        {DeliverStoredMedia(peer,conversation,id,decision.Path!,decision.Name??media.Name,decision.Mime??media.Mime);return;}
        if(Store.State.Messages.Any(m=>m.Conversation==conversation&&m.Id==id))return;
        // Live frames may be incomplete. Only the finalized file can commit a
        // durable receipt; its arrival replaces this provisional playback file.
        DeliverStoredMedia(peer,conversation,id,Store.SaveMedia(media),media.Name,media.Mime);
    }

    private async Task SendAttachment(string conversation,MediaPacket media,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();string? peer=conversation=="mesh"?null:conversation[5..];bool allowLegacy=false;
        if(peer!=null)
        {
            var policy=await Mesh.ResolvePrivateMediaPolicy(peer);
            if(policy==PrivateMediaSendPolicy.LegacyRequiresConsent)
            {
                allowLegacy=ConfirmLegacyMedia!=null&&await ConfirmLegacyMedia(Peers.GetValueOrDefault(peer)?.Nickname??peer);
                if(!allowLegacy)throw new IOException("Attachment canceled; this contact needs unencrypted legacy transfer consent");
            }
            else if(policy!=PrivateMediaSendPolicy.Encrypted)throw new IOException("Encrypted media support could not be authenticated; no attachment was sent");
        }
        cancellation.ThrowIfCancellationRequested();string id=(peer==null?null:PrivateMediaIdentity.StableId(Mesh.Id,peer,media.Name))??VoiceBurstId(media.Name)??Guid.NewGuid().ToString();
        Add(new(conversation,id,Store.State.Nickname,media.Name,Bytes.Now,true,"sending",Store.SaveMedia(media),media.Mime,peer));
        try
        {
            UpdateReceipt(id,"sending",conversation);
            await Mesh.SendMedia(media,peer,allowLegacy,cancellation);
            cancellation.ThrowIfCancellationRequested();
            // A receipt may have raced local completion. Never downgrade it.
            var message=Store.State.Messages.FirstOrDefault(m=>m.Own&&m.Id==id&&m.Conversation==conversation);
            if(message?.Status=="sending")UpdateReceipt(id,peer==null?"sent":allowLegacy?"sent without encryption":PrivateMediaIdentity.IsStable(id)?"awaiting receipt":"sent",conversation);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){await Mesh.ForgetPrivateMedia([id]);UpdateReceipt(id,"canceled",conversation);throw;}
        catch{await Mesh.ForgetPrivateMedia([id]);UpdateReceipt(id,"failed",conversation);throw;}
    }

    public async Task ClearConversation(string conversation)
    {
        var messages=Store.State.Messages.Where(m=>m.Conversation==conversation).ToArray();
        var incoming=messages.Where(m=>!m.Own&&m.Attachment!=null&&PrivateMediaIdentity.IsStable(m.Id))
            .ToDictionary(m=>m.Id,m=>m.Attachment);
        // Commit deletion intent before touching the visible timeline or files.
        if(incoming.Count>0&&!mediaLedger.Delete(incoming))throw new IOException("Private-media deletion could not be saved; history was preserved");
        await Mesh.ForgetPrivateMedia(messages.Where(m=>m.Own).Select(m=>m.Id));
        var previous=Store.State.Messages.ToList();
        Store.State.Messages.RemoveAll(m=>m.Conversation==conversation);
        try{Store.Save();}catch{Store.State.Messages=previous;throw;}
        foreach(var message in messages.Where(m=>m.Attachment!=null&&!incoming.ContainsKey(m.Id)))
        {
            // Each ordinary media file has a fresh local UUID; shared durable
            // receiver files are removed only through the ledger above.
            try{Store.RemoveMedia(message.Attachment!);}catch(Exception e){Status?.Invoke("Media cleanup: "+e.Message);}
        }
        TimelineChanged?.Invoke();
    }

    public async Task<int> CleanupExpiredMedia()
    {
        lastMediaRetentionSweep=Bytes.Now;
        int days=Store.State.FileRetentionDays;if(days<=0)return 0;
        long cutoff=DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days,1,36500)).ToUnixTimeMilliseconds();
        return await DeleteLocalAttachments(Store.State.Messages.Where(m=>m.Attachment!=null&&m.Timestamp<cutoff).ToArray());
    }

    public async Task<int> DeleteLocalAttachments(IEnumerable<StoredMessage> selected)
    {
        var messages=selected.Where(m=>m.Attachment!=null&&!m.LocalAttachmentDeleted)
            .Select(m=>Store.State.Messages.FirstOrDefault(current=>current.Conversation==m.Conversation&&current.Id==m.Id&&current.Attachment==m.Attachment))
            .Where(m=>m!=null).Cast<StoredMessage>().DistinctBy(m=>m.Conversation+"\0"+m.Id).ToArray();
        if(messages.Length==0)return 0;
        var incoming=messages.Where(m=>!m.Own&&PrivateMediaIdentity.IsStable(m.Id)).GroupBy(m=>m.Id).ToDictionary(g=>g.Key,g=>(string?)g.First().Attachment);
        if(incoming.Count>0&&!mediaLedger.Delete(incoming))throw new IOException("The media deletion record could not be saved; files were kept");
        await Mesh.ForgetPrivateMedia(messages.Where(m=>m.Own).Select(m=>m.Id));
        var replacements=messages.ToDictionary(m=>m,m=>m with{Attachment=null,Mime=null,LocalAttachmentDeleted=true});
        foreach(var (old,replacement) in replacements)
        {int index=Store.State.Messages.FindIndex(m=>m.Conversation==old.Conversation&&m.Id==old.Id);if(index>=0)Store.State.Messages[index]=replacement;}
        try{Store.Save();}
        catch{foreach(var old in messages){int index=Store.State.Messages.FindIndex(m=>m.Conversation==old.Conversation&&m.Id==old.Id);if(index>=0)Store.State.Messages[index]=old;}throw;}
        foreach(var message in messages.Where(m=>!incoming.ContainsKey(m.Id)))
            try{Store.RemoveMedia(message.Attachment!);}catch(Exception e){Status?.Invoke("A deleted attachment could not be removed: "+e.Message);}
        TimelineChanged?.Invoke();return messages.Length;
    }
}

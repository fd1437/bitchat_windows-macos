using System.Security.Cryptography;

namespace BitChat.Core;

public enum PrivateMediaSendPolicy { Encrypted, AwaitingCapabilityProof, LegacyRequiresConsent, BlockedDowngrade }

public sealed partial class MeshEngine
{
    private sealed class RetainedMedia(string peer, MediaPacket media, Guid generation, long created)
    {
        public string Peer=peer;public MediaPacket Media=media;public Guid Generation=generation,LastLinkEpoch;
        public long Created=created;public int Retries;
    }
    private readonly Dictionary<string,RetainedMedia> retainedMedia=[];
    private readonly Dictionary<string,Guid> mediaLinkEpochs=[];
    private readonly Dictionary<string,int> mediaRetryBudgets=[];
    private readonly HashSet<string> mediaCapable=[];
    public Func<string,string,MediaPacket,Task<bool>>? AcceptPrivateMedia{get;set;}
    public event Action<string>? MediaCapabilityObserved;
    public event Action<string,string,string>? MediaTransferStatus;
    public Func<long> MediaClock{get;set;}=()=>Bytes.Now;
    public void RestoreMediaCapable(string fingerprint)=>mediaCapable.Add(fingerprint);
    private static bool HasCapability(byte[] caps,int bit)=>caps.Length>bit/8&&(caps[bit/8]&(1<<(bit%8)))!=0;
    private static bool ReceiptCapable(Session session)=>session.Send!=null&&session.ProvenCapabilities is byte[] caps&&HasCapability(caps,8)&&HasCapability(caps,9);

    private PrivateMediaSendPolicy MediaPolicyLocked(string peer)
    {
        if(IsBlocked(peer)||!peers.TryGetValue(peer,out var known))return PrivateMediaSendPolicy.BlockedDowngrade;
        bool pinned=mediaCapable.Contains(Bytes.Hex(Crypto.Hash(known.NoiseKey)));
        if(sessions.TryGetValue(peer,out var session))
        {
            if(session.Send!=null&&session.ProvenCapabilities is byte[] caps)
                return HasCapability(caps,8)?PrivateMediaSendPolicy.Encrypted:pinned?PrivateMediaSendPolicy.BlockedDowngrade:PrivateMediaSendPolicy.LegacyRequiresConsent;
            if(session.ProofTimedOut)return pinned?PrivateMediaSendPolicy.BlockedDowngrade:PrivateMediaSendPolicy.LegacyRequiresConsent;
        }
        return HasCapability(known.Capabilities,8)||pinned?PrivateMediaSendPolicy.AwaitingCapabilityProof:PrivateMediaSendPolicy.LegacyRequiresConsent;
    }
    private async Task<Session> EnsureMediaSessionLocked(string peer)
    {
        if(!sessions.TryGetValue(peer,out var session)||session.Send==null&&Bytes.Now-session.Started>30000)
        {
            if(sessions.Count>=128&&!sessions.ContainsKey(peer))throw new IOException("Noise session limit reached");
            session?.Proof.TrySetResult(false);session?.Handshake.Dispose();
            sessions[peer]=session=new(true,noiseKey);
            await send(Packet(0x10,session.Handshake.Write(),peer,signed:false));
        }
        return session;
    }

    public async Task<PrivateMediaSendPolicy> ResolvePrivateMediaPolicy(string peer)
    {
        Session? pending=null;
        await gate.WaitAsync();
        try
        {
            var policy=MediaPolicyLocked(peer);
            if(policy!=PrivateMediaSendPolicy.AwaitingCapabilityProof)return policy;
            pending=await EnsureMediaSessionLocked(peer);
        }
        finally{gate.Release();}
        try{await pending.Proof.Task.WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){}
        await gate.WaitAsync();
        try
        {
            // A timeout never classifies a replacement Noise generation.
            if(sessions.GetValueOrDefault(peer)!=pending)return PrivateMediaSendPolicy.AwaitingCapabilityProof;
            if(pending.ProvenCapabilities==null)pending.ProofTimedOut=true;
            return MediaPolicyLocked(peer);
        }
        finally{gate.Release();}
    }

    public async Task SendMedia(MediaPacket media,string? recipient=null,bool allowLegacyFallback=false)
    {
        var encoded=media.Encode();
        if(recipient==null){if(await send(Packet(0x22,encoded,version:2))==0)throw new IOException("No Bluetooth connection accepted this attachment");return;}
        var policy=await ResolvePrivateMediaPolicy(recipient);
        if(policy==PrivateMediaSendPolicy.AwaitingCapabilityProof)throw new IOException("Encrypted media support is still being authenticated; retry shortly");
        await gate.WaitAsync();
        try
        {
            PruneMediaLocked();policy=MediaPolicyLocked(recipient);
            if(policy==PrivateMediaSendPolicy.LegacyRequiresConsent&&allowLegacyFallback)
            {if(await send(Packet(0x22,encoded,recipient,version:2))==0)throw new IOException("No Bluetooth connection accepted this attachment");return;}
            if(policy==PrivateMediaSendPolicy.LegacyRequiresConsent)throw new IOException("This contact needs explicit consent for each unencrypted legacy attachment");
            if(policy!=PrivateMediaSendPolicy.Encrypted)throw new IOException("Encrypted media capability could not be confirmed; downgrade blocked");
            var session=sessions[recipient];
            string? id=PrivateMediaIdentity.StableId(Id,recipient,media.Name);
            if(id!=null&&ReceiptCapable(session)&&!retainedMedia.ContainsKey(id)
                &&retainedMedia.Count<8&&retainedMedia.Values.Sum(p=>p.Media.Content.Length)+media.Content.Length<=4*1024*1024)
                retainedMedia[id]=new(recipient,media with{Content=media.Content.ToArray()},session.Generation,MediaClock()){LastLinkEpoch=mediaLinkEpochs.GetValueOrDefault(recipient)};
            if(await SendMediaOnSessionLocked(recipient,encoded,session)==0)throw new IOException("Bluetooth transfer interrupted; waiting for reconnection");
            if(id!=null&&retainedMedia.TryGetValue(id,out var completed))completed.Created=MediaClock();
        }
        finally{gate.Release();}
    }
    private Task<int> SendMediaOnSessionLocked(string peer,byte[] encoded,Session session)
    {
        if(sessions.GetValueOrDefault(peer)!=session||session.Send==null||session.ProvenCapabilities==null||!HasCapability(session.ProvenCapabilities,8))
            throw new IOException("Private media session changed before send");
        var plain=Bytes.Join([0x20],encoded);
        return send(Packet(0x11,session.Send.Encrypt(plain),peer,plain.Length>60000?(byte)2:(byte)1,false));
    }
    private void ReleaseRetainedMedia(string id)
    {if(retainedMedia.Remove(id,out var item))CryptographicOperations.ZeroMemory(item.Media.Content);}
    private void PruneMediaLocked()
    {
        foreach(var (id,item) in retainedMedia.ToArray())
            if(MediaClock()-item.Created>120000||IsBlocked(item.Peer))
            {ReleaseRetainedMedia(id);MediaTransferStatus?.Invoke(item.Peer,id,"unconfirmed");}
    }
    public async Task MaintainPrivateMedia()
    {await gate.WaitAsync();try{PruneMediaLocked();}finally{gate.Release();}}
    public async Task ForgetPrivateMedia(IEnumerable<string> ids)
    {await gate.WaitAsync();try{foreach(string id in ids)ReleaseRetainedMedia(id);}finally{gate.Release();}}

    // Called only when a direct peer is observed on a physical link epoch.
    // Repeated announce packets on the same connection cannot chain retries.
    public async Task PeerLinkReady(string peer,Guid linkEpoch)
    {
        await gate.WaitAsync();
        try
        {
            if(mediaLinkEpochs.GetValueOrDefault(peer)==linkEpoch)return;
            mediaLinkEpochs[peer]=linkEpoch;
            PruneMediaLocked();
            if(!retainedMedia.Values.Any(p=>p.Peer==peer&&p.Retries<2))return;
            // A peer may have restarted while the radio was disconnected.
            // Re-prove capabilities with fresh keys instead of replaying a
            // retained file into the old session and waiting for user traffic.
            if(sessions.TryGetValue(peer,out var old)&&old.Send!=null)
            {
                var fresh=new Session(true,noiseKey){Previous=old,MediaRetryLinkEpoch=linkEpoch};sessions[peer]=fresh;
                await send(Packet(0x10,fresh.Handshake.Write(),peer,signed:false));
            }
            else if(old!=null)old.MediaRetryLinkEpoch=linkEpoch;
        }
        finally{gate.Release();}
        var policy=await ResolvePrivateMediaPolicy(peer);
        await gate.WaitAsync();
        try
        {
            PruneMediaLocked();
            if(mediaLinkEpochs.GetValueOrDefault(peer)!=linkEpoch||!sessions.TryGetValue(peer,out var session)||policy!=PrivateMediaSendPolicy.Encrypted)return;
            await RetryMediaLocked(peer,session,linkEpoch,false);
        }
        finally{gate.Release();}
    }
    private async Task RetryMediaLocked(string peer,Session session,Guid epoch,bool newAuthentication)
    {
        PruneMediaLocked();
        if(!ReceiptCapable(session))
        {
            if(session.ProvenCapabilities!=null)
                foreach(var (id,item) in retainedMedia.Where(p=>p.Value.Peer==peer&&p.Value.Generation!=session.Generation).ToArray())
                {ReleaseRetainedMedia(id);MediaTransferStatus?.Invoke(peer,id,"retry blocked: receipt capability unavailable");}
            return;
        }
        string budgetKey=peer+":"+epoch.ToString("N");
        int count=mediaRetryBudgets.GetValueOrDefault(budgetKey);
        if(!mediaRetryBudgets.ContainsKey(budgetKey)&&mediaRetryBudgets.Count>=256)mediaRetryBudgets.Remove(mediaRetryBudgets.Keys.First());
        foreach(var (id,item) in retainedMedia.Where(p=>p.Value.Peer==peer).OrderBy(p=>p.Value.Created).ThenBy(p=>p.Key).ToArray())
        {
            if(count>=2)break;
            if(item.Retries>=2||!newAuthentication&&item.LastLinkEpoch==epoch||newAuthentication&&item.Generation==session.Generation)continue;
            item.LastLinkEpoch=epoch;item.Generation=session.Generation;item.Retries++;item.Created=MediaClock();count++;
            mediaRetryBudgets[budgetKey]=count;
            int accepted=await SendMediaOnSessionLocked(peer,item.Media.Encode(),session);
            MediaTransferStatus?.Invoke(peer,id,accepted>0?"awaiting receipt":"interrupted; waiting for reconnection");
        }
    }
}

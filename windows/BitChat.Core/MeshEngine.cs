using System.Security.Cryptography;

namespace BitChat.Core;

public sealed record MeshPeer(string Id,string Nickname,byte[] NoiseKey,byte[] SigningKey,byte[] Capabilities,long LastSeen,bool Authenticated=false);

public sealed class MeshEngine:IDisposable,IAsyncDisposable
{
    private sealed class Session(bool initiator,byte[] key){public bool Initiator=initiator;public NoiseHandshake Handshake=new(initiator,key);public NoiseCipher? Send,Receive;public long Started=Bytes.Now;public Session? Previous;public byte[]? FirstHandshake,Response;}
    private readonly byte[] noiseKey,signingSeed;
    private readonly Func<MeshPacket,Task<int>> send;
    private readonly SemaphoreSlim gate=new(1);private bool stopping;
    private readonly Dictionary<string,Session> sessions=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte[]> pinnedKeys=new();
    public event Action<string,byte[]>? IdentityAuthenticated;
    public void RestorePinnedKey(string peer,byte[] key)=>pinnedKeys[peer]=key.ToArray();
    private readonly Dictionary<string,List<byte[]>> pending=[];
    private readonly Dictionary<string,MeshPeer> peers=[];
    private readonly Dictionary<string,long> seen=[];
    private readonly FragmentAssembler fragments=new();
    private readonly Dictionary<string,MeshPacket> archive=[];
    private readonly Dictionary<string,long> lastSyncResponse=[];
    private long lastSyncRequest;
    private readonly Dictionary<string,string[]> topology=[];
    private readonly Dictionary<string,(string Peer,long Started)> probes=[];
    public Func<byte[][]> DirectNeighbors{get;set;}=()=>[];
    public event Action<string,string>? IngressPeerValidated;
    public event Action<MeshPacket>? Archived;
    private readonly HashSet<string> delivered=[];
    public string Nickname{get;set;}
    public string? BridgeCell{get;set;}
    public byte[] PeerId {get;}
    public string Id=>Bytes.Hex(PeerId);
    public Func<string,bool> IsBlocked{get;set;}=_=>false;
    public event Action<MeshPeer>? PeerChanged;
    public event Action<ChatMessage>? Message;
    public event Action<string,MediaPacket,bool>? Media;
    public event Action<string,string>? Receipt;
    public event Action<string>? Status;
    public event Action<string,byte,byte[]>? TypedPayload;
    // Capability bit 8 = private encrypted media, bit 6 = mesh diagnostics.
    public byte[] Capabilities {get;set;}=[0x40,0x01];
    public MeshEngine(byte[] noiseKey,byte[] signingSeed,string nickname,Func<MeshPacket,Task<int>> send)
    {this.noiseKey=noiseKey.ToArray();this.signingSeed=signingSeed.ToArray();Nickname=nickname;this.send=send;PeerId=Crypto.Hash(Crypto.PublicX(noiseKey))[..8];}
    private MeshPacket Packet(byte type,byte[] payload,string? recipient=null,byte version=1,bool signed=true,ulong? timestamp=null)
    {
        var p=new MeshPacket(type,PeerId,payload,recipient==null?null:Convert.FromHexString(recipient),Version:version,Timestamp:timestamp??(ulong)Bytes.Now);
        return signed?p with{Signature=Crypto.SignEd(signingSeed,p.SigningBytes())}:p;
    }
    public async Task<int> Announce()
    {var p=Packet(1,new Announcement(Nickname,Crypto.PublicX(noiseKey),Crypto.PublicEd(signingSeed),Capabilities,BridgeCell,DirectNeighbors()).Encode());await gate.WaitAsync();try{Remember(p);}finally{gate.Release();}return await send(p);}
    public async Task<int> SendPublic(string text,ulong? timestamp=null)
    {
        if(Bytes.Utf8(text).Length>60000)throw new ArgumentException("Public message exceeds 60000 bytes");
        var packet=Packet(2,Bytes.Utf8(text),timestamp:timestamp);await gate.WaitAsync();try{seen[Dedup(packet)]=Bytes.Now;}finally{gate.Release();}
        int sent=await send(packet);if(sent>0){await gate.WaitAsync();try{Remember(packet);}finally{gate.Release();}}return sent;
    }
    public async Task SendPrivate(string peer,string text,string messageId)
    {
        if(Bytes.Utf8(text).Length>255)throw new ArgumentException("The iOS mesh private-message format accepts at most 255 UTF-8 bytes per message.");
        await SendTyped(peer,Bytes.Join([1],Announcement.Tlv(0,Bytes.Utf8(messageId)),Announcement.Tlv(1,Bytes.Utf8(text))));
    }
    public async Task SendMedia(MediaPacket media,string? recipient=null)
    {if(recipient==null)await send(Packet(0x22,media.Encode(),version:2));else await SendTyped(recipient,Bytes.Join([0x20],media.Encode()));}
    public async Task SendTyped(string peer,byte[] payload)
    {
        await gate.WaitAsync();try{await SendTypedLocked(peer,payload);}finally{gate.Release();}
    }
    private async Task SendTypedLocked(string peer,byte[] payload)
    {
        if(IsBlocked(peer))throw new InvalidOperationException("Peer is blocked");
        if(sessions.TryGetValue(peer,out var session)&&(session.Send??session.Previous?.Send) is NoiseCipher activeSend){await send(Packet(0x11,activeSend.Encrypt(payload),peer,payload.Length>60000?(byte)2:(byte)1,false));return;}
        if(!pending.TryGetValue(peer,out var list))pending[peer]=list=[];
        if(list.Count>=64)throw new IOException("Private send queue is full");list.Add(payload);
        if(session==null||Bytes.Now-session.Started>30000)
        {
            session?.Handshake.Dispose();if(sessions.Count>=128&&!sessions.ContainsKey(peer))throw new InvalidOperationException("Noise session limit reached");sessions[peer]=session=new(true,noiseKey);
            await send(Packet(0x10,session.Handshake.Write(),peer,signed:false));Status?.Invoke("Negotiating encrypted session with "+peer);
        }
    }
        private void Remember(MeshPacket p)
    {
        if(p.Type is not (1 or 2 or 0x23 or 0x24 or 0x25)||p.Recipient!=null||p.Timestamp<(ulong)(Bytes.Now-7L*86400000))return;
        foreach(var old in archive.Where(x=>x.Value.Timestamp<(ulong)(Bytes.Now-7L*86400000)).Select(x=>x.Key).ToArray())archive.Remove(old);
        string id=Bytes.Hex(GossipFilter.PacketId(p));if(p.Type==1)foreach(var old in archive.Where(x=>x.Value.Type==1&&x.Value.Sender.SequenceEqual(p.Sender)).Select(x=>x.Key).ToArray())archive.Remove(old);
        if(archive.Count>=1000)archive.Remove(archive.Keys.First());if(archive.Values.Sum(x=>x.Payload.Length)+p.Payload.Length>8*1024*1024)return;
        if(archive.TryAdd(id,p))Archived?.Invoke(p);
    }
    public void RestoreArchive(IEnumerable<byte[]> frames)
    {
        foreach(var bytes in frames.TakeLast(1000))
        {
            try{var p=MeshPacket.Decode(bytes);string peer=Bytes.Hex(p.Sender);var key=peer==Id?Crypto.PublicEd(signingSeed):peers.GetValueOrDefault(peer)?.SigningKey;if(key!=null&&p.Signature!=null&&Crypto.VerifyEd(key,p.SigningBytes(),p.Signature))Remember(p);}catch(Exception e)when(e is InvalidDataException or ArgumentException or OverflowException){}
        }
    }
    public async Task RequestSync()
    {
        await gate.WaitAsync();try
        {
            var list=archive.Values.OrderByDescending(p=>p.Timestamp).ToArray();var filter=GossipFilter.Build(list.Select(GossipFilter.PacketId).ToArray());
            ulong? since=filter.Included>0&&filter.Included<list.Length?list[filter.Included-1].Timestamp:null;lastSyncRequest=Bytes.Now;
            await send(Packet(0x21,filter.Request(since),signed:false) with{Ttl=0});
        }finally{gate.Release();}
    }
    private async Task RespondSync(string peer,byte[] bytes)
    {
        if(lastSyncResponse.TryGetValue(peer,out var last)&&Bytes.Now-last<10000)return;lastSyncResponse[peer]=Bytes.Now;
        var (filter,types,since)=GossipFilter.Parse(bytes);var values=filter.Values();
        var missing=archive.Values.Where(p=>GossipFilter.BitFor(p.Type) is int bit&&(types&(1UL<<bit))!=0&&(p.Type==1||since==null||p.Timestamp>=since)&&!values.Contains(GossipFilter.Bucket(GossipFilter.PacketId(p),filter.M)));
        int budget=128*1024,count=0;foreach(var packet in missing.OrderBy(p=>p.Type==1?0:1).ThenBy(p=>p.Timestamp))
        {if(++count>200||packet.Payload.Length>budget)break;budget-=packet.Payload.Length;await send(packet with{Ttl=0,IsRsr=true});}
    }
    private static string Dedup(MeshPacket p)=>Bytes.Hex(Crypto.Hash(Bytes.Join(p.Sender,[p.Type],Bytes.U64(p.Timestamp),p.Payload)));
    public async Task Receive(byte[] bytes,string? ingress=null)
    {
        if(stopping)return;await gate.WaitAsync();
        try
        {
            if(stopping)return;var p=MeshPacket.Decode(bytes);if(p.Type==0x20){var result=fragments.Add(p);if(result==null)return;p=result;}
            string peerId=Bytes.Hex(p.Sender);if(peerId==Id||IsBlocked(peerId)||p.Ttl>7)return;
            if(p.Timestamp>(ulong)(Bytes.Now+300000))return;
            if(p.IsRsr&&Bytes.Now-lastSyncRequest>60000)return;
            var dedup=Dedup(p);if(seen.ContainsKey(dedup))return;
            foreach(var old in seen.Where(x=>Bytes.Now-x.Value>300000).Select(x=>x.Key).ToArray())seen.Remove(old);
            if(seen.Count>=10000)seen.Remove(seen.Keys.First());seen[dedup]=Bytes.Now;
            bool addressed=p.Recipient==null||p.Recipient.All(b=>b==255)||p.Recipient.SequenceEqual(PeerId);
            peers.TryGetValue(peerId,out var peer);
            if(p.Type==1)
            {
                var ann=Announcement.Decode(p.Payload);
                if(!Crypto.Hash(ann.NoiseKey)[..8].SequenceEqual(p.Sender)||p.Signature==null||!Crypto.VerifyEd(ann.SigningKey,p.SigningBytes(),p.Signature))return;
                if(pinnedKeys.TryGetValue(peerId,out var pinned)&&!pinned.SequenceEqual(ann.SigningKey))return;
                if(peer!=null&&(!peer.NoiseKey.SequenceEqual(ann.NoiseKey)||!peer.SigningKey.SequenceEqual(ann.SigningKey)))return;
                if(peers.Count>=512&&!peers.ContainsKey(peerId)){var oldest=peers.Values.Where(x=>!pinnedKeys.ContainsKey(x.Id)).OrderBy(x=>x.LastSeen).FirstOrDefault();if(oldest==null)return;peers.Remove(oldest.Id);}
                peers[peerId]=peer=new(peerId,ann.Nickname,ann.NoiseKey,ann.SigningKey,ann.Capabilities??[0],Bytes.Now,peer?.Authenticated??false);topology[peerId]=(ann.Neighbors??[]).Select(Bytes.Hex).ToArray();if(ingress!=null&&p.Ttl==7&&!p.IsRsr)IngressPeerValidated?.Invoke(ingress,peerId);PeerChanged?.Invoke(peer);
            }
            else if(addressed)
            {
                switch(p.Type)
                {
                    case 2:
                        if(peer==null||p.Signature==null||!Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))return;
                        Message?.Invoke(new(dedup,peer.Nickname,Bytes.Text(p.Payload),(long)p.Timestamp,PeerId:peerId));break;
                    case 3:
                        if(peer==null||p.Signature==null||!Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))return;
                        peers.Remove(peerId);PeerChanged?.Invoke(peer with{LastSeen=0});break;
                    case 4:if(p.Recipient!=null&&p.Recipient.SequenceEqual(PeerId)&&peer!=null&&p.Signature!=null&&Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))TypedPayload?.Invoke(peerId,p.Type,p.Payload);break;
                    case 0x28:
                        if(peer!=null&&p.Signature!=null&&Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))
                        {var fields=Tlv16.Parse(p.Payload);byte direction=fields.Require(1,1)[0];bool directed=p.Recipient!=null&&p.Recipient.SequenceEqual(PeerId);if((direction is 1 or 3)&&directed||(direction is 2 or 4)&&p.Recipient==null)TypedPayload?.Invoke(peerId,p.Type,p.Payload);}break;
                    case 0x29:if(peer!=null&&p.Signature!=null&&p.Timestamp>=(ulong)(Bytes.Now-30000)&&Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))TypedPayload?.Invoke(peerId,p.Type,p.Payload);break;
                    case 0x21:if(peer!=null)await RespondSync(peerId,p.Payload);break;
                    case 0x10:if(peer!=null)await Handshake(peerId,p.Payload);break;
                    case 0x11:
                        if(!sessions.TryGetValue(peerId,out var session)||(session.Receive??session.Previous?.Receive) is not NoiseCipher activeReceive)return;
                        byte[] plain;try{plain=activeReceive.Decrypt(p.Payload);}catch(CryptographicException){if(session.Send!=null&&Bytes.Now-session.Started>30000){var candidate=new Session(true,noiseKey){Previous=session};sessions[peerId]=candidate;await send(Packet(0x10,candidate.Handshake.Write(),peerId,signed:false));}throw;}if(plain.Length<1)return;await HandleTyped(peerId,plain[0],plain[1..],p.Timestamp);break;
                    case 0x22:
                        if(peer==null||p.Signature==null||!Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))return;
                        Media?.Invoke(peerId,MediaPacket.Decode(p.Payload),p.Recipient!=null);break;
                    case 0x26:
                        if(p.Payload.Length!=9||peer==null||p.Signature==null||!Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))return;
                        await send(Packet(0x27,p.Payload,peerId));break;
                    case 0x27:
                        if(peer!=null&&p.Payload.Length==9&&p.Signature!=null&&Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature)&&probes.Remove(Bytes.Hex(p.Payload[..8]),out var probe)&&probe.Peer==peerId)Status?.Invoke($"Pong from {peer.Nickname} · {Bytes.Now-probe.Started} ms · {8-p.Ttl} hops");break;
                    default:TypedPayload?.Invoke(peerId,p.Type,p.Payload);break;
                }
            }
            if(p.Signature!=null&&peer!=null&&Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature))Remember(p);
            // Relay only packets with a validated author/signature, or bounded
            // encrypted/handshake traffic from known peers. TTL is mutable.
            bool relay=!p.IsRsr&&p.Ttl>1&&peer!=null&&(p.Type is 0x10 or 0x11||(p.Signature!=null&&Crypto.VerifyEd(peer.SigningKey,p.SigningBytes(),p.Signature)));
            if(relay&&(p.Recipient==null||!p.Recipient.SequenceEqual(PeerId)))await send(p with{Ttl=(byte)(p.Ttl-1)});
        }
        catch(Exception e)when(e is InvalidDataException or CryptographicException or ArgumentException or OverflowException or InvalidOperationException){Status?.Invoke("Rejected mesh packet: "+e.Message);}
        finally{gate.Release();}
    }
    private async Task Handshake(string peerId,byte[] bytes)
    {
        sessions.TryGetValue(peerId,out var session);
        if(bytes.Length==32)
        {
            if(session!=null&&session.Send==null&&!session.Initiator&&Bytes.Now-session.Started<30000)
            {if(session.FirstHandshake?.SequenceEqual(bytes)==true&&session.Response!=null)await send(Packet(0x10,session.Response,peerId,signed:false));return;}
            // Deterministic collision resolution: lower ID remains initiator.
            if(session?.Initiator==true&&string.CompareOrdinal(Id,peerId)<0)return;
            if(sessions.Count>=128&&!sessions.ContainsKey(peerId))return;var previous=session?.Send!=null?session:session?.Previous;
            session?.Handshake.Dispose();sessions[peerId]=session=new(false,noiseKey){Previous=previous,FirstHandshake=bytes.ToArray()};session.Handshake.Read(bytes);session.Response=session.Handshake.Write();await send(Packet(0x10,session.Response,peerId,signed:false));return;
        }
        if(session==null||session.Send!=null||Bytes.Now-session.Started>30000)return;
        session.Handshake.Read(bytes);
        if(!session.Handshake.Complete){var response=session.Handshake.Write();await send(Packet(0x10,response,peerId,signed:false));}
        if(!session.Handshake.Complete)return;
        var remote=session.Handshake.RemoteStatic!;
        if(Bytes.Hex(Crypto.Hash(remote)[..8])!=peerId)throw new CryptographicException("Noise peer ID mismatch");
        if(peers.TryGetValue(peerId,out var known)&&!known.NoiseKey.SequenceEqual(remote))throw new CryptographicException("Noise identity changed");
        (session.Send,session.Receive)=session.Handshake.Split();session.Previous?.Send?.Dispose();session.Previous?.Receive?.Dispose();session.Previous=null;
        if(known!=null){peers[peerId]=known=known with{Authenticated=true};PeerChanged?.Invoke(known);}
        var state=Bytes.Join([1],Announcement.Tlv(1,Capabilities),Announcement.Tlv(2,Crypto.PublicEd(signingSeed)));
        await SendTypedLocked(peerId,Bytes.Join([0x21],state));
        if(pending.Remove(peerId,out var list))foreach(var payload in list)await SendTypedLocked(peerId,payload);
        Status?.Invoke("Encrypted session established: "+peerId);
    }
    private async Task HandleTyped(string peer,byte type,byte[] payload,ulong timestamp)
    {
        var known=peers.GetValueOrDefault(peer);
        if(type==1)
        {
            var r=new WireReader(payload);string? id=null,text=null;
            while(r.Remaining>0){byte t=r.Byte();var value=Bytes.Text(r.Read(r.Byte()));if(t==0)id=value;else if(t==1)text=value;else throw new InvalidDataException("Invalid private-message TLV");}
            if(id==null||text==null)throw new InvalidDataException("Missing private-message fields");
            if(delivered.Add(peer+id)){if(delivered.Count>20000)delivered.Remove(delivered.First());Message?.Invoke(new(id,known?.Nickname??peer,text,(long)timestamp,true,peer,Nickname));}
            await SendTypedLocked(peer,Bytes.Join([3],Bytes.Utf8(id)));
        }
        else if(type is 2 or 3)Receipt?.Invoke(Bytes.Text(payload),type==2?"read":"delivered");
        else if(type is 0x20 or 0x09)Media?.Invoke(peer,MediaPacket.Decode(payload),true);
        else if(type==0x21)
        {
            var r=new WireReader(payload);if(r.Byte()!=1)throw new InvalidDataException("Unsupported authenticated state");byte[]? signing=null,caps=null;
            var fields=new HashSet<byte>();while(r.Remaining>0){byte t=r.Byte();if(!fields.Add(t))throw new InvalidDataException("Duplicate authenticated state field");var b=r.Read(r.Byte());if(t==1)caps=b;else if(t==2)signing=b;}
            if(signing?.Length!=32||caps==null)throw new InvalidDataException("Invalid authenticated state");
            if(known!=null&&!signing.SequenceEqual(known.SigningKey))throw new CryptographicException("Signing identity mismatch");
            if(known!=null){pinnedKeys[peer]=signing.ToArray();IdentityAuthenticated?.Invoke(peer,signing);known=known with{Capabilities=caps,Authenticated=true};peers[peer]=known;PeerChanged?.Invoke(known);}
        }
        else TypedPayload?.Invoke(peer,type,payload);
    }
    public async Task<int> Ping(string peer)
    {var nonce=RandomNumberGenerator.GetBytes(8);await gate.WaitAsync();try{if(probes.Count>=128)probes.Remove(probes.Keys.First());probes[Bytes.Hex(nonce)]=(peer,Bytes.Now);}finally{gate.Release();}return await send(Packet(0x26,Bytes.Join(nonce,[7]),peer));}
    public async Task<string[]> Trace(string target)
    {
        await gate.WaitAsync();try
        {
            var queue=new Queue<string[]>();queue.Enqueue([Id]);var visited=new HashSet<string>{Id};
            while(queue.Count>0){var path=queue.Dequeue();var current=path[^1];if(current==target)return path;if(path.Length>8)continue;var neighbors=current==Id?DirectNeighbors().Select(Bytes.Hex):topology.GetValueOrDefault(current,[]);foreach(var next in neighbors)if(visited.Add(next))queue.Enqueue(path.Append(next).ToArray());}return [];
        }finally{gate.Release();}
    }
    public async Task<int> SendBroadcast(byte type,byte[] payload,byte version=1)
    {var p=Packet(type,payload,version:version);int count=await send(p);if(count>0){await gate.WaitAsync();try{Remember(p);}finally{gate.Release();}}return count;}
    public Task<int> SendDirected(byte type,byte[] payload,string recipient)=>send(Packet(type,payload,recipient));
    public void RestorePeer(MeshPeer peer){if(Bytes.Hex(Crypto.Hash(peer.NoiseKey)[..8])==peer.Id)peers[peer.Id]=peer with{LastSeen=0,Authenticated=false};}
    public Task<int> Leave()=>send(Packet(3,[]));
    public async ValueTask DisposeAsync(){stopping=true;await gate.WaitAsync();try{Dispose();}finally{gate.Release();}}
    public void Dispose(){stopping=true;foreach(var s in sessions.Values){s.Handshake.Dispose();s.Send?.Dispose();s.Receive?.Dispose();s.Previous?.Send?.Dispose();s.Previous?.Receive?.Dispose();}CryptographicOperations.ZeroMemory(noiseKey);CryptographicOperations.ZeroMemory(signingSeed);}
}

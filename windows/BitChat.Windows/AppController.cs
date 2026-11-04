using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using BitChat.Core;

namespace BitChat.Windows;

public sealed partial class AppController:IAsyncDisposable
{
    public readonly StateStore Store;
    public readonly BluetoothTransport Bluetooth=new();
    public readonly RelayClient Relays=new();
    public readonly MeshEngine Mesh;
    public readonly ConcurrentDictionary<string,MeshPeer> Peers=new();
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<string,byte> seenEvents=new();
    private Task? maintenance;private TorProcess? tor;
    private readonly Dictionary<string,(VerificationQr Qr,byte[] Nonce,long Started)> pendingVerification=[];
    private readonly Dictionary<string,string> lastVerificationNonce=[];
    public Action<Action> Dispatch{get;set;}=action=>action();
    public Func<bool> IsAppActive{get;set;}=()=>false;
    private bool disposed;
    private void Post(Action action){if(disposed)return;Dispatch(()=>{if(!disposed)action();});}
    public event Action<StoredMessage>? Message;
    public event Action? PeersChanged;
    public event Action<string>? Status;
    public event Action<string,string>? Receipt;
    public AppController(StateStore store)
    {
        Store=store;mediaLedger=new(Path.Combine(StateStore.Root,"media"),ProtectMediaDecision,UnprotectMediaDecision);Mesh=new(store.State.NoiseKey,store.State.SigningSeed,store.State.Nickname,Bluetooth.Send){Capabilities=[0x79,0x03],IsBlocked=id=>Store.State.Blocked.Contains(id)};
        foreach(var pair in Store.State.KnownPeers){Peers[pair.Key]=pair.Value with{LastSeen=0,Authenticated=false};Mesh.RestorePeer(pair.Value);}
        foreach(var pair in Store.State.PinnedSigningKeys)Mesh.RestorePinnedKey(pair.Key,pair.Value);
        Mesh.RestoreArchive(Store.State.GossipArchive);
        Mesh.IdentityAuthenticated+=(peer,key)=>Post(()=>{Store.State.PinnedSigningKeys[peer]=key;Store.Save();});
        Bluetooth.Status+=s=>Status?.Invoke(s);Bluetooth.Packet+=(ingress,b)=>_=Mesh.Receive(b,ingress);Mesh.DirectNeighbors=()=>Bluetooth.DirectPeers;Mesh.IngressPeerValidated+=(ingress,peer)=>{Bluetooth.RememberDirectPeer(ingress,peer);_=Mesh.PeerLinkReady(peer,Bluetooth.LinkEpoch(ingress));};Bluetooth.LinkReady+=()=>_=OnLinkReady();
        Mesh.Status+=s=>Status?.Invoke(s);Mesh.PeerChanged+=p=>{Peers[p.Id]=p;Post(()=>{Store.State.KnownPeers[p.Id]=p;Store.Save();});PeersChanged?.Invoke();};
        Mesh.Message+=m=>Post(()=>{if(m.IsPrivate&&HandleFavorite(m.PeerId!,m.Content))return;Add(new(m.IsPrivate?"mesh:"+m.PeerId:"mesh",m.Id,m.Sender,m.Content,m.Timestamp,Peer:m.PeerId));});
        Mesh.Media+=(peer,media,isPrivate)=>Post(()=>ReceiveRawMedia(peer,media,isPrivate));
        Mesh.AcceptPrivateMedia=AcceptMedia;
        foreach(string fingerprint in Store.State.PrivateMediaCapable)Mesh.RestoreMediaCapable(fingerprint);
        Mesh.MediaCapabilityObserved+=fingerprint=>Post(()=>{Store.State.PrivateMediaCapable.Add(fingerprint);Store.Save();});
        Mesh.Receipt+=(peer,id,status)=>Post(()=>UpdateReceipt(id,status,"mesh:"+peer));
        Mesh.MediaTransferStatus+=(peer,id,status)=>Post(()=>UpdateReceipt(id,status,"mesh:"+peer));
        Relays.Status+=s=>Status?.Invoke(s);Relays.Received+=e=>Post(()=>OnNostr(e));
        Mesh.TypedPayload+=(peer,type,bytes)=>Post(()=>HandleTyped(peer,type,bytes));
        Mesh.Archived+=packet=>Post(()=>ArchivePacket(packet));ConfigureGateway();
    }
    private async Task OnLinkReady(){try{await Mesh.Announce();await Task.Delay(1500,stop.Token);await PublishPrekeys(true);await Mesh.RequestSync();}catch(OperationCanceledException){}}
    public async Task Start()
    {
        try{await Bluetooth.Start();await PublishPrekeys();}catch(Exception e){Status?.Invoke("Bluetooth unavailable: "+e.Message);}
        maintenance=Task.Run(async()=>
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
            try{while(await timer.WaitForNextTickAsync(stop.Token)){await Mesh.Announce();if(Bluetooth.Connections>0)await Mesh.RequestSync();Post(()=>_=MaintainOnUi());PeersChanged?.Invoke();}}catch(OperationCanceledException){}
        });
        if(Store.State.UseTor){tor=new();tor.Status+=s=>Status?.Invoke(s);Relays.Proxy=await tor.Start(stop.Token);}
        Relays.Start(Store.State.Relays);
        await SubscribeDm(Store.State.NostrKey,"identity");
        foreach(var (cell,key) in Store.State.LocationKeys)await SubscribeDm(key,"dm-"+cell);
        if(Geohash.Valid(Store.State.Cell))await JoinCell(Store.State.Cell);
    }
    private Task SubscribeDm(byte[] key,string id)=>Relays.Subscribe(id,new Dictionary<string,object>{{"kinds",new[]{1059}},{"#p",new[]{Bytes.Hex(Schnorr.Public(key))}},{"since",DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds()},{"limit",100}});
    public async Task JoinCell(string cell)
    {
        cell=cell.Trim().ToLowerInvariant();if(!Geohash.Valid(cell))throw new ArgumentException("Enter a valid geohash (1–12 characters).");
        Store.State.Cell=cell;var key=Store.State.LocationKey(cell);Store.Save();
        await Relays.Subscribe("location",new Dictionary<string,object>{{"kinds",new[]{20000,20001,1}},{"#g",new[]{cell}},{"since",DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds()},{"limit",200}});
        await SubscribeDm(key,"dm-"+cell);await SubscribeBridge();ConfigureGateway();Status?.Invoke("Joined #"+cell);
    }
    private void OnNostr(NostrEvent e,bool fromMesh=false)
    {
        try
        {
            if(!fromMesh)GatewayRelayEvent(e);
            if(!seenEvents.TryAdd(e.id,0))return;if(seenEvents.Count>20000)seenEvents.Clear();
            if(e.kind==1059)
            {
                var tag=e.tags.FirstOrDefault(t=>t.Length==2&&t[0]=="p")?[1];var key=new[]{Store.State.NostrKey}.Concat(Store.State.LocationKeys.Values).FirstOrDefault(k=>Bytes.Hex(Schnorr.Public(k))==tag);if(key==null)return;
                var rumor=PrivateEnvelope.Open(e,key);string peer=rumor.pubkey;if(Store.State.Blocked.Contains(peer))return;
                Store.State.NostrPeers.TryAdd(peer,peer[..8]);Store.State.NostrReplyAs[peer]=Bytes.Hex(Schnorr.Public(key));
                if(rumor.content.StartsWith("bitchat1:"))
                {
                    var embedded=NostrEmbedded.Decode(rumor.content);if(embedded.Recipient!=null&&!embedded.Recipient.SequenceEqual(Mesh.PeerId))return;byte type=embedded.Payload[0];
                    if(type==1)
                    {
                        var pm=NostrEmbedded.PrivateMessage(embedded.Payload[1..]);if(HandleFavorite(peer,pm.Text))return;
                        Add(new("nostr:"+peer,pm.Id,Store.State.NostrPeers[peer],pm.Text,rumor.created_at*1000,Peer:peer));_=SendNostrAck(peer,pm.Id,key,false);
                    }
                    else if(type is 2 or 3){UpdateNostrReceipt(peer,Bytes.Text(embedded.Payload[1..]),type==2?"read":"delivered");}
                }
                else Add(new("nostr:"+peer,e.id,Store.State.NostrPeers[peer],rumor.content,rumor.created_at*1000,Peer:peer));PeersChanged?.Invoke();
            }
            else if(e.kind==20001)
            {string? cell=e.tags.FirstOrDefault(t=>t.Length==2&&t[0] is "g" or "r")?[1];if(cell==Store.State.Cell){if(locationPresence.Count>2000)locationPresence.Remove(locationPresence.Keys.First());locationPresence[cell+":"+e.pubkey]=Bytes.Now;PeersChanged?.Invoke();}}
            else if(e.kind==20000&&e.tags.Any(t=>t.Length==2&&t[0]=="r"))
            {
                string? cell=e.tags.First(t=>t.Length==2&&t[0]=="r")[1];if(cell!=Store.State.Cell||Store.State.Blocked.Contains(e.pubkey))return;
                if(Store.State.LocationKeys.Values.Any(k=>Bytes.Hex(Schnorr.Public(k))==e.pubkey))return;
                var hint=e.tags.FirstOrDefault(t=>t.Length==4&&t[0]=="m");if(hint!=null&&ulong.TryParse(hint[3],out ulong ts)){string stable=RadioId(hint[2],ts,e.content);if(radioIds.Contains(stable))return;bridgeAliases[e.id]=stable;}
                string name=e.tags.FirstOrDefault(t=>t.Length==2&&t[0]=="n")?[1]??e.pubkey[..8];Add(new("mesh",e.id,name+" [bridge]",e.content,e.created_at*1000,Peer:"bridge:"+e.pubkey));
            }
            else if(e.kind is 20000 or 1)
            {
                var cell=e.tags.FirstOrDefault(t=>t.Length==2&&t[0]=="g")?[1];if(cell==null||Store.State.Blocked.Contains(e.pubkey))return;
                bool own=Store.State.LocationKeys.Values.Any(k=>Bytes.Hex(Schnorr.Public(k))==e.pubkey);if(own)return;
                string name=e.tags.FirstOrDefault(t=>t.Length==2&&t[0]=="n")?[1]??e.pubkey[..8];Store.State.NostrPeers[e.pubkey]=name;Store.State.NostrReplyAs.TryAdd(e.pubkey,Bytes.Hex(Schnorr.Public(Store.State.LocationKey(cell))));
                Add(new((e.kind==1?"notes:":"geo:")+cell,e.id,name,e.content,e.created_at*1000,Peer:e.pubkey));PeersChanged?.Invoke();
            }
        }
        catch(Exception ex)when(ex is CryptographicException or InvalidDataException or FormatException or ArgumentException){Status?.Invoke("Rejected Nostr event: "+ex.Message);}
    }
    private void ArchivePacket(MeshPacket packet)
    {
        RadioObserved(packet);var bytes=packet.Encode();var archive=Store.State.GossipArchive;
        if(packet.Type==1)archive.RemoveAll(old=>{try{var p=MeshPacket.Decode(old);return p.Type==1&&p.Sender.SequenceEqual(packet.Sender);}catch{return true;}});
        if(archive.Count>=1000)archive.RemoveAt(0);if(archive.Sum(b=>b.Length)+bytes.Length<=8*1024*1024)archive.Add(bytes);Store.Save();
    }
    public void Add(StoredMessage message)
    {
        lock(Store.State.Messages){if(Store.State.Messages.Any(m=>m.Id==message.Id&&m.Conversation==message.Conversation))return;Store.State.Messages.Add(message);}Store.Save();Message?.Invoke(message);
    }
    public async Task Send(string conversation,string text)
    {
        string id=Guid.NewGuid().ToString().ToUpperInvariant();long now=Bytes.Now;string status;
        if(conversation=="board"){await PostBoard(text);return;}
        else if(conversation.StartsWith("group:")){var group=Store.State.Groups[conversation];var payload=group.Seal(text,id,Store.State.Nickname,Store.State.SigningSeed,(ulong)now);int links=await Mesh.SendBroadcast(0x25,payload);if(links==0)throw new IOException("No Bluetooth link available");status="sent";}
        else if(conversation=="mesh"){int links=await Mesh.SendPublic(text,(ulong)now);if(links==0&&!(Store.State.Bridge&&Relays.Connected>0))throw new IOException("No Bluetooth link is available. The message was not sent.");await MirrorOwnMesh(text,now);status="sent";}
        else if(conversation.StartsWith("mesh:"))
        {
            string peer=conversation[5..];bool reachable=Peers.TryGetValue(peer,out var p)&&p.LastSeen>Bytes.Now-60000;
            if(!reachable&&Store.State.MeshNostrKeys.TryGetValue(peer,out var nostr)&&Relays.Connected>0)
            {var embedded=NostrEmbedded.Message(text,id,Mesh.PeerId,Convert.FromHexString(peer));var e=PrivateEnvelope.Wrap(embedded,Store.State.NostrKey,nostr);status=await Relays.Publish(e)?"accepted by relay":"unconfirmed";}
            else{await QueuePrivate(peer,id,text);status="queued";}
        }
        else
        {
            NostrEvent e;
            if(conversation.StartsWith("nostr:")){string peer=conversation[6..];if(Bytes.Utf8(text).Length>255)throw new ArgumentException("BitChat private messages accept up to 255 UTF-8 bytes");var key=ReplyKey(peer);e=PrivateEnvelope.Wrap(NostrEmbedded.Message(text,id),key,peer);}
            else
            {
                bool note=conversation.StartsWith("notes:");string cell=conversation[(note?6:4)..];var key=Store.State.LocationKey(cell);
                var tags=new List<string[]>{new[]{"g",cell},new[]{"n",Store.State.Nickname}};if(Store.State.Teleported&&!note)tags.Add(["t","teleport"]);if(note)tags.Add(["expiration",DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds().ToString()]);e=NostrEvent.Create(key,note?1:20000,text,tags.ToArray());
            }
            if(!conversation.StartsWith("nostr:"))id=e.id;status=await Relays.Publish(e)?"accepted by relay":(e.kind==20000&&await UplinkViaMesh(e,Store.State.Cell,false)?"sent to mesh gateway":"unconfirmed");
        }
        Add(new(conversation,id,Store.State.Nickname,text,now,true,status));
    }
    public async Task SendMedia(string conversation,string path)
    {
        if(conversation!="mesh"&&!conversation.StartsWith("mesh:"))throw new InvalidOperationException("Attachments are sent over Bluetooth mesh.");
        if(Bluetooth.Connections==0)throw new IOException("No Bluetooth link is available");
        var file=new FileInfo(path);if(file.Length>MediaPacket.MaxBytes)throw new ArgumentException("Attachments are limited to 1 MiB.");
        string mime=file.Extension.ToLowerInvariant() switch{".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".webp"=>"image/webp",".wav"=>"audio/wav",".m4a"=>"audio/mp4",_=>"application/octet-stream"};
        string name=mime=="image/jpeg"?"img_"+Guid.NewGuid().ToString().ToUpperInvariant()+".jpg":mime=="audio/mp4"?"voice_"+Guid.NewGuid().ToString().ToUpperInvariant()+".m4a":file.Name;
        await SendAttachment(conversation,new(name,mime,await File.ReadAllBytesAsync(path)));
    }
    private async Task Observe(Func<Task> action){try{await action();}catch(Exception e){Status?.Invoke(e.Message);}}
    private byte[] ReplyKey(string peer)
    {string? pub=Store.State.NostrReplyAs.GetValueOrDefault(peer);return pub==null?Store.State.NostrKey:new[]{Store.State.NostrKey}.Concat(Store.State.LocationKeys.Values).FirstOrDefault(k=>Bytes.Hex(Schnorr.Public(k))==pub)??Store.State.NostrKey;}
    private async Task SendNostrAck(string peer,string id,byte[] key,bool read)
    {try{await Relays.Publish(PrivateEnvelope.Wrap(NostrEmbedded.Ack(id,read),key,peer));}catch(Exception e){Status?.Invoke("Private receipt: "+e.Message);}}
    public Task MarkNostrRead(string peer,string id)=>SendNostrAck(peer,id,ReplyKey(peer),true);
    private void UpdateNostrReceipt(string peer,string id,string status)
    {
        UpdateReceipt(id,status,"nostr:"+peer);
        foreach(var mapping in Store.State.MeshNostrKeys.Where(p=>p.Value==peer))UpdateReceipt(id,status,"mesh:"+mapping.Key);
    }
    private void UpdateReceipt(string id,string status,string? expectedConversation=null)
    {
        StoredMessage? message=null;
        lock(Store.State.Messages)
        {
            int i=Store.State.Messages.FindIndex(m=>m.Id==id&&m.Own&&(expectedConversation==null||m.Conversation==expectedConversation));
            if(i<0)return;
            message=Store.State.Messages[i];
            if(message.Status=="read"&&status=="delivered")return;
            Store.State.Messages[i]=message with{Status=status};
        }
        if(status is "read" or "delivered")Store.State.Outbox.RemoveAll(p=>p.Id==id&&message.Conversation=="mesh:"+p.Peer);
        Store.Save();Receipt?.Invoke(id,status);
    }
    private bool HandleFavorite(string peer,string text)
    {
        bool favorite=text.StartsWith("[FAVORITED]"),unfavorite=text.StartsWith("[UNFAVORITED]");if(!favorite&&!unfavorite)return false;
        if(favorite)Store.State.FavoritedBy.Add(peer);else Store.State.FavoritedBy.Remove(peer);
        int colon=text.IndexOf(':');if(colon>=0)
        {try{string pub=Bytes.Hex(Bech32.Decode(text[(colon+1)..],"npub"));if(pub.Length==64){Store.State.MeshNostrKeys[peer]=pub;Store.State.NostrPeers[pub]=Peers.GetValueOrDefault(peer)?.Nickname??peer[..8];}}catch(FormatException){}}
        Store.Save();PeersChanged?.Invoke();return true;
    }
    public async Task SetFavorite(string peer,bool enabled)
    {
        if(enabled)Store.State.Favorites.Add(peer);else Store.State.Favorites.Remove(peer);Store.Save();
        string control=(enabled?"[FAVORITED]:":"[UNFAVORITED]:")+Bech32.Encode("npub",Schnorr.Public(Store.State.NostrKey));await Mesh.SendPrivate(peer,control,Guid.NewGuid().ToString().ToUpperInvariant());PeersChanged?.Invoke();
    }
    public string MyVerificationUrl()=>VerificationQr.Create(Store.State.NoiseKey,Store.State.SigningSeed,Store.State.Nickname,Bech32.Encode("npub",Schnorr.Public(Store.State.NostrKey))).Url();
    public async Task VerifyQr(string url)
    {
        var qr=VerificationQr.Parse(url);string peer=Bytes.Hex(Crypto.Hash(Convert.FromHexString(qr.Noise)))[..16];if(!Peers.TryGetValue(peer,out var discovered)||!Bytes.Hex(discovered.SigningKey).Equals(qr.Signing,StringComparison.OrdinalIgnoreCase))throw new CryptographicException("QR identity does not match a discovered peer");
        var nonce=RandomNumberGenerator.GetBytes(16);pendingVerification[peer]=(qr,nonce,Bytes.Now);await Mesh.SendTyped(peer,qr.Challenge(nonce));Status?.Invoke("Verification challenge sent to "+qr.Nickname);
    }
    private async Task ShareVouches()
    {
        var attestations=Store.State.Verified.Select(p=>Vouch.Create(Convert.FromHexString(p.Key),Convert.FromHexString(p.Value),Store.State.SigningSeed)).Take(16).ToArray();var batch=Vouch.Batch(attestations);
        foreach(var peer in Peers.Values.Where(p=>p.Authenticated&&Store.State.Verified.ContainsKey(Bytes.Hex(Crypto.Hash(p.NoiseKey)))))await Mesh.SendTyped(peer.Id,Bytes.Join([0x12],batch));
    }
    public async Task PostBoard(string text,bool urgent=false)
    {
        var post=BoardPost.Create(text,"",Store.State.Nickname,Store.State.SigningSeed,urgent);int links=await Mesh.SendBroadcast(0x23,post.Encode());if(links==0)throw new IOException("No Bluetooth link available");Store.State.Board[post.Key]=post;Add(new("board",post.Key,post.Nickname,post.Content,(long)post.Created,true,"sent"));
    }
    public async Task DeleteBoard(string id)
    {
        var post=Store.State.Board[id];var deletion=BoardDeletion.Create(post,Store.State.SigningSeed);await Mesh.SendBroadcast(0x23,deletion.Encode());Store.State.BoardDeletions[id]=deletion;Store.State.Board.Remove(id);Store.State.Messages.RemoveAll(m=>m.Id==id);Store.Save();PeersChanged?.Invoke();
    }
    public async Task<PrivateGroup> CreateGroup(string name,string[] peerIds)
    {
        string mine=Bytes.Hex(Crypto.Hash(Crypto.PublicX(Store.State.NoiseKey)));
        var members=new List<GroupMember>{new(mine,Crypto.PublicEd(Store.State.SigningSeed),Store.State.Nickname)};
        foreach(var id in peerIds.Distinct())
        {if(!Peers.TryGetValue(id,out var peer))throw new ArgumentException("Unknown group peer: "+id);members.Add(new(Bytes.Hex(Crypto.Hash(peer.NoiseKey)),peer.SigningKey,peer.Nickname));}
        if(members.Count>16||Bytes.Utf8(name).Length is <1 or >64)throw new ArgumentException("Groups accept 1–16 members and a name up to 64 bytes");
        var group=new PrivateGroup(RandomNumberGenerator.GetBytes(16),name,RandomNumberGenerator.GetBytes(32),1,members.ToArray(),mine,[]).Sign(Store.State.SigningSeed);
        Store.State.Groups[group.Conversation]=group;Store.Save();
        foreach(var id in peerIds.Distinct())await Mesh.SendTyped(id,Bytes.Join([6],group.Encode()));PeersChanged?.Invoke();return group;
    }
    public async Task ChangeGroupMember(string conversation,string peerId,bool remove)
    {
        var old=Store.State.Groups[conversation];string mine=Bytes.Hex(Crypto.Hash(Crypto.PublicX(Store.State.NoiseKey)));if(old.Creator!=mine)throw new InvalidOperationException("Only the creator can change group membership");
        if(!Peers.TryGetValue(peerId,out var peer))throw new ArgumentException("Unknown peer");string fingerprint=Bytes.Hex(Crypto.Hash(peer.NoiseKey));if(fingerprint==mine)throw new InvalidOperationException("The creator cannot remove themselves");
        var members=old.Members.Where(m=>m.Fingerprint!=fingerprint).ToList();if(!remove)members.Add(new(fingerprint,peer.SigningKey,peer.Nickname));
        if(members.Count>16)throw new ArgumentException("Group member limit reached");
        var group=(old with{Members=members.ToArray(),Key=RandomNumberGenerator.GetBytes(32),Epoch=checked(old.Epoch+1)}).Sign(Store.State.SigningSeed);
        Store.State.Groups[conversation]=group;Store.Save();foreach(var m in group.Members.Where(m=>m.Fingerprint!=mine))await Mesh.SendTyped(m.Fingerprint[..16],Bytes.Join([7],group.Encode()));PeersChanged?.Invoke();
    }
    private void HandleTyped(string peer,byte type,byte[] bytes)
    {
        try
        {
            if(type==0x28){_=Observe(()=>ReceiveCarrier(peer,bytes));}
            else if(type==4){ReceiveCourier(peer,bytes);}
            else if(type==0x24){ReceivePrekeys(peer,bytes);}
            else if(type is 0x29 or 8){ReceiveVoice(peer,bytes,type==8);}
            else if(type==0x10)
            {
                var f=Tlv8.Parse(bytes);string noise=Bytes.Text(f[1]);byte[] nonce=f[2];string mine=Bytes.Hex(Crypto.PublicX(Store.State.NoiseKey));if(noise!=mine||nonce.Length!=16)return;
                string nonceKey=Bytes.Hex(nonce);if(lastVerificationNonce.GetValueOrDefault(peer)==nonceKey)return;lastVerificationNonce[peer]=nonceKey;
                var signature=Crypto.SignEd(Store.State.SigningSeed,VerificationQr.ResponseSigning(noise,nonce));
                _=Mesh.SendTyped(peer,Bytes.Join([0x11],Announcement.Tlv(1,Bytes.Utf8(noise)),Announcement.Tlv(2,nonce),Announcement.Tlv(3,signature)));
            }
            else if(type==0x11)
            {
                var f=Tlv8.Parse(bytes);if(!pendingVerification.TryGetValue(peer,out var pending)||Bytes.Now-pending.Started>300000)return;
                string noise=Bytes.Text(f[1]);if(noise!=pending.Qr.Noise||!f[2].SequenceEqual(pending.Nonce)||!Crypto.VerifyEd(Convert.FromHexString(pending.Qr.Signing),VerificationQr.ResponseSigning(noise,f[2]),f[3]))return;
                pendingVerification.Remove(peer);string fingerprint=Bytes.Hex(Crypto.Hash(Convert.FromHexString(noise)));Store.State.Verified[fingerprint]=pending.Qr.Signing;Store.State.PinnedSigningKeys[peer]=Convert.FromHexString(pending.Qr.Signing);Store.Save();PeersChanged?.Invoke();Status?.Invoke("Identity verified: "+pending.Qr.Nickname);_=ShareVouches();
            }
            else if(type==0x12)
            {
                if(!Peers.TryGetValue(peer,out var voucher)||!voucher.Authenticated)return;string voucherFingerprint=Bytes.Hex(Crypto.Hash(voucher.NoiseKey));if(!Store.State.Verified.ContainsKey(voucherFingerprint))return;
                var attestations=Vouch.ParseBatch(bytes).Where(v=>v.Valid(voucher.SigningKey)).ToList();Store.State.Vouches[voucherFingerprint]=attestations;Store.Save();Status?.Invoke($"Received {attestations.Count} identity attestations from {voucher.Nickname}");
            }
            else if(type==0x23)
            {
                var fields=Tlv16.Parse(bytes);byte kind=fields.Require(1,1)[0];
                if(kind==1){var post=BoardPost.Decode(bytes);if(Store.State.BoardDeletions.ContainsKey(post.Key))return;Store.State.Board[post.Key]=post;Add(new("board",post.Key,post.Nickname,post.Content,(long)post.Created,Peer:peer));}
                else if(kind==2){var deletion=BoardDeletion.Decode(bytes);Store.State.BoardDeletions[deletion.Key]=deletion;Store.State.Board.Remove(deletion.Key);Store.State.Messages.RemoveAll(m=>m.Conversation=="board"&&m.Id==deletion.Key);Store.Save();PeersChanged?.Invoke();}
            }
            else if(type is 6 or 7)
            {
                var group=PrivateGroup.Decode(bytes);string mine=Bytes.Hex(Crypto.Hash(Crypto.PublicX(Store.State.NoiseKey)));
                if(!group.Members.Any(m=>m.Fingerprint==mine&&m.SigningKey.SequenceEqual(Crypto.PublicEd(Store.State.SigningSeed))))throw new CryptographicException("Group invite does not include our identity");
                if(!Peers.TryGetValue(peer,out var sender)||!sender.Authenticated||Bytes.Hex(Crypto.Hash(sender.NoiseKey))!=group.Creator)throw new CryptographicException("Group state must come from the authenticated creator");
                var creator=group.Members.Single(m=>m.Fingerprint==group.Creator);if(!creator.SigningKey.SequenceEqual(sender.SigningKey))throw new CryptographicException("Group creator key mismatch");
                if(Store.State.Groups.TryGetValue(group.Conversation,out var old))
                {var oldCreator=old.Members.Single(m=>m.Fingerprint==old.Creator);if(old.Creator!=group.Creator||!oldCreator.SigningKey.SequenceEqual(creator.SigningKey)||group.Epoch<=old.Epoch)throw new CryptographicException("Group takeover or epoch rollback rejected");}
                Store.State.Groups[group.Conversation]=group;Store.Save();PeersChanged?.Invoke();Status?.Invoke("Group state updated: "+group.Name);
            }
            else if(type==0x25)
            {
                var fields=Tlv16.Parse(bytes);string conversation="group:"+Bytes.Hex(fields.Require(1,16));if(!Store.State.Groups.TryGetValue(conversation,out var group))return;
                var message=group.Open(bytes);Add(new(conversation,message.Id,message.Sender,message.Content,message.Timestamp,Peer:message.PeerId));
            }
        }
        catch(Exception e)when(e is InvalidDataException or CryptographicException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or System.Text.Json.JsonException){Status?.Invoke("Rejected group payload: "+e.Message);}
    }
    public async ValueTask DisposeAsync(){if(disposed)return;disposed=true;stop.Cancel();Bluetooth.Dispose();await DisposeVoice();if(maintenance!=null)try{await maintenance;}catch(OperationCanceledException){}await Relays.DisposeAsync();if(tor!=null)await tor.DisposeAsync();await Mesh.DisposeAsync();stop.Dispose();}
}

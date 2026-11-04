using System.IO;
using System.Security.Cryptography;
using BitChat.Core;
namespace BitChat.Windows;

public sealed record LocalPrekey(byte[] Secret,long? Consumed=null);
public sealed class PendingPrivate
{
    public string Peer{get;set;}="";public string Id{get;set;}="";public string Text{get;set;}="";public long Created{get;set;}=Bytes.Now;public long LastAttempt{get;set;}
    public CourierEnvelope? Envelope{get;set;}public HashSet<string> DepositedTo{get;set;}=[];
}
public sealed record CarriedEnvelope(CourierEnvelope Envelope,string Depositor,long LastTransfer=0);

public sealed partial class AppController
{
    private long lastPrekeys;private bool maintaining;
    public async Task PublishPrekeys(bool force=false)
    {
        if(!force&&Bytes.Now-lastPrekeys<60000)return;
        foreach(var id in Store.State.LocalPrekeys.Where(p=>p.Value.Consumed!=null&&Bytes.Now-p.Value.Consumed>48L*3600000).Select(p=>p.Key).ToArray()){CryptographicOperations.ZeroMemory(Store.State.LocalPrekeys[id].Secret);Store.State.LocalPrekeys.Remove(id);}
        int active=Store.State.LocalPrekeys.Count(p=>p.Value.Consumed==null);
        if(active<4)
        {
            if(Store.State.LocalPrekeys.Count>512)throw new InvalidOperationException("Prekey retention limit reached");
            while(active<8){uint id=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));if(Store.State.LocalPrekeys.TryAdd(id,new(RandomNumberGenerator.GetBytes(32))))active++;}
        }
        var keys=Store.State.LocalPrekeys.Where(p=>p.Value.Consumed==null).Take(8).Select(p=>new Prekey(p.Key,Crypto.PublicX(p.Value.Secret))).ToArray();
        var unsigned=PrekeyBundle.Create(Crypto.PublicX(Store.State.NoiseKey),keys,Store.State.SigningSeed) with{Generated=Math.Max((ulong)Bytes.Now,Store.State.OwnBundleGeneration+1)};var bundle=unsigned with{Signature=Crypto.SignEd(Store.State.SigningSeed,unsigned.SigningBytes())};Store.State.OwnBundleGeneration=bundle.Generated;Store.Save();lastPrekeys=Bytes.Now;await Mesh.SendBroadcast(0x24,bundle.Encode());
    }
    private void ReceivePrekeys(string peer,byte[] payload)
    {
        var bundle=PrekeyBundle.Decode(payload);if(!Peers.TryGetValue(peer,out var owner)||(!owner.Authenticated&&!Store.State.PinnedSigningKeys.ContainsKey(peer))||!owner.NoiseKey.SequenceEqual(bundle.Owner)||!bundle.Valid(owner.SigningKey))throw new CryptographicException("Unbound prekey bundle");
        if(Store.State.PrekeyBundles.TryGetValue(peer,out var old)&&old.Generated>=bundle.Generated)return;Store.State.PrekeyBundles[peer]=bundle;Store.Save();
    }
    private async Task QueuePrivate(string peer,string id,string text)
    {
        if(Store.State.Outbox.Count>=256)throw new IOException("Private outbox is full");
        Store.State.Outbox.Add(new(){Peer=peer,Id=id,Text=text,LastAttempt=Bytes.Now});Store.Save();await Mesh.SendPrivate(peer,text,id);await DepositOutbox();
    }
    private async Task DepositOutbox()
    {
        var couriers=Peers.Values.Where(p=>p.LastSeen>Bytes.Now-60000&&p.Authenticated&&Store.State.Favorites.Contains(p.Id)&&Store.State.FavoritedBy.Contains(p.Id)).ToArray();
        foreach(var pending in Store.State.Outbox.ToArray())
        {
            if(Bytes.Now-pending.Created>86400000){UpdateReceipt(pending.Id,"expired");Store.State.Outbox.Remove(pending);continue;}
            if(!Peers.TryGetValue(pending.Peer,out var recipient))continue;
            if(recipient.LastSeen>Bytes.Now-60000&&Bytes.Now-pending.LastAttempt>30000){pending.LastAttempt=Bytes.Now;await Mesh.SendPrivate(pending.Peer,pending.Text,pending.Id);}
            if(couriers.Length==0)continue;
            if(pending.Envelope==null)
            {
                Prekey? prekey=null;if(Store.State.PrekeyBundles.TryGetValue(pending.Peer,out var bundle)&&bundle.Valid(recipient.SigningKey)&&bundle.Prekeys.Length>0)
                {prekey=bundle.Prekeys.FirstOrDefault(p=>!Store.State.UsedRemotePrekeys.Contains(pending.Peer+":"+p.Id));if(prekey!=null)Store.State.UsedRemotePrekeys.Add(pending.Peer+":"+prekey.Id);}
                var payload=Bytes.Join([1],Announcement.Tlv(0,Bytes.Utf8(pending.Id)),Announcement.Tlv(1,Bytes.Utf8(pending.Text)));pending.Envelope=CourierEnvelope.Seal(payload,Store.State.NoiseKey,recipient.NoiseKey,prekey);
            }
            foreach(var courier in couriers.Where(p=>p.Id!=pending.Peer&&!pending.DepositedTo.Contains(p.Id)))
            {
                if(pending.Envelope.Copies<=1)break;byte given=(byte)(pending.Envelope.Copies/2);var envelope=pending.Envelope with{Copies=given};int links=await Mesh.SendDirected(4,envelope.Encode(),courier.Id);
                if(links>0){pending.Envelope=pending.Envelope with{Copies=(byte)(pending.Envelope.Copies-given)};pending.DepositedTo.Add(courier.Id);}
            }
        }
        Store.Save();
    }
    private void ReceiveCourier(string peer,byte[] bytes)
    {
        var envelope=CourierEnvelope.Decode(bytes);var mine=Crypto.PublicX(Store.State.NoiseKey);
        if(envelope.For(mine))
        {
            byte[] secret=Store.State.NoiseKey;
            if(envelope.PrekeyId is uint keyId){if(!Store.State.LocalPrekeys.TryGetValue(keyId,out var prekey))return;secret=prekey.Secret;}
            var (payload,senderKey)=envelope.Open(secret);if(payload.Length==0||payload[0]!=1)return;
            string sender=Bytes.Hex(Crypto.Hash(senderKey))[..16];if(Store.State.Blocked.Contains(sender))return;var message=NostrEmbedded.PrivateMessage(payload[1..]);
            if(envelope.PrekeyId is uint consumed){var key=Store.State.LocalPrekeys[consumed];if(key.Consumed==null){Store.State.LocalPrekeys[consumed]=key with{Consumed=Bytes.Now};_=PublishPrekeys(true);}}
            if(Store.State.OpenedCourierIds.Add(sender+":"+message.Id))
            {if(Store.State.OpenedCourierIds.Count>10000)Store.State.OpenedCourierIds.Remove(Store.State.OpenedCourierIds.First());Add(new("mesh:"+sender,message.Id,Peers.GetValueOrDefault(sender)?.Nickname??sender,message.Text,Bytes.Now,Peer:sender));}
            Store.Save();_=Mesh.SendTyped(sender,Bytes.Join([3],Bytes.Utf8(message.Id)));return;
        }
        if(!Peers.TryGetValue(peer,out var depositor)||!depositor.Authenticated||!Store.State.Favorites.Contains(peer)||!Store.State.FavoritedBy.Contains(peer))throw new CryptographicException("Courier depositor must be a mutual favorite with an authenticated Noise session");
        if(Store.State.CourierSpool.ContainsKey(envelope.Id))return;
        if(Store.State.CourierSpool.Count>=64||Store.State.CourierSpool.Count(p=>p.Value.Depositor==peer)>=16||Store.State.CourierSpool.Values.Sum(p=>p.Envelope.Ciphertext.Length)+envelope.Ciphertext.Length>1024*1024)throw new InvalidDataException("Courier storage quota reached");
        Store.State.CourierSpool[envelope.Id]=new(envelope,peer);Store.Save();Status?.Invoke("Stored encrypted courier envelope");
    }
    private async Task MaintainOnUi()
    {
        if(maintaining)return;maintaining=true;
        try
        {
            ExpireVoice();await Mesh.MaintainPrivateMedia();await DrainGateway();await PublishPresence();await PublishPrekeys();await DepositOutbox();
            foreach(var (id,carried) in Store.State.CourierSpool.ToArray())
            {
                if(!carried.Envelope.Valid){Store.State.CourierSpool.Remove(id);continue;}if(Bytes.Now-carried.LastTransfer<30000)continue;
                var target=Peers.Values.FirstOrDefault(p=>p.LastSeen>Bytes.Now-60000&&carried.Envelope.For(p.NoiseKey));
                if(target!=null){await Mesh.SendDirected(4,carried.Envelope.Encode(),target.Id);Store.State.CourierSpool[id]=carried with{LastTransfer=Bytes.Now};}
            }
            Store.Save();
        }
        catch(Exception e){Status?.Invoke("Background delivery: "+e.Message);}finally{maintaining=false;}
    }
}

using System.IO;
using System.Security.Cryptography;
using BitChat.Core;

namespace BitChat.Windows;

public sealed record NoiseContact(byte[] Key,string Nickname);

public sealed partial class AppController
{
    private readonly BridgeCourierEngine bridgeCourier;
    private long lastCourierWatchRefresh;private bool courierRefreshing;
    private void ConfigureCourierBridge()
    {
        bridgeCourier.Connected=()=>Relays.Connected>0;
        bridgeCourier.Publish=Relays.Publish;
        bridgeCourier.Subscribe=tags=>Relays.Subscribe("courier-drops",BridgeDrop.Filter(tags,Bytes.Now));
        bridgeCourier.Unsubscribe=()=>Relays.Unsubscribe("courier-drops");
        bridgeCourier.OpenOwn=envelope=>Task.FromResult(OpenCourierEnvelope(envelope));
        bridgeCourier.Deliver=async(envelope,peer)=>
        {
            foreach(var lease in Bluetooth.AuthenticatedFor(peer))
                if(await Mesh.DeliverCourierDirect(envelope,lease,Bluetooth.SendDirect))return true;
            return false;
        };
        bridgeCourier.Status+=s=>Status?.Invoke(s);
        bridgeCourier.SetEnabled(Store.State.Bridge);
    }
    private async Task RefreshCourierBridge(bool force=false)
    {
        if(courierRefreshing)return;courierRefreshing=true;
        try
        {
            bridgeCourier.SetEnabled(Store.State.Bridge);
            if(!force&&Bytes.Now-lastCourierWatchRefresh<60000)return;
            lastCourierWatchRefresh=Bytes.Now;
            var peers=Peers.Values.Where(p=>p.LastSeen>Bytes.Now-60000&&!Store.State.Blocked.Contains(p.Id))
                .OrderByDescending(p=>p.LastSeen).ThenBy(p=>p.Id).Take(16).Select(p=>new BridgeWatchPeer(p.Id,p.NoiseKey)).ToArray();
            await bridgeCourier.Refresh(Crypto.PublicX(Store.State.NoiseKey),peers);
            if(Store.State.Bridge)
                foreach(var route in Bluetooth.DirectRoutes)
                    _=Mesh.ProveDirectLink(route.Peer,route.Epoch,Bluetooth.AuthenticatedFor(route.Peer).Select(l=>l.Epoch).ToArray());
        }
        finally{courierRefreshing=false;}
    }
    private CourierEnvelope SealPending(PendingPrivate pending,byte[] recipient,bool bridge)
    {
        var existing=bridge?pending.BridgeEnvelope:pending.Envelope;
        if(existing!=null)return existing;
        if(!pending.PrekeyAssigned)
        {
            if(pending.Envelope?.PrekeyId is uint legacyId)
            {
                // A 0.1.2 pending message may have consumed a prekey whose
                // public part is no longer in the recipient's latest bundle.
                pending.AssignedPrekey=Store.State.PrekeyBundles.GetValueOrDefault(pending.Peer)?.Prekeys.FirstOrDefault(p=>p.Id==legacyId);
                if(pending.AssignedPrekey==null&&bridge)
                {pending.BridgeEnvelope=pending.Envelope with{Copies=1};Store.Save();return pending.BridgeEnvelope;}
            }
            else if(pending.Envelope==null&&Peers.TryGetValue(pending.Peer,out var known)
                &&known.Capabilities.Length>0&&(known.Capabilities[0]&1)!=0
                &&Store.State.PrekeyBundles.TryGetValue(pending.Peer,out var bundle)&&bundle.Valid(known.SigningKey))
            {
                pending.AssignedPrekey=bundle.Prekeys.FirstOrDefault(p=>!Store.State.UsedRemotePrekeys.Contains(pending.Peer+":"+p.Id));
                if(pending.AssignedPrekey!=null)Store.State.UsedRemotePrekeys.Add(pending.Peer+":"+pending.AssignedPrekey.Id);
            }
            pending.PrekeyAssigned=true;
        }
        byte[] payload=Bytes.Join([1],Announcement.Tlv(0,Bytes.Utf8(pending.Id)),Announcement.Tlv(1,Bytes.Utf8(pending.Text)));
        var envelope=CourierEnvelope.Seal(payload,Store.State.NoiseKey,recipient,pending.AssignedPrekey);
        if(bridge){envelope=envelope with{Copies=1};pending.BridgeEnvelope=envelope;}else pending.Envelope=envelope;
        Store.Save();return envelope;
    }
    private void DepositBridgeDrop(PendingPrivate pending,byte[] recipient)
    {
        if(!Store.State.Bridge)return;
        bridgeCourier.SetEnabled(true);
        if(bridgeCourier.WasPublished(pending.Id,recipient))
        {UpdateReceipt(pending.Id,"carried","mesh:"+pending.Peer);return;}
        _=Observe(async()=>
        {
            if(await bridgeCourier.Deposit(pending.Id,recipient,()=>SealPending(pending,recipient,true))&&!disposed)
                UpdateReceipt(pending.Id,"carried","mesh:"+pending.Peer);
        });
    }
    private void PublishHeldDrops()
    {
        if(!Store.State.Bridge||Relays.Connected==0)return;
        foreach(var (id,held) in Store.State.CourierSpool.Where(p=>p.Value.Envelope.Valid&&Bytes.Now-p.Value.LastBridgePublish>=1800000).ToArray())
            _=Observe(async()=>
            {
                if(await bridgeCourier.PublishHeld(held.Envelope)&&!disposed&&Store.State.CourierSpool.TryGetValue(id,out var current))
                {Store.State.CourierSpool[id]=current with{LastBridgePublish=Bytes.Now};Store.Save();}
            });
    }
    internal bool OpenCourierEnvelope(CourierEnvelope envelope)
    {
        if(!envelope.Valid||!envelope.For(Crypto.PublicX(Store.State.NoiseKey)))return false;
        try
        {
            byte[] secret=Store.State.NoiseKey;LocalPrekey? consumed=null;
            if(envelope.PrekeyId is uint keyId)
            {if(!Store.State.LocalPrekeys.TryGetValue(keyId,out consumed))return false;secret=consumed.Secret;}
            var (payload,senderKey)=envelope.Open(secret);
            if(payload.Length==0||payload[0]!=1)return true;
            (string Id,string Text) message;
            try{message=NostrEmbedded.PrivateMessage(payload[1..]);}catch(Exception e)when(e is InvalidDataException or KeyNotFoundException or ArgumentException){return true;}
            if(string.IsNullOrEmpty(message.Id))return true;
            string sender=Bytes.Hex(Crypto.Hash(senderKey))[..16],dedup=sender+":"+message.Id;
            if(Store.State.Blocked.Contains(sender))return true;
            if(Store.State.OpenedCourierIds.Contains(dedup))return true;
            string conversation="mesh:"+sender;
            var stored=new StoredMessage(conversation,message.Id,Peers.GetValueOrDefault(sender)?.Nickname??Store.State.NoiseContacts.GetValueOrDefault(sender)?.Nickname??sender[..8],message.Text,Bytes.Now,Peer:sender);
            bool hasMessage=Store.State.Messages.Any(m=>m.Conversation==conversation&&m.Id==message.Id);
            var previousContact=Store.State.NoiseContacts.GetValueOrDefault(sender);
            Store.State.NoiseContacts[sender]=new(senderKey,stored.Sender);
            Store.State.OpenedCourierIds.Add(dedup);
            bool priorFavorite=Store.State.FavoritedBy.Contains(sender);
            string? priorMeshKey=Store.State.MeshNostrKeys.GetValueOrDefault(sender);
            var priorNostrPeers=new Dictionary<string,string>(Store.State.NostrPeers);
            bool control=!hasMessage&&HandleFavorite(sender,message.Text,false);
            if(!hasMessage&&!control)Store.State.Messages.Add(stored);
            if(envelope.PrekeyId is uint consumedId&&consumed!.Consumed==null)Store.State.LocalPrekeys[consumedId]=consumed with{Consumed=Bytes.Now};
            try{Store.Save();}
            catch
            {
                Store.State.OpenedCourierIds.Remove(dedup);if(!hasMessage)Store.State.Messages.Remove(stored);
                if(priorFavorite)Store.State.FavoritedBy.Add(sender);else Store.State.FavoritedBy.Remove(sender);
                if(priorMeshKey==null)Store.State.MeshNostrKeys.Remove(sender);else Store.State.MeshNostrKeys[sender]=priorMeshKey;
                Store.State.NostrPeers.Clear();foreach(var pair in priorNostrPeers)Store.State.NostrPeers[pair.Key]=pair.Value;
                if(previousContact==null)Store.State.NoiseContacts.Remove(sender);else Store.State.NoiseContacts[sender]=previousContact;
                if(envelope.PrekeyId is uint restore)Store.State.LocalPrekeys[restore]=consumed!;
                throw;
            }
            if(Store.State.OpenedCourierIds.Count>10000){Store.State.OpenedCourierIds.Remove(Store.State.OpenedCourierIds.First());Store.Save();}
            if(!hasMessage)
            {
                if(!control)Message?.Invoke(stored);
                _=Mesh.SendTyped(sender,Bytes.Join([3],Bytes.Utf8(message.Id)));
            }
            if(consumed?.Consumed==null&&envelope.PrekeyId!=null)_=Observe(()=>PublishPrekeys(true));
            PeersChanged?.Invoke();return true;
        }
        catch(Exception e)when(e is CryptographicException or InvalidDataException or IOException or ArgumentException or KeyNotFoundException)
        {Status?.Invoke("Courier could not be opened: "+e.Message);return false;}
    }
}

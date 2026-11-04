using System.IO;
using BitChat.Core;
namespace BitChat.Windows;
public sealed partial class AppController
{
    private readonly HashSet<string> carrierLearned=[],carrierPublished=[],carrierDownlinked=[],radioIds=[];
    private readonly Dictionary<string,string> bridgeAliases=[];
    private readonly Dictionary<string,Queue<long>> uplinkRate=[];
    private readonly Queue<long> downlinkRate=[];
    private readonly Queue<(string Peer,NostrCarrier Carrier)> pendingUplinks=[];
    private readonly Queue<NostrCarrier> pendingDownlinks=[];
    private readonly Dictionary<string,long> locationPresence=[];
    private long lastPresence;
    public event Action? TimelineChanged;
    private static void RememberId(HashSet<string> set,string id,int cap=512){if(set.Count>=cap)set.Remove(set.First());set.Add(id);}
    private static string RadioId(string peer,ulong time,string content)=>Bytes.Hex(Crypto.Hash(Bytes.Utf8(peer.ToLowerInvariant()+"|"+time+"|"+content.Trim())))[..32];
    public void ConfigureGateway()
    {
        byte caps=0x79;if(Store.State.Gateway)caps|=4;if(Store.State.Bridge&&Geohash.Valid(Store.State.Cell))caps|=128;
        Mesh.Capabilities=[caps,1];Mesh.BridgeCell=(caps&128)!=0?Store.State.Cell:null;_=Mesh.Announce();
    }
    public async Task RefreshGateway(){ConfigureGateway();await SubscribeBridge();Store.Save();}
    private async Task SubscribeBridge()
    {
        if(!Store.State.Bridge||!Geohash.Valid(Store.State.Cell)){await Relays.Unsubscribe("bridge");return;}
        string cell=Store.State.Cell;Store.State.LocationKey("bridge|"+cell);Store.Save();
        await Relays.Subscribe("bridge",new Dictionary<string,object>{{"kinds",new[]{20000,20001}},{"#r",new[]{cell}},{"since",DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeSeconds()},{"limit",100}});
    }
    private void RadioObserved(MeshPacket packet)
    {
        if(packet.Type!=2)return;string stable=RadioId(Bytes.Hex(packet.Sender),packet.Timestamp,Bytes.Text(packet.Payload));RememberId(radioIds,stable,2000);
        var duplicates=bridgeAliases.Where(p=>p.Value==stable).Select(p=>p.Key).ToHashSet();if(duplicates.Count>0){Store.State.Messages.RemoveAll(m=>duplicates.Contains(m.Id));foreach(var id in duplicates)bridgeAliases.Remove(id);Store.Save();TimelineChanged?.Invoke();}
    }
    private async Task MirrorOwnMesh(string text,long timestamp)
    {
        if(!Store.State.Bridge||!Geohash.Valid(Store.State.Cell))return;
        string cell=Store.State.Cell;var key=Store.State.LocationKey("bridge|"+cell);var stable=RadioId(Mesh.Id,(ulong)timestamp,text);
        var e=NostrEvent.Create(key,20000,text,[["r",cell],["n",Store.State.Nickname],["m",stable,Mesh.Id,timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)]]);
        if(Relays.Connected>0){RememberId(carrierPublished,e.id);await Relays.Publish(e);}else await UplinkViaMesh(e,cell,true);
    }
    private async Task<bool> UplinkViaMesh(NostrEvent e,string cell,bool bridge)
    {
        var gateway=Peers.Values.FirstOrDefault(p=>p.LastSeen>Bytes.Now-60000&&p.Capabilities.Length>0&&(p.Capabilities[0]&(bridge?128:4))!=0);
        if(gateway==null)return false;var carrier=new NostrCarrier((byte)(bridge?3:1),cell,e);if(!carrier.Valid())return false;return await Mesh.SendDirected(0x28,carrier.Encode(),gateway.Id)>0;
    }
    private void GatewayRelayEvent(NostrEvent e)
    {
        if(e.kind is not(20000 or 20001)||carrierLearned.Contains(e.id)||carrierPublished.Contains(e.id)||carrierDownlinked.Contains(e.id))return;
        bool bridge=e.tags.Any(t=>t.Length==2&&t[0]=="r");string? cell=e.tags.FirstOrDefault(t=>t.Length==2&&t[0]==(bridge?"r":"g"))?[1];
        if(cell!=Store.State.Cell||!(bridge?Store.State.Bridge:Store.State.Gateway)||e.kind==20001)return;
        var carrier=new NostrCarrier((byte)(bridge?4:2),cell!,e);if(!carrier.Valid())return;if(pendingDownlinks.Count>=30)pendingDownlinks.Dequeue();pendingDownlinks.Enqueue(carrier);_=DrainGateway();
    }
    private async Task ReceiveCarrier(string peer,byte[] bytes)
    {
        var carrier=NostrCarrier.Decode(bytes);var e=carrier.Event;bool bridge=carrier.Direction>=3;
        if(carrier.Direction is 2 or 4)
        {if(carrier.Cell!=Store.State.Cell)return;RememberId(carrierLearned,e.id);OnNostr(e,true);return;}
        if(!(bridge?Store.State.Bridge:Store.State.Gateway)||carrier.Cell!=Store.State.Cell||carrierLearned.Contains(e.id)||carrierPublished.Contains(e.id)||pendingUplinks.Any(p=>p.Carrier.Event.id==e.id))return;
        if(!uplinkRate.TryGetValue(peer,out var rate))uplinkRate[peer]=rate=new();while(rate.TryPeek(out long old)&&Bytes.Now-old>=60000)rate.Dequeue();if(rate.Count>=10)return;rate.Enqueue(Bytes.Now);
        if(pendingUplinks.Count>=20||pendingUplinks.Count(p=>p.Peer==peer)>=5)return;pendingUplinks.Enqueue((peer,carrier));OnNostr(e,true);await DrainGateway();
    }
    private bool drainingGateway;
    private async Task DrainGateway()
    {
        if(drainingGateway)return;drainingGateway=true;
        try
        {
            while(Relays.Connected>0&&pendingUplinks.Count>0)
            {var uplink=pendingUplinks.Dequeue();RememberId(carrierPublished,uplink.Carrier.Event.id);await Relays.Publish(uplink.Carrier.Event);}
            while(downlinkRate.TryPeek(out long old)&&Bytes.Now-old>=60000)downlinkRate.Dequeue();
            while(pendingDownlinks.Count>0&&downlinkRate.Count<30)
            {var carrier=pendingDownlinks.Dequeue();if(carrierLearned.Contains(carrier.Event.id)||carrierDownlinked.Contains(carrier.Event.id))continue;RememberId(carrierDownlinked,carrier.Event.id);downlinkRate.Enqueue(Bytes.Now);await Mesh.SendBroadcast(0x28,carrier.Encode());}
        }
        catch(Exception e){Status?.Invoke("Gateway: "+e.Message);}finally{drainingGateway=false;}
    }
    private async Task PublishPresence()
    {
        if(Bytes.Now-lastPresence<60000||!Geohash.Valid(Store.State.Cell))return;lastPresence=Bytes.Now;
        string cell=Store.State.Cell;await Relays.Publish(NostrEvent.Create(Store.State.LocationKey(cell),20001,"",[["g",cell]]));
        if(Store.State.Bridge)await Relays.Publish(NostrEvent.Create(Store.State.LocationKey("bridge|"+cell),20001,"",[["r",cell]]));
    }
    public int NearbyPresence=>locationPresence.Count(p=>Bytes.Now-p.Value<180000);
}

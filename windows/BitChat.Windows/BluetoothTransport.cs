using System.IO;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using BitChat.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BitChat.Windows;

public sealed class BluetoothTransport:IDisposable
{
    public static readonly Guid ServiceId=new("F47B5E2D-4A9E-4C5A-9B3F-8E1D2C3A4B5C");
    public static readonly Guid CharacteristicId=new("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");
    private sealed class Link(BluetoothLEDevice device,GattDeviceService service,GattCharacteristic characteristic,Guid epoch)
    {public Guid Epoch=epoch;public ulong Address;public int Retired;public bool WasConnected;public long DisconnectedAt;public BluetoothLEDevice Device=device;public GattDeviceService Service=service;public GattCharacteristic Characteristic=characteristic;public PacketStream Stream=new();public SemaphoreSlim Gate=new(1);}
    private readonly ConcurrentDictionary<ulong,Link> links=new();
    private readonly ConcurrentDictionary<ulong,byte> connecting=new();
    private readonly ConcurrentDictionary<ulong,(int Failures,long RetryAt)> connectionBackoff=new();
    private sealed class Incoming(Guid epoch){public Guid Epoch=epoch;public PacketStream Stream=new();}
    private readonly ConcurrentDictionary<string,Incoming> inboundStreams=new();
    private readonly ConcurrentDictionary<string,string> directPeers=new();
    private readonly AuthenticatedLinks authenticatedLinks=new();
    private readonly ConcurrentDictionary<string,byte> peripheralLinks=new();
    public Guid LinkEpoch(string ingress)=>authenticatedLinks.Epoch(ingress)??Guid.Empty;
    public AuthenticatedLink[] AuthenticatedFor(string peer)=>authenticatedLinks.For(peer);
    public (string Peer,Guid Epoch)[] DirectRoutes=>directPeers.Select(p=>(Peer:p.Value,Epoch:authenticatedLinks.Epoch(p.Key))).Where(p=>p.Epoch!=null).Select(p=>(p.Peer,p.Epoch!.Value)).ToArray();
    public void RememberAuthenticated(string peer,string ingress,Guid epoch,Guid generation)=>authenticatedLinks.Authenticate(ingress,epoch,peer,generation);
    private readonly ConcurrentDictionary<string,SemaphoreSlim> clientSendGates=new();
    private readonly ConcurrentDictionary<string,SemaphoreSlim> writeReceiveGates=new();
    public byte[][] DirectPeers=>directPeers.Values.Distinct().Select(Convert.FromHexString).ToArray();
    public void RememberDirectPeer(string ingress,string peer){if(authenticatedLinks.Epoch(ingress)!=null)directPeers[ingress]=peer;}
    private BluetoothLEAdvertisementWatcher? watcher;
    private GattServiceProvider? provider;
    private GattLocalCharacteristic? local;
    private readonly CancellationTokenSource stop=new();
    public event Action<string,Guid,byte[]>? Packet;
    public event Action<string>? Status;
    public event Action? LinkReady;
    public event Action<MeshPacket,long,long>? SendProgress;
    public event Action<MeshPacket>? SendFinished;
    public int Connections=>links.Count+(local?.SubscribedClients.Count??0);
    private static IBuffer Buffer(byte[] bytes){using var w=new DataWriter();w.WriteBytes(bytes);return w.DetachBuffer();}
    private static byte[] BytesFrom(IBuffer data){using var r=DataReader.FromBuffer(data);var b=new byte[r.UnconsumedBufferLength];r.ReadBytes(b);return b;}
    public async Task Start()
    {
        var adapter=await BluetoothAdapter.GetDefaultAsync();if(adapter==null){Status?.Invoke("No Bluetooth LE adapter detected. Nostr remains available.");return;}
        if(adapter.IsPeripheralRoleSupported)
        {
            var result=await GattServiceProvider.CreateAsync(ServiceId);
            if(result.Error==BluetoothError.Success)
            {
                provider=result.ServiceProvider;
                var parameters=new GattLocalCharacteristicParameters{CharacteristicProperties=GattCharacteristicProperties.Write|GattCharacteristicProperties.WriteWithoutResponse|GattCharacteristicProperties.Notify,WriteProtectionLevel=GattProtectionLevel.Plain,ReadProtectionLevel=GattProtectionLevel.Plain};
                var created=await provider.Service.CreateCharacteristicAsync(CharacteristicId,parameters);
                if(created.Error==BluetoothError.Success)
                {
                    local=created.Characteristic;local.WriteRequested+=OnWrite;local.SubscribedClientsChanged+=(_,_)=>
                    {
                        var current=local.SubscribedClients.Select(c=>c.Session.DeviceId.Id).ToHashSet();
                        foreach(string old in peripheralLinks.Keys.Where(k=>!current.Contains(k))){peripheralLinks.TryRemove(old,out _);inboundStreams.TryRemove(old,out _);directPeers.TryRemove(old,out _);authenticatedLinks.Close(old);}
                        foreach(var client in local.SubscribedClients)if(peripheralLinks.TryAdd(client.Session.DeviceId.Id,0)){try{client.Session.MaintainConnection=true;}catch{}authenticatedLinks.Open(client.Session.DeviceId.Id);LinkReady?.Invoke();}
                    };
                    provider.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsConnectable=true,IsDiscoverable=true});
                }
                else Status?.Invoke("GATT characteristic unavailable: "+created.Error);
            }
            else Status?.Invoke("GATT peripheral unavailable: "+result.Error);
        }
        else Status?.Invoke("Adapter supports BLE central only; nearby phones can still be discovered.");
        if(!adapter.IsCentralRoleSupported){Status?.Invoke("Adapter does not support BLE central role");return;}
        watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceId);
        watcher.Received+=OnAdvertisement;watcher.Stopped+=(sender,e)=>{Status?.Invoke("Bluetooth scan stopped: "+e.Error);_=RestartScan();};watcher.Start();Status?.Invoke("Bluetooth mesh discovery active");
    }
    private async void OnAdvertisement(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs e)
    {
        if(stop.IsCancellationRequested||links.Count>=16||connecting.Count>=4||links.ContainsKey(e.BluetoothAddress)||connectionBackoff.TryGetValue(e.BluetoothAddress,out var delay)&&delay.RetryAt>Bytes.Now||!connecting.TryAdd(e.BluetoothAddress,0))return;
        BluetoothLEDevice? device=null;GattDeviceService? service=null;Guid? openedEpoch=null;bool succeeded=false;
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            device=await BluetoothLEDevice.FromBluetoothAddressAsync(e.BluetoothAddress).AsTask(deadline.Token);if(device==null)return;
            var services=await device.GetGattServicesForUuidAsync(ServiceId,BluetoothCacheMode.Uncached).AsTask(deadline.Token);
            if(services.Status!=GattCommunicationStatus.Success||services.Services.Count==0)return;service=services.Services[0];
            var chars=await service.GetCharacteristicsForUuidAsync(CharacteristicId,BluetoothCacheMode.Uncached).AsTask(deadline.Token);
            if(chars.Status!=GattCommunicationStatus.Success||chars.Characteristics.Count==0)return;
            var characteristic=chars.Characteristics[0];openedEpoch=authenticatedLinks.Open(e.BluetoothAddress.ToString("x"));var link=new Link(device,service,characteristic,openedEpoch.Value){Address=e.BluetoothAddress};
            service.Session.MaintainConnection=true;
            characteristic.ValueChanged+=(_,ev)=>Receive(link.Stream,BytesFrom(ev.CharacteristicValue),e.BluetoothAddress.ToString("x"),link.Epoch);
            var subscribed=await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(deadline.Token);
            if(subscribed!=GattCommunicationStatus.Success)throw new IOException("Notification subscription failed: "+subscribed);
            if(stop.IsCancellationRequested)return;
            links[e.BluetoothAddress]=link;connectionBackoff.TryRemove(e.BluetoothAddress,out _);link.WasConnected=device.ConnectionStatus==BluetoothConnectionStatus.Connected;
            device.ConnectionStatusChanged+=(d,_)=>
            {
                if(d.ConnectionStatus==BluetoothConnectionStatus.Connected){link.WasConnected=true;Interlocked.Exchange(ref link.DisconnectedAt,0);}
                else if(link.WasConnected){Interlocked.CompareExchange(ref link.DisconnectedAt,Bytes.Now,0);_=CheckDisconnect(link);}
            };
            succeeded=true;device=null;service=null;Status?.Invoke("Bluetooth peer connected");LinkReady?.Invoke();
        }
        catch(Exception ex){Status?.Invoke("Bluetooth connection: "+ex.Message);}
        finally{if(!succeeded&&!stop.IsCancellationRequested)connectionBackoff.AddOrUpdate(e.BluetoothAddress,_=>(1,Bytes.Now+2000),(_,old)=>(Math.Min(5,old.Failures+1),Bytes.Now+Math.Min(30000,2000L<<Math.Min(4,old.Failures))));if(device!=null&&openedEpoch!=null)authenticatedLinks.Close(e.BluetoothAddress.ToString("x"),openedEpoch);service?.Dispose();device?.Dispose();connecting.TryRemove(e.BluetoothAddress,out _);}
    }
    private async void OnWrite(GattLocalCharacteristic sender,GattWriteRequestedEventArgs args)
    {
        string key=args.Session.DeviceId.Id;Guid epoch=authenticatedLinks.EnsureOpen(key);using var deferral=args.GetDeferral();
        var receiveGate=writeReceiveGates.GetOrAdd(key,_=>new(1));await receiveGate.WaitAsync();
        try
        {
            var request=await args.GetRequestAsync();if(request==null)return;
            var state=inboundStreams.AddOrUpdate(key,_=>new(epoch),(_,old)=>old.Epoch==epoch?old:new(epoch));Receive(state.Stream,BytesFrom(request.Value),key,epoch);
            if(request.Option==GattWriteOption.WriteWithResponse)request.Respond();
        }
        catch(Exception e){Status?.Invoke("Bluetooth write: "+e.Message);}
        finally{receiveGate.Release();}
    }
    private void Receive(PacketStream stream,byte[] bytes,string ingress,Guid epoch)
    {
        try{if(authenticatedLinks.Epoch(ingress)!=epoch)return;lock(stream){foreach(var frame in stream.Add(bytes))Packet?.Invoke(ingress,epoch,frame);}}catch(Exception e){Status?.Invoke("Rejected BLE frame: "+e.Message);}
    }
    public Task<int> Send(MeshPacket packet)=>Send(packet,CancellationToken.None);
    public async Task<int> Send(MeshPacket packet,CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // An authenticated direct route avoids flooding large private files
            // onto unrelated radios. Fall back to mesh only if every route fails.
            if(packet.Type is 0x11 or 0x22&&packet.Recipient is {} recipient)
            {
                foreach(var route in authenticatedLinks.For(Bytes.Hex(recipient)))
                {cancellation.ThrowIfCancellationRequested();if(await SendDirect(packet,route,cancellation)>0)return 1;}
            }
            var tasks=links.Values.Select(link=>SendCentral(link,packet,cancellation:cancellation)).ToList();
            if(local!=null)tasks.AddRange(local.SubscribedClients.ToArray().Select(client=>SendPeripheral(client,packet,cancellation:cancellation)));
            var results=await Task.WhenAll(tasks);cancellation.ThrowIfCancellationRequested();return results.Count(accepted=>accepted);
        }
        finally{SendFinished?.Invoke(packet);}
    }
    public Task<int> SendDirect(MeshPacket packet,AuthenticatedLink lease)=>SendDirect(packet,lease,CancellationToken.None);
    private async Task<int> SendDirect(MeshPacket packet,AuthenticatedLink lease,CancellationToken cancellation)
    {
        bool Valid()=>authenticatedLinks.Current(lease);
        if(!Valid())return 0;
        foreach(var pair in links)
            if(pair.Key.ToString("x")==lease.Ingress&&pair.Value.Epoch==lease.Epoch)return await SendCentral(pair.Value,packet,Valid,cancellation)?1:0;
        var client=local?.SubscribedClients.FirstOrDefault(c=>c.Session.DeviceId.Id==lease.Ingress);
        return client!=null&&await SendPeripheral(client,packet,Valid,cancellation)?1:0;
    }
    private async Task<bool> SendCentral(Link link,MeshPacket packet,Func<bool>? stillValid=null,CancellationToken cancellation=default)
    {
        try
        {
            if(link.Retired!=0)return false;
            int mtu=Math.Clamp((int)link.Service.Session.MaxPduSize-3,20,512);
            // iOS accumulates central writes by ATT offset, not by appending
            // independent offset-zero writes. Use complete protocol frames;
            // low-MTU links need an actual prepare/execute long write.
            var frames=FragmentAssembler.Split(packet,BleFraming.CentralFrameLimit(mtu)).Select(part=>(Part:part,Bytes:part.Encode(part.Type is 0x10 or 0x11))).ToArray();long total=frames.Sum(frame=>(long)frame.Bytes.Length),written=0;
            foreach(var frame in frames)
            {
                cancellation.ThrowIfCancellationRequested();if(link.Retired!=0||stillValid!=null&&!stillValid())return false;
                await link.Gate.WaitAsync(cancellation);
                try
                {
                cancellation.ThrowIfCancellationRequested();if(link.Retired!=0||stillValid!=null&&!stillValid())return false;
                var encoded=frame.Bytes;GattCommunicationStatus status;
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                if(encoded.Length<=mtu)
                    status=await link.Characteristic.WriteValueAsync(Buffer(encoded),GattWriteOption.WriteWithResponse).AsTask(deadline.Token);
                else
                {
                    var transaction=new GattReliableWriteTransaction();transaction.WriteValue(link.Characteristic,Buffer(encoded));
                    status=await transaction.CommitAsync().AsTask(deadline.Token);
                }
                }
                catch(OperationCanceledException) when(!stop.IsCancellationRequested){throw new TimeoutException("Bluetooth frame timed out");}
                if(status!=GattCommunicationStatus.Success)throw new IOException(status.ToString());
                written+=encoded.Length;SendProgress?.Invoke(packet,written,total);
                }
                finally{link.Gate.Release();}
                await Task.Yield();
            }
            cancellation.ThrowIfCancellationRequested();return stillValid==null||stillValid();
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(Exception e){Retire(link,"BLE send failed; reconnecting: "+e.Message);return false;}
    }
    private async Task<bool> SendPeripheral(GattSubscribedClient client,MeshPacket packet,Func<bool>? stillValid=null,CancellationToken cancellation=default)
    {
        string clientKey=client.Session.DeviceId.Id;Guid epoch=authenticatedLinks.EnsureOpen(clientKey);
        bool Valid()=>!stop.IsCancellationRequested&&authenticatedLinks.Epoch(clientKey)==epoch&&(stillValid==null||stillValid());
        var clientGate=clientSendGates.GetOrAdd(clientKey,_=>new(1));
        try
        {
            int mtu=Math.Min(512,Math.Max(20,(int)client.Session.MaxPduSize-3));
            var frames=FragmentAssembler.Split(packet,512).Select(part=>part.Encode(part.Type is 0x10 or 0x11)).ToArray();long total=frames.Sum(frame=>(long)frame.Length),written=0;
            foreach(var encoded in frames)
            {
                cancellation.ThrowIfCancellationRequested();await clientGate.WaitAsync(cancellation);
                try
                {
                cancellation.ThrowIfCancellationRequested();
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
                foreach(var bytes in BleFraming.Chunks(encoded,mtu))
                {
                    if(!Valid())return false;
                    GattClientNotificationResult result;
                    try{result=await local!.NotifyValueAsync(Buffer(bytes),client).AsTask(deadline.Token);}
                    catch(OperationCanceledException) when(!stop.IsCancellationRequested){throw new TimeoutException("Bluetooth notification frame timed out");}
                    if(result.Status!=GattCommunicationStatus.Success)throw new IOException(result.Status.ToString());
                    written+=bytes.Length;SendProgress?.Invoke(packet,written,total);
                }
                }
                finally{clientGate.Release();}
                await Task.Yield();
            }
            cancellation.ThrowIfCancellationRequested();return Valid();
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(Exception e){authenticatedLinks.Close(clientKey,epoch);directPeers.TryRemove(clientKey,out _);Status?.Invoke("BLE notify failed; route expired: "+e.Message);return false;}
    }
    private async Task CheckDisconnect(Link link)
    {
        try{await Task.Delay(5000,stop.Token);if(link.Retired==0&&Interlocked.Read(ref link.DisconnectedAt)!=0&&link.Device.ConnectionStatus==BluetoothConnectionStatus.Disconnected)Retire(link,"Bluetooth peer disconnected; discovery will reconnect");}
        catch(OperationCanceledException){}catch(ObjectDisposedException){}
    }
    private void Retire(Link link,string reason)
    {
        if(Interlocked.Exchange(ref link.Retired,1)!=0)return;
        string ingress=link.Address.ToString("x");
        if(((ICollection<KeyValuePair<ulong,Link>>)links).Remove(new(link.Address,link)))directPeers.TryRemove(ingress,out _);
        authenticatedLinks.Close(ingress,link.Epoch);
        connectionBackoff[link.Address]=(1,Bytes.Now+2000);
        try{link.Service.Session.MaintainConnection=false;link.Service.Dispose();link.Device.Dispose();}catch{}
        Status?.Invoke(reason);
    }
    public void Maintain()
    {
        if(stop.IsCancellationRequested)return;
        foreach(var item in clientSendGates)if(!peripheralLinks.ContainsKey(item.Key)&&item.Value.CurrentCount==1)clientSendGates.TryRemove(item.Key,out _);
        if(connectionBackoff.Count>1024)foreach(var address in connectionBackoff.OrderBy(item=>item.Value.RetryAt).Take(connectionBackoff.Count-512).Select(item=>item.Key))connectionBackoff.TryRemove(address,out _);
        foreach(var link in links.Values)
            try{if(link.WasConnected&&link.Device.ConnectionStatus==BluetoothConnectionStatus.Disconnected&&Interlocked.Read(ref link.DisconnectedAt) is long since&&since!=0&&Bytes.Now-since>=5000)Retire(link,"Bluetooth link expired; reconnecting");}
            catch(ObjectDisposedException){}
        if(watcher?.Status is BluetoothLEAdvertisementWatcherStatus.Stopped or BluetoothLEAdvertisementWatcherStatus.Aborted)
            try{watcher.Start();}catch(Exception e){Status?.Invoke("Bluetooth discovery retry: "+e.Message);}
    }
    private async Task RestartScan()
    {
        try{await Task.Delay(3000,stop.Token);if(!stop.IsCancellationRequested&&watcher?.Status is BluetoothLEAdvertisementWatcherStatus.Stopped or BluetoothLEAdvertisementWatcherStatus.Aborted)watcher.Start();}
        catch(OperationCanceledException){}catch(Exception e){Status?.Invoke("Bluetooth scan restart: "+e.Message);}
    }
    public void Dispose(){stop.Cancel();watcher?.Stop();provider?.StopAdvertising();foreach(var link in links.Values){link.Service.Dispose();link.Device.Dispose();}links.Clear();foreach(string ingress in directPeers.Keys)authenticatedLinks.Close(ingress);directPeers.Clear();}
}

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
    private sealed class Link(BluetoothLEDevice device,GattDeviceService service,GattCharacteristic characteristic)
    {public BluetoothLEDevice Device=device;public GattDeviceService Service=service;public GattCharacteristic Characteristic=characteristic;public PacketStream Stream=new();public SemaphoreSlim Gate=new(1);}
    private readonly ConcurrentDictionary<ulong,Link> links=new();
    private readonly ConcurrentDictionary<ulong,byte> connecting=new();
    private readonly ConcurrentDictionary<string,PacketStream> inboundStreams=new();
    private readonly ConcurrentDictionary<string,string> directPeers=new();
    private readonly ConcurrentDictionary<string,SemaphoreSlim> clientSendGates=new();
    public byte[][] DirectPeers=>directPeers.Values.Distinct().Select(Convert.FromHexString).ToArray();
    public void RememberDirectPeer(string ingress,string peer)=>directPeers[ingress]=peer;
    private BluetoothLEAdvertisementWatcher? watcher;
    private GattServiceProvider? provider;
    private GattLocalCharacteristic? local;
    private readonly CancellationTokenSource stop=new();
    public event Action<string,byte[]>? Packet;
    public event Action<string>? Status;
    public event Action? LinkReady;
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
                    local=created.Characteristic;local.WriteRequested+=OnWrite;local.SubscribedClientsChanged+=(_,_)=>LinkReady?.Invoke();
                    provider.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsConnectable=true,IsDiscoverable=true});
                }
                else Status?.Invoke("GATT characteristic unavailable: "+created.Error);
            }
            else Status?.Invoke("GATT peripheral unavailable: "+result.Error);
        }
        else Status?.Invoke("Adapter supports BLE central only; nearby phones can still be discovered.");
        if(!adapter.IsCentralRoleSupported){Status?.Invoke("Adapter does not support BLE central role");return;}
        watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceId);
        watcher.Received+=OnAdvertisement;watcher.Stopped+=(_,e)=>Status?.Invoke("Bluetooth scan stopped: "+e.Error);watcher.Start();Status?.Invoke("Bluetooth mesh discovery active");
    }
    private async void OnAdvertisement(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs e)
    {
        if(stop.IsCancellationRequested||links.ContainsKey(e.BluetoothAddress)||!connecting.TryAdd(e.BluetoothAddress,0))return;
        BluetoothLEDevice? device=null;GattDeviceService? service=null;
        try
        {
            device=await BluetoothLEDevice.FromBluetoothAddressAsync(e.BluetoothAddress);if(device==null)return;
            var services=await device.GetGattServicesForUuidAsync(ServiceId,BluetoothCacheMode.Uncached);
            if(services.Status!=GattCommunicationStatus.Success||services.Services.Count==0)return;service=services.Services[0];
            var chars=await service.GetCharacteristicsForUuidAsync(CharacteristicId,BluetoothCacheMode.Uncached);
            if(chars.Status!=GattCommunicationStatus.Success||chars.Characteristics.Count==0)return;
            var characteristic=chars.Characteristics[0];var link=new Link(device,service,characteristic);
            characteristic.ValueChanged+=(_,ev)=>Receive(link.Stream,BytesFrom(ev.CharacteristicValue),e.BluetoothAddress.ToString("x"));
            var subscribed=await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if(subscribed!=GattCommunicationStatus.Success)throw new IOException("Notification subscription failed: "+subscribed);
            if(stop.IsCancellationRequested)return;
            links[e.BluetoothAddress]=link;
            device.ConnectionStatusChanged+=(d,_)=>{if(d.ConnectionStatus==BluetoothConnectionStatus.Disconnected&&links.TryRemove(e.BluetoothAddress,out var old)){directPeers.TryRemove(e.BluetoothAddress.ToString("x"),out var removedDirectPeer);old.Service.Dispose();old.Device.Dispose();Status?.Invoke("Bluetooth peer disconnected");}};
            device=null;service=null;Status?.Invoke("Bluetooth peer connected");LinkReady?.Invoke();
        }
        catch(Exception ex){Status?.Invoke("Bluetooth connection: "+ex.Message);}
        finally{service?.Dispose();device?.Dispose();connecting.TryRemove(e.BluetoothAddress,out _);}
    }
    private async void OnWrite(GattLocalCharacteristic sender,GattWriteRequestedEventArgs args)
    {
        using var deferral=args.GetDeferral();
        try
        {
            var request=await args.GetRequestAsync();if(request==null)return;
            var key=args.Session.DeviceId.Id;var stream=inboundStreams.GetOrAdd(key,_=>new());Receive(stream,BytesFrom(request.Value),key);
            if(request.Option==GattWriteOption.WriteWithResponse)request.Respond();
        }
        catch(Exception e){Status?.Invoke("Bluetooth write: "+e.Message);}
    }
    private void Receive(PacketStream stream,byte[] bytes,string ingress)
    {
        try{lock(stream){foreach(var frame in stream.Add(bytes))Packet?.Invoke(ingress,frame);}}catch(Exception e){Status?.Invoke("Rejected BLE frame: "+e.Message);}
    }
    public async Task<int> Send(MeshPacket packet)
    {
        int successes=0;
        foreach(var link in links.Values)
        {
            await link.Gate.WaitAsync(stop.Token);
            try
            {
                // Fragment each wire frame to the GATT value limit rather than
                // splitting a write request into an ambiguous byte stream.
                int mtu=Math.Min(512,Math.Max(64,(int)link.Service.Session.MaxPduSize-3));
                foreach(var part in FragmentAssembler.Split(packet,mtu))
                {
                    var status=await link.Characteristic.WriteValueAsync(Buffer(part.Encode(part.Type is 0x10 or 0x11)),GattWriteOption.WriteWithResponse);
                    if(status!=GattCommunicationStatus.Success)throw new IOException(status.ToString());
                    if(part.Type==0x20)await Task.Delay(12,stop.Token);
                }
                successes++;
            }
            catch(Exception e){Status?.Invoke("BLE send: "+e.Message);}
            finally{link.Gate.Release();}
        }
        if(local!=null)
        {
            foreach(var client in local.SubscribedClients.ToArray())
            {
                try
                {
                    int mtu=Math.Min(512,Math.Max(20,(int)client.Session.MaxPduSize-3));var clientGate=clientSendGates.GetOrAdd(client.Session.DeviceId.Id,_=>new(1));await clientGate.WaitAsync(stop.Token);
                    try{foreach(var part in FragmentAssembler.Split(packet,512))
                    {var encoded=part.Encode(part.Type is 0x10 or 0x11);for(int offset=0;offset<encoded.Length;offset+=mtu)await local.NotifyValueAsync(Buffer(encoded.Skip(offset).Take(mtu).ToArray()),client);if(part.Type==0x20)await Task.Delay(12,stop.Token);}successes++;}finally{clientGate.Release();}
                }
                catch(Exception e){Status?.Invoke("BLE notify: "+e.Message);}
            }
        }
        return successes;
    }
    public void Dispose(){stop.Cancel();watcher?.Stop();provider?.StopAdvertising();foreach(var link in links.Values){link.Service.Dispose();link.Device.Dispose();}links.Clear();}
}

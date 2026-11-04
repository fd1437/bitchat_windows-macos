using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BitChat.Core;

public sealed class RelayClient: IAsyncDisposable
{
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<string,(ClientWebSocket Socket,SemaphoreSlim SendLock)> sockets=new();
    private readonly ConcurrentDictionary<string,object> subscriptions=new();
    private readonly ConcurrentDictionary<string,TaskCompletionSource<bool>> acknowledgements=new();
    private readonly List<Task> workers=[];
    public event Action<NostrEvent>? Received;
    public event Action<string>? Status;
    public System.Net.IWebProxy? Proxy{get;set;}
    public int Connected=>sockets.Count;
    public void Start(IEnumerable<string> urls)
    {
        foreach(var url in urls.Distinct())
        {if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="wss")throw new ArgumentException("Relay URL must use wss://");workers.Add(Task.Run(()=>Run(uri,stop.Token)));}
    }
    private async Task Run(Uri uri,CancellationToken cancellation)
    {
        int attempt=0;
        while(!cancellation.IsCancellationRequested)
        {
            using var socket=new ClientWebSocket();var sendLock=new SemaphoreSlim(1);
            try
            {
                socket.Options.Proxy=Proxy;
                socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(20);
                using(var connect=CancellationTokenSource.CreateLinkedTokenSource(cancellation)){connect.CancelAfter(TimeSpan.FromSeconds(15));await socket.ConnectAsync(uri,connect.Token);}
                sockets[uri.ToString()]=(socket,sendLock);attempt=0;Status?.Invoke($"Connected to {uri.Host}");
                foreach(var (id,filter) in subscriptions)await Send(socket,sendLock,new object[]{"REQ",id,filter},cancellation);
                var buffer=new byte[8192];
                while(socket.State==WebSocketState.Open&&!cancellation.IsCancellationRequested)
                {
                    using var frame=new MemoryStream();WebSocketReceiveResult result;
                    do{result=await socket.ReceiveAsync(buffer,cancellation);if(result.MessageType==WebSocketMessageType.Close)break;if(frame.Length+result.Count>256*1024)throw new InvalidDataException("Relay frame too large");frame.Write(buffer,0,result.Count);}while(!result.EndOfMessage);
                    if(result.MessageType==WebSocketMessageType.Close)break;
                    if(result.MessageType!=WebSocketMessageType.Text)continue;
                    try
                    {
                        using var json=JsonDocument.Parse(frame.ToArray(),new(){MaxDepth=32});var root=json.RootElement;
                        if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()<2)continue;
                        var type=root[0].GetString();
                        if(type=="EVENT"&&root.GetArrayLength()==3){var e=NostrEvent.Parse(root[2].GetRawText());if(e.Valid())Received?.Invoke(e);}
                        else if(type=="OK"&&root.GetArrayLength()>=3){var id=root[1].GetString()!;if(acknowledgements.TryGetValue(id,out var ack)&&root[2].ValueKind==JsonValueKind.True)ack.TrySetResult(true);else if(root.GetArrayLength()>3)Status?.Invoke(uri.Host+": "+root[3].GetString());}
                        else if(type=="NOTICE"||type=="CLOSED")Status?.Invoke(uri.Host+": "+root[root.GetArrayLength()-1].GetString());
                    }catch(Exception e)when(e is JsonException or InvalidDataException or FormatException){Status?.Invoke("Rejected malformed relay frame");}
                }
            }
            catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}
            catch(Exception e){Status?.Invoke($"{uri.Host}: {e.Message}");}
            finally{sockets.TryRemove(uri.ToString(),out _);}
            try{await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,Math.Pow(2,Math.Min(++attempt,6)))),cancellation);}catch(OperationCanceledException){break;}
        }
    }
    private static async Task Send(ClientWebSocket socket,SemaphoreSlim gate,object frame,CancellationToken cancellation)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(frame,NostrEvent.Json);await gate.WaitAsync(cancellation);
        try{await socket.SendAsync(bytes,WebSocketMessageType.Text,true,cancellation);}finally{gate.Release();}
    }
    private async Task Broadcast(object frame)
    {
        await Task.WhenAll(sockets.Values.Select(async link=>{try{await Send(link.Socket,link.SendLock,frame,stop.Token);}catch(Exception e)when(e is WebSocketException or OperationCanceledException){Status?.Invoke("Relay disconnected during send");}}));
    }
    public async Task Subscribe(string id,object filter){subscriptions[id]=filter;await Broadcast(new object[]{"REQ",id,filter});}
    public async Task Unsubscribe(string id){subscriptions.TryRemove(id,out _);await Broadcast(new object[]{"CLOSE",id});}
    public async Task<bool> Publish(NostrEvent e)
    {
        if(!e.Valid())throw new InvalidDataException("Cannot publish invalid signature");if(Connected==0)return false;
        var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);acknowledgements[e.id]=ack;
        try{await Broadcast(new object[]{"EVENT",e});return await ack.Task.WaitAsync(TimeSpan.FromSeconds(12),stop.Token);}catch(TimeoutException){return false;}finally{acknowledgements.TryRemove(e.id,out _);}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();foreach(var link in sockets.Values)link.Socket.Abort();await Task.WhenAll(workers);stop.Dispose();}
}

public static class Geohash
{
    public const string Alphabet="0123456789bcdefghjkmnpqrstuvwxyz";
    public static string Encode(double latitude,double longitude,int precision=6)
    {
        if(latitude is <-90 or >90||longitude is <-180 or >180||precision is <1 or >12)throw new ArgumentException("Invalid location");
        double loLat=-90,hiLat=90,loLon=-180,hiLon=180;var text=new StringBuilder();int acc=0,bit=0;bool even=true;
        while(text.Length<precision)
        {
            double mid=even?(loLon+hiLon)/2:(loLat+hiLat)/2;bool high=(even?longitude:latitude)>=mid;acc=(acc<<1)|(high?1:0);
            if(even){if(high)loLon=mid;else hiLon=mid;}else{if(high)loLat=mid;else hiLat=mid;}even=!even;
            if(++bit==5){text.Append(Alphabet[acc]);acc=0;bit=0;}
        }
        return text.ToString();
    }
    public static bool Valid(string cell)=>cell.Length is >=1 and <=12&&cell.All(c=>Alphabet.Contains(c));
}

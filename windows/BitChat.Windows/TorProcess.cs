using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
namespace BitChat.Windows;

public sealed class TorProcess:IAsyncDisposable
{
    private Process? process;private Task? logs,errors;
    public event Action<string>? Status;
    public async Task<IWebProxy> Start(CancellationToken cancellation)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string directory=Path.Combine(StateStore.Root,"tor");Directory.CreateDirectory(directory);
        string binary=Path.Combine(AppContext.BaseDirectory,"tor","tor.exe");if(!File.Exists(binary))throw new FileNotFoundException("Bundled Tor client is missing",binary);
        var info=new ProcessStartInfo(binary){CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--ClientOnly","1","--SocksPort","127.0.0.1:"+port,"--DataDirectory",directory,"--AvoidDiskWrites","1","--Log","notice stdout","--SafeLogging","1"})info.ArgumentList.Add(arg);
        process=Process.Start(info)??throw new IOException("Could not start Tor");var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        logs=Task.Run(async()=>{string? line;while((line=await process.StandardOutput.ReadLineAsync())!=null){if(line.Contains("Bootstrapped"))Status?.Invoke("Tor: "+line[(line.IndexOf("Bootstrapped",StringComparison.Ordinal))..]);if(line.Contains("Bootstrapped 100%"))ready.TrySetResult(true);}ready.TrySetException(new IOException("Tor stopped before becoming ready"));});
        errors=Task.Run(async()=>{string? line;while((line=await process.StandardError.ReadLineAsync())!=null)Status?.Invoke("Tor: "+line);});
        try{await ready.Task.WaitAsync(TimeSpan.FromMinutes(2),cancellation);}catch{await DisposeAsync();throw;}
        return new WebProxy("socks5://127.0.0.1:"+port);
    }
    public async ValueTask DisposeAsync(){if(process is {HasExited:false})process.Kill(true);if(logs!=null)try{await Task.WhenAll(logs,errors??Task.CompletedTask);}catch{}process?.Dispose();process=null;}
}

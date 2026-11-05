using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BitChat.Windows;

internal sealed record DownloadProgress(long Bytes,long? Total,bool Verified=false);

internal sealed record AvailableUpdate(string Tag,string VersionText,string SetupUrl,string ChecksumUrl,string ReleaseUrl,Version Version);

internal sealed class ReleaseUpdater(StateStore store,HttpMessageHandler? handler=null):IDisposable
{
    private const string ReleasesApi="https://api.github.com/repos/fd1437/bitchat_windows-macos/releases?per_page=20";
    private readonly HttpClient http=new(handler??new HttpClientHandler{AutomaticDecompression=DecompressionMethods.All}){Timeout=TimeSpan.FromSeconds(20)};
    private readonly SemaphoreSlim gate=new(1,1);
    private DateTime retryAfterUtc=DateTime.MinValue;
    private static Version CurrentVersion=>Normalize(typeof(Program).Assembly.GetName().Version??new Version(0,0));
    private static Version Normalize(Version version)=>new(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));

    public static Version? ParseTagVersion(string? tag)
    {
        if(string.IsNullOrWhiteSpace(tag))return null;
        string value=tag.Trim().TrimStart('v','V').Split('-',2)[0];
        return Version.TryParse(value,out var version)?Normalize(version):null;
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellation=default)
    {
        if(DateTime.UtcNow<retryAfterUtc)return CachedUpdate();
        await gate.WaitAsync(cancellation);
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,ReleasesApi);
            request.Headers.UserAgent.ParseAdd("BitChat-Windows-Updater/1.0");request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if(EntityTagHeaderValue.TryParse(store.State.UpdateETag,out var etag))request.Headers.IfNoneMatch.Add(etag);
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
            if(response.StatusCode==HttpStatusCode.NotModified)return CachedUpdate();
            if((int)response.StatusCode==403||(int)response.StatusCode==429)
            {
                retryAfterUtc=response.Headers.RetryAfter?.Date?.UtcDateTime??DateTime.UtcNow.AddMinutes(2);
                return CachedUpdate();
            }
            response.EnsureSuccessStatusCode();
            string json=await response.Content.ReadAsStringAsync(cancellation);
            using var document=JsonDocument.Parse(json);
            var releases=document.RootElement.EnumerateArray().Where(r=>!r.GetProperty("draft").GetBoolean())
                .Select(ParseRelease).Where(r=>r!=null).Cast<AvailableUpdate>().OrderByDescending(r=>r.Version).FirstOrDefault();
            store.State.UpdateETag=response.Headers.ETag?.Tag;
            store.State.UpdateTag=releases?.Tag;store.State.UpdateSetupUrl=releases?.SetupUrl;
            store.State.UpdateChecksumUrl=releases?.ChecksumUrl;store.State.UpdateReleaseUrl=releases?.ReleaseUrl;store.Save();
            retryAfterUtc=DateTime.UtcNow.AddSeconds(10);
            return releases!=null&&releases.Version>CurrentVersion?releases:null;
        }
        catch(HttpRequestException){retryAfterUtc=DateTime.UtcNow.AddSeconds(10);return CachedUpdate();}
        catch(TaskCanceledException)when(!cancellation.IsCancellationRequested){retryAfterUtc=DateTime.UtcNow.AddSeconds(30);return CachedUpdate();}
        finally{gate.Release();}
    }

    private static AvailableUpdate? ParseRelease(JsonElement release)
    {
        if(!release.TryGetProperty("tag_name",out var tagValue))return null;
        string tag=tagValue.GetString()??"";Version? version=ParseTagVersion(tag);if(version==null)return null;
        if(!release.TryGetProperty("assets",out var assets)||assets.ValueKind!=JsonValueKind.Array)return null;
        string? setup=null,checksum=null;
        foreach(var asset in assets.EnumerateArray())
        {
            string name=asset.GetProperty("name").GetString()??"";string url=asset.GetProperty("browser_download_url").GetString()??"";
            if(name.Equals("BitChat-Setup-unsigned.exe",StringComparison.OrdinalIgnoreCase))setup=url;
            if(name.Equals("BitChat-Setup-unsigned.exe.sha256.txt",StringComparison.OrdinalIgnoreCase))checksum=url;
        }
        if(setup==null||checksum==null||!IsReleaseAssetUrl(setup,tag,"BitChat-Setup-unsigned.exe")||!IsReleaseAssetUrl(checksum,tag,"BitChat-Setup-unsigned.exe.sha256.txt"))return null;
        return new(tag,version.ToString(3),setup,checksum,"https://github.com/fd1437/bitchat_windows-macos/releases/tag/"+Uri.EscapeDataString(tag),version);
    }

    internal static bool IsReleaseAssetUrl(string value,string tag,string asset)=>
        Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps&&uri.IsDefaultPort&&
        uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&
        uri.AbsolutePath.Equals("/fd1437/bitchat_windows-macos/releases/download/"+Uri.EscapeDataString(tag)+"/"+asset,StringComparison.Ordinal);
    private static bool IsReleaseDownloadHost(Uri uri)=>uri.Scheme==Uri.UriSchemeHttps&&(uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".githubusercontent.com",StringComparison.OrdinalIgnoreCase));
    private static async Task CopyLimitedAsync(Stream source,Stream target,long limit,CancellationToken cancellation,Action<long>? progress=null)
    {
        byte[] buffer=new byte[128*1024];long total=0;int read;
        while((read=await source.ReadAsync(buffer,cancellation))>0)
        {total+=read;if(total>limit)throw new InvalidDataException("The GitHub download exceeds its size limit");await target.WriteAsync(buffer.AsMemory(0,read),cancellation);progress?.Invoke(total);}
    }
    private AvailableUpdate? CachedUpdate()
    {
        Version? version=ParseTagVersion(store.State.UpdateTag);
        if(version==null||version<=CurrentVersion||store.State.UpdateSetupUrl==null||store.State.UpdateChecksumUrl==null)return null;
        if(!IsReleaseAssetUrl(store.State.UpdateSetupUrl,store.State.UpdateTag!,"BitChat-Setup-unsigned.exe")||!IsReleaseAssetUrl(store.State.UpdateChecksumUrl,store.State.UpdateTag!,"BitChat-Setup-unsigned.exe.sha256.txt"))return null;
        return new(store.State.UpdateTag!,version.ToString(3),store.State.UpdateSetupUrl,store.State.UpdateChecksumUrl,"https://github.com/fd1437/bitchat_windows-macos/releases/tag/"+Uri.EscapeDataString(store.State.UpdateTag!),version);
    }

    public async Task<string> DownloadVerifiedAsync(AvailableUpdate update,IProgress<DownloadProgress>? progress=null,CancellationToken cancellation=default)
    {
        if(!IsReleaseAssetUrl(update.SetupUrl,update.Tag,"BitChat-Setup-unsigned.exe")||!IsReleaseAssetUrl(update.ChecksumUrl,update.Tag,"BitChat-Setup-unsigned.exe.sha256.txt"))throw new InvalidDataException("The update must come from the matching release in fd1437/bitchat_windows-macos");
        string directory=Path.Combine(StateStore.Root,"updates");Directory.CreateDirectory(directory);
        string destination=Path.Combine(directory,$"BitChat-Setup-{update.Version}.exe");string partial=destination+".download";
        using var setupResponse=await http.GetAsync(update.SetupUrl,HttpCompletionOption.ResponseHeadersRead,cancellation);setupResponse.EnsureSuccessStatusCode();
        Uri? final=setupResponse.RequestMessage?.RequestUri;
        if(final==null||!IsReleaseDownloadHost(final))throw new InvalidDataException("Unexpected release download redirect");
        long? length=setupResponse.Content.Headers.ContentLength;if(length is > 800_000_000)throw new InvalidDataException("The installer exceeds the allowed download size");
        try
        {
            await using(var source=await setupResponse.Content.ReadAsStreamAsync(cancellation))
            await using(var target=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,128*1024,true))
            {await CopyLimitedAsync(source,target,800_000_000,cancellation,bytes=>progress?.Report(new(bytes,length)));if(target.Length<1_000_000)throw new InvalidDataException("The downloaded installer size is invalid");}
            using var checksumResponse=await http.GetAsync(update.ChecksumUrl,HttpCompletionOption.ResponseHeadersRead,cancellation);checksumResponse.EnsureSuccessStatusCode();
            Uri? checksumFinal=checksumResponse.RequestMessage?.RequestUri;
            if(checksumFinal==null||!IsReleaseDownloadHost(checksumFinal))throw new InvalidDataException("Unexpected checksum download redirect");
            await using var checksumStream=await checksumResponse.Content.ReadAsStreamAsync(cancellation);using var checksumBuffer=new MemoryStream();
            await CopyLimitedAsync(checksumStream,checksumBuffer,16*1024,cancellation);string checksumText=Encoding.UTF8.GetString(checksumBuffer.ToArray());
            var match=System.Text.RegularExpressions.Regex.Match(checksumText,@"(?i)\b([0-9a-f]{64})\b");
            if(!match.Success)throw new InvalidDataException("The release checksum file is missing a SHA-256 digest");
            byte[] expected=Convert.FromHexString(match.Groups[1].Value);byte[] actual;
            await using(var file=File.OpenRead(partial)){actual=await SHA256.HashDataAsync(file,cancellation);}
            if(!CryptographicOperations.FixedTimeEquals(expected,actual))throw new CryptographicException("The installer SHA-256 does not match the GitHub checksum");
            File.Move(partial,destination,true);progress?.Report(new(new FileInfo(destination).Length,length,true));return destination;
        }
        catch{try{File.Delete(partial);}catch{}throw;}
    }

    public bool StartInstallerAfterExit(string installer)
    {
        if(!Path.GetFullPath(installer).StartsWith(Path.GetFullPath(Path.Combine(StateStore.Root,"updates"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installer is outside the update cache");
        string app=Environment.ProcessPath??throw new IOException("BitChat executable path is unavailable");
        static string Ps(string value)=>"'"+value.Replace("'","''",StringComparison.Ordinal)+"'";
        string script="$ErrorActionPreference='Stop'; Wait-Process -Id "+Environment.ProcessId+" -ErrorAction SilentlyContinue; $setup="+Ps(Path.GetFullPath(installer))+"; $install=Start-Process -FilePath $setup -ArgumentList @('/S','/UPDATE') -Wait -PassThru; if($install.ExitCode -ne 0){exit $install.ExitCode}; Start-Process -FilePath "+Ps(app)+"; Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath ($setup+'.download') -Force -ErrorAction SilentlyContinue";
        string encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var info=new ProcessStartInfo("powershell.exe",$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}"){UseShellExecute=false,CreateNoWindow=true};
        using var helper=Process.Start(info);return helper!=null;
    }

    public static void CleanupStaleFiles()
    {
        try
        {
            string directory=Path.Combine(StateStore.Root,"updates");if(!Directory.Exists(directory))return;
            foreach(string path in Directory.EnumerateFiles(directory))
                try{if(File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-1))File.Delete(path);}catch{}
        }
        catch{}
    }

    public void Dispose(){http.Dispose();gate.Dispose();}
}

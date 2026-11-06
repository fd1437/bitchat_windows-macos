using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BitChat.Core;
using Windows.Devices.Bluetooth;
namespace BitChat.Windows;

public static class Diagnostics
{
    private sealed class UpdateDownloadHandler(byte[] payload):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            bool checksum=request.RequestUri!.AbsolutePath.EndsWith(".txt",StringComparison.Ordinal);
            byte[] bytes=checksum?Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(payload))+"  BitChat-Setup-unsigned.exe"):payload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new ByteArrayContent(bytes)});
        }
    }
    private sealed class DownloadProgressLog:IProgress<DownloadProgress>
    {public readonly List<DownloadProgress> Values=[];public void Report(DownloadProgress value)=>Values.Add(value);}
    private sealed record Result(string Name,bool Passed,string Detail);
    public static async Task Run(string reportPath)
    {
        reportPath=Path.GetFullPath(reportPath);Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);var checks=new List<Result>();
        async Task Check(string name,Func<Task<object>> test)
        {try{checks.Add(new(name,true,(await test()).ToString()??""));}catch(Exception e){checks.Add(new(name,false,e.Message));}}
        await Check("GitHub release version parsing",()=>
        {
            if(ReleaseUpdater.ParseTagVersion("v1.2.3-windows-preview")!=new Version(1,2,3,0)||ReleaseUpdater.ParseTagVersion("not-a-version")!=null)throw new InvalidDataException("Release tags were not parsed safely");
            return Task.FromResult<object>("semantic versions are compared independently of preview suffixes");
        });
        await Check("Fresh profiles contain no conversations or shared identity",()=>
        {
            string temporary=Path.Combine(Path.GetDirectoryName(reportPath)!,"bitchat-profile-diagnostic-"+Guid.NewGuid().ToString("N"));string? prior=StateStore.OverrideRoot;
            try
            {
                StateStore.OverrideRoot=Path.Combine(temporary,"existing");var existing=new StateStore();
                existing.State.Messages.Add(new("mesh","synthetic-canary","test","synthetic profile isolation marker",0));existing.Save();
                StateStore.OverrideRoot=Path.Combine(temporary,"fresh");var fresh=new StateStore();
                if(fresh.State.Messages.Count!=0||fresh.State.KnownPeers.Count!=0||fresh.State.Outbox.Count!=0||fresh.State.NoiseContacts.Count!=0||fresh.State.Favorites.Count!=0)throw new IOException("Fresh profile contains existing conversation data");
                if(fresh.State.NoiseKey.SequenceEqual(existing.State.NoiseKey)||fresh.State.SigningSeed.SequenceEqual(existing.State.SigningSeed)||fresh.State.NostrKey.SequenceEqual(existing.State.NostrKey))throw new CryptographicException("Fresh profiles share identity keys");
                StateStore.OverrideRoot=Path.Combine(temporary,"existing");if(new StateStore().State.Messages.Count!=1)throw new IOException("Existing profile data was modified");
                return Task.FromResult<object>("new profile is empty, keys are generated independently, existing history is preserved");
            }
            finally{StateStore.OverrideRoot=prior;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
        });
        await Check("Update assets are restricted to this repository and release",()=>
        {
            const string tag="v0.1.9-windows-preview",asset="BitChat-Setup-unsigned.exe";
            const string url="https://github.com/fd1437/bitchat_windows-macos/releases/download/v0.1.9-windows-preview/BitChat-Setup-unsigned.exe";
            if(!ReleaseUpdater.IsReleaseAssetUrl(url,tag,asset))throw new InvalidDataException("Valid release rejected");
            foreach(string invalid in new[]{url.Replace("fd1437/","other-owner/"),url.Replace("bitchat_windows-macos/","other-repo/"),url.Replace("https:","http:"),url.Replace("github.com/","github.com:444/"),url.Replace("github.com/","user@github.com/"),url.Replace(tag,"v0.1.8-windows-preview"),url+"?download=1",url+"#fragment",url+".other"})
                if(ReleaseUpdater.IsReleaseAssetUrl(invalid,tag,asset))throw new InvalidDataException("Foreign or mismatched update URL accepted");
            return Task.FromResult<object>("wrong owner, repository, tag, asset, scheme, credentials, port, query and fragment rejected");
        });
        await Check("Configurable local file retention",async()=>
        {
            string temporary=Path.Combine(Path.GetDirectoryName(reportPath)!,"bitchat-retention-diagnostic-"+Guid.NewGuid().ToString("N"));string? prior=StateStore.OverrideRoot;StateStore.OverrideRoot=temporary;
            try
            {
                var store=new StateStore();var media=new MediaPacket("expired.png","image/png",[1,2,3]);string path=store.SaveMedia(media);
                store.State.FileRetentionDays=1;store.State.Messages.Add(new("mesh","retention-diagnostic","peer","expired.png",DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds(),Attachment:path,Mime:media.Mime));store.Save();
                await using var controller=new AppController(store);int removed=await controller.CleanupExpiredMedia();
                if(removed!=1||File.Exists(path)||store.State.Messages[0].Attachment!=null||!store.State.Messages[0].LocalAttachmentDeleted)throw new IOException("Expired media was not removed locally");
                return "expired attachment file removed while retaining its message row";
            }
            finally{StateStore.OverrideRoot=prior;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
        });
        await Check("Update download progress, checksum and file release",async()=>
        {
            string temporary=Path.Combine(Path.GetDirectoryName(reportPath)!,"bitchat-update-diagnostic-"+Guid.NewGuid().ToString("N"));string? prior=StateStore.OverrideRoot;StateStore.OverrideRoot=temporary;
            try
            {
                var store=new StateStore();byte[] payload=RandomNumberGenerator.GetBytes(1024*1024);var progress=new DownloadProgressLog();
                using var updater=new ReleaseUpdater(store,new UpdateDownloadHandler(payload));
                var update=new AvailableUpdate("v0.1.9-windows-preview","0.1.9","https://github.com/fd1437/bitchat_windows-macos/releases/download/v0.1.9-windows-preview/BitChat-Setup-unsigned.exe","https://github.com/fd1437/bitchat_windows-macos/releases/download/v0.1.9-windows-preview/BitChat-Setup-unsigned.exe.sha256.txt","https://github.com/fd1437/bitchat_windows-macos/releases",new Version(0,1,9,0));
                string file=await updater.DownloadVerifiedAsync(update,progress);
                if(!File.ReadAllBytes(file).SequenceEqual(payload)||progress.Values.Count<2||!progress.Values[^1].Verified||progress.Values[^1].Total!=payload.Length)throw new IOException("Download progress or checksum verification failed");
                File.Delete(file);if(File.Exists(file))throw new IOException("Downloaded installer is still held open");
                return "streamed bytes and total, SHA-256 validated, file renamed and released on Windows";
            }
            finally{StateStore.OverrideRoot=prior;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
        });
        await Check("Native Windows notification registration",()=>Task.FromResult<object>(NativeNotifications.ProbeRegistration()));
        await Check("Windows DPAPI",()=>{var clear=RandomNumberGenerator.GetBytes(32);var encrypted=ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser);if(!ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser).SequenceEqual(clear))throw new CryptographicException("DPAPI roundtrip failed");return Task.FromResult<object>("per-user encryption works");});
        await Check("Durable private media (DPAPI)",()=>
        {
            string temporary=Path.Combine(Path.GetDirectoryName(reportPath)!,"bitchat-media-diagnostic-"+Guid.NewGuid().ToString("N"));string? prior=StateStore.OverrideRoot;StateStore.OverrideRoot=temporary;
            try
            {
                var store=new StateStore();string mediaRoot=Path.Combine(temporary,"media");
                byte[] Protect(byte[] bytes)=>ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
                byte[] Unprotect(byte[] bytes)=>ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser);
                const string id="media-0123456789abcdef0123456789abcdef";
                var payload=new MediaPacket("native-media.bin","application/octet-stream",RandomNumberGenerator.GetBytes(512));
                var ledger=new PrivateMediaLedger(mediaRoot,Protect,Unprotect);var accepted=ledger.Accept(id,payload,store.SaveMedia);
                if(accepted.Kind!=MediaDecisionKind.Accepted||!store.ReadMedia(accepted.Path!).SequenceEqual(payload.Content))throw new IOException("Encrypted media storage failed");
                ledger=new(mediaRoot,Protect,Unprotect);
                if(ledger.Get(id).Path!=accepted.Path)throw new IOException("Durable media mapping did not survive restart");
                if(!ledger.Delete(new Dictionary<string,string?>{{id,accepted.Path}})||File.Exists(accepted.Path))throw new IOException("Durable media deletion failed");
                ledger=new(mediaRoot,Protect,Unprotect);
                if(ledger.Get(id).Kind!=MediaDecisionKind.Deleted)throw new IOException("Deleted media could be resurrected");
                return Task.FromResult<object>("encrypted payload, persisted receipt, restart and deletion tombstone work");
            }
            finally{StateStore.OverrideRoot=prior;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
        });
        await Check("Durable bridge courier inbox (DPAPI)",async()=>
        {
            string temporary=Path.Combine(Path.GetDirectoryName(reportPath)!,"bitchat-courier-diagnostic-"+Guid.NewGuid().ToString("N"));string? prior=StateStore.OverrideRoot;StateStore.OverrideRoot=temporary;
            try
            {
                var store=new StateStore();byte[] sender=RandomNumberGenerator.GetBytes(32);string peer=Bytes.Hex(Crypto.Hash(Crypto.PublicX(sender)))[..16];
                const string id="native-offline-courier";
                byte[] Payload(string messageId,string text)=>Bytes.Join([1],Announcement.Tlv(0,Bytes.Utf8(messageId)),Announcement.Tlv(1,Bytes.Utf8(text)));
                var envelope=CourierEnvelope.Seal(Payload(id,"offline courier"),sender,Crypto.PublicX(store.State.NoiseKey));
                await using(var controller=new AppController(store))
                {
                    if(!controller.OpenCourierEnvelope(envelope)||!controller.OpenCourierEnvelope(envelope)||store.State.Messages.Count!=1)throw new IOException("Courier inbox failed or duplicated");
                    var control=CourierEnvelope.Seal(Payload("native-favorite","[FAVORITED]"),sender,Crypto.PublicX(store.State.NoiseKey));
                    if(!controller.OpenCourierEnvelope(control)||!store.State.FavoritedBy.Contains(peer)||store.State.Messages.Count!=1)throw new IOException("Courier control did not commit atomically");
                }
                store=new StateStore();
                await using(var restarted=new AppController(store))
                {
                    if(!restarted.OpenCourierEnvelope(envelope)||store.State.Messages.Count!=1||!store.State.NoiseContacts[peer].Key.SequenceEqual(Crypto.PublicX(sender)))throw new IOException("Offline sender/contact/dedup did not survive restart");
                    uint prekeyId=42;byte[] secret=RandomNumberGenerator.GetBytes(32);
                    var prekeyEnvelope=CourierEnvelope.Seal(Payload("native-prekey","prekey courier"),sender,Crypto.PublicX(store.State.NoiseKey),new(prekeyId,Crypto.PublicX(secret)));
                    if(restarted.OpenCourierEnvelope(prekeyEnvelope))throw new IOException("Missing prekey was consumed");
                    store.State.LocalPrekeys[prekeyId]=new(secret);store.Save();
                    if(!restarted.OpenCourierEnvelope(prekeyEnvelope)||store.State.LocalPrekeys[prekeyId].Consumed==null||store.State.Messages.Count!=2)throw new IOException("Prekey retry failed");
                }
                return "unknown offline sender, durable contact/dedup, favorite control and missing-prekey retry work";
            }
            finally{StateStore.OverrideRoot=prior;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
        });
        await Check("Noise XX",()=>
        {
            using var a=new NoiseHandshake(true,RandomNumberGenerator.GetBytes(32));using var b=new NoiseHandshake(false,RandomNumberGenerator.GetBytes(32));b.Read(a.Write());a.Read(b.Write());b.Read(a.Write());var tx=a.Split();var rx=b.Split();var plain=Bytes.Utf8("native Windows cryptographic smoke test");if(!rx.Receive.Decrypt(tx.Send.Encrypt(plain)).SequenceEqual(plain))throw new CryptographicException("Noise session failed");return Task.FromResult<object>("handshake and transport work");
        });
        await Check("Nostr private envelope",()=>{var a=Schnorr.GenerateKey();var b=Schnorr.GenerateKey();var wrap=PrivateEnvelope.Wrap("native test",a,Bytes.Hex(Schnorr.Public(b)));if(PrivateEnvelope.Open(wrap,b).content!="native test")throw new CryptographicException("Nostr envelope failed");return Task.FromResult<object>("BIP340 + XChaCha20-Poly1305 work");});
        await Check("Bundled AAC codec",async()=>
        {
            var pcm=new byte[32000];for(int i=0;i<16000;i++)System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i*2), (short)(Math.Sin(i*2*Math.PI*440/16000)*12000));
            using var encoder=AudioProcess.Start(["-f","s16le","-ar","16000","-ac","1","-i","pipe:0","-c:a","aac","-b:a","16000","-f","adts","pipe:1"]);using var encoded=new MemoryStream();var errors=encoder.StandardError.ReadToEndAsync();var read=encoder.StandardOutput.BaseStream.CopyToAsync(encoded);await encoder.StandardInput.BaseStream.WriteAsync(pcm);encoder.StandardInput.Close();
            try{await Task.WhenAll(read,encoder.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));if(encoder.ExitCode!=0)throw new IOException(await errors);}finally{if(!encoder.HasExited)encoder.Kill(true);}
            var aac=encoded.ToArray();var note=await AudioProcess.VoiceNote(aac);if(!note.Mime.Equals("audio/mp4")||!note.Content.AsSpan(4,4).SequenceEqual("ftyp"u8))throw new InvalidDataException("M4A note failed");if(aac.Length<100||aac[0]!=0xff)throw new InvalidDataException("AAC encoder produced invalid output");
            using var decoder=AudioProcess.Start(["-f","mov","-i","pipe:0","-f","s16le","-ar","16000","-ac","1","pipe:1"]);using var decoded=new MemoryStream();var decodeErrors=decoder.StandardError.ReadToEndAsync();var decodeRead=decoder.StandardOutput.BaseStream.CopyToAsync(decoded);await decoder.StandardInput.BaseStream.WriteAsync(note.Content);decoder.StandardInput.Close();
            try{await Task.WhenAll(decodeRead,decoder.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));if(decoder.ExitCode!=0)throw new IOException(await decodeErrors);}finally{if(!decoder.HasExited)decoder.Kill(true);}
            if(decoded.Length<30000)throw new InvalidDataException("AAC decoder produced too little audio");return $"{aac.Length} AAC bytes, {note.Content.Length} M4A bytes, {decoded.Length} decoded PCM bytes";
        });
        await Check("Bundled Tor",async()=>
        {
            var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"tor","tor.exe"),"--version"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};using var process=Process.Start(info)??throw new IOException("Tor could not launch");string output=await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));if(process.ExitCode!=0)throw new IOException(await process.StandardError.ReadToEndAsync());return output.Split('\n')[0].Trim();
        });
        await Check("Bluetooth adapter",async()=>{var adapter=await BluetoothAdapter.GetDefaultAsync();return adapter==null?"No adapter detected; Bluetooth hardware validation is still required":$"Central={adapter.IsCentralRoleSupported}, Peripheral={adapter.IsPeripheralRoleSupported}";});
        var report=new{generatedUtc=DateTimeOffset.UtcNow,os=Environment.OSVersion.VersionString,architecture=RuntimeInformation.ProcessArchitecture.ToString(),checks};Environment.ExitCode=checks.Any(c=>!c.Passed)?1:0;await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    }
    public static async Task RenderUi(string path)
    {
        string originalRoot=StateStore.Root;string temporary=Path.Combine(Path.GetDirectoryName(path)!,"bitchat-diagnostic-"+Guid.NewGuid().ToString("N"));StateStore.OverrideRoot=temporary;
        MainWindow? window=null;
        try
        {
            window=new MainWindow(new StateStore(),true);
            window.Show();window.VerifyNotificationVisibilityRouting();window.VerifyWindowControls();window.Close();
            if(window.IsVisible)throw new InvalidOperationException("Closing must hide the window");
            window.RestoreFromTray();
            if(!window.IsVisible)throw new InvalidOperationException("Tray restore must reopen the same window");
            window.VerifyComposerKeyHandling();window.VerifyImageReveal();window.VerifyDraftStaging();window.VerifyFileProgress();window.VerifyMediaViewer();
            string nativeXml=NativeNotifications.BuildContent("mesh","Alice","Photo",new Uri("file:///C:/preview.png"),false).GetToastContent().GetContent();
            if(!nativeXml.Contains("<input")||!nativeXml.Contains("<image")||!nativeXml.Contains("reply"))throw new InvalidDataException("Native notification is missing its image or reply input");
            window.PrepareVisualDiagnostics();
            var content=(FrameworkElement)window.Content;content.Measure(new Size(1120,780));content.Arrange(new Rect(0,0,1120,780));content.UpdateLayout();
            var render=new RenderTargetBitmap(1120,780,96,96,PixelFormats.Pbgra32);render.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));using(var stream=File.Create(path))encoder.Save(stream);
            window.SetDiagnosticHackMode();content.UpdateLayout();var hackRender=new RenderTargetBitmap(1120,780,96,96,PixelFormats.Pbgra32);hackRender.Render(content);var hackEncoder=new PngBitmapEncoder();hackEncoder.Frames.Add(BitmapFrame.Create(hackRender));using(var hackFile=File.Create(Path.ChangeExtension(path,"hack.png")))hackEncoder.Save(hackFile);
        }
        finally{if(window!=null){await window.ShutdownDiagnostics();window.Close();}StateStore.OverrideRoot=null;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
}

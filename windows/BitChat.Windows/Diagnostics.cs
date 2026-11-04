using System.Diagnostics;
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
    private sealed record Result(string Name,bool Passed,string Detail);
    public static async Task Run(string reportPath)
    {
        reportPath=Path.GetFullPath(reportPath);Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);var checks=new List<Result>();
        async Task Check(string name,Func<Task<object>> test)
        {try{checks.Add(new(name,true,(await test()).ToString()??""));}catch(Exception e){checks.Add(new(name,false,e.Message));}}
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
            window.Show();window.Close();
            if(window.IsVisible)throw new InvalidOperationException("Closing must hide the window");
            window.RestoreFromTray();
            if(!window.IsVisible)throw new InvalidOperationException("Tray restore must reopen the same window");
            window.VerifyComposerKeyHandling();window.VerifyImageReveal();
            var content=(FrameworkElement)window.Content;content.Measure(new Size(1120,780));content.Arrange(new Rect(0,0,1120,780));content.UpdateLayout();
            var render=new RenderTargetBitmap(1120,780,96,96,PixelFormats.Pbgra32);render.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));using var stream=File.Create(path);encoder.Save(stream);
        }
        finally{if(window!=null){await window.ShutdownDiagnostics();window.Close();}StateStore.OverrideRoot=null;if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
}

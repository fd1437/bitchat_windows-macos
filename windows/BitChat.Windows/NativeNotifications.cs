using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.WinUI.Notifications;
using BitChat.Core;

namespace BitChat.Windows;

internal sealed record NotificationActivation(string Conversation,string Action,string? Reply);
internal static class NativeNotifications
{
    private const string PipeName="BitChat.Windows.NotificationActivation";
    private static readonly CancellationTokenSource stop=new();
    private static bool initialized;
    internal static bool WasActivated=>ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
    internal static void Uninstall()=>ToastNotificationManagerCompat.Uninstall();
    internal static string ProbeRegistration()
    {
        ToastNotificationManagerCompat.OnActivated+=static _=>{};
        try{return ToastNotificationManagerCompat.CreateToastNotifier().Setting.ToString();}
        finally{ToastNotificationManagerCompat.Uninstall();}
    }
    internal static void Stop()=>stop.Cancel();
    private static NotificationActivation? Parse(ToastNotificationActivatedEventArgsCompat args)
    {
        var values=ToastArguments.Parse(args.Argument);
        if(!values.TryGetValue("conversation",out string conversation)||conversation.Length>256)return null;
        values.TryGetValue("action",out string action);
        string? reply=args.UserInput.TryGetValue("reply",out var value)?value?.ToString():null;
        if(reply?.Length>4096)return null;
        return new(conversation,action??"view",reply);
    }
    internal static void Initialize(Action<NotificationActivation> activation)
    {
        ToastNotificationManagerCompat.OnActivated+=args=>{try{var item=Parse(args);if(item!=null)activation(item);}catch{}};
        initialized=true;
        _=Task.Run(async()=>
        {
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token);using var reader=new StreamReader(pipe,Encoding.UTF8);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var text=new StringBuilder();char[] buffer=new char[1024];int count;
                    while((count=await reader.ReadAsync(buffer.AsMemory(),timeout.Token))>0){text.Append(buffer,0,count);if(text.Length>16384)throw new InvalidDataException("Notification activation too large");}
                    var item=JsonSerializer.Deserialize<NotificationActivation>(text.ToString());
                    if(item!=null&&item.Conversation.Length<=256&&item.Reply?.Length is not >4096)activation(item);
                }
                catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
                catch{}
            }
        });
        CleanupPreviews();
    }
    internal static void ForwardToRunningInstance(Application app)
    {
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        ToastNotificationManagerCompat.OnActivated+=async args=>
        {
            try
            {
                var item=Parse(args);if(item==null)return;
                using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.Out,PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);using var writer=new StreamWriter(pipe,Encoding.UTF8);await writer.WriteAsync(JsonSerializer.Serialize(item));await writer.FlushAsync();
            }
            catch{}
            finally{_=app.Dispatcher.BeginInvoke(new Action(()=>app.Shutdown()));}
        };
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(12)};timer.Tick+=(_,_)=>{timer.Stop();app.Shutdown();};timer.Start();
    }
    internal static ToastContentBuilder BuildContent(string conversation,string sender,string text,Uri? image,bool hide)
    {
        var builder=new ToastContentBuilder().AddArgument("conversation",conversation).AddArgument("action","view").AddText(hide?"BitChat":sender).AddText(hide?"New message":text);
        if(!hide&&image!=null)builder.AddInlineImage(image);
        builder.AddInputTextBox("reply",placeHolderContent:"Write a reply").AddButton(new ToastButton().SetContent("Send").AddArgument("conversation",conversation).AddArgument("action","reply").SetTextBoxId("reply").SetBackgroundActivation());
        return builder;
    }
    internal static bool TryShow(StateStore store,StoredMessage message)
    {
        if(!initialized)return false;
        try
        {
            Uri? image=null;
            if(!store.State.HideNotificationPreviews&&message.Attachment!=null&&message.Mime?.StartsWith("image/")==true)
            {
                try
                {
                    using var stream=new MemoryStream(store.ReadMedia(message.Attachment));var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];
                    if(frame.PixelWidth>0&&frame.PixelHeight>0&&(long)frame.PixelWidth*frame.PixelHeight<=40000000)
                    {
                        stream.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=360;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
                        string directory=Path.Combine(StateStore.Root,"notification-previews");Directory.CreateDirectory(directory);string path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".png");
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);image=new Uri(path);
                    }
                }
                catch{}
            }
            string Tag(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
            BuildContent(message.Conversation,IdentityNames.SenderLabel(message.Sender,message.Peer),message.Text.Length>800?message.Text[..800]:message.Text,image,store.State.HideNotificationPreviews).Show(toast=>{toast.Tag=Tag(message.Id);toast.Group=Tag(message.Conversation);toast.ExpirationTime=DateTimeOffset.Now.AddDays(1);});
            return true;
        }
        catch{return false;}
    }
    private static void CleanupPreviews()
    {
        foreach(string folder in new[]{"notification-previews","clipboard"})
        {
            try
            {
                string directory=Path.Combine(StateStore.Root,folder);if(!Directory.Exists(directory))continue;
                foreach(var path in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))if(File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-1))try{File.Delete(path);}catch{}
            }
            catch{}
        }
    }
}

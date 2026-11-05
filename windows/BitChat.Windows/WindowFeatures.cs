using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using BitChat.Core;

namespace BitChat.Windows;

public sealed partial class MainWindow
{
    private string? activeTypingPeer;
    private bool typingSent;
    private readonly List<Window> toastWindows=[];
    private sealed record FileGalleryRow(StoredMessage Message,string Label);

    private void ComposerChanged()
    {
        string? peer=conversation.StartsWith("mesh:",StringComparison.Ordinal)?conversation[5..]:null;
        if(peer!=activeTypingPeer){SendTyping(false);activeTypingPeer=peer;}
        if(peer==null||input.Text.Length==0){typingStopTimer.Stop();SendTyping(false);return;}
        SendTyping(true);typingStopTimer.Stop();typingStopTimer.Start();
    }

    private void SendTyping(bool value)
    {
        if(activeTypingPeer==null||typingSent==value)return;
        typingSent=value;_=Guard(()=>controller.SendTyping(activeTypingPeer,value));
    }

    private void RefreshTypingLabel()
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(string peer in typingPeers.Where(pair=>now-pair.Value>5000).Select(pair=>pair.Key).ToArray())typingPeers.Remove(peer);
        if(conversation.StartsWith("mesh:")&&typingPeers.ContainsKey(conversation[5..]))
            typingLabel.Text=(controller.Peers.GetValueOrDefault(conversation[5..])?.Nickname??title.Text.TrimStart('@',' '))+" is typing…";
        else typingLabel.Text="";
    }

    private void ShowConversationFiles()
    {
        var files=store.State.Messages.Where(m=>m.Conversation==conversation&&(m.Mime!=null||m.LocalAttachmentDeleted))
            .OrderByDescending(m=>m.Timestamp).Select(m=>new FileGalleryRow(m,$"{DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).ToLocalTime():g}   {m.Text}   ·   {m.Sender}{(m.LocalAttachmentDeleted?"   (deleted locally)":"")}")).ToArray();
        if(files.Length==0)return;
        var dialog=new Window{Owner=this,Title="Conversation files",Width=760,Height=570,MinWidth=520,MinHeight=360,Background=bg,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new Grid{Margin=new Thickness(18)};root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});dialog.Content=root;
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var delete=new Button{Content="Delete selected local files",Background=panel,Foreground=green,Padding=new Thickness(12,8,12,8),Margin=new Thickness(4)};
        var save=new Button{Content="Save selected file as…",Background=panel,Foreground=green,Padding=new Thickness(12,8,12,8),Margin=new Thickness(4)};
        var close=new Button{Content="Close",Background=panel,Foreground=green,Padding=new Thickness(12,8,12,8),Margin=new Thickness(4)};
        buttons.Children.Add(delete);buttons.Children.Add(save);buttons.Children.Add(close);Grid.SetRow(buttons,2);root.Children.Add(buttons);
        var list=new ListBox{SelectionMode=SelectionMode.Extended,Background=bg,Foreground=Foreground,BorderBrush=muted,Margin=new Thickness(0,12,0,8),DisplayMemberPath="Label"};
        foreach(var row in files)list.Items.Add(row);Grid.SetRow(list,0);root.Children.Add(list);
        var description=new TextBlock{Text="Select rows with Ctrl-click or Shift-click. Deletion removes the saved local copies from this device.",Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,6)};Grid.SetRow(description,1);root.Children.Add(description);
        delete.Click+=async(_,_)=>{var selected=list.SelectedItems.Cast<FileGalleryRow>().Select(row=>row.Message).ToArray();if(selected.Length==0)return;try{int count=await controller.DeleteLocalAttachments(selected);status.Text=$"Deleted {count} local file(s)";dialog.Close();Select(conversation);}catch(Exception e){status.Text=e.Message;}};
        save.Click+=(_,_)=>{if(list.SelectedItems.Count==0||list.SelectedItems[0] is not FileGalleryRow row)return;if(row.Message.Attachment!=null)ExportAttachment(row.Message);};close.Click+=(_,_)=>dialog.Close();
        dialog.ShowDialog();
    }

    private void ShowMessageToast(StoredMessage message)
    {
        bool hide=store.State.HideNotificationPreviews;
        var toast=new Window{Width=390,Height=hide?172:IsImage(message)?295:210,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,Background=panel,Foreground=Foreground,AllowsTransparency=true,Opacity=.98};
        var border=new Border{Background=panel,BorderBrush=green,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Padding=new Thickness(15),Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=16,ShadowDepth=3,Opacity=.35}};
        var stack=new StackPanel();border.Child=stack;toast.Content=border;
        var heading=new DockPanel();var close=new Button{Content="×",Background=Brushes.Transparent,Foreground=muted,BorderThickness=new Thickness(0),Padding=new Thickness(8,0,8,0),FontSize=17};close.Click+=(_,_)=>toast.Close();DockPanel.SetDock(close,Dock.Right);heading.Children.Add(close);heading.Children.Add(new TextBlock{Text=hide?"BitChat":"BitChat · "+message.Sender,Foreground=green,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});stack.Children.Add(heading);
        if(!hide)
        {
            var body=new TextBlock{Text=message.Text,Foreground=Foreground,TextWrapping=TextWrapping.Wrap,MaxHeight=56,Margin=new Thickness(0,8,0,4)};stack.Children.Add(body);
            if(IsImage(message)&&message.Attachment!=null)
            {
                try
                {
                    byte[] bytes=store.ReadMedia(message.Attachment);using var stream=new MemoryStream(bytes);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.OnLoad);var frame=decoder.Frames[0];
                    if(frame.PixelWidth>0&&frame.PixelHeight>0&&(long)frame.PixelWidth*frame.PixelHeight<=40000000)
                    {stream.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=260;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();stack.Children.Add(new Image{Source=bitmap,Width=260,Height=80,Stretch=Stretch.UniformToFill,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,7)});}
                }
                catch{}
            }
        }
        var replyRow=new DockPanel{Margin=new Thickness(0,8,0,0)};var reply=new TextBox{Background=bg,Foreground=Foreground,CaretBrush=green,BorderBrush=muted,Padding=new Thickness(8),MinWidth=240};reply.ToolTip="Write a reply";var send=new Button{Content="Send",Background=green,Foreground=Brushes.Black,Padding=new Thickness(12,7,12,7),Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(send,Dock.Right);replyRow.Children.Add(send);replyRow.Children.Add(reply);stack.Children.Add(replyRow);
        send.Click+=async(_,_)=>{string text=reply.Text.Trim();if(text.Length==0)return;send.IsEnabled=false;try{await controller.Send(message.Conversation,text);toast.Close();}catch(Exception e){reply.ToolTip=e.Message;send.IsEnabled=true;}};
        reply.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;send.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));}};
        toast.Closed+=(_,_)=>toastWindows.Remove(toast);toastWindows.Add(toast);
        var handle=new WindowInteropHelper(this).Handle;
        var screen=handle==IntPtr.Zero?System.Windows.Forms.Screen.PrimaryScreen:System.Windows.Forms.Screen.FromHandle(handle);
        if(screen!=null)
        {
            var pixels=screen.WorkingArea;
            var deviceRect=new Rect(pixels.Left,pixels.Top,pixels.Width,pixels.Height);
            Matrix fromDevice=PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice??Matrix.Identity;
            var area=new Rect(fromDevice.Transform(new Point(deviceRect.Left,deviceRect.Top)),fromDevice.Transform(new Point(deviceRect.Right,deviceRect.Bottom)));
            toast.Left=Math.Max(area.Left+8,area.Right-toast.Width-14);
            double maxTop=Math.Max(area.Top+8,area.Bottom-toast.Height-8);
            toast.Top=Math.Clamp(area.Bottom-toast.Height-14-(toastWindows.Count-1)*(toast.Height+8),area.Top+8,maxTop);
        }
        toast.Show();
        if(!toast.IsVisible){toast.Close();ShowFallbackNotification(message,new InvalidOperationException("The message alert window could not be displayed"));return;}
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(15)};timer.Tick+=(_,_)=>{timer.Stop();toast.Close();};toast.Closed+=(_,_)=>timer.Stop();timer.Start();
    }

    private void ShowFallbackNotification(StoredMessage message,Exception error)
    {
        status.Text="Message alert fallback: "+error.Message;
        try
        {
            tray.BalloonTipTitle=store.State.HideNotificationPreviews?"BitChat":"BitChat · "+message.Sender;
            string body=store.State.HideNotificationPreviews?"New message":message.Text;
            tray.BalloonTipText=body.Length>240?body[..240]:body;
            tray.ShowBalloonTip(6000);
        }
        catch{}
    }
}

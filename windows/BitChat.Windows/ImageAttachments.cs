using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BitChat.Core;

namespace BitChat.Windows;

public sealed partial class MainWindow
{
    private readonly HashSet<string> revealedImages=[];
    private static string ImageKey(StoredMessage message)=>message.Conversation+":"+message.Id;
    private static bool IsImage(StoredMessage message)=>message.Attachment!=null&&
        (message.Mime?.StartsWith("image/",StringComparison.OrdinalIgnoreCase)==true||
        new[]{".png",".jpg",".jpeg",".gif",".bmp",".webp",".heic",".tif",".tiff"}.Contains(Path.GetExtension(message.Text).ToLowerInvariant()));
    private bool CanMarkRead(StoredMessage message)=>IsActive&&(!IsImage(message)||message.Own||revealedImages.Contains(ImageKey(message)));
    private FrameworkElement ImageAttachment(StoredMessage message)
    {
        var panel=new StackPanel{HorizontalAlignment=HorizontalAlignment.Left,MaxWidth=640};
        var placeholder=Button("Tap to reveal · image",()=>{});placeholder.MinWidth=240;placeholder.MinHeight=110;
        placeholder.ToolTip="Reveal this image";
        System.Windows.Automation.AutomationProperties.SetName(placeholder,"Tap to reveal image");
        var preview=new Image{MaxWidth=640,MaxHeight=420,Stretch=Stretch.Uniform,Cursor=Cursors.Hand,Visibility=Visibility.Collapsed};
        var hide=Button("hide image",()=>{});hide.Visibility=Visibility.Collapsed;
        panel.Children.Add(placeholder);panel.Children.Add(preview);panel.Children.Add(hide);
        void Reveal()
        {
            try
            {
                byte[] bytes=store.ReadMedia(message.Attachment!);
                using var stream=new MemoryStream(bytes);
                var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                var frame=decoder.Frames[0];int width=frame.PixelWidth,height=frame.PixelHeight;
                if(width<1||height<1||width>32768||height>32768||(long)width*height>100000000)throw new InvalidDataException("Image dimensions exceed the preview limit");
                double scale=Math.Min(1,1024d/Math.Max(width,height));stream.Position=0;
                var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth=Math.Max(1,(int)(width*scale));bitmap.DecodePixelHeight=Math.Max(1,(int)(height*scale));bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
                preview.Source=bitmap;preview.Visibility=Visibility.Visible;placeholder.Visibility=Visibility.Collapsed;hide.Visibility=Visibility.Visible;
                revealedImages.Add(ImageKey(message));
                if(!message.Own&&message.Status!="read"&&CanMarkRead(message)&&message.Conversation.StartsWith("mesh:"))_=Guard(()=>controller.MarkMeshRead(message));
            }
            catch(Exception e){status.Text="Image preview unavailable: "+e.Message+". You can still save the attachment.";placeholder.Content="Preview unavailable · save attachment below";}
        }
        placeholder.Click+=(_,_)=>Reveal();
        hide.Click+=(_,_)=>{revealedImages.Remove(ImageKey(message));preview.Source=null;preview.Visibility=Visibility.Collapsed;hide.Visibility=Visibility.Collapsed;placeholder.Visibility=Visibility.Visible;placeholder.Content="Tap to reveal · image";};
        preview.MouseLeftButtonUp+=(_,_)=>
        {
            OpenMediaViewer(message);
        };
        if(message.Own||revealedImages.Contains(ImageKey(message)))Reveal();
        return panel;
    }
    internal void VerifyImageReveal()
    {
        byte[] content=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGOwWZCHFTEMLQkAMChSgdtUpbYAAAAASUVORK5CYII=");
        var media=new MediaPacket("diagnostic.png","image/png",content);
        var message=new StoredMessage("mesh",Guid.NewGuid().ToString(),"image diagnostic","diagnostic.png",Bytes.Now,Attachment:store.SaveMedia(media),Mime:media.Mime);
        var panel=(StackPanel)ImageAttachment(message);var reveal=(Button)panel.Children[0];var preview=(Image)panel.Children[1];var hide=(Button)panel.Children[2];
        if(preview.Source!=null||preview.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Incoming image was exposed before reveal");
        reveal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if(preview.Source is not BitmapSource bitmap||bitmap.PixelWidth!=8||preview.Visibility!=Visibility.Visible)throw new InvalidOperationException("Encrypted received image did not reveal");
        hide.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if(preview.Source!=null||preview.Visibility!=Visibility.Collapsed||revealedImages.Contains(ImageKey(message)))throw new InvalidOperationException("Image did not hide again");
        messages.Children.Add(panel);
    }
    internal void VerifyComposerKeyHandling()
    {
        input.Text="/help";int before=messages.Children.Count;
        var source=PresentationSource.FromVisual(this)??throw new InvalidOperationException("Composer needs a visible window");
        input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        if(input.Text.Length!=0||messages.Children.Count<=before)throw new InvalidOperationException("Enter did not submit the composer command");
    }
}

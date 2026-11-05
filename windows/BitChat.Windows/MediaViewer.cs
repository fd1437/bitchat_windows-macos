using System.IO;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BitChat.Windows;

public sealed partial class MainWindow
{
    private void OpenMediaViewer(StoredMessage selected)
    {
        var files=store.State.Messages.Where(message=>message.Conversation==selected.Conversation&&message.Attachment!=null&&!message.LocalAttachmentDeleted).OrderBy(message=>message.Timestamp).ToArray();
        if(files.Length==0)return;
        int index=Array.FindIndex(files,item=>item.Id==selected.Id);if(index<0)index=0;double zoom=1;BitmapSource? bitmap=null;
        var viewer=new Window{Owner=this,Title="BitChat · Media viewer",Width=1060,Height=790,MinWidth=640,MinHeight=480,Background=bg,Foreground=Foreground,FontFamily=FontFamily,WindowStartupLocation=WindowStartupLocation.CenterOwner};viewer.Resources[typeof(Button)]=CreateButtonStyle(store.State.HackMode);
        var root=new DockPanel{Margin=new Thickness(16)};viewer.Content=root;
        var toolbar=new WrapPanel{Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        var info=new TextBlock{Foreground=muted,Margin=new Thickness(4,10,4,4),TextTrimming=TextTrimming.CharacterEllipsis};DockPanel.SetDock(info,Dock.Bottom);root.Children.Add(info);
        var image=new Image{Stretch=Stretch.None,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};var empty=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=Foreground,FontSize=19,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var stage=new Grid();stage.Children.Add(image);stage.Children.Add(empty);var scrollViewer=new ScrollViewer{Content=stage,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=panel};root.Children.Add(scrollViewer);
        var zoomLabel=new TextBlock{Width=58,TextAlignment=TextAlignment.Center,Foreground=muted,VerticalAlignment=VerticalAlignment.Center};
        void ApplyZoom(double value){zoom=Math.Clamp(value,.1,8);if(bitmap!=null){image.Width=bitmap.PixelWidth*zoom;image.Height=bitmap.PixelHeight*zoom;}zoomLabel.Text=$"{zoom:P0}";}
        void Fit(){if(bitmap!=null)ApplyZoom(Math.Min(1,Math.Min(Math.Max(100,viewer.ActualWidth-64)/bitmap.PixelWidth,Math.Max(100,viewer.ActualHeight-160)/bitmap.PixelHeight)));}
        void Load()
        {
            var message=files[index];viewer.Title=message.Text+" · BitChat";info.Text=$"{index+1} / {files.Length}  ·  {message.Sender}  ·  {message.Text}";bitmap=null;image.Source=null;image.Visibility=Visibility.Collapsed;empty.Visibility=Visibility.Visible;
            try
            {
                if(IsImage(message))
                {
                    using var stream=new MemoryStream(store.ReadMedia(message.Attachment!));var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var frame=decoder.Frames[0];
                    if(frame.PixelWidth<1||frame.PixelHeight<1||(long)frame.PixelWidth*frame.PixelHeight>100000000)throw new InvalidDataException("Image dimensions exceed the viewer limit");
                    stream.Position=0;var decoded=new BitmapImage();decoded.BeginInit();decoded.CacheOption=BitmapCacheOption.OnLoad;decoded.DecodePixelWidth=Math.Min(4096,frame.PixelWidth);decoded.StreamSource=stream;decoded.EndInit();decoded.Freeze();bitmap=decoded;image.Source=bitmap;image.Visibility=Visibility.Visible;empty.Visibility=Visibility.Collapsed;Fit();
                    revealedImages.Add(ImageKey(message));if(CanMarkRead(message)&&message.Conversation.StartsWith("mesh:")&&!message.Own)_=Guard(()=>controller.MarkMeshRead(message));
                }
                else empty.Text="▤  "+message.Text+"\n\nUse Save or Copy file to open it in another application.";
            }
            catch(Exception error){empty.Text="Preview unavailable\n"+error.Message;}
        }
        void Move(int step){index=(index+step+files.Length)%files.Length;Load();}
        void Copy()
        {
            try
            {
                if(bitmap!=null)Clipboard.SetImage(bitmap);
                else
                {
                    string directory=Path.Combine(StateStore.Root,"clipboard",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,Path.GetFileName(files[index].Text));File.WriteAllBytes(path,store.ReadMedia(files[index].Attachment!));Clipboard.SetFileDropList(new StringCollection{path});
                }
                info.Text="Copied · "+files[index].Text;
            }
            catch(Exception error){info.Text="Copy failed: "+error.Message;}
        }
        toolbar.Children.Add(Button("← Previous",()=>Move(-1)));toolbar.Children.Add(Button("Next →",()=>Move(1)));toolbar.Children.Add(Button("−",()=>ApplyZoom(zoom/1.25)));toolbar.Children.Add(zoomLabel);toolbar.Children.Add(Button("＋",()=>ApplyZoom(zoom*1.25)));toolbar.Children.Add(Button("Fit",Fit));toolbar.Children.Add(Button("Copy",Copy));toolbar.Children.Add(Button("Save as…",()=>ExportAttachment(files[index])));toolbar.Children.Add(Button("Close",viewer.Close));
        viewer.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Left){Move(-1);e.Handled=true;}else if(e.Key==Key.Right){Move(1);e.Handled=true;}else if(e.Key==Key.Escape)viewer.Close();else if(e.Key==Key.C&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){Copy();e.Handled=true;}else if(e.Key is Key.Add or Key.OemPlus)ApplyZoom(zoom*1.25);else if(e.Key is Key.Subtract or Key.OemMinus)ApplyZoom(zoom/1.25);};
        image.PreviewMouseWheel+=(_,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)!=0){ApplyZoom(zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true;}};
        viewer.Loaded+=(_,_)=>Load();viewer.SizeChanged+=(_,_)=>{if(bitmap!=null&&zoom<=1)Fit();};viewer.ShowDialog();
    }
}

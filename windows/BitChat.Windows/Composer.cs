using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BitChat.Core;
using Microsoft.Win32;

namespace BitChat.Windows;

public sealed partial class MainWindow
{
    private sealed record DraftAttachment(string Conversation,MediaPacket Media);
    private readonly List<DraftAttachment> draftAttachments=[];
    private readonly WrapPanel draftPanel=new(){Margin=new Thickness(6,4,6,0)};
    private Button? recordButton;
    private Border CreateComposer()
    {
        var content=new StackPanel();content.Children.Add(draftPanel);
        input.Background=Brushes.Transparent;input.Foreground=Foreground;input.CaretBrush=green;input.BorderThickness=new Thickness(0);input.Padding=new Thickness(14,12,14,12);input.MinHeight=56;
        input.ToolTip="Write a message · Shift+Enter for a new line · Ctrl+V to attach files or an image";
        input.TextChanged+=(_,_)=>ComposerChanged();
        input.PreviewKeyDown+=async(_,e)=>
        {
            if(e.Key==Key.V&&(Keyboard.Modifiers&ModifierKeys.Control)!=0&&TryPasteAttachments()){e.Handled=true;return;}
            if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Shift)==0){e.Handled=true;await Send();}
        };
        DataObject.AddPastingHandler(input,(_,e)=>{if(e.DataObject.GetDataPresent(DataFormats.FileDrop)||e.DataObject.GetDataPresent(DataFormats.Bitmap)){e.CancelCommand();TryPasteAttachments();}});
        input.AllowDrop=true;input.PreviewDragOver+=(_,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effects=DragDropEffects.Copy;e.Handled=true;}};
        input.PreviewDrop+=(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] paths){e.Handled=true;_=Guard(()=>StageFiles(paths));}};
        var textArea=new Grid();textArea.Children.Add(input);var placeholder=new TextBlock{Text="Write a message, paste files, or record a voice note…",Foreground=muted,Margin=new Thickness(14,13,14,0),IsHitTestVisible=false};textArea.Children.Add(placeholder);input.TextChanged+=(_,_)=>placeholder.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;content.Children.Add(textArea);
        var toolbar=new DockPanel{Margin=new Thickness(6,0,6,6)};
        var send=Button("Send  ↵",async()=>await Send());send.MinWidth=92;send.Background=green;send.Foreground=Brushes.White;DockPanel.SetDock(send,Dock.Right);toolbar.Children.Add(send);
        var tools=new StackPanel{Orientation=Orientation.Horizontal};
        var attach=Button("＋  Files",async()=>await Attach());attach.ToolTip="Add files to the draft";tools.Children.Add(attach);
        recordButton=Button("●  Record",async()=>await Guard(async()=>{if(controller.IsRecording)await controller.EndVoice(false);else await controller.BeginVoice(conversation,false);recordButton!.Content=controller.IsRecording?"■  Stop":"●  Record";}));tools.Children.Add(recordButton);
        var ptt=Button("Hold to talk",()=>{});ptt.ToolTip="Live voice · hold to broadcast immediately";
        ptt.PreviewMouseLeftButtonDown+=async(_,e)=>{e.Handled=true;ptt.CaptureMouse();await Guard(()=>controller.BeginVoice(conversation,true));};
        ptt.PreviewMouseLeftButtonUp+=async(_,e)=>{e.Handled=true;ptt.ReleaseMouseCapture();await Guard(()=>controller.EndVoice(false));};tools.Children.Add(ptt);
        toolbar.Children.Add(tools);content.Children.Add(toolbar);
        return new Border{Background=panel,CornerRadius=new CornerRadius(store.State.HackMode?0:14),BorderBrush=new SolidColorBrush(Color.FromArgb(50,128,128,128)),BorderThickness=new Thickness(1),Child=content,Margin=new Thickness(0,12,0,0)};
    }
    private async Task Attach()
    {
        var picker=new OpenFileDialog{Title="Add files to your message (up to 1 MiB each)",Multiselect=true,CheckFileExists=true};
        if(picker.ShowDialog(this)==true)await Guard(()=>StageFiles(picker.FileNames));
    }
    private async Task StageFiles(IEnumerable<string> paths)
    {
        string target=conversation;
        foreach(string path in paths)
        {
            if(Directory.Exists(path)){status.Text="Choose files rather than folders";continue;}
            try{AddDraft(target,await AppController.LoadAttachment(path));}catch(Exception error){status.Text=Path.GetFileName(path)+": "+error.Message;}
        }
    }
    private void AddDraft(string target,MediaPacket media)
    {
        if(media.Content.Length>MediaPacket.MaxBytes)throw new InvalidDataException("Each attachment must be at most 1 MiB");
        if(draftAttachments.Count>=16||draftAttachments.Sum(item=>item.Media.Content.Length)+media.Content.Length>8*1024*1024)throw new InvalidDataException("Draft limit: 16 files / 8 MiB");
        draftAttachments.Add(new(target,media));RefreshDraft();
    }
    private void RefreshDraft()
    {
        draftPanel.Children.Clear();
        foreach(var item in draftAttachments.Where(item=>item.Conversation==conversation).ToArray())
        {
            var row=new DockPanel{Width=220};
            var remove=Button("×",()=>{draftAttachments.Remove(item);RefreshDraft();});remove.MinWidth=28;DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
            if(item.Media.Mime.StartsWith("image/"))
            {
                try{using var stream=new MemoryStream(item.Media.Content);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=48;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();var image=new Image{Source=bitmap,Width=42,Height=42,Margin=new Thickness(0,0,8,0),Stretch=Stretch.UniformToFill};DockPanel.SetDock(image,Dock.Left);row.Children.Add(image);}catch{}
            }
            row.Children.Add(new TextBlock{Text=item.Media.Name+"\n"+(item.Media.Content.Length/1024d).ToString("0.#")+" KB · ready to send",TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Foreground=Foreground,FontSize=11});
            draftPanel.Children.Add(new Border{Child=row,Background=bg,CornerRadius=new CornerRadius(8),Padding=new Thickness(8),Margin=new Thickness(4),BorderBrush=muted,BorderThickness=new Thickness(.4)});
        }
        draftPanel.Visibility=draftPanel.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
    }
    private bool TryPasteAttachments()
    {
        try
        {
            if(Clipboard.ContainsFileDropList())
            {var paths=Clipboard.GetFileDropList().Cast<string>().ToArray();_=Guard(()=>StageFiles(paths));return true;}
            if(Clipboard.ContainsImage())
            {
                var image=Clipboard.GetImage();if(image==null)return false;
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=new MemoryStream();encoder.Save(stream);
                AddDraft(conversation,new("img_"+Guid.NewGuid().ToString().ToUpperInvariant()+".png","image/png",stream.ToArray()));return true;
            }
        }
        catch(Exception error){status.Text="Paste: "+error.Message;return true;}
        return false;
    }
    private bool sending;
    private async Task Send()
    {
        if(sending)return;sending=true;
        try
        {
            if(controller.IsRecording){await controller.EndVoice(false);if(recordButton!=null)recordButton.Content="●  Record";}
            string target=conversation,original=input.Text,text=original.Trim();var attachments=draftAttachments.Where(item=>item.Conversation==target).ToArray();
            if(text.Length==0&&attachments.Length==0)return;
            if(text.Length>0)
            {
                if(text.StartsWith('/'))await Command(text);else await controller.Send(target,text);
                SendTyping(false);if(conversation==target&&input.Text==original)input.Clear();
            }
            int sent=0;
            foreach(var attachment in attachments)
            {
                await controller.SendPreparedMedia(target,attachment.Media);draftAttachments.Remove(attachment);sent++;RefreshDraft();status.Text=$"Sending files · {sent}/{attachments.Length}";
            }
            if(attachments.Length>0)status.Text=$"Sent {attachments.Length} attachment(s)";
        }
        catch(Exception error){status.Text=error.Message;}
        finally{sending=false;}
    }
    internal void VerifyDraftStaging()
    {
        int before=store.State.Messages.Count;
        AddDraft(conversation,new("draft-test.txt","text/plain",Bytes.Utf8("not sent yet")));
        if(store.State.Messages.Count!=before||draftPanel.Children.Count==0)throw new InvalidOperationException("Draft attachment was sent before Send");
        draftAttachments.Clear();RefreshDraft();
        if(draftPanel.Children.Count!=0)throw new InvalidOperationException("Draft did not clear");
    }
    internal void PrepareVisualDiagnostics()
    {
        store.State.Messages.Add(new("mesh","visual-incoming","Camille","Hey! Everything is ready for the weekend. Share the photos here when you arrive.",Bytes.Now-120000));
        store.State.Messages.Add(new("mesh","visual-outgoing",store.State.Nickname,"Perfect, I’ll send the photos and a voice note together.",Bytes.Now-60000,true,"delivered"));
        Select("mesh");AddDraft("mesh",new("Weekend notes.txt","text/plain",Bytes.Utf8("Ready for the weekend")));status.Text="Connected · your files stay in the draft until you press Send";
    }
    internal void SetDiagnosticHackMode(){store.State.HackMode=true;ApplyThemeColors();Select(conversation);}

}

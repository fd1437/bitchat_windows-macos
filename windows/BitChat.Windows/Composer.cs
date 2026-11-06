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
    private sealed class FileTransfer(DraftAttachment attachment)
    {
        public DraftAttachment Attachment=attachment;public CancellationTokenSource Stop=new();
        public string State="queued";public int Percent;public bool Finished;
    }
    private readonly List<FileTransfer> transfers=[];
    private readonly CorrespondentSendQueue sendQueue=new();
    private readonly StackPanel transferRows=new();
    private readonly ScrollViewer transferPanel=new(){MaxHeight=165,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed};
    private readonly Dictionary<DraftAttachment,string> draftStates=[];
    private readonly Dictionary<DraftAttachment,TextBlock> draftLabels=[];
    private StackPanel? composerTools;
    private Grid? composerTextArea;
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
        var textArea=new Grid();composerTextArea=textArea;textArea.Children.Add(input);var placeholder=new TextBlock{Text="Write a message, paste files, or record a voice note…",Foreground=muted,Margin=new Thickness(14,13,14,0),IsHitTestVisible=false};textArea.Children.Add(placeholder);input.TextChanged+=(_,_)=>placeholder.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;content.Children.Add(textArea);
        var toolbar=new DockPanel{Margin=new Thickness(6,0,6,6)};
        var send=Button("Send  ↵",async()=>await Send());send.MinWidth=92;send.Background=green;send.Foreground=Brushes.White;DockPanel.SetDock(send,Dock.Right);toolbar.Children.Add(send);
        var tools=new StackPanel{Orientation=Orientation.Horizontal};composerTools=tools;
        var attach=Button("＋  Files",async()=>await Attach());attach.ToolTip="Add files to the draft";tools.Children.Add(attach);
        recordButton=Button("●  Record",async()=>await Guard(async()=>{if(controller.IsRecording)await controller.EndVoice(false);else await controller.BeginVoice(conversation,false);recordButton!.Content=controller.IsRecording?"■  Stop":"●  Record";}));tools.Children.Add(recordButton);
        var ptt=Button("Hold to talk",()=>{});ptt.ToolTip="Live voice · hold to broadcast immediately";
        ptt.PreviewMouseLeftButtonDown+=async(_,e)=>{e.Handled=true;ptt.CaptureMouse();await Guard(()=>controller.BeginVoice(conversation,true));};
        ptt.PreviewMouseLeftButtonUp+=async(_,e)=>{e.Handled=true;ptt.ReleaseMouseCapture();await Guard(()=>controller.EndVoice(false));};tools.Children.Add(ptt);
        toolbar.Children.Add(tools);content.Children.Add(toolbar);
        var container=new Border{Background=panel,CornerRadius=new CornerRadius(store.State.HackMode?0:14),BorderBrush=new SolidColorBrush(Color.FromArgb(50,128,128,128)),BorderThickness=new Thickness(1),Child=content,Margin=new Thickness(0,12,0,0)};
        container.AllowDrop=true;container.PreviewDragOver+=(_,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effects=DragDropEffects.Copy;e.Handled=true;}};
        container.PreviewDrop+=(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] paths){e.Handled=true;_=Guard(()=>StageFiles(paths));}};
        return container;
    }
    private async Task Attach()
    {
        var picker=new OpenFileDialog{Title="Add files to your message (up to 1 MiB each)",Multiselect=true,CheckFileExists=true};
        if(picker.ShowDialog(this)==true)await Guard(()=>StageFiles(picker.FileNames));
    }
    private async Task StageFiles(IEnumerable<string> paths)
    {
        string target=conversation;int added=0;var errors=new List<string>();
        foreach(string path in paths)
        {
            if(Directory.Exists(path)){errors.Add("Choose files rather than folders");continue;}
            try{AddDraft(target,await AppController.LoadAttachment(path));added++;}catch(Exception error){errors.Add(Path.GetFileName(path)+": "+error.Message);}
        }
        status.Text=$"Added {added} file(s)"+(errors.Count>0?$" · {errors.Count} rejected · {errors[0]}":" · press Send when ready");
    }
    private void AddDraft(string target,MediaPacket media)
    {
        if(media.Content.Length>MediaPacket.MaxBytes)throw new InvalidDataException("Each attachment must be at most 1 MiB");
        if(store.State.FilesOnly&&!IsPrivateConversation(target))throw new InvalidOperationException("Choose a Bluetooth correspondent first");
        if(draftAttachments.Count>=16||draftAttachments.Sum(item=>item.Media.Content.Length)+media.Content.Length>8*1024*1024)throw new InvalidDataException("Draft limit: 16 files / 8 MiB");
        draftAttachments.Add(new(target,media));RefreshDraft();
    }
    private void RefreshDraft()
    {
        draftPanel.Children.Clear();draftLabels.Clear();
        foreach(var item in draftAttachments.Where(item=>item.Conversation==conversation).ToArray())
        {
            var row=new DockPanel{Width=220};
            var remove=Button("×",()=>{draftAttachments.Remove(item);draftStates.Remove(item);RefreshDraft();});remove.MinWidth=28;DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
            if(item.Media.Mime.StartsWith("image/"))
            {
                try{using var stream=new MemoryStream(item.Media.Content);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=48;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();var image=new Image{Source=bitmap,Width=42,Height=42,Margin=new Thickness(0,0,8,0),Stretch=Stretch.UniformToFill};DockPanel.SetDock(image,Dock.Left);row.Children.Add(image);}catch{}
            }
            var label=new TextBlock{Text=DraftLabel(item),TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Foreground=Foreground,FontSize=11};draftLabels[item]=label;row.Children.Add(label);
            draftPanel.Children.Add(new Border{Child=row,Background=bg,CornerRadius=new CornerRadius(8),Padding=new Thickness(8),Margin=new Thickness(4),BorderBrush=muted,BorderThickness=new Thickness(.4)});
        }
        draftPanel.Visibility=draftPanel.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
    }
    private string DraftLabel(DraftAttachment item)=>item.Media.Name+"\n"+(item.Media.Content.Length/1024d).ToString("0.#")+" KB · "+draftStates.GetValueOrDefault(item,"ready to send");
    private void RefreshTransfers()
    {
        transferRows.Children.Clear();transferPanel.Content=transferRows;
        foreach(var item in transfers.Where(t=>t.Attachment.Conversation==conversation).ToArray())
        {
            var row=new DockPanel{Margin=new Thickness(4)};
            if(item.Finished&&item.State.StartsWith("failed"))
            {var retry=Button("Retry",()=>{item.Stop.Dispose();item.Stop=new();item.State="queued";item.Percent=0;item.Finished=false;_=RunTransfer(item);RefreshTransfers();});DockPanel.SetDock(retry,Dock.Right);row.Children.Add(retry);}
            var action=Button(item.Finished?"Remove":"Cancel",()=>
            {
                if(item.Finished){transfers.Remove(item);item.Stop.Dispose();}
                else{item.Stop.Cancel();item.State=item.State=="queued"?"canceled · this queued file will not be sent":"canceling after current Bluetooth frame…";}
                RefreshTransfers();
            });DockPanel.SetDock(action,Dock.Right);row.Children.Add(action);
            var details=new StackPanel();details.Children.Add(new TextBlock{Text=item.Attachment.Media.Name+" · "+item.State,Foreground=Foreground,TextTrimming=TextTrimming.CharacterEllipsis});
            details.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=item.Percent,Height=4,Margin=new Thickness(0,5,8,0)});row.Children.Add(details);transferRows.Children.Add(row);
        }
        transferPanel.Visibility=transferRows.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
    }
    private void ShowFileProgress(AppController.FileSendProgress progress)
    {
        if(receiptLabels.TryGetValue(progress.Id,out var receipt)&&store.State.Messages.Any(m=>m.Id==progress.Id&&m.Own&&m.Status=="sending"))receipt.Text=$"Sending {progress.Percent}%";
        var item=transfers.FirstOrDefault(t=>!t.Finished&&t.State.StartsWith("sending")&&t.Attachment.Conversation==progress.Conversation&&t.Attachment.Media.Name==progress.Name);
        if(item==null||item.Stop.IsCancellationRequested)return;item.Percent=progress.Percent;item.State=$"sending {progress.Percent}%";RefreshTransfers();
    }
    private void CancelTransfers(string? target=null)
    {
        foreach(var item in transfers.Where(t=>target==null||t.Attachment.Conversation==target))item.Stop.Cancel();
        draftAttachments.RemoveAll(draft=>target==null||draft.Conversation==target);RefreshDraft();RefreshTransfers();
    }
    private async Task RunTransfer(FileTransfer item)
    {
        var sender=controller;
        try
        {
            await sendQueue.Enqueue(item.Attachment.Conversation,async cancellation=>
            {item.State="sending 0%";RefreshTransfers();await sender.SendPreparedMedia(item.Attachment.Conversation,item.Attachment.Media,cancellation);},item.Stop.Token);
            item.Percent=100;item.State="transmitted · see delivery receipt";
        }
        catch(OperationCanceledException) when(item.Stop.IsCancellationRequested){item.State="canceled · received data cannot be recalled";}
        catch(Exception error){item.State="failed · "+error.Message;}
        finally{item.Finished=true;RefreshTransfers();}
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
    private bool preparingSend;
    private async Task Send()
    {
        if(preparingSend)return;preparingSend=true;
        try
        {
            string target=conversation;
            if(store.State.FilesOnly&&!IsPrivateConversation(target)){status.Text="Choose a correspondent to send files";return;}
            if(controller.IsRecording){await controller.EndVoice(false);if(recordButton!=null)recordButton.Content="●  Record";}
            string original=input.Text,text=store.State.FilesOnly?"":original.Trim();
            var attachments=draftAttachments.Where(item=>item.Conversation==target).ToArray();
            if(text.Length==0&&attachments.Length==0)return;
            if(transfers.Count(t=>!t.Finished)+attachments.Length>32||transfers.Sum(t=>t.Attachment.Media.Content.Length)+attachments.Sum(t=>t.Media.Content.Length)>16*1024*1024)
            {status.Text="Transfer limit reached · remove completed transfers first";return;}
            // Snapshot and detach the accepted batch before any radio I/O.
            foreach(var attachment in attachments)
            {draftAttachments.Remove(attachment);draftStates.Remove(attachment);var item=new FileTransfer(attachment);transfers.Add(item);_=RunTransfer(item);}
            RefreshDraft();RefreshTransfers();
            if(text.Length>0)
            {
                SendTyping(false);if(conversation==target&&input.Text==original)input.Clear();
                _=SendCapturedText(target,text);
            }
        }
        catch(Exception error){status.Text=error.Message;}
        finally{preparingSend=false;}
    }
    private async Task SendCapturedText(string target,string text)
    {
        try{if(text.StartsWith('/'))await Command(text);else await controller.Send(target,text);}
        catch(Exception error){status.Text="Message failed: "+error.Message;}
    }
    internal void VerifyDraftStaging()
    {
        int before=store.State.Messages.Count;
        AddDraft(conversation,new("draft-test.txt","text/plain",Bytes.Utf8("not sent yet")));
        if(store.State.Messages.Count!=before||draftPanel.Children.Count==0)throw new InvalidOperationException("Draft attachment was sent before Send");
        draftAttachments.Clear();RefreshDraft();
        if(draftPanel.Children.Count!=0)throw new InvalidOperationException("Draft did not clear");
    }
    internal void VerifyFileProgress()
    {
        var attachment=new DraftAttachment(conversation,new("batch-test.txt","text/plain",Bytes.Utf8("synthetic transfer")));
        var item=new FileTransfer(attachment){State="sending 0%"};transfers.Add(item);
        ShowFileProgress(new(conversation,"diagnostic-progress",attachment.Media.Name,50));
        if(item.Percent!=50||!item.State.Contains("50%")||transferRows.Children.Count!=1||draftAttachments.Contains(attachment))throw new InvalidOperationException("Transfer progress must be outside the draft");
        item.Stop.Cancel();ShowFileProgress(new(conversation,"diagnostic-progress",attachment.Media.Name,90));
        if(item.Percent!=50)throw new InvalidOperationException("Canceled transfer accepted late progress");
        transfers.Remove(item);item.Stop.Dispose();RefreshTransfers();
        bool previous=store.State.FilesOnly;store.State.FilesOnly=true;ApplyFilesOnlyMode();
        if(input.IsEnabled||composerTools?.Children.Cast<UIElement>().Skip(1).Any(child=>child.IsEnabled)==true)throw new InvalidOperationException("Files-only mode exposes chat or voice controls");
        store.State.FilesOnly=previous;ApplyFilesOnlyMode();
    }
    internal async Task VerifyTransferSnapshot()
    {
        int before=store.State.Messages.Count;
        AddDraft(conversation,new("snapshot-a.txt","text/plain",Bytes.Utf8("synthetic A")));
        AddDraft(conversation,new("snapshot-b.txt","text/plain",Bytes.Utf8("synthetic B")));
        await Send();
        if(draftAttachments.Count!=0||transfers.Count!=2||transferRows.Children.Count!=2)throw new InvalidOperationException("Send must detach the whole selected batch before radio I/O");
        CancelTransfers();
        long until=Bytes.Now+2000;while(transfers.Any(item=>!item.Finished)&&Bytes.Now<until)await Task.Delay(10);
        if(transfers.Any(item=>!item.Finished)||store.State.Messages.Count!=before)throw new InvalidOperationException("Canceled queued files were sent or left blocked");
        foreach(var item in transfers)item.Stop.Dispose();transfers.Clear();RefreshTransfers();
    }
    internal void PrepareVisualDiagnostics()
    {
        store.State.Messages.Add(new("mesh","visual-incoming","Camille","Hey! Everything is ready for the weekend. Share the photos here when you arrive.",Bytes.Now-120000));
        store.State.Messages.Add(new("mesh","visual-outgoing",store.State.Nickname,"Perfect, I’ll send the photos and a voice note together.",Bytes.Now-60000,true,"delivered"));
        Select("mesh");AddDraft("mesh",new("Weekend notes.txt","text/plain",Bytes.Utf8("Ready for the weekend")));status.Text="Connected · your files stay in the draft until you press Send";
    }
    internal void SetDiagnosticHackMode(){store.State.HackMode=true;ApplyThemeColors();Select(conversation);}

}

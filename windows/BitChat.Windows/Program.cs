using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shell;
using Microsoft.Win32;
using BitChat.Core;

namespace BitChat.Windows;

public static class Program
{
    [STAThread]public static void Main(string[] args)
    {
        try
        {
            var app=new Application();
            if(args.Contains("--unregister-notifications")){NativeNotifications.Uninstall();return;}
            if(args.Length>=2&&args[0]=="--self-test"){Task.Run(()=>Diagnostics.Run(args[1])).GetAwaiter().GetResult();Diagnostics.RenderUi(Path.ChangeExtension(args[1],".png")).GetAwaiter().GetResult();return;}
            app.DispatcherUnhandledException+=(_,e)=>{MessageBox.Show(e.Exception.Message,"BitChat",MessageBoxButton.OK,MessageBoxImage.Error);e.Handled=true;};
            using var instance=new Mutex(true,@"Local\BitChat.Windows.Instance",out bool first);
            using var activate=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\BitChat.Windows.Activate");
            using var updateLatest=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\BitChat.Windows.InstallLatest");
            bool toastActivated=false;try{toastActivated=NativeNotifications.WasActivated;}catch{}
            bool background=args.Contains("--background")||toastActivated;
            if(!first&&toastActivated){NativeNotifications.ForwardToRunningInstance(app);app.Run();return;}
            if(!first){if(args.Contains("--install-latest"))updateLatest.Set();if(!background)activate.Set();return;}
            app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var store=new StateStore();var window=new MainWindow(store,false,background,args.Contains("--install-latest"));
            try{NativeNotifications.Initialize(action=>window.Dispatcher.BeginInvoke(new Action(()=>_=window.HandleNativeNotification(action))));}catch{}
            app.Exit+=(_,_)=>NativeNotifications.Stop();
            var activationTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
            activationTimer.Tick+=(_,_)=>{if(activate.WaitOne(0))window.RestoreFromTray();if(updateLatest.WaitOne(0))_=window.RequestUpdateInstall();};activationTimer.Start();
            app.SessionEnding+=(_,_)=>window.EndWindowsSession();
            app.Run(window);activationTimer.Stop();
        }
        catch(Exception e){if(args.Length>=2&&args[0]=="--self-test"){File.WriteAllText(args[1]+".error.txt",e.ToString());Environment.ExitCode=1;return;}MessageBox.Show("BitChat could not start:\n"+e.Message,"BitChat",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
}

public sealed partial class MainWindow:Window
{
    private readonly StateStore store;
    private AppController controller;
    private readonly StackPanel channels=new(),messages=new();
    private readonly ScrollViewer scroll;
    private readonly TextBox input=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=130,MinHeight=44,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock title=new(){FontSize=20,FontWeight=FontWeights.SemiBold},status=new(){FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis},network=new(){FontSize=11};
    private readonly Dictionary<string,TextBlock> receiptLabels=[];
    private string conversation="mesh";
    private readonly SolidColorBrush bg=new(Color.FromRgb(13,17,20)),panel=new(Color.FromRgb(20,26,30)),green=new(Color.FromRgb(99,230,153)),muted=new(Color.FromRgb(151,166,175));
    private Button? filesButton,updateButton,maximizeButton;
    private ProgressBar? updateProgress;
    private readonly List<Button> chromeButtons=[];
    private readonly TextBlock typingLabel=new(){FontSize=11};
    private readonly Dictionary<string,long> typingPeers=[];
    private readonly System.Windows.Threading.DispatcherTimer typingTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly System.Windows.Threading.DispatcherTimer typingStopTimer=new(){Interval=TimeSpan.FromSeconds(2)};
    private readonly System.Windows.Threading.DispatcherTimer updateCheckTimer=new(){Interval=TimeSpan.FromSeconds(10)};
    private ReleaseUpdater? updater;
    private AvailableUpdate? availableUpdate;
    private bool updating;
    private readonly TaskCompletionSource<bool> controllerReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool installLatestOnLaunch;
    private long lastPanic;private int panicTaps;private bool closing;
    private readonly System.Windows.Forms.NotifyIcon tray=new(){Text="BitChat",Visible=true};
    public MainWindow(StateStore store,bool diagnostics=false,bool background=false,bool installLatest=false)
    {
        tray.Visible=!diagnostics;
        this.store=store;installLatestOnLaunch=installLatest;controller=new(store);Title="bitchat · Windows";Width=1120;Height=780;MinWidth=780;MinHeight=520;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;Background=bg;FontFamily=new FontFamily("Cascadia Mono, Consolas");FontSize=13;ApplyThemeColors();
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=38,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false});
        SourceInitialized+=(_,_)=>WindowChromeWorkAreaHook.Attach(this);
        var root=new Grid{Background=bg};root.RowDefinitions.Add(new(){Height=new GridLength(38)});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.ColumnDefinitions.Add(new(){Width=new GridLength(store.State.HackMode?240:276)});root.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Content=root;
        var titleBar=new DockPanel{Background=panel,LastChildFill=true};Grid.SetRow(titleBar,0);Grid.SetColumnSpan(titleBar,2);root.Children.Add(titleBar);
        var windowButtons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        Button ChromeButton(string label,Action click){var b=new Button{Content=label,Width=44,Height=34,Background=Brushes.Transparent,Foreground=Foreground,BorderThickness=new Thickness(0),FontSize=15,Cursor=Cursors.Hand};WindowChrome.SetIsHitTestVisibleInChrome(b,true);b.Click+=(_,_)=>click();windowButtons.Children.Add(b);chromeButtons.Add(b);return b;}
        ChromeButton("—",()=>WindowState=WindowState.Minimized);maximizeButton=ChromeButton("□",()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized);ChromeButton("×",()=>Hide());
        StateChanged+=(_,_)=>{if(maximizeButton!=null)maximizeButton.Content=WindowState==WindowState.Maximized?"❐":"□";};DockPanel.SetDock(windowButtons,Dock.Right);titleBar.Children.Add(windowButtons);
        titleBar.Children.Add(new TextBlock{Text="  ◉  BitChat",Foreground=green,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)});
        var sidebar=new DockPanel{Background=panel,Margin=new Thickness(0,0,1,0)};Grid.SetRow(sidebar,1);root.Children.Add(sidebar);
        var brand=new Button{Content="bitchat",Foreground=green,Background=Brushes.Transparent,BorderThickness=new Thickness(0),FontSize=25,FontWeight=FontWeights.SemiBold,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(20)};
        brand.ToolTip="Triple-click to wipe local identities and messages";brand.Click+=async(_,_)=>await PanicTap();DockPanel.SetDock(brand,Dock.Top);sidebar.Children.Add(brand);
        var footer=new StackPanel{Margin=new Thickness(16)};network.Foreground=muted;network.Text="connecting…";footer.Children.Add(network);footer.Children.Add(Button("settings / identity",()=>ShowSettings()));updateButton=Button("● update available",()=>_=InstallUpdate());updateButton.Visibility=Visibility.Collapsed;footer.Children.Add(updateButton);updateProgress=new ProgressBar{Minimum=0,Maximum=100,Height=5,Margin=new Thickness(4,3,4,6),Foreground=green,Visibility=Visibility.Collapsed};footer.Children.Add(updateProgress);DockPanel.SetDock(footer,Dock.Bottom);sidebar.Children.Add(footer);
        sidebar.Children.Add(new ScrollViewer{Content=channels,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var main=new DockPanel{Margin=new Thickness(28,20,28,18)};Grid.SetRow(main,1);Grid.SetColumn(main,1);root.Children.Add(main);
        var heading=new DockPanel{Margin=new Thickness(0,0,0,12)};var controls=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        filesButton=Button("files",ShowConversationFiles);filesButton.ToolTip="Show conversation files and remove selected local copies";controls.Children.Add(filesButton);controls.Children.Add(Button("clear",()=>ClearChat()));controls.Children.Add(Button("identity",()=>ShowIdentity()));DockPanel.SetDock(controls,Dock.Right);heading.Children.Add(controls);
        var titleStack=new StackPanel();typingLabel.Foreground=muted;titleStack.Children.Add(title);titleStack.Children.Add(typingLabel);heading.Children.Add(titleStack);DockPanel.SetDock(heading,Dock.Top);main.Children.Add(heading);
        var bottom=new StackPanel();status.Foreground=muted;status.Margin=new Thickness(0,10,0,0);status.Text="/help for commands";bottom.Children.Add(status);DockPanel.SetDock(bottom,Dock.Bottom);main.Children.Add(bottom);
        var composer=CreateComposer();DockPanel.SetDock(composer,Dock.Bottom);main.Children.Add(composer);
        scroll=new ScrollViewer{Content=messages,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};main.Children.Add(scroll);
        try{tray.Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);}catch{}tray.DoubleClick+=(_,_)=>Ui(RestoreFromTray);
        var trayMenu=new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open BitChat",null,(_,_)=>Ui(RestoreFromTray));
        trayMenu.Items.Add("Quit BitChat",null,(_,_)=>Ui(async()=>await Quit()));tray.ContextMenuStrip=trayMenu;
        Deactivated+=(_,_)=>_=controller.StopVoiceAutoplay();typingTimer.Tick+=(_,_)=>RefreshTypingLabel();typingTimer.Start();typingStopTimer.Tick+=(_,_)=>{typingStopTimer.Stop();SendTyping(false);};updateCheckTimer.Tick+=async(_,_)=>await CheckForUpdates();BindController();RefreshChannels();Select("mesh");if(!diagnostics)
        {
            bool started=false;
            Loaded+=async(_,_)=>
            {
                if(started)return;started=true;
                if(background)Hide();
                if(!StartupRegistration.Prompted)
                {
                    bool enabled=MessageBox.Show(this,"Start BitChat automatically when you sign in to Windows? It will run in the notification area. You can change this in Settings.","BitChat startup",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
                    await Guard(()=>{StartupRegistration.Set(enabled);StartupRegistration.Prompted=true;return Task.CompletedTask;});
                }
                await Guard(()=>controller.Start());controllerReady.TrySetResult(true);
                await Guard(async()=>{int removed=await controller.CleanupExpiredMedia();if(removed>0)status.Text=$"Removed {removed} expired local file(s)";});
                ReleaseUpdater.CleanupStaleFiles();updater=new(store);updateCheckTimer.Start();_=CheckForUpdates();if(installLatestOnLaunch)_=RequestUpdateInstall();
            };
        }
        Closing+=(_,e)=>{if(closing)return;e.Cancel=true;Hide();};
    }
    internal async Task HandleNativeNotification(NotificationActivation action)
    {
        if(!store.State.Messages.Any(message=>message.Conversation==action.Conversation))return;
        if(action.Action=="reply"&&!string.IsNullOrWhiteSpace(action.Reply))
        {await controllerReady.Task;await Guard(()=>controller.Send(action.Conversation,action.Reply.Trim()));}
        else{RestoreFromTray();Select(action.Conversation);}
    }
    public void RestoreFromTray(){Show();WindowState=WindowState.Normal;Activate();}
    internal bool ShouldShowMessageNotification(StoredMessage message)
        =>!message.Own&&store.State.Notifications&&(!IsVisible||!IsActive||WindowState==WindowState.Minimized||message.Conversation!=conversation);
    internal void VerifyNotificationVisibilityRouting()
    {
        store.State.Notifications=true;
        var message=new StoredMessage(conversation,"notification-diagnostic","peer","test",Bytes.Now);
        if(IsActive&&ShouldShowMessageNotification(message))throw new InvalidOperationException("An active conversation should not create a notification");
        Hide();
        if(!ShouldShowMessageNotification(message))throw new InvalidOperationException("A hidden BitChat window must still notify about a new message");
        RestoreFromTray();
    }
    internal void VerifyWindowControls()
    {
        if(chromeButtons.Count!=3||chromeButtons.Any(button=>!WindowChrome.GetIsHitTestVisibleInChrome(button)))
            throw new InvalidOperationException("Window controls are not clickable in the title bar");
        chromeButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if(WindowState!=WindowState.Minimized)throw new InvalidOperationException("Minimize button did not minimize the window");
        WindowState=WindowState.Normal;
        maximizeButton!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        UpdateLayout();
        if(WindowState!=WindowState.Maximized)throw new InvalidOperationException("Maximize button did not maximize the window");
        if(!WindowChromeWorkAreaHook.IsWithinMonitorWorkArea(new System.Windows.Interop.WindowInteropHelper(this).Handle))
            throw new InvalidOperationException("Maximized window extends beyond the monitor work area");
        maximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if(WindowState!=WindowState.Normal)throw new InvalidOperationException("Restore button did not restore the window");
        chromeButtons[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if(IsVisible)throw new InvalidOperationException("Close button did not hide BitChat to the notification area");
        RestoreFromTray();
    }
    private async Task Quit()
    {
        if(closing)return;closing=true;
        typingTimer.Stop();typingStopTimer.Stop();updateCheckTimer.Stop();SendTyping(false);updater?.Dispose();updater=null;
        try{await controller.DisposeAsync();}
        finally{tray.Visible=false;tray.Dispose();Application.Current.Shutdown();}
    }
    public void EndWindowsSession(){closing=true;typingTimer.Stop();typingStopTimer.Stop();updateCheckTimer.Stop();tray.Visible=false;tray.Dispose();updater?.Dispose();_=controller.DisposeAsync();}
    public async Task ShutdownDiagnostics(){closing=true;typingTimer.Stop();typingStopTimer.Stop();updateCheckTimer.Stop();updater?.Dispose();await controller.DisposeAsync();tray.Visible=false;tray.Dispose();}
    private void ApplyThemeColors()
    {
        bool dark=store.State.Dark,hack=store.State.HackMode;
        bg.Color=dark?(hack?Color.FromRgb(13,17,20):Color.FromRgb(29,31,36)):Color.FromRgb(248,249,251);
        panel.Color=dark?(hack?Color.FromRgb(20,26,30):Color.FromRgb(36,39,46)):Color.FromRgb(237,239,244);
        green.Color=hack?(dark?Color.FromRgb(99,230,153):Color.FromRgb(0,112,70)):(dark?Color.FromRgb(138,148,255):Color.FromRgb(82,94,214));
        muted.Color=dark?Color.FromRgb(158,165,181):Color.FromRgb(94,103,123);
        Foreground=dark?new SolidColorBrush(Color.FromRgb(232,235,242)):new SolidColorBrush(Color.FromRgb(31,36,48));
        FontFamily=new FontFamily(hack?"Cascadia Mono, Consolas":"Segoe UI Variable, Segoe UI");FontSize=hack?13:14;
        Resources[typeof(Button)]=CreateButtonStyle(hack);
        if(Application.Current!=null)
        {
            Application.Current.Resources[typeof(Button)]=Resources[typeof(Button)];
            var textStyle=new Style(typeof(TextBox));textStyle.Setters.Add(new Setter(Control.BackgroundProperty,panel));textStyle.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));textStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty,green));textStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,8,10,8)));textStyle.Setters.Add(new Setter(Control.BorderBrushProperty,muted));textStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(.5)));Application.Current.Resources[typeof(TextBox)]=textStyle;
        }
        if(Content is Grid layout&&layout.ColumnDefinitions.Count>0)layout.ColumnDefinitions[0].Width=new GridLength(hack?240:276);
        input.Foreground=Foreground;foreach(var button in chromeButtons)button.Foreground=Foreground;
        if(Content is DependencyObject root)RefreshTextBoxes(root);
    }
    private Style CreateButtonStyle(bool hack)
    {
        var style=new Style(typeof(Button));
        var border=new FrameworkElementFactory(typeof(Border));border.Name="surface";border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(hack?0:8));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);
        var template=new ControlTemplate(typeof(Button)){VisualTree=border};
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(UIElement.OpacityProperty,.78));template.Triggers.Add(hover);
        var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.42));template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(Control.TemplateProperty,template));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));return style;
    }
    private void RefreshTextBoxes(DependencyObject node)
    {if(node is TextBox textBox)textBox.Foreground=Foreground;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)RefreshTextBoxes(VisualTreeHelper.GetChild(node,i));}
    private Button Button(string text,Action action)
    {var b=new Button{Content=text,Background=panel,Foreground=green,BorderBrush=new SolidColorBrush(Color.FromRgb(48,63,70)),MinHeight=36,Padding=new Thickness(12,7,12,7),Margin=new Thickness(4),Cursor=Cursors.Hand};b.Click+=(_,_)=>action();return b;}
    private void Ui(Action action){if(Dispatcher.CheckAccess())action();else Dispatcher.BeginInvoke(action);}
    private void BindController()
    {
        controller.VoiceNoteReady+=(target,media)=>Ui(()=>{AddDraft(target,media);if(recordButton!=null)recordButton.Content="●  Record";});
        controller.Dispatch=action=>Ui(action);controller.IsAppActive=()=>IsActive&&WindowState!=WindowState.Minimized;
        controller.ConfirmLegacyMedia=peer=>Task.FromResult(MessageBox.Show(this,"This contact ("+peer+") has not proved encrypted attachment support. Send this attachment without end-to-end encryption? Nearby relays may read it. This approval applies only to this send.","Legacy attachment",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes);
        controller.Status+=s=>Ui(()=>{status.Text=s;network.Text=$"BLE {controller.Bluetooth.Connections} · relays {controller.Relays.Connected}";});
        controller.PeersChanged+=()=>Ui(()=>{RefreshChannels();network.Text=$"BLE {controller.Bluetooth.Connections} · relays {controller.Relays.Connected} · nearby {controller.NearbyPresence}";});controller.TimelineChanged+=()=>Ui(()=>Select(conversation));
        controller.Message+=m=>Ui(()=>{if(ShouldShowMessageNotification(m)){try{if(!NativeNotifications.TryShow(store,m))ShowMessageToast(m);}catch(Exception error){ShowFallbackNotification(m,error);}}if(m.Conversation==conversation){Append(m);scroll.ScrollToEnd();if(CanMarkRead(m)&&!m.Own&&m.Conversation.StartsWith("mesh:"))_=controller.MarkMeshRead(m);if(IsActive&&!m.Own&&m.Conversation.StartsWith("nostr:"))_=controller.MarkNostrRead(m.Conversation[6..],m.Id); }else if(!m.Own)status.Text=$"New message from {m.Sender}";RefreshChannels();});
        controller.TypingChanged+=(peer,isTyping)=>Ui(()=>{if(isTyping)typingPeers[peer]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();else typingPeers.Remove(peer);RefreshTypingLabel();});
        controller.Receipt+=(id,value)=>Ui(()=>{if(receiptLabels.TryGetValue(id,out var label))label.Text=value;});
    }
    private void RefreshChannels()
    {
        channels.Children.Clear();void Label(string s)=>channels.Children.Add(new TextBlock{Text=s,Foreground=muted,Margin=new Thickness(18,18,12,6),FontSize=10});
        void Channel(string id,string label){var b=Button(label,()=>Select(id));b.Content=new TextBlock{Text=label,TextTrimming=TextTrimming.CharacterEllipsis};b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Padding=new Thickness(14,9,8,9);b.BorderThickness=new Thickness(id==conversation?1:0);channels.Children.Add(b);}
        Label("CHANNELS");Channel("mesh","# bluetooth mesh");Channel("board","⌖ mesh board");
        if(Geohash.Valid(store.State.Cell)){Channel("geo:"+store.State.Cell,"# "+store.State.Cell);Channel("notes:"+store.State.Cell,"⌖ nearby notes");}
        channels.Children.Add(Button("+ location channel",()=>JoinLocation()));Label("GROUPS");foreach(var g in store.State.Groups.Values)Channel(g.Conversation,"# "+g.Name);channels.Children.Add(Button("+ private group",CreateGroup));Label("NEARBY");
        foreach(var p in controller.Peers.Values.Where(p=>p.LastSeen>Bytes.Now-120000&&!store.State.Blocked.Contains(p.Id)).OrderBy(p=>p.Nickname))
        {string favorite=store.State.Favorites.Contains(p.Id)?"★ ":"";string verified=store.State.Verified.ContainsKey(Bytes.Hex(Crypto.Hash(p.NoiseKey)))?"✓ ":"";string battery=p.BatteryPercent is byte value?$" · {value}% ⚡":"";Channel("mesh:"+p.Id,$"{favorite}{verified}{(p.Authenticated?"●":"○")} {p.Nickname}{battery}");}
        foreach(var recent in store.State.Messages.Where(m=>m.Conversation.StartsWith("mesh:")&&!store.State.Blocked.Contains(m.Conversation[5..]))
            .GroupBy(m=>m.Conversation).Select(g=>g.OrderByDescending(m=>m.Timestamp).First()).OrderByDescending(m=>m.Timestamp))
            if(!controller.Peers.TryGetValue(recent.Conversation[5..],out var live)||live.LastSeen<=Bytes.Now-120000)
                Channel(recent.Conversation,"@ "+(store.State.NoiseContacts.GetValueOrDefault(recent.Conversation[5..])?.Nickname??store.State.KnownPeers.GetValueOrDefault(recent.Conversation[5..])?.Nickname??(!recent.Own?recent.Sender:recent.Conversation[5..13])));
        Label("PRIVATE MESSAGES");foreach(var p in store.State.NostrPeers.Where(p=>!store.State.Blocked.Contains(p.Key)).OrderBy(p=>p.Value))Channel("nostr:"+p.Key,"@ "+p.Value);
        channels.Children.Add(Button("+ Nostr contact",()=>NewContact()));
    }
    private void Select(string id)
    {
        SendTyping(false);typingStopTimer.Stop();activeTypingPeer=null;
        conversation=id;RefreshDraft();title.Text=id switch{"mesh"=>"# bluetooth mesh","board"=>"⌖ mesh board",_ when id.StartsWith("mesh:")=>"@ "+(controller.Peers.GetValueOrDefault(id[5..])?.Nickname??id[5..]),_ when id.StartsWith("group:")=>"# "+store.State.Groups[id].Name,_ when id.StartsWith("nostr:")=>"@ "+store.State.NostrPeers.GetValueOrDefault(id[6..],id[6..8]),_ when id.StartsWith("notes:")=>"⌖ notes #"+id[6..],_=>"# "+id[4..]};
        if(filesButton!=null){int count=store.State.Messages.Count(m=>m.Conversation==id&&m.Mime!=null);filesButton.Content=$"files · {count}";filesButton.IsEnabled=count>0;}
        messages.Children.Clear();receiptLabels.Clear();StoredMessage[] history;lock(store.State.Messages)history=store.State.Messages.Where(m=>m.Conversation==id).OrderBy(m=>m.Timestamp).TakeLast(500).ToArray();foreach(var m in history)Append(m);
        if(history.Length==0)messages.Children.Add(new TextBlock{Text=id=="mesh"?"Messages travel through nearby devices over Bluetooth LE.\nChoose a location channel to chat over Nostr relays.":"No messages yet.",Foreground=muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,20,0,20)});
        foreach(var message in history.Where(m=>CanMarkRead(m)&&!m.Own&&m.Status!="read"&&m.Conversation.StartsWith("mesh:")))_=controller.MarkMeshRead(message);
        RefreshChannels();RefreshTypingLabel();scroll.ScrollToEnd();input.Focus();
    }
    private void Append(StoredMessage m)
    {
        var block=new StackPanel{Margin=new Thickness(0,8,0,10)};
        var header=new StackPanel{Orientation=Orientation.Horizontal};header.Children.Add(new TextBlock{Text=m.Sender,Foreground=m.Own?green:Brushes.LightSkyBlue,FontWeight=FontWeights.SemiBold});
        header.Children.Add(new TextBlock{Text="  "+DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).ToLocalTime().ToString("HH:mm"),Foreground=muted,FontSize=10,VerticalAlignment=VerticalAlignment.Center});
        if(m.Own){var label=new TextBlock{Text="  "+m.Status,Foreground=muted,FontSize=10};receiptLabels[m.Id]=label;header.Children.Add(label);}block.Children.Add(header);
        var text=new TextBox{Text=m.LocalAttachmentDeleted?"File deleted on this device · "+m.Text:m.Text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),Background=Brushes.Transparent,Foreground=Foreground,Padding=new Thickness(0,4,0,0)};block.Children.Add(text);
        if(m.Conversation=="board"&&m.Own)block.Children.Add(Button("delete note",async()=>await Guard(async()=>{await controller.DeleteBoard(m.Id);Select("board");})));
        try{if(m.Text.StartsWith("cashu")){var token=Cashu.Decode(m.Text);block.Children.Add(Button($"{token.Amount} {token.Unit} · {token.Mint} · copy token",()=>Clipboard.SetText(token.Token)));}}catch{}
        if(IsImage(m))block.Children.Add(ImageAttachment(m));
        if(m.Attachment!=null&&m.Mime?.StartsWith("audio/")==true)block.Children.Add(Button("play voice note",async()=>await Guard(()=>PlayAudio(m))));
        if(m.Attachment!=null)block.Children.Add(Button("save attachment · "+m.Text,()=>ExportAttachment(m)));
        var choices=new StackPanel{Orientation=Orientation.Horizontal};
        foreach(var group in store.State.Reactions.Where(r=>r.Conversation==m.Conversation&&r.MessageId==m.Id).GroupBy(r=>r.Emoji).OrderBy(g=>Array.IndexOf(ReactionOptions,g.Key)))
            choices.Children.Add(Button(group.Key+" "+group.Count(),async()=>await Guard(()=>controller.ToggleReaction(m,group.Key))));
        var picker=Button("react…",()=>{});var menu=new ContextMenu();foreach(string emoji in ReactionOptions){var item=new MenuItem{Header=emoji};item.Click+=async(_,_)=>await Guard(()=>controller.ToggleReaction(m,emoji));menu.Items.Add(item);}picker.ContextMenu=menu;picker.Click+=(_,_)=>menu.IsOpen=true;choices.Children.Add(picker);block.Children.Add(choices);
        if(store.State.HackMode)messages.Children.Add(block);
        else
        {
            var row=new Grid{Margin=new Thickness(0,4,0,4)};row.ColumnDefinitions.Add(new(){Width=new GridLength(46)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var avatar=new Border{Width=32,Height=32,CornerRadius=new CornerRadius(11),Background=m.Own?green:muted,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,14,10,0),Child=new TextBlock{Text=m.Sender.Length>0?m.Sender[..1].ToUpperInvariant():"?",Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold}};row.Children.Add(avatar);
            var card=new Border{Child=block,Background=panel,CornerRadius=new CornerRadius(12),Padding=new Thickness(16,4,16,4)};Grid.SetColumn(card,1);row.Children.Add(card);messages.Children.Add(row);
        }
    }
    private static readonly string[] ReactionOptions=["👍","❤️","😂","😮","😢","🙏"];
    private async Task Guard(Func<Task> action)
    {try{await action();}catch(Exception e){status.Text=e.Message;}}
    private async Task Command(string text)
    {
        var parts=text.Split(' ',3,StringSplitOptions.RemoveEmptyEntries);string command=parts[0].ToLowerInvariant();string arg=parts.Length>1?parts[1]:"";
        string Resolve(string name)=>controller.Peers.Values.FirstOrDefault(p=>p.Id.Equals(name,StringComparison.OrdinalIgnoreCase)||p.Nickname.Equals(name,StringComparison.OrdinalIgnoreCase))?.Id??store.State.NostrPeers.FirstOrDefault(p=>p.Key==name||p.Value==name).Key??throw new ArgumentException("Unknown peer: "+name);
        switch(command)
        {
            case "/help":SystemMessage("/msg name text · /who · /block name · /unblock id · /fav name · /unfav name · /slap name · /hug name · /ping name · /clear · /drop note · /pay token · /trace name · /group create|invite|remove|leave\nShift+Enter inserts a line break. Triple-click bitchat to wipe local data.");break;
            case "/w":
            case "/who":SystemMessage(string.Join("\n",controller.Peers.Values.Select(p=>$"{p.Nickname} · {p.Id} · {(p.Authenticated?"Noise authenticated":"discovered")}")));break;
            case "/clear":ClearChat();break;
            case "/m":
            case "/msg":if(parts.Length<3)throw new ArgumentException("Usage: /msg name text");var target=Resolve(arg);await controller.Send((target.Length==16?"mesh:":"nostr:")+target,parts[2]);break;
            case "/block":store.State.Blocked.Add(Resolve(arg));store.Save();RefreshChannels();break;
            case "/unblock":store.State.Blocked.Remove(arg);store.Save();RefreshChannels();break;
            case "/fav":await controller.SetFavorite(Resolve(arg),true);RefreshChannels();break;
            case "/unfav":await controller.SetFavorite(Resolve(arg),false);RefreshChannels();break;
            case "/slap":await controller.Send(conversation,$"* {store.State.Nickname} slaps {arg} around a bit with a large trout");break;
            case "/hug":await controller.Send(conversation,$"* {store.State.Nickname} hugs {arg}");break;
            case "/ping":await controller.Mesh.Ping(Resolve(arg));break;
            case "/pay":{var info=Cashu.Decode(arg);bool publicChat=conversation is "mesh" or "board"||conversation.StartsWith("geo:")||conversation.StartsWith("notes:");if(publicChat&&(parts.Length<3||parts[2]!="public"))throw new ArgumentException("This is public: anyone can redeem the token. Use /pay <token> public to send it.");await controller.Send(conversation,info.Token);break;}
            case "/trace":{string targetId=Resolve(arg);var path=await controller.Mesh.Trace(targetId);SystemMessage(path.Length==0?"no known route":"estimated route: "+string.Join(" → ",path.Select(id=>id==controller.Mesh.Id?"you":controller.Peers.GetValueOrDefault(id)?.Nickname??id))+$" ({path.Length-1} hops)");break;}
            case "/group":if(arg=="list"){SystemMessage(string.Join("\n",store.State.Groups.Values.Select(g=>g.Name+" · "+g.Members.Length+" members")));break;}if(arg=="create"){CreateGroup();break;}if(!conversation.StartsWith("group:"))throw new ArgumentException("Select a group first");if(arg=="leave"){store.State.Groups.Remove(conversation);store.Save();Select("mesh");break;}if(parts.Length<3||arg is not ("invite" or "remove"))throw new ArgumentException("/group create | invite name | remove name | leave");await controller.ChangeGroupMember(conversation,Resolve(parts[2]),arg=="remove");break;
            case "/drop":if(conversation is "mesh" or "board"){await controller.PostBoard(text.Length>6?text[6..]:throw new ArgumentException("Usage: /drop note"));break;}if(!Geohash.Valid(store.State.Cell))throw new ArgumentException("Join a location channel first");await controller.Send("notes:"+store.State.Cell,text.Length>6?text[6..]:throw new ArgumentException("Usage: /drop note"));break;
            default:throw new ArgumentException("Unknown command. Use /help.");
        }
    }
    private void SystemMessage(string text)=>Append(new(conversation,Guid.NewGuid().ToString(),"system",text,Bytes.Now));
    private async void ClearChat(){await Guard(async()=>{await controller.ClearConversation(conversation);Select(conversation);});}
    private async Task PlayAudio(StoredMessage message)
    {
        var bytes=store.ReadMedia(message.Attachment!);using var codec=AudioProcess.Start(["-i","pipe:0","-t","600","-f","s16le","-ar","16000","-ac","1","pipe:1"]);var errors=codec.StandardError.ReadToEndAsync();using var decoded=new MemoryStream();
        var writer=Task.Run(async()=>{await codec.StandardInput.BaseStream.WriteAsync(bytes);codec.StandardInput.Close();});var reader=Task.Run(async()=>{var chunk=new byte[8192];int n;while((n=await codec.StandardOutput.BaseStream.ReadAsync(chunk))>0){if(decoded.Length+n>32*1024*1024)throw new InvalidDataException("Audio playback limit exceeded");decoded.Write(chunk,0,n);}});
        await Task.WhenAll(writer,reader).WaitAsync(TimeSpan.FromSeconds(20));await codec.WaitForExitAsync();if(codec.ExitCode!=0)throw new InvalidDataException(await errors);decoded.Position=0;
        using var source=new NAudio.Wave.RawSourceWaveStream(decoded,new NAudio.Wave.WaveFormat(16000,16,1));using var player=new NAudio.Wave.WaveOutEvent();var finished=new TaskCompletionSource<bool>();player.PlaybackStopped+=(_,e)=>{if(e.Exception!=null)finished.TrySetException(e.Exception);else finished.TrySetResult(true);};player.Init(source);player.Play();await finished.Task;
    }
    private void ExportAttachment(StoredMessage m)
    {var picker=new SaveFileDialog{FileName=Path.GetFileName(m.Text),Title="Save attachment"};if(picker.ShowDialog(this)==true){File.WriteAllBytes(picker.FileName,store.ReadMedia(m.Attachment!));status.Text="Attachment saved";}}
    private void JoinLocation()
    {var picker=new LocationPicker{Owner=this};if(picker.ShowDialog()==true&&picker.SelectedCell is string cell){store.State.Teleported=picker.Teleported;_=Guard(async()=>{await controller.JoinCell(cell);Select("geo:"+cell);});}}
    private void NewContact()
    {var value=Prompt("Nostr contact","Public key (npub or 64 hexadecimal characters)","");if(value==null)return;try{var b=value.Trim().StartsWith("npub1")?Bech32.Decode(value.Trim(),"npub"):Convert.FromHexString(value.Trim());if(b.Length!=32)throw new FormatException();store.State.NostrPeers[Bytes.Hex(b)]=Bytes.Hex(b)[..8];store.Save();Select("nostr:"+Bytes.Hex(b));}catch{status.Text="Invalid Nostr public key";}}
    private void CreateGroup()
    {
        var name=Prompt("Private group","Group name","");if(name==null)return;
        var members=Prompt("Private group members","Comma-separated nearby nicknames (up to 15)","");if(members==null)return;
        _=Guard(async()=>{var names=members.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);var ids=names.Select(n=>controller.Peers.Values.FirstOrDefault(p=>p.Nickname==n||p.Id==n)?.Id??throw new ArgumentException("Unknown peer: "+n)).ToArray();var group=await controller.CreateGroup(name,ids);Select(group.Conversation);});
    }
    private void ShowQr()
    {
        string url=controller.MyVerificationUrl();using var generator=new QRCoder.QRCodeGenerator();using var data=generator.CreateQrCode(url,QRCoder.QRCodeGenerator.ECCLevel.M);using var code=new QRCoder.PngByteQRCode(data);byte[] bytes=code.GetGraphic(6);
        var image=new System.Windows.Media.Imaging.BitmapImage();image.BeginInit();image.StreamSource=new MemoryStream(bytes);image.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;image.EndInit();image.Freeze();
        var d=new Window{Owner=this,FontFamily=FontFamily,Title="Verify BitChat identity",Width=580,Height=710,Background=bg,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner};var p=new StackPanel{Margin=new Thickness(20)};d.Content=p;p.Children.Add(new TextBlock{Text="Scan with another BitChat client",Foreground=green,FontSize=18,Margin=new Thickness(0,0,0,12)});p.Children.Add(new Image{Source=image,Width=480,Height=480});p.Children.Add(new TextBox{Text=url,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=90,Margin=new Thickness(0,12,0,0)});d.ShowDialog();
    }
    private void ScanQr()
    {
        var d=new Window{Owner=this,FontFamily=FontFamily,Title="Verify a peer",Width=550,Height=230,Background=bg,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner};var p=new StackPanel{Margin=new Thickness(20)};d.Content=p;
        p.Children.Add(Button("paste verification link",()=>{var url=Prompt("Verification link","bitchat://verify URL","");if(url!=null)_=Guard(async()=>{await controller.VerifyQr(url);d.Close();});}));
        p.Children.Add(Button("scan QR image",()=>{var picker=new OpenFileDialog{Filter="Images|*.png;*.jpg;*.jpeg;*.bmp"};if(picker.ShowDialog(d)==true)_=Guard(async()=>{var decoder=System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(picker.FileName),System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);var pixels=new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);int stride=checked(pixels.PixelWidth*4);if(pixels.PixelWidth>10000||pixels.PixelHeight>10000)throw new InvalidDataException("Image too large");var bytes=new byte[checked(stride*pixels.PixelHeight)];pixels.CopyPixels(bytes,stride,0);var result=new ZXing.BarcodeReaderGeneric().Decode(bytes,pixels.PixelWidth,pixels.PixelHeight,ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);if(result==null)throw new InvalidDataException("No QR code found");await controller.VerifyQr(result.Text);d.Close();});}));d.ShowDialog();
    }
    private void ShowIdentity()
    {
        string own=$"Nickname: {store.State.Nickname}\nMesh ID: {controller.Mesh.Id}\nNoise fingerprint: {Bytes.Hex(Crypto.Hash(Crypto.PublicX(store.State.NoiseKey)))}\nNostr public key: {Bytes.Hex(Schnorr.Public(store.State.NostrKey))}";
        if(conversation.StartsWith("mesh:")&&controller.Peers.TryGetValue(conversation[5..],out var peer))own+=$"\n\nPeer: {peer.Nickname}\nFingerprint: {Bytes.Hex(Crypto.Hash(peer.NoiseKey))}\nAuthenticated Noise session: {peer.Authenticated}";
        var d=Dialog("Identity",own);d.ShowDialog();
    }
    private void ShowSettings()
    {
        var d=new Window{Owner=this,FontFamily=FontFamily,Title="BitChat settings",Width=600,Height=800,Background=bg,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner};var p=new StackPanel{Margin=new Thickness(24)};d.Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        p.Children.Add(new TextBlock{Text="identity & connections",FontSize=22,Foreground=green,Margin=new Thickness(0,0,0,20)});
        var startupOption=new CheckBox{Content="Start BitChat when I sign in to Windows",IsChecked=StartupRegistration.Enabled,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(startupOption);
        p.Children.Add(new TextBlock{Text="Closing the window keeps BitChat connected in the notification area. Use its tray menu to quit.",TextWrapping=TextWrapping.Wrap,Foreground=muted,Margin=new Thickness(0,0,0,16)});
        var hackOption=new CheckBox{Content="Hack mode · use the original terminal-style interface",IsChecked=store.State.HackMode,Foreground=green,Margin=new Thickness(0,0,0,12)};p.Children.Add(hackOption);
        var lightOption=new CheckBox{Content="Use light theme",IsChecked=!store.State.Dark,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(lightOption);
        var batteryOption=new CheckBox{Content="Share my battery percentage with nearby BitChat users",IsChecked=store.State.ShareBattery,Foreground=green,Margin=new Thickness(0,0,0,12)};p.Children.Add(batteryOption);
        p.Children.Add(new TextBlock{Text="Keep received files"});var retention=new ComboBox{Margin=new Thickness(0,6,0,16),Background=panel,Foreground=Foreground};
        foreach(var option in new[]{(Days:0,Label:"Never delete"),(Days:1,Label:"1 day"),(Days:7,Label:"7 days"),(Days:30,Label:"30 days")})retention.Items.Add(new ComboBoxItem{Content=option.Label,Tag=option.Days});
        retention.SelectedIndex=Array.FindIndex(new[]{0,1,7,30},n=>n==store.State.FileRetentionDays);if(retention.SelectedIndex<0)retention.SelectedIndex=0;p.Children.Add(retention);
        p.Children.Add(new TextBlock{Text="nickname"});var nick=new TextBox{Text=store.State.Nickname,Margin=new Thickness(0,6,0,16)};p.Children.Add(nick);
        p.Children.Add(new TextBlock{Text="Nostr relays · one wss:// URL per line"});var urls=new TextBox{Text=string.Join("\n",store.State.Relays),AcceptsReturn=true,Height=100,Margin=new Thickness(0,6,0,16)};p.Children.Add(urls);
        var gatewayOption=new CheckBox{Content="Share Nostr access with the nearby mesh",IsChecked=store.State.Gateway,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(gatewayOption);var bridgeOption=new CheckBox{Content="Internet bridge (mesh and private courier drops)",IsChecked=store.State.Bridge,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(bridgeOption);
        var torOption=new CheckBox{Content="Route Nostr through Tor (restart required)",IsChecked=store.State.UseTor,Foreground=green,Margin=new Thickness(0,0,0,12)};p.Children.Add(torOption);
        var notificationsOption=new CheckBox{Content="Message notifications",IsChecked=store.State.Notifications,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(notificationsOption);
        var previewsOption=new CheckBox{Content="Hide notification previews (sender, text and location)",IsChecked=store.State.HideNotificationPreviews,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(previewsOption);
        var liveOption=new CheckBox{Content="Live push-to-talk (autoplay only while this app is active)",IsChecked=store.State.LiveVoice,Foreground=green,Margin=new Thickness(0,0,0,10)};p.Children.Add(liveOption);
        p.Children.Add(new TextBlock{Text="Keys, history and attachments are encrypted for your Windows account with DPAPI. Relay changes take effect after restarting BitChat.",TextWrapping=TextWrapping.Wrap,Foreground=muted});
        p.Children.Add(new TextBlock{Text="BitChat checks GitHub releases every 10 seconds while online and displays an update button when a newer version is available.",TextWrapping=TextWrapping.Wrap,Foreground=muted,Margin=new Thickness(0,12,0,12)});
        p.Children.Add(Button("save",()=>{string n=nick.Text.Trim();if(Bytes.Utf8(n).Length is <1 or >64){MessageBox.Show(d,"Nickname must contain 1–64 UTF-8 bytes.");return;}var relays=urls.Text.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);if(relays.Any(u=>!Uri.TryCreate(u,UriKind.Absolute,out var uri)||uri.Scheme!="wss")){MessageBox.Show(d,"Relays must use wss://");return;}try{StartupRegistration.Set(startupOption.IsChecked==true);StartupRegistration.Prompted=true;}catch(Exception error){MessageBox.Show(d,"Could not update Windows startup: "+error.Message);return;}store.State.Notifications=notificationsOption.IsChecked==true;store.State.HideNotificationPreviews=previewsOption.IsChecked==true;store.State.LiveVoice=liveOption.IsChecked==true;store.State.ShareBattery=batteryOption.IsChecked==true;store.State.FileRetentionDays=retention.SelectedItem is ComboBoxItem selected?(int)selected.Tag:0;store.State.Dark=lightOption.IsChecked!=true;store.State.HackMode=hackOption.IsChecked==true;ApplyThemeColors();Select(conversation);if(!store.State.LiveVoice)_=controller.StopVoiceAutoplay();store.State.Gateway=gatewayOption.IsChecked==true;store.State.Bridge=bridgeOption.IsChecked==true;_=controller.RefreshGateway();store.State.UseTor=torOption.IsChecked==true;store.State.Nickname=n;store.State.Relays=relays.ToList();controller.Mesh.Nickname=n;store.Save();_=controller.Mesh.Announce();_=Guard(async()=>{int removed=await controller.CleanupExpiredMedia();if(removed>0)status.Text=$"Removed {removed} expired local file(s)";});d.Close();}));
        p.Children.Add(Button("view identity",ShowIdentity));p.Children.Add(Button("show verification QR",ShowQr));p.Children.Add(Button("verify peer QR / link",ScanQr));p.Children.Add(new TextBlock{Text="BitChat for Windows · unsigned development build\nSource baseline: 04995f68e470",Foreground=muted,Margin=new Thickness(0,20,0,0)});d.ShowDialog();
    }
    private Window Dialog(string name,string text)=>new(){Owner=this,Title=name,Width=650,Height=350,Background=bg,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Background=bg,Foreground=Foreground,BorderThickness=new Thickness(0),Margin=new Thickness(20)}};
    private string? Prompt(string name,string description,string value)
    {
        string? result=null;var d=new Window{Owner=this,FontFamily=FontFamily,Title=name,Width=530,Height=220,Background=bg,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};var p=new StackPanel{Margin=new Thickness(20)};d.Content=p;p.Children.Add(new TextBlock{Text=description,TextWrapping=TextWrapping.Wrap});var field=new TextBox{Text=value,Margin=new Thickness(0,16,0,12)};p.Children.Add(field);p.Children.Add(Button("continue",()=>{result=field.Text;d.DialogResult=true;}));d.Loaded+=(_,_)=>field.Focus();d.ShowDialog();return result;
    }
    private async Task PanicTap()
    {
        long now=Bytes.Now;panicTaps=now-lastPanic<800?panicTaps+1:1;lastPanic=now;if(panicTaps<3)return;panicTaps=0;
        await controller.DisposeAsync();store.Wipe();controller=new(store);BindController();Select("mesh");await Guard(()=>controller.Start());status.Text="Local identity and message history wiped";
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow : Window
{
    private const string ItemFormat = "DesktopNook.ManagedItem.v1";
    private string? _activeCategory;
    private int _placementVersion;
    private bool _onlineBusy;
    private OnlineRelease? _latestRelease;
    private DateTime _lastUpdateCheck=DateTime.MinValue;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer=new(){Interval=TimeSpan.FromHours(1)};
    internal static string CurrentVersionLabel=>"v"+System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    internal string UpdateMenuText=>_latestRelease is null?$"检查在线更新（当前 {CurrentVersionLabel}）…":$"更新到 v{_latestRelease.Version.ToString(3)}（当前 {CurrentVersionLabel}）…";
    private Point _mouseDown; private bool _dragging; private int _interactions; private int _menus; private bool _allowExit; private DateTime _suppressUntil;
    public bool KeepOpenForVerification { get; set; }
    internal bool HoldVisibleForMotionProbe {get;set;}
    public NookStore Store => App.Store;
    public string? ActiveCategory => _activeCategory;
    public int VisibleCount => Icons.Items.Count;
    internal ContextMenu? LastMenu { get; private set; }
    internal Button IconButton(string id) => FindVisualChildren<Button>(Icons).Single(b => b.Tag is NookItem item && item.Id == id);
    internal Point DropPoint => DropSurface.PointToScreen(new Point(DropSurface.ActualWidth/2,DropSurface.ActualHeight/2));
    internal List<Point> IconPositions() => FindVisualChildren<Button>(Icons).Select(b=>b.TranslatePoint(new Point(0,0),this)).ToList();
    public MainWindow()
    {
        InitializeComponent();InitializePanelMotion();InitializeSizing();Activated+=(_,_)=>{Place();if(IsLoaded&&!App.IsSandbox&&DateTime.UtcNow-_lastUpdateCheck>TimeSpan.FromMinutes(15))_=CheckOnlineUpdateAsync(false);};
        SourceInitialized+=(_,_)=>UiMotion.PrepareWindow(this);
        _updateTimer.Tick+=(_,_)=>_=CheckOnlineUpdateAsync(false);
        Closed+=(_,_)=>_updateTimer.Stop();
        Render();
    }
    private bool _loadedOnce;
    private void OnLoaded(object sender,RoutedEventArgs e)
    {
        if(_loadedOnce){if(WindowState==WindowState.Normal){Place();int revision=_hideRevision;Dispatcher.BeginInvoke(()=>{if(WindowState==WindowState.Normal&&revision==_hideRevision)OpenPanelMotion();},System.Windows.Threading.DispatcherPriority.Loaded);if(!App.IsSandbox&&DateTime.UtcNow-_lastUpdateCheck>TimeSpan.FromMinutes(15))_=CheckOnlineUpdateAsync(false);}return;}
        _loadedOnce=true;TaskbarPlacement.EnableToggle(this);Place();if(WindowState==WindowState.Normal)OpenPanelMotion();if(Store.RecoveryMessages.Count>0)SetStatus(Store.RecoveryMessages.Last());if(!App.IsSandbox){_updateTimer.Start();_=CheckOnlineUpdateAsync(false);}
    }
    internal void SetAvailableRelease(OnlineRelease? release)
    {
        _shutter.InvalidateSnapshot();
        _latestRelease=release;
        UpdateAvailableButton.Content=release is null?"":$"发现 v{release.Version.ToString(3)} · 更新";
        UpdateAvailableButton.Visibility=release is null?Visibility.Collapsed:Visibility.Visible;
    }
    private async void OnUpdateAvailable(object sender,RoutedEventArgs e)=>await CheckOnlineUpdateAsync(true);
    private async void Place()
    {
        if(_resizing || KeepOpenForVerification || !IsLoaded || WindowState!=WindowState.Normal)return;
        var version=++_placementVersion;
        var fallback=TaskbarPlacement.Fallback();if(TaskbarPlacement.WouldMove(this,fallback,PreferredWindowSize))UiMotion.StopReveal(Surface);
        TaskbarPlacement.Apply(this,fallback,PreferredWindowSize);
        var anchor=await TaskbarPlacement.FindAsync();
        if(anchor is not null && version==_placementVersion && !_resizing && !KeepOpenForVerification && WindowState==WindowState.Normal){if(TaskbarPlacement.WouldMove(this,anchor,PreferredWindowSize))UiMotion.StopReveal(Surface);TaskbarPlacement.Apply(this,anchor,PreferredWindowSize);}
    }
    public void ShowPanel() { EnsurePanelContent();bool reverse=_hidePending;WindowState=WindowState.Normal;Show();Place();if(reverse)OpenPanelMotion();Activate(); }
    public void TogglePanel(){if(_hidePending)ShowPanel();else if(WindowState==WindowState.Normal&&IsVisible)BeginHidePanel();else ShowPanel();}
    private void OnStateChanged(object? sender,EventArgs e) { if(WindowState!=WindowState.Minimized){Place();if(IsLoaded||ContentParked)OpenPanelMotion();}else {FinishPanelMotion();UiMotion.CancelAll();ScheduleIdleRelease();} }
    private void OnDeactivated(object? sender,EventArgs e)
    {
        if(HoldVisibleForMotionProbe||TaskbarPlacement.PointerOverButton())return;
        Dispatcher.BeginInvoke(()=>{if(!HoldVisibleForMotionProbe&&!KeepOpenForVerification&&!IsActive&&_interactions==0&&!_dragging&&_menus==0&&WindowState!=WindowState.Minimized)BeginHidePanel();},System.Windows.Threading.DispatcherPriority.Background);
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_allowExit)return;e.Cancel=true;
        if(_dragging||_interactions>0)return;
        if(App.Options.CloseEndsProcess){_allowExit=true;e.Cancel=false;Dispatcher.BeginInvoke(()=>Application.Current.Shutdown());}else BeginHidePanel();
    }
    private void OnCloseButton(object sender,RoutedEventArgs e)=>Close();
    private void OnMinimizeButton(object sender,RoutedEventArgs e)=>BeginHidePanel();
    public void Quit() { _idleRelease.Stop();FinishPanelMotion();_shutter.ReleaseBuffer();UiMotion.CancelAll();_allowExit=true; Application.Current.Shutdown(); }
    internal void QuitForMaintenance(){if(!_dragging&&_interactions==0)Quit();}
    private bool _storageMoving;
    internal bool TryBeginStorageMove(){if(_dragging||_interactions>0||_menus>0||_onlineBusy)return false;_storageMoving=true;_interactions++;IsEnabled=false;return true;}
    internal void EndStorageMove(){_storageMoving=false;_interactions--;IsEnabled=true;Render();}
    public void SetStatus(string message) { _shutter.InvalidateSnapshot();Status.Text=message;Status.ToolTip=message; }
    public void Render()
    {
        _shutter.InvalidateSnapshot();
        if(_activeCategory is not null && !Store.State.Categories.Any(c=>c.Id==_activeCategory)) _activeCategory=null;
        Dispatcher.BeginInvoke(UpdateSizingLayout,System.Windows.Threading.DispatcherPriority.Loaded);
        Tabs.Children.Clear(); AddTab("全部",null);
        foreach(var category in Store.State.Categories) AddTab(category.Name,category.Id);
        var visible=VisibleOrderedItems().Select(item=>new IconEntry(item,item.Kind=="recycle-bin"?"":Store.ManagedPath(item))).ToList();
        Icons.ItemsSource=visible; EmptyState.Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
        EmptyText.Text=_activeCategory is null?"把常用图标，放进轻匣":"此分类还没有图标";
        TotalCount.Text=Store.State.Items.Count.ToString();
        Summary.Text=(_activeCategory is null?"全部":Store.State.Categories.Single(c=>c.Id==_activeCategory).Name)+" · "+visible.Count+" 个图标";
    }
    private void AddTab(string name,string? id)
    {
        var button=new Button { Content=name,Tag=id,FontSize=11,Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,4,0),Background=id==_activeCategory?Brush("#DDF2FF"):Brushes.Transparent,Foreground=id==_activeCategory?Brush("#0877BE"):Brush("#698398"),ToolTip=id is null?"显示所有图标":name+" · 右键管理分类" };
        AutomationProperties.SetAutomationId(button,"Category-"+(id??"all"));
        button.Click+=(_,_)=>SelectCategory(id);
        button.MouseRightButtonUp+=(_,e)=>{e.Handled=true;if(id is not null) CategoryMenu(button,id);};
        button.AllowDrop=true;button.DragOver+=(_,e)=>HandleDragOver(e,true);
        button.Drop+=(_,e)=>HandleDrop(e,id,true);
        button.PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Left or Key.Right or Key.Home or Key.End){var buttons=Tabs.Children.OfType<Button>().ToList();var current=buttons.IndexOf(button);var next=e.Key==Key.Home?0:e.Key==Key.End?buttons.Count-1:(current+(e.Key==Key.Left?-1:1)+buttons.Count)%buttons.Count;SelectCategory(buttons[next].Tag as string);Tabs.Children.OfType<Button>().ElementAt(next).Focus();e.Handled=true;}};
        Tabs.Children.Add(button);
    }
    public void SelectCategory(string? id) {FinishPanelMotion();bool changed=_activeCategory!=id;UiMotion.StopReveal(Icons); _activeCategory=id;Render();if(changed)UiMotion.Reveal(Icons,180,0); }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private bool Run(Action action,string? message=null)
    {
        if(_storageMoving)return false;
        try { FinishPanelMotion();action();Render();if(message is not null)SetStatus(message);return true; }
        catch(Exception ex) { ShowFailure(ex);return false; }
    }
    public void ShowFailure(Exception exception)
    {
        try { App.ReloadStore();Render(); } catch(Exception recovery) { SetStatus("恢复未完成："+recovery.Message); }
        _interactions++;
        try { UiDialogs.Show(this,exception.Message,"轻匣",MessageBoxButton.OK,MessageBoxImage.Information); }
        finally { _interactions--; }
    }
    private void OnCollect(object sender,RoutedEventArgs e)
    {
        if(_storageMoving)return;
        FinishPanelMotion();
        _interactions++;
        try
        {
            var picker=new CollectWindow(Store,_activeCategory??Store.DefaultCategoryId){Owner=this};
            if(picker.ShowDialog()!=true) return;
            var errors=new List<string>();var completed=0;
            foreach(var entry in picker.Selection)
            {
                try { if(entry.Recycle) Store.CollectRecycleBin(picker.CategoryId);else Store.Collect(entry.Path,picker.CategoryId);completed++; }
                catch(Exception ex) { errors.Add(entry.Name+"："+ex.Message);try{App.ReloadStore();}catch{break;} }
            }
            _activeCategory=picker.CategoryId;Render();ShellIntegration.RefreshDesktop();SetStatus("已收进 "+completed+" 个图标");
            if(errors.Count>0) UiDialogs.Show(this,string.Join("\n",errors),"部分图标未完成收纳",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        finally {_interactions--;}
    }
    private void OnNewCategory(object sender,RoutedEventArgs e) { var name=Ask("新建分类","分类名称","");if(name is not null)Run(()=>{Store.AddCategory(name);_activeCategory=Store.State.Categories.Single(c=>c.Name==name.Trim()).Id;},"分类已创建"); }
    private string? Ask(string title,string label,string initial)
    {
        _interactions++;try{var dialog=new TextDialog(title,label,initial){Owner=this};return dialog.ShowDialog()==true?dialog.Value:null;}finally{_interactions--;}
    }
    private void Popup(FrameworkElement owner,IEnumerable<object> entries,Action? closed=null)
    {
        FinishPanelMotion();UiMotion.StopReveal(Surface);UiMotion.StopReveal(Icons);
        var menu=new ContextMenu { PlacementTarget=owner };
        MenuLayout.Adapt(menu,this);
        LastMenu=menu;
        foreach(var entry in entries)menu.Items.Add(entry);
        menu.Opened+=(_,_)=>{_menus++;UiMotion.Reveal(menu,160,0);};
        menu.Closed+=(_,_)=>{UiMotion.StopReveal(menu);_menus=Math.Max(0,_menus-1);if(closed is not null)Dispatcher.BeginInvoke(closed,System.Windows.Threading.DispatcherPriority.ContextIdle);};
        menu.IsOpen=true;
    }
    private static MenuItem Menu(string name,Action action,bool enabled=true) {var item=new MenuItem{Header=name,IsEnabled=enabled};item.Click+=(_,_)=>action();return item;}
    internal void CategoryMenu(FrameworkElement owner,string id)
    {
        var category=Store.State.Categories.Single(c=>c.Id==id);var index=Store.State.Categories.IndexOf(category);
        var items=new List<MenuItem>{Menu("收进此分类",()=>{_activeCategory=id;OnCollect(this,new());})};
        if(id!="uncategorized")items.AddRange([
            Menu("重命名",()=>{var name=Ask("重命名分类","分类名称",category.Name);if(name is not null)Run(()=>Store.RenameCategory(id,name),"分类已更新");}),
            Menu("向前移动",()=>Run(()=>Store.OrderCategory(id,-1)),index>0),Menu("向后移动",()=>Run(()=>Store.OrderCategory(id,1)),index<Store.State.Categories.Count-(Store.State.Categories.Any(c=>c.Id=="uncategorized")?2:1))
        ]);
        var delete=Menu("删除分类",()=>{
            _interactions++;
            try{
                var targetId=Store.DeleteDestination(id);var target=Store.State.Categories.Single(c=>c.Id==targetId);
                if(UiDialogs.Show(this,$"删除“{category.Name}”？其中的图标将移入“{target.Name}”，不会删除文件或移回桌面。","删除分类",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
                int warnings=Store.RecoveryMessages.Count;bool selected=_activeCategory==id;
                if(Run(()=>Store.DeleteCategory(id,targetId),"分类已删除")){if(selected){_activeCategory=targetId;Render();}if(Store.RecoveryMessages.Count>warnings)SetStatus(Store.RecoveryMessages.Last());}
            }finally{_interactions--;}
        },Store.State.Categories.Count>1);
        if(!delete.IsEnabled){delete.ToolTip="至少保留一个分类，请先新建分类再删除";ToolTipService.SetShowOnDisabled(delete,true);}items.Add(delete);
        Popup(owner,items);
    }
    private void OnMore(object sender,RoutedEventArgs e)
    {
        var options=App.Options;
        var startup=new MenuItem{Header="开机自启",IsCheckable=true,IsChecked=options.Enabled,IsEnabled=!App.IsSandbox};
        startup.Checked+=(_,_)=>Run(()=>options.SetEnabled(true),"已开启开机自启");
        startup.Unchecked+=(_,_)=>Run(()=>options.SetEnabled(false),"已关闭开机自启");
        var closing=new MenuItem{Header="关闭时结束进程",IsCheckable=true,IsChecked=options.CloseEndsProcess};
        closing.Checked+=(_,_)=>Run(()=>options.CloseEndsProcess=true,"关闭按钮将退出程序");
        closing.Unchecked+=(_,_)=>Run(()=>options.CloseEndsProcess=false,"关闭按钮将收起面板");
        var motion=new MenuItem{Header="动画效果"};
        foreach(var (name,mode) in new[]{("跟随系统（默认）","system"),("开启","on"),("关闭","off")})
        {
            var choice=Menu(name,()=>Run(()=>{options.MotionMode=mode;UiMotion.Configure(mode);},"动画效果已更新"));choice.IsCheckable=true;choice.IsChecked=options.MotionMode==mode;motion.Items.Add(choice);
        }
        Popup((FrameworkElement)sender,[
        startup,closing,WindowSizeMenu(),motion,new Separator(),
        Menu(UpdateMenuText,()=>_ = CheckOnlineUpdateAsync(true)),
        Menu("从本地文件更新…",()=>_ = SelectUpdateAsync()),
        Menu("全部移回桌面",RestoreAll,Store.State.Items.Count>0),
        Menu("创建开始菜单入口",()=>Run(()=>ShortcutInstaller.Install(),"已创建开始菜单入口，可右键固定到任务栏")),
        Menu("如何固定到任务栏",()=>{_interactions++;try{UiDialogs.Show(this,"右键任务栏上的“轻匣”图标，选择“固定到任务栏”。\n\n固定后点击同一个入口即可打开或收起轻匣。","轻匣");}finally{_interactions--;}}),
        Menu("打开数据目录",()=>Process.Start(new ProcessStartInfo("explorer.exe",Store.DataDirectory){UseShellExecute=true})),
        new Separator(),Menu("退出并结束进程",Quit)
    ]);
    }
    private async Task SelectUpdateAsync()
    {
        _interactions++;
        try
        {
            var picker=new Microsoft.Win32.OpenFileDialog{Title="选择轻匣新版本",Filter="轻匣程序或安装包 (*.exe)|*.exe",CheckFileExists=true};if(picker.ShowDialog(this)!=true)return;
            var source=picker.FileName;var target=Environment.ProcessPath!;var candidate=await Task.Run(()=>LocalUpdater.Inspect(source));
            if(UiDialogs.ShowUpdate(this,"当前版本："+CurrentVersionLabel+"\n更新到：v"+candidate.Version.ToString(3)+"\n\n程序会在原位置更新，保留分类、收纳内容和设置，完成后自动打开。","更新轻匣",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            SetStatus("正在准备更新…");var staged=await Task.Run(()=>LocalUpdater.Stage(source,target,Store.DataDirectory,App.IsSandbox?App.SandboxRoot:null,options:App.Options,state:Store.State));
            var helper=new ProcessStartInfo(staged.Helper){UseShellExecute=false,CreateNoWindow=true};helper.ArgumentList.Add("--apply-update");helper.ArgumentList.Add(staged.Plan);Process.Start(helper);Quit();
        }
        catch(Exception ex){ShowFailure(ex);}
        finally{_interactions--;}
    }
    private async Task CheckOnlineUpdateAsync(bool manual)
    {
        if(_onlineBusy){if(manual)SetStatus("正在检查或下载更新，请稍候…");return;}
        _onlineBusy=true;_lastUpdateCheck=DateTime.UtcNow;if(manual)_interactions++;
        try
        {
            if(manual)SetStatus($"正在检查更新（当前 {CurrentVersionLabel}）…");
            using var client=OnlineUpdater.CreateClient();
            SetAvailableRelease(await OnlineUpdater.CheckAsync(client,System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!));
            if(_latestRelease is null)
            {
                if(manual){SetStatus($"当前 {CurrentVersionLabel} 已是最新版本");UiDialogs.ShowUpdate(this,$"当前版本：{CurrentVersionLabel}\n已是最新版本。","检查更新");}return;
            }
            SetStatus($"发现新版本 v{_latestRelease.Version.ToString(3)}，点击更新查看详情");
            if(!manual)return;
            string notes=_latestRelease.Notes.Length>600?_latestRelease.Notes[..600]+"…":_latestRelease.Notes;
            if(UiDialogs.ShowUpdate(this,$"当前版本：{CurrentVersionLabel}\n更新到：v{_latestRelease.Version.ToString(3)}\n来源：GitHub 官方发行版\n\n{notes}\n\n选择确定后从 GitHub 下载更新并自动重启，分类、收纳内容和设置保留。","发现新版本",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            var download=new UpdateDownloadWindow(this,_latestRelease,Store.DataDirectory);
            if(download.ShowDialog()!=true||download.DownloadedFile is null)return;
            SetStatus("正在准备更新…");
            var staged=await Task.Run(()=>LocalUpdater.Stage(download.DownloadedFile,Environment.ProcessPath!,Store.DataDirectory,App.IsSandbox?App.SandboxRoot:null,options:App.Options,state:Store.State));
            using(var plan=System.Text.Json.JsonDocument.Parse(File.ReadAllText(staged.Plan)))
                if(!plan.RootElement.GetProperty("CandidateHash").GetString()!.Equals(_latestRelease.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件校验失败，请重新下载。");
            var helper=new ProcessStartInfo(staged.Helper){UseShellExecute=false,CreateNoWindow=true};helper.ArgumentList.Add("--apply-update");helper.ArgumentList.Add(staged.Plan);
            if(Process.Start(helper) is null)throw new IOException("无法启动更新助手。");Quit();
        }
        catch(Exception ex)
        {
            if(manual){SetStatus("更新检查未完成");UiDialogs.ShowUpdate(this,$"当前版本：{CurrentVersionLabel}\n{OnlineUpdater.FailureMessage(ex)}\n也可以使用“从本地文件更新”。","在线更新");}
        }
        finally{_onlineBusy=false;if(manual)_interactions--;}
    }
    private void RestoreAll()
    {
        _interactions++;
        try
        {
            if(UiDialogs.Show(this,"将 "+Store.State.Items.Count+" 个图标全部移回桌面？","轻匣",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            var restored=0;var remaining=new List<string>();
            foreach(var item in Store.State.Items.ToArray()) {if(RestoreItem(item.Id))restored++;else remaining.Add(item.Name);}
            SetStatus("已移回 "+restored+" 个，剩余 "+remaining.Count+" 个");
            if(remaining.Count>0)UiDialogs.Show(this,"这些图标仍保留在轻匣，可稍后重试：\n"+string.Join("\n",remaining),"恢复结果",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        finally{_interactions--;}
    }
    public bool RestoreItem(string id)
    {
        try {Store.Restore(id);ShellIntegration.RefreshDesktop();Render();SetStatus("已移回桌面");return true;}
        catch(NameConflictException)
        {
            _interactions++;try{if(UiDialogs.Show(this,"桌面已有同名文件。是否使用新名称恢复？原文件不会被覆盖。","轻匣",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return false;return Run(()=>Store.Restore(id,true),"已改名移回桌面");}finally{_interactions--;}
        }
        catch(Exception ex){ShowFailure(ex);return false;}
    }
    private void OnIconClick(object sender,RoutedEventArgs e)
    {
        if(_dragging || DateTime.UtcNow<_suppressUntil)return;
        if(((Button)sender).Tag is NookItem item)Run(()=>{ShellIntegration.Launch(Store,item);if(!KeepOpenForVerification)BeginHidePanel();});
    }
    private void OnIconRightClick(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;var button=(Button)sender;if(button.Tag is not NookItem item)return;
        ShowIconMenu(button,item);
    }
    internal void ShowIconMenu(Button button,NookItem item)
    {
        var siblings=Store.State.Items.Where(i=>i.CategoryId==item.CategoryId).ToList();var index=siblings.FindIndex(i=>i.Id==item.Id);
        var move=new MenuItem{Header="移动到分类"};foreach(var cat in Store.State.Categories)move.Items.Add(Menu(cat.Name,()=>Run(()=>Store.MoveItem(item.Id,cat.Id),"已移动分类")));
        var own=new List<object>{Menu("移回桌面",()=>RestoreItem(item.Id)),move,Menu("修改显示名称",()=>Rename()),Menu("前移",()=>Run(()=>Store.OrderItem(item.Id,-1)),index>0),Menu("后移",()=>Run(()=>Store.OrderItem(item.Id,1)),index<siblings.Count-1),new Separator()};
        void Rename(){var name=Ask("显示名称","图标在轻匣中的名称",item.Name);if(name is not null)Run(()=>Store.RenameItem(item.Id,name));}
        try
        {
            var shell=new ShellContextMenu(this,Store,item);
            void Execute(string verb,Action invoke)
            {
                _interactions++;
                try
                {
                    if(verb.Equals("rename",StringComparison.OrdinalIgnoreCase))Rename();
                    else if(verb.Equals("delete",StringComparison.OrdinalIgnoreCase))
                    {if(item.Kind=="recycle-bin")RestoreItem(item.Id);else if(UiDialogs.Show(this,"从轻匣移除这个入口？恢复副本会保留，软件不会卸载。","移除入口",MessageBoxButton.OKCancel)==MessageBoxResult.OK)Run(()=>Store.ArchiveItem(item.Id),"入口已移除，恢复副本保留");}
                    else if(verb.Equals("cut",StringComparison.OrdinalIgnoreCase))
                    {if(item.Kind=="recycle-bin"){RestoreItem(item.Id);return;}string? restored=null;if(Run(()=>restored=Store.Restore(item.Id),"已移回桌面并剪切")){var data=new DataObject(DataFormats.FileDrop,new[]{restored!});data.SetData("Preferred DropEffect",new MemoryStream(BitConverter.GetBytes(2)));Clipboard.SetDataObject(data,true);}}
                    else Run(invoke);
                }
                finally{_interactions--;}
            }
            own.AddRange(shell.Build(Execute));own.Add(new Separator());own.Add(Menu("Windows 完整菜单…",()=>{_interactions++;try{shell.FullMenu(this,Execute);}finally{_interactions--;}}));
            Popup(button,own,shell.Dispose);
        }
        catch(Exception ex){own.Add(Menu("打开",()=>Run(()=>ShellIntegration.Launch(Store,item))));SetStatus("系统扩展菜单暂不可用："+ex.Message);Popup(button,own);}
    }
    private void OnIconDown(object sender,MouseButtonEventArgs e) {FinishPanelMotion();UiMotion.StopReveal(Surface);UiMotion.StopReveal(Icons);_mouseDown=e.GetPosition(this);}
    private void OnIconMove(object sender,MouseEventArgs e)
    {
        if(_dragging || e.LeftButton!=MouseButtonState.Pressed || sender is not Button button || button.Tag is not NookItem item)return;
        var current=e.GetPosition(this);if(Math.Abs(current.X-_mouseDown.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y-_mouseDown.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        _dragging=true;var restoreOnDesktop=false;
        var data=new DataObject();data.SetData(ItemFormat,item.Id);
        QueryContinueDragEventHandler query=(_,args)=>{
            if(args.EscapePressed){args.Action=DragAction.Cancel;args.Handled=true;}
            else if((args.KeyStates&DragDropKeyStates.LeftMouseButton)==0 && ShellIntegration.IsDesktopUnderCursor()){restoreOnDesktop=true;args.Action=DragAction.Cancel;args.Handled=true;}
        };
        GiveFeedbackEventHandler feedback=(_,args)=>{if(ShellIntegration.IsDesktopUnderCursor()){args.UseDefaultCursors=false;Mouse.SetCursor(Cursors.Arrow);SetStatus("松开即可移回桌面");args.Handled=true;}};
        DragDrop.AddQueryContinueDragHandler(button,query);DragDrop.AddGiveFeedbackHandler(button,feedback);
        try {DragDrop.DoDragDrop(button,data,DragDropEffects.Move);}
        finally {DragDrop.RemoveQueryContinueDragHandler(button,query);DragDrop.RemoveGiveFeedbackHandler(button,feedback);_dragging=false;_suppressUntil=DateTime.UtcNow.AddMilliseconds(250);DropOutline.Visibility=Visibility.Collapsed;OrderMarker.Visibility=Visibility.Collapsed;}
        if(restoreOnDesktop)RestoreItem(item.Id);
    }
    private bool CanImport(IDataObject data)
    {
        if(data.GetDataPresent(ItemFormat))return true;
        if(data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length>0)return files.All(path=>Store.DesktopRoots.Any(root=>string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))),Path.TrimEndingDirectorySeparator(root),StringComparison.OrdinalIgnoreCase)) && (File.Exists(path)||Directory.Exists(path)) && !string.Equals(Path.GetFullPath(path),Environment.ProcessPath,StringComparison.OrdinalIgnoreCase));
        return ShellIntegration.ContainsRecycleBin(data);
    }
    private void OnDragOver(object sender,DragEventArgs e) => HandleDragOver(e);
    private void HandleDragOver(DragEventArgs e,bool onCategory=false)
    {
        FinishPanelMotion();UiMotion.StopReveal(Surface);UiMotion.StopReveal(Icons);
        if(!onCategory && e.Data.GetDataPresent(ItemFormat)){OrderDragOver(e);return;}
        try{var accepted=CanImport(e.Data);e.Effects=accepted?(e.Data.GetDataPresent(ItemFormat)?DragDropEffects.Move:DragDropEffects.Copy):DragDropEffects.None;DropOutline.Visibility=accepted?Visibility.Visible:Visibility.Collapsed;e.Handled=true;}
        catch{e.Effects=DragDropEffects.None;e.Handled=true;}
    }
    private void OnDragLeave(object sender,DragEventArgs e) {DropOutline.Visibility=Visibility.Collapsed;OrderMarker.Visibility=Visibility.Collapsed;}
    private void OnDrop(object sender,DragEventArgs e) => HandleDrop(e,_activeCategory??Store.DefaultCategoryId);
    private void HandleDrop(DragEventArgs e,string? categoryId,bool onCategory=false)
    {
        if(!onCategory && e.Data.GetDataPresent(ItemFormat)){OrderDrop(e);return;}
        e.Handled=true;DropOutline.Visibility=Visibility.Collapsed;OrderMarker.Visibility=Visibility.Collapsed;if(onCategory && categoryId is null && e.Data.GetDataPresent(ItemFormat)){e.Effects=DragDropEffects.None;SetStatus("全部按分类汇总，移动分类请拖到具体分类标签");return;}categoryId??=Store.DefaultCategoryId;
        if(!CanImport(e.Data)){e.Effects=DragDropEffects.None;return;}
        _interactions++;
        try
        {
            if(e.Data.GetDataPresent(ItemFormat) && e.Data.GetData(ItemFormat) is string id)Run(()=>Store.MoveItem(id,categoryId),"已移动分类");
            else
            {
                if(e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)foreach(var path in files){if(!Run(()=>Store.Collect(path,categoryId),"已收进轻匣"))break;}
                if(ShellIntegration.ContainsRecycleBin(e.Data) && !Store.State.Items.Any(i=>i.Kind=="recycle-bin"))Run(()=>Store.CollectRecycleBin(categoryId),"回收站已收进轻匣");
            }
            _activeCategory=categoryId;Render();ShellIntegration.RefreshDesktop();e.Effects=e.Data.GetDataPresent(ItemFormat)?DragDropEffects.Move:DragDropEffects.Copy;
        }
        finally{_interactions--;}
    }
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape && !_dragging){e.Handled=true;if(!KeepOpenForVerification)BeginHidePanel();return;}
        if(_shutter.Active)FinishPanelMotion();
        if(e.OriginalSource is not Button button || button.Tag is not NookItem)return;
        if(e.Key==Key.Apps || (e.Key==Key.F10&&Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))){e.Handled=true;ShowIconMenu(button,(NookItem)button.Tag);return;}
        if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var buttons=FindVisualChildren<Button>(Icons).ToList();var index=buttons.IndexOf(button);var offset=e.Key==Key.Left?-1:e.Key==Key.Right?1:e.Key==Key.Up?-IconColumns:IconColumns;var next=Math.Clamp(index+offset,0,buttons.Count-1);buttons[next].Focus();e.Handled=true;
        }
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject source) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(source);i++){var child=VisualTreeHelper.GetChild(source,i);if(child is T value)yield return value;foreach(var descendant in FindVisualChildren<T>(child))yield return descendant;}
    }
    public sealed record IconEntry(NookItem Item,string Path) {public string Id=>Item.Id;public string Name=>Item.Name;public bool Recycle=>Item.Kind=="recycle-bin";}
}

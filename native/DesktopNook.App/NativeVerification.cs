using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNook.Core;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class NativeVerification
{
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr window);
    public static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();var failure="";window.KeepOpenForVerification=true;
        try
        {
            if(!App.IsSandbox)throw new InvalidOperationException("界面自检只允许在 --sandbox 目录运行。");
            var optionKey=@"Software\DesktopNook\OptionVerification\"+Guid.NewGuid().ToString("N");
            try
            {
                var options=new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings");
                if(options.Enabled)throw new Exception("自启默认状态异常。");
                using(var unrelated=Registry.CurrentUser.CreateSubKey(optionKey+@"\Run"))unrelated.SetValue("OtherEntry","keep");
                options.SetEnabled(true,@"C:\验证 空格\轻匣.exe");
                if(!options.Enabled||options.Command!="\"C:\\验证 空格\\轻匣.exe\" --autostart")throw new Exception("自启路径引用异常。");
                options.CloseEndsProcess=false;if(new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings").CloseEndsProcess)throw new Exception("关闭行为设置未保留。");
                options.SetEnabled(false);using var check=Registry.CurrentUser.OpenSubKey(optionKey+@"\Run");if(options.Enabled||check?.GetValue("OtherEntry") as string!="keep")throw new Exception("关闭自启影响其他入口。");
                checks.Add("Startup option persists quoted command, can be disabled and preserves other entries");
                checks.Add("Close-to-exit preference persists independently of collection data");
                var preferencePath=Path.Combine(root,"preference-fixture.json");
                var persistent=new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings",preferencePath);
                persistent.PersistForUpdate();
                if(persistent.CloseEndsProcess)throw new Exception("迁移设置时把取消勾选变成了默认勾选。");
                persistent.WriteWindowSize(new(){Free=true,Locked=false,Width=800,Height=500});
                Registry.CurrentUser.DeleteSubKeyTree(optionKey+@"\Settings",false);
                persistent=new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings",preferencePath);
                if(persistent.CloseEndsProcess||persistent.ReadWindowSize().Locked||persistent.ReadWindowSize().Width!=800)throw new Exception("偏好文件未保留关闭和宽高比的取消勾选状态。");
                checks.Add("Unchecked close preference migrates durably and survives missing registry values");
            }
            finally{Registry.CurrentUser.DeleteSubKeyTree(optionKey,false);}
            var desktop=App.Store.DesktopRoots[0];
            var type=Type.GetTypeFromProgID("WScript.Shell")!;dynamic shell=Activator.CreateInstance(type)!;
            var source=Path.Combine(desktop,"验证快捷方式.lnk");dynamic shortcut=shell.CreateShortcut(source);
            try{shortcut.TargetPath=Environment.ProcessPath;shortcut.Arguments="--verification-marker --sandbox \""+root+"\"";shortcut.WorkingDirectory=desktop;shortcut.Save();}finally{Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
            var before=File.ReadAllBytes(source);var entry=App.Store.Collect(source,"office");
            if(!File.ReadAllBytes(App.Store.ManagedPath(entry)).SequenceEqual(before)||File.Exists(source))throw new Exception("真实 Shell Link 收纳校验失败。");
            window.Render();if(window.VisibleCount!=1)throw new Exception("入口未显示。");checks.Add("Real .lnk collection preserves binary contents, arguments and working directory");
            using(var menu=new ShellContextMenu(window,App.Store,entry))
            {
                var verbs=menu.RootVerbs();if(!verbs.Contains("open")||!verbs.Contains("properties")||!verbs.Contains("copy"))throw new Exception("系统快捷方式菜单内容不完整："+string.Join(",",verbs));
                if(menu.Build((_,_)=>{}).Count<3)throw new Exception("统一样式的 Shell 菜单未生成。");
                menu.InvokeVerb("open");
                for(var i=0;i<50&&!File.Exists(Path.Combine(root,"shortcut-launched.txt"));i++)await Task.Delay(100);
                if(!File.Exists(Path.Combine(root,"shortcut-launched.txt")))throw new Exception("Shell 菜单打开命令未执行。");
                File.Delete(Path.Combine(root,"shortcut-launched.txt"));
            }
            checks.Add("Real Shell context menu includes open, properties, copy and extension entries");
            checks.Add("Shell context-menu open verb executes the controlled shortcut successfully");
            window.TogglePanel();if(window.WindowState!=WindowState.Minimized)throw new Exception("第二次点击未收起。");window.TogglePanel();if(window.WindowState!=WindowState.Normal)throw new Exception("单击未打开。");
            checks.Add("Panel toggle opens and minimizes without terminating process");
            ShellIntegration.Launch(App.Store,entry);
            for(var i=0;i<40 && !File.Exists(Path.Combine(root,"shortcut-launched.txt"));i++)await Task.Delay(100);
            if(!File.Exists(Path.Combine(root,"shortcut-launched.txt")))throw new Exception("托管快捷方式的实际 ShellExecute 启动未完成。");checks.Add("Managed .lnk launches the real application with preserved quoted arguments");
            window.SelectCategory("dev");if(window.VisibleCount!=0)throw new Exception("标签筛选失败。");
            App.Store.MoveItem(entry.Id,"dev");window.Render();if(window.VisibleCount!=1)throw new Exception("移动分类失败。");checks.Add("Native category tabs and reassignment");
            App.Store.Restore(entry.Id);if(!File.ReadAllBytes(source).SequenceEqual(before)||App.Store.State.Items.Count!=0)throw new Exception("真实 Shell Link 恢复失败。");checks.Add("Real .lnk restored exactly, vault recovery copy retained");
            for(var i=0;i<7;i++)
            {
                var file=Path.Combine(desktop,"示例应用 "+(i+1)+".url");File.WriteAllText(file,"[InternetShortcut]\r\nURL=https://example.invalid/"+i+"\r\n");App.Store.Collect(file,i<3?"office":"dev");
            }
            window.SelectCategory(null);if(window.VisibleCount!=7)throw new Exception("六列网格数据数量异常。");checks.Add("Native six-column grid with a seventh icon");
            var registryPath=@"Software\DesktopNook\Verification\"+Guid.NewGuid().ToString("N");
            var registry=new WindowsRecycleBin(registryPath);var snapshot=registry.Capture();
            try{registry.Hide();if(registry.IsVisible)throw new Exception("隔离注册表隐藏失败。");registry.Restore(snapshot);if(!registry.IsVisible)throw new Exception("隔离注册表恢复失败。");checks.Add("Recycle Bin registry implementation tested under isolated HKCU test key; live desktop not changed");}
            finally{Registry.CurrentUser.DeleteSubKeyTree(registryPath,false);}
            App.Store.CollectRecycleBin("uncategorized");var bin=App.Store.State.Items.Single(i=>i.Kind=="recycle-bin");App.Store.Restore(bin.Id);checks.Add("System-item integration with isolated desktop policy");
            if(ShellIntegration.ContainsRecycleBinCida([1,0,0,0,255,255,255,255,255,255,255,255]))throw new Exception("无效 Shell IDList 被接受。");checks.Add("Invalid shell drag payload rejected");
            if(!ShellIntegration.ContainsRecycleBinCida(ShellIntegration.RecyclePayloadForVerification()))throw new Exception("真实系统回收站的 Shell 拖放标识未被识别。");checks.Add("Recycle Bin shell PIDL drag payload recognized without changing desktop settings");
            await OleDragVerification.Run(window);checks.Add("Real mouse and OLE roundtrip between owned windows using isolated desktop directory");
            await OleDragVerification.Run(window,"file");checks.Add("Regular document uses actual native OLE drag in and out");
            await OleDragVerification.Run(window,"folder");checks.Add("Complete folder uses actual native OLE drag in and out");
            var minimize=FindNamedButton(window,"最小化轻匣");minimize.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if(window.WindowState!=WindowState.Minimized)throw new Exception("最小化按钮未收起。");window.ShowPanel();checks.Add("Minimize button preserves process and collection state");
            window.Render();window.SetStatus("隔离验证模式 · 不操作真实桌面");
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var positions=window.IconPositions();
            if(positions.Count!=7 || positions.Take(6).Any(p=>Math.Abs(p.Y-positions[0].Y)>1) || positions[6].Y-positions[0].Y>90)throw new Exception("图标行距或六列排列与紧凑布局不符。");
            checks.Add("Six icons share the first row, seventh wraps with compact row spacing");
            if(Math.Abs(window.ActualWidth-716.8)>2 || Math.Abs(window.ActualHeight-627.2)>2)throw new Exception("整体放大 40% 的尺寸校验失败。");
            var bottom=new TaskbarAnchor(new Rect(100,1040,44,40),new Rect(0,1040,1920,40),new Rect(0,0,1920,1040),96);
            var placement=TaskbarPlacement.Calculate(bottom,716.8,627.2);
            if(placement.Left!=100 || Math.Abs(placement.Bottom-1032)>1)throw new Exception("任务栏左边缘对齐校验失败。");
            var edge=TaskbarPlacement.Calculate(bottom with{Button=new Rect(1820,1040,44,40)},716.8,627.2);
            if(edge.Right>1920)throw new Exception("任务栏边缘溢出。");
            var secondary=TaskbarPlacement.Calculate(new(new Rect(-1800,1040,44,40),new Rect(-1920,1040,1920,40),new Rect(-1920,0,1920,1040),144),716.8,627.2);
            if(secondary.Left!=-1800 || Math.Abs(secondary.Width-1075.2)>1)throw new Exception("副屏 DPI 换算校验失败。");
            checks.Add("40 percent larger surfaces and taskbar alignment, screen edges and 150 percent DPI math");
            var actualAnchor=await TaskbarPlacement.FindAsync();
            if(actualAnchor is null)throw new Exception("本机任务栏未找到轻匣按钮。");
            File.WriteAllText(Path.Combine(root,"taskbar-anchor.json"),JsonSerializer.Serialize(new{button=actualAnchor.Button.ToString(),tray=actualAnchor.Tray.ToString(),work=actualAnchor.Work.ToString(),dpi=actualAnchor.Dpi}));
            checks.Add("Live taskbar button bounds discovered using application identity");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"native-preview.png")))encoder.Save(file);
            checks.Add("Native window rendered at 716.8x627.2 with production icon");
            window.ShowIconMenu(window.IconButton(App.Store.State.Items[0].Id),App.Store.State.Items[0]);
            try
            {
                await SaveMenuPreview(window.LastMenu??throw new Exception("右键菜单没有打开。"),Path.Combine(root,"context-menu-preview.png"));
                checks.Add("Context-menu preview contains visible menu background and text after popup rendering");
            }
            finally{if(window.LastMenu is not null)window.LastMenu.IsOpen=false;}
            await VerifyMenuLayout(window,root,checks);
            var stateHash=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));
            window.SetAvailableRelease(new OnlineRelease(new Version(9,9,9),new Uri($"https://github.com/{OnlineUpdater.Repository}/releases/download/v9.9.9/{OnlineUpdater.AssetName}"),1,new string('0',64),"演示更新"));
            window.UpdateLayout();
            if(window.UpdateAvailableButton.Visibility!=Visibility.Visible||!window.UpdateAvailableButton.Content.ToString()!.Contains("v9.9.9"))throw new Exception("新版本没有显示可点击提示。");
            if(Directory.Exists(Path.Combine(App.Store.DataDirectory,"updates"))||LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))!=stateHash)throw new Exception("发现新版时不应自动下载或改动数据。");
            checks.Add("New release shows an actionable version notice without downloading or changing collection data");
            window.SetAvailableRelease(null);
            if(window.UpdateAvailableButton.Visibility!=Visibility.Collapsed)throw new Exception("没有新版本时提示未隐藏。");
            checks.Add("No new release hides the update notice");
            Exception? dialogFailure=null;
            _=window.Dispatcher.BeginInvoke(()=>
            {
                var dialog=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="更新布局验证");
                try
                {
                dialog.UpdateLayout();
                if(dialog.ActualWidth>390||dialog.ActualHeight>window.ActualHeight)throw new Exception("更新确认窗口比例过大。");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),(int)Math.Ceiling(dialog.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"update-dialog-preview.png")))png.Save(file);
                FindNamedButton(dialog,"取消").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
                catch(Exception ex){dialogFailure=ex;dialog.DialogResult=false;}
            },DispatcherPriority.ContextIdle);
            if(UiDialogs.ShowUpdate(window,"当前版本："+MainWindow.CurrentVersionLabel+"\n更新到：v9.9.9\n\n"+new string('更',600),"更新布局验证",MessageBoxButton.OKCancel)!=MessageBoxResult.Cancel)throw new Exception("取消更新未生效。");
            if(dialogFailure is not null)throw dialogFailure;
            checks.Add("Compact update confirmation fits panel and can be cancelled without downloading");
            await BrandVerification.Run(window,root,checks);
            if(Environment.GetCommandLineArgs().Contains("--leave-payloads"))
            {
                App.Store.Collect(Path.Combine(desktop,"真实文档拖放验证.txt"),"office");App.Store.Collect(Path.Combine(desktop,"真实文件夹拖放验证"),"office");App.Store.CollectRecycleBin("uncategorized");checks.Add("Uninstall fixture retains document, folder and system item alongside shortcuts");
            }
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"native-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks,realDesktopModified=false},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(failure.Length==0?0:1);
        }
    }
    private static async Task VerifyMenuLayout(MainWindow window,string root,List<string> checks)
    {
        var textMetrics=new List<object>();
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
        void CheckBounds(System.Windows.Controls.ContextMenu menu)
        {
            var visual=(FrameworkElement)PresentationSource.FromVisual(menu)!.RootVisual;
            if(visual.ActualHeight>window.ActualHeight-20||visual.ActualWidth>window.ActualWidth-20)
                throw new Exception($"菜单超出面板可用尺寸：{visual.ActualWidth:F1}×{visual.ActualHeight:F1}。");
            CheckTextRendering(visual);
        }
        IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
        {
            for(int n=0;n<VisualTreeHelper.GetChildrenCount(parent);n++){var child=VisualTreeHelper.GetChild(parent,n);if(child is T match)yield return match;foreach(var nested in Find<T>(child))yield return nested;}
        }
        void CheckTextRendering(FrameworkElement visual)
        {
            var source=(System.Windows.Interop.HwndSource)PresentationSource.FromVisual(visual)!;
            double nativeDpi=GetDpiForWindow(source.Handle)/96d;
            if(Math.Abs(VisualTreeHelper.GetDpi(visual).DpiScaleX-nativeDpi)>.001)throw new Exception("菜单渲染 DPI 与当前弹出窗口 DPI 不一致。");
            var texts=Find<System.Windows.Controls.TextBlock>(visual).Where(t=>!string.IsNullOrWhiteSpace(t.Text)).ToList();
            if(texts.Count==0)throw new Exception("菜单没有可检查的文字。");
            foreach(var text in texts)
            {
                if(TextOptions.GetTextFormattingMode(text)!=TextFormattingMode.Display||!text.UseLayoutRounding)throw new Exception("菜单文字没有使用屏幕像素对齐排版。");
                var transformed=text.TransformToAncestor(visual).TransformBounds(new Rect(0,0,1,1));
                if(Math.Abs(transformed.Width-1)>.001||Math.Abs(transformed.Height-1)>.001)throw new Exception("菜单文字仍有整体缩放。");
                for(DependencyObject? current=text;current is not null;current=VisualTreeHelper.GetParent(current))
                {
                    if(current is UIElement element&&(element.Effect is not null||element.CacheMode is BitmapCache))throw new Exception("菜单文字被阴影效果或位图缓存包裹。");
                    if(ReferenceEquals(current,visual))break;
                }
            }
            textMetrics.Add(new{nativeDpi=nativeDpi*96,wpfDpi=VisualTreeHelper.GetDpi(visual).PixelsPerInchX,textElements=texts.Count,formatting="Display",textScale=1,effectAncestors=0});
        }
        foreach(double preset in new[]{.8,1,1.5})
        {
            window.SelectWindowSize(preset);await Layout();
            FindNamedButton(window,"更多操作").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var menu=window.LastMenu!;
            try
            {
                await SaveMenuPreview(menu,Path.Combine(root,$"more-menu-{preset*100:0}.png"));CheckBounds(menu);
                if(!menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i=>i.Header as string=="退出并结束进程"))throw new Exception("适配菜单丢失退出入口。");
                checks.Add($"More menu fits {preset*100:0}% panel with all commands retained");
            }
            finally{menu.IsOpen=false;}
        }
        window.SelectWindowSize(reset:true);await Layout();
        var tab=window.Tabs.Children.OfType<System.Windows.Controls.Button>().Single(b=>b.Tag as string=="office");
        window.CategoryMenu(tab,"office");
        try{await SaveMenuPreview(window.LastMenu!,Path.Combine(root,"category-menu-preview.png"));CheckBounds(window.LastMenu!);checks.Add("Category menu uses the same compact layout");}
        finally{window.LastMenu!.IsOpen=false;}
        window.SelectWindowSize();window.Width=560;window.Height=360;await Layout();
        window.ShowIconMenu(window.IconButton(App.Store.State.Items[0].Id),App.Store.State.Items[0]);
        var iconMenu=window.LastMenu!;
        try
        {
            await SaveMenuPreview(iconMenu,Path.Combine(root,"compact-icon-menu.png"));CheckBounds(iconMenu);
            var scroll=Find<System.Windows.Controls.ScrollViewer>(iconMenu).First();
            if(scroll.ScrollableHeight<=0)throw new Exception("长菜单未提供滚动区域。");
            scroll.ScrollToEnd();await Layout();
            if(scroll.VerticalOffset<=0)throw new Exception("长菜单无法滚动到末尾命令。");
            await SaveMenuPreview(iconMenu,Path.Combine(root,"compact-icon-menu-end.png"));
            scroll.ScrollToHome();await Layout();
            var move=iconMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i=>i.Header as string=="移动到分类");
            for(int n=0;n<40;n++)move.Items.Add(new System.Windows.Controls.MenuItem{Header=$"较长分类名称用于验证宽度与滚动 {n+1}"});
            move.IsSubmenuOpen=true;await Layout();
            var popup=(System.Windows.Controls.Primitives.Popup)move.Template.FindName("PART_Popup",move);
            var body=(FrameworkElement)popup.Child;
            await SaveMenuPreview(iconMenu,Path.Combine(root,"compact-submenu.png"),body);
            var submenuRoot=(FrameworkElement)PresentationSource.FromVisual(body)!.RootVisual;
            CheckTextRendering(submenuRoot);
            if(submenuRoot.ActualHeight>window.ActualHeight-20||submenuRoot.ActualWidth>window.ActualWidth-20)throw new Exception("子菜单尺寸未跟随面板约束。");
            var submenuScroll=Find<System.Windows.Controls.ScrollViewer>(body).First();
            if(submenuScroll.ScrollableHeight<=0)throw new Exception("长子菜单无法滚动。");
            submenuScroll.ScrollToEnd();await Layout();
            if(submenuScroll.VerticalOffset<=0)throw new Exception("子菜单末尾项目不可访问。");
            move.IsSubmenuOpen=false;
            checks.Add("Small free-size panel bounds long icon and nested menus while keeping last commands scrollable");
            checks.Add("Root and nested menu text uses native popup DPI, pixel-aligned formatting and no scaled/effect-cached text layer");
            File.WriteAllText(Path.Combine(root,"menu-text-dpi-verification.json"),JsonSerializer.Serialize(new{success=true,samples=textMetrics,physicalMixedDpiMonitorsTested=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{iconMenu.IsOpen=false;window.SelectWindowSize(reset:true);await Layout();}
    }
    private static async Task SaveMenuPreview(System.Windows.Controls.ContextMenu menu,string path,FrameworkElement? popupChild=null)
    {
        // ContextIdle can run before the popup's opening animation paints.
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(DateTime.UtcNow<deadline)
        {
            await Task.Delay(50);
            await menu.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
            if(!menu.IsOpen)throw new Exception("截图前右键菜单已关闭。");
            if(PresentationSource.FromVisual(popupChild??(FrameworkElement)menu)?.RootVisual is not FrameworkElement root)continue;
            root.UpdateLayout();
            var dpi=VisualTreeHelper.GetDpi(root);
            int width=(int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),height=(int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY);
            if(width<=0||height<=0)continue;
            var image=new RenderTargetBitmap(width,height,96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
            image.Render(root);
            var pixels=new byte[width*height*4];image.CopyPixels(pixels,width*4,0);
            int visible=0,light=0,dark=0;
            for(int n=0;n<pixels.Length;n+=4)
            {
                if(pixels[n+3]<200)continue;
                visible++;
                int brightness=pixels[n]+pixels[n+1]+pixels[n+2];
                if(brightness>650)light++;
                if(brightness<500)dark++;
            }
            if(visible<width*height/10||light<100||dark<20)continue;
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
            using var file=File.Create(path);png.Save(file);return;
        }
        throw new Exception("右键菜单截图为空或缺少可见文字，未作为通过记录保存。");
    }
    private static System.Windows.Controls.Button FindNamedButton(DependencyObject root,string name)
    {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is System.Windows.Controls.Button button&&(System.Windows.Automation.AutomationProperties.GetName(button)==name||button.Content as string==name))return button;try{return FindNamedButton(child,name);}catch(InvalidOperationException){}}
        throw new InvalidOperationException("未找到按钮："+name);
    }
}

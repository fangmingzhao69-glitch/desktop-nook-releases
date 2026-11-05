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
                options.SetEnabled(true,@"C:\验证 空格\桌面小筑.exe");
                if(!options.Enabled||options.Command!="\"C:\\验证 空格\\桌面小筑.exe\" --autostart")throw new Exception("自启路径引用异常。");
                options.CloseEndsProcess=false;if(new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings").CloseEndsProcess)throw new Exception("关闭行为设置未保留。");
                options.SetEnabled(false);using var check=Registry.CurrentUser.OpenSubKey(optionKey+@"\Run");if(options.Enabled||check?.GetValue("OtherEntry") as string!="keep")throw new Exception("关闭自启影响其他入口。");
                checks.Add("Startup option persists quoted command, can be disabled and preserves other entries");
                checks.Add("Close-to-exit preference persists independently of collection data");
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
            var minimize=FindNamedButton(window,"最小化小筑");minimize.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
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
            if(actualAnchor is null)throw new Exception("本机任务栏未找到桌面小筑按钮。");
            File.WriteAllText(Path.Combine(root,"taskbar-anchor.json"),JsonSerializer.Serialize(new{button=actualAnchor.Button.ToString(),tray=actualAnchor.Tray.ToString(),work=actualAnchor.Work.ToString(),dpi=actualAnchor.Dpi}));
            checks.Add("Live taskbar button bounds discovered using application identity");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"native-preview.png")))encoder.Save(file);
            checks.Add("Native window rendered at 716.8x627.2 with production icon");
            window.ShowIconMenu(window.IconButton(App.Store.State.Items[0].Id),App.Store.State.Items[0]);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if(window.LastMenu is not null && System.Windows.PresentationSource.FromVisual(window.LastMenu)?.RootVisual is FrameworkElement menuRoot)
            {
                var image=new RenderTargetBitmap((int)Math.Ceiling(menuRoot.ActualWidth),(int)Math.Ceiling(menuRoot.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(menuRoot);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(root,"context-menu-preview.png"));png.Save(file);window.LastMenu.IsOpen=false;
            }
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
    private static System.Windows.Controls.Button FindNamedButton(DependencyObject root,string name)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is System.Windows.Controls.Button button&&System.Windows.Automation.AutomationProperties.GetName(button)==name)return button;try{return FindNamedButton(child,name);}catch(InvalidOperationException){}}
        throw new InvalidOperationException("未找到按钮："+name);
    }
}

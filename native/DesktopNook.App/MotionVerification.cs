using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;

namespace DesktopNook.App;
internal static class MotionVerification
{
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,int message,IntPtr wParam,IntPtr lParam);
    internal static async Task Run(MainWindow owner,string root,List<string> checks)
    {
        if(!App.IsSandbox)throw new InvalidOperationException("动画检查只允许隔离目录。");
        string mode=App.Options.MotionMode,view=App.Options.DefaultCollectView;bool closing=App.Options.CloseEndsProcess;
        var hash=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));
        var tile=new ToggleButton{Content="选择反馈",Style=(Style)Application.Current.FindResource("CollectSelectionTile"),Width=130,Height=80};
        var panel=new Border{Child=tile,Padding=new Thickness(10)};
        var host=new Window{Content=panel,Width=180,Height=130,Left=-32000,Top=-32000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};
        int passed=0;
        void Check(bool condition,string text){if(!condition)throw new Exception(text);checks.Add(text);passed++;}
        async Task Settle(){await Task.Delay(650);await owner.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
        try
        {
            UiMotion.Configure("on");host.Show();host.UpdateLayout();await owner.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            UiMotion.Reveal(panel);Check(panel.HasAnimatedProperties&&UiMotion.ActiveClocks>=2,"Enabled reveal creates short-lived opacity/translation clocks");
            await Settle();Check(!panel.HasAnimatedProperties&&panel.RenderTransform.Value.IsIdentity&&panel.Opacity==1&&UiMotion.ActiveClocks==0,"Completed reveal restores opacity/transform and releases all clocks");
            UiMotion.Reveal(panel);await Task.Delay(30);UiMotion.Reveal(panel);UiMotion.StopReveal(panel);
            Check(panel.Opacity==1&&panel.RenderTransform.Value.IsIdentity&&UiMotion.ActiveClocks==0,"Interrupted/replaced reveal can be cancelled without residual visual state");
            tile.IsChecked=true;Check(tile.IsChecked==true,"Selection changes immediately without waiting for animation");
            await Settle();var surface=(Border)tile.Template.FindName("Tile",tile);
            Check(surface.Background is SolidColorBrush brush&&brush.Color==Color.FromRgb(221,242,255)&&UiMotion.ActiveClocks==0,"Selection feedback settles to the exact highlight without idle clocks");
            UiMotion.Reveal(panel);UiMotion.Configure("off");
            UiMotion.Reveal(panel);Check(!panel.HasAnimatedProperties&&UiMotion.ActiveClocks==0&&panel.Opacity==1,"Turning motion off finishes current transitions and prevents new clocks");
            UiMotion.Configure("system");Check(UiMotion.Enabled==SystemParameters.ClientAreaAnimation,"System mode follows the Windows client-area animation preference");
            UiMotion.Configure("on");owner.ShowPanel();
            await Settle();owner.BeginHidePanel();
            Check(owner.WindowState==WindowState.Normal&&owner.PanelMotion.Active&&owner.PanelMotion.Closing,"Close keeps the native window visible while shutters play");
            Check(owner.PanelMotion.LastStartOrder.SequenceEqual(new[]{0,1,2,3,4}),"Closing shutters start from top to bottom");
            await Task.Delay(150);Check(owner.PanelMotion.Angles.Any(a=>a>1&&a<83),"Closing produces intermediate shutter angles through matching 2D projection");
            await Settle();Check(owner.WindowState==WindowState.Minimized&&!owner.PanelMotion.Active&&owner.PanelMotion.SnapshotBytes==0,"Completed close minimizes and releases snapshot and clocks");
            owner.ShowPanel();Check(owner.PanelMotion.Active&&owner.PanelMotion.LastStartOrder.SequenceEqual(new[]{4,3,2,1,0}),"Opening shutters start from bottom to top");
            Check(owner.PanelMotion.SnapshotBytes<=8*1024*1024+16384&&owner.PanelMotion.Failure is null,"Five strips share one bounded snapshot without graphics errors");
            await Settle();Check(!owner.PanelMotion.Active&&owner.PanelMotion.SnapshotBytes==0,"Completed opening releases active shutter resources");
            int allocations=owner.PanelMotion.BufferAllocations;
            owner.BeginHidePanel();await Settle();owner.ShowPanel();await Settle();
            Check(owner.PanelMotion.BufferAllocations==allocations&&owner.PanelMotion.BufferedBytes is >0 and <=8*1024*1024,"Repeated opening/closing reuses one bounded snapshot buffer");
            owner.BeginHidePanel();await Settle();owner.ReleaseIdleResources();
            Check(owner.PanelMotion.BufferedBytes==0&&ShellIntegration.CachedIconBytes<=2*1024*1024&&ShellIntegration.CachedIconCount<=64,"Idle release drops screenshot and trims the warm icon cache");
            var icons=VisualIcons(owner).ToArray();
            Check(icons.Length>0&&icons.All(i=>i.Source is null),"Minimized panel pauses icon controls and clears their image references");
            await Task.Delay(100);Check(icons.All(i=>i.Source is null),"Late asynchronous icon delivery cannot repopulate paused controls");
            owner.ShowPanel();await Settle();Check(VisualIcons(owner).Any(i=>i.Source is not null),"Restoring a trimmed panel resumes icon delivery without losing items");
            for(int n=0;n<4;n++){owner.TogglePanel();await Task.Delay(70);owner.TogglePanel();}
            await Settle();Check(owner.WindowState==WindowState.Normal&&UiMotion.ActiveClocks==0&&!owner.PanelMotion.Active,"Rapid reversed panel toggles obey the last request without delayed minimization");
            owner.BeginHidePanel();await Task.Delay(60);UiMotion.Configure("off");Check(owner.WindowState==WindowState.Minimized&&!owner.PanelMotion.Active,"Turning motion off during close commits hiding immediately");
            owner.ShowPanel();Check(!owner.PanelMotion.Active,"Motion-off restore skips snapshots and shutters");UiMotion.Configure("on");
            var hwnd=new WindowInteropHelper(owner).Handle;
            SendMessage(hwnd,0x112,(IntPtr)0xf020,IntPtr.Zero);
            Check(owner.PanelMotion.Closing&&owner.WindowState==WindowState.Normal,"Native taskbar minimize command waits for closing shutters");
            await Settle();Check(owner.WindowState==WindowState.Minimized,"Native minimize commits after shutter completion");
            owner.ShowPanel();await Task.Delay(90);owner.WindowState=WindowState.Minimized;
            Check(!owner.PanelMotion.Active&&owner.PanelMotion.SnapshotBytes==0,"Externally forced minimization releases an interrupted shutter immediately");
            owner.ShowPanel();await Settle();
            using var process=Process.GetCurrentProcess();process.Refresh();long beforePrivate=process.PrivateMemorySize64;double beforeCpu=process.TotalProcessorTime.TotalMilliseconds;
            var watch=Stopwatch.StartNew();
            for(int n=0;n<100;n++){owner.BeginHidePanel();await Task.Delay(5);owner.ShowPanel();owner.FinishPanelMotion();UiMotion.Reveal(panel,60);UiMotion.StopReveal(panel);}
            await Settle();process.Refresh();
            Check(UiMotion.ActiveClocks==0&&!owner.PanelMotion.Active&&owner.PanelMotion.SnapshotBytes==0&&owner.WindowState==WindowState.Normal&&panel.RenderTransform.Value.IsIdentity,"100 shutter close/reverse/cancel cycles leave no clocks or snapshots");
            long onElapsed=watch.ElapsedMilliseconds,onPrivate=process.PrivateMemorySize64;double onCpu=process.TotalProcessorTime.TotalMilliseconds-beforeCpu;
            double idleStart=process.TotalProcessorTime.TotalMilliseconds;await Task.Delay(500);process.Refresh();double onIdleCpu=process.TotalProcessorTime.TotalMilliseconds-idleStart;
            UiMotion.Configure("off");watch.Restart();process.Refresh();long offPrivateBefore=process.PrivateMemorySize64;double offCpuBefore=process.TotalProcessorTime.TotalMilliseconds;
            for(int n=0;n<100;n++){owner.BeginHidePanel();await Task.Delay(5);owner.ShowPanel();owner.FinishPanelMotion();UiMotion.Reveal(panel,60);UiMotion.StopReveal(panel);}
            await Settle();process.Refresh();long offElapsed=watch.ElapsedMilliseconds,offPrivate=process.PrivateMemorySize64;double offCpu=process.TotalProcessorTime.TotalMilliseconds-offCpuBefore;
            idleStart=process.TotalProcessorTime.TotalMilliseconds;await Task.Delay(500);process.Refresh();double offIdleCpu=process.TotalProcessorTime.TotalMilliseconds-idleStart;
            Check(UiMotion.ActiveClocks==0,"Equivalent motion-off stress also leaves no active clocks");
            File.WriteAllText(Path.Combine(root,"motion-stress.json"),JsonSerializer.Serialize(new{cyclesPerMode=100,on=new{elapsedMs=onElapsed,cpuMs=onCpu,privateBefore=beforePrivate,privateAfter=onPrivate,idleSampleMs=500,idleCpuMs=onIdleCpu},off=new{elapsedMs=offElapsed,cpuMs=offCpu,privateBefore=offPrivateBefore,privateAfter=offPrivate,idleSampleMs=500,idleCpuMs=offIdleCpu},activeClocks=UiMotion.ActiveClocks,forcedGc=false},new JsonSerializerOptions{WriteIndented=true}));
            UiMotion.Configure("on");
            UiMotion.Reveal(panel);host.Close();await Settle();Check(UiMotion.ActiveClocks==0,"Closing a window cancels motion for its unloaded content");
            App.Options.MotionMode="off";App.Options.DefaultCollectView="details";App.Options.CloseEndsProcess=!closing;App.Options.WriteWindowSize(App.Options.ReadWindowSize());App.Options.PersistForUpdate();
            var reopened=new StartupOptions(preferencesPath:Path.Combine(App.Store.DataDirectory,"preferences.json"));
            Check(reopened.MotionMode=="off"&&reopened.DefaultCollectView=="details"&&reopened.CloseEndsProcess==!closing,"Motion preference survives other settings and fresh preference readers");
            Check(LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))==hash,"Motion and preference stress do not change collection/index ordering");
        }
        finally{host.Close();App.Options.CloseEndsProcess=closing;App.Options.DefaultCollectView=view;App.Options.MotionMode=mode;UiMotion.Configure(mode);File.WriteAllText(Path.Combine(root,"motion-verification.json"),JsonSerializer.Serialize(new{passed,activeClocks=UiMotion.ActiveClocks}));}
    }
    private static IEnumerable<ShellIcon> VisualIcons(DependencyObject root)
    {
        for(int n=0;n<VisualTreeHelper.GetChildrenCount(root);n++){var child=VisualTreeHelper.GetChild(root,n);if(child is ShellIcon icon)yield return icon;foreach(var nested in VisualIcons(child))yield return nested;}
    }
}

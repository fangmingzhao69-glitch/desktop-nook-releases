using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNook.App;
internal static class MotionVerification
{
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
        async Task Settle(){await Task.Delay(260);await owner.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
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
            for(int n=0;n<4;n++){owner.TogglePanel();Check(owner.WindowState==WindowState.Minimized&&UiMotion.ActiveClocks==0,"Minimize immediately cancels visual motion #"+n);owner.TogglePanel();}
            await Settle();Check(owner.WindowState==WindowState.Normal&&UiMotion.ActiveClocks==0,"Rapid panel toggles obey the last requested state without delayed minimization");
            using var process=Process.GetCurrentProcess();process.Refresh();long beforePrivate=process.PrivateMemorySize64;double beforeCpu=process.TotalProcessorTime.TotalMilliseconds;
            var watch=Stopwatch.StartNew();
            for(int n=0;n<100;n++){UiMotion.Reveal(panel,60);UiMotion.Reveal(panel,60);UiMotion.StopReveal(panel);await Task.Delay(5);}
            await Settle();process.Refresh();
            Check(UiMotion.ActiveClocks==0&&panel.RenderTransform.Value.IsIdentity,"100 repeated reveal/replace/cancel cycles leave no clocks or transforms");
            long onElapsed=watch.ElapsedMilliseconds,onPrivate=process.PrivateMemorySize64;double onCpu=process.TotalProcessorTime.TotalMilliseconds-beforeCpu;
            double idleStart=process.TotalProcessorTime.TotalMilliseconds;await Task.Delay(500);process.Refresh();double onIdleCpu=process.TotalProcessorTime.TotalMilliseconds-idleStart;
            UiMotion.Configure("off");watch.Restart();process.Refresh();long offPrivateBefore=process.PrivateMemorySize64;double offCpuBefore=process.TotalProcessorTime.TotalMilliseconds;
            for(int n=0;n<100;n++){UiMotion.Reveal(panel,60);UiMotion.Reveal(panel,60);UiMotion.StopReveal(panel);await Task.Delay(5);}
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
}

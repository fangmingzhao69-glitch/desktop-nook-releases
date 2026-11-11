using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace DesktopNook.App;
internal static class ResidencyVerification
{
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
    internal static async Task Run(MainWindow owner,string root)
    {
        var rows=new List<object>();var invalidations=new Dictionary<string,int>();string failure="";
        try
        {
            if(!App.IsSandbox)throw new InvalidOperationException("常驻检查仅允许沙箱。");
            owner.KeepOpenForVerification=true;UiMotion.Configure("on");
            for(int n=0;n<58;n++){var path=Path.Combine(App.Store.DesktopRoots[0],$"入口{n}.url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(path,"office");}
            owner.Render();await Task.Delay(3000);string hash=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));
            owner.PanelMotion.InvalidationObserver=reason=>invalidations[reason]=invalidations.GetValueOrDefault(reason)+1;
            using var process=Process.GetCurrentProcess();
            void Sample(string stage){process.Refresh();rows.Add(new{stage,parked=owner.ContentParked,captures=owner.PanelMotion.BufferCaptures,allocations=owner.PanelMotion.BufferAllocations,buffered=owner.PanelMotion.BufferedBytes,activeClocks=owner.PanelMotion.ActiveClocks,cacheBytes=ShellIntegration.CachedIconBytes,cacheCount=ShellIntegration.CachedIconCount,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,managedBytes=GC.GetTotalMemory(false),invalidations=new Dictionary<string,int>(invalidations)});}
            Sample("before");
            for(int n=0;n<10;n++){owner.BeginHidePanel();await Task.Delay(650);owner.ShowPanel();await Task.Delay(650);Sample("cycle-"+n);}
            owner.IconsScroll.ScrollToVerticalOffset(120);owner.UpdateLayout();await Task.Delay(100);double scroll=owner.IconsScroll.VerticalOffset;
            owner.BeginHidePanel();await Task.Delay(3500);Sample("idle-3s");await Task.Delay(3000);Sample("idle-6s");await Task.Delay(9000);Sample("idle-15s");
            if(!owner.ContentParked||owner.PanelMotion.BufferedBytes!=0||owner.PanelMotion.Active||ShellIntegration.CachedIconBytes>2*1024*1024)throw new Exception("闲置资源核验失败。");
            var watch=Stopwatch.StartNew();var frame=new TaskCompletionSource<double>();EventHandler? rendered=null;bool flash=false;int maskedFrames=0;
            rendered=(_,_)=>
            {
                if(owner.WindowState!=WindowState.Normal||owner.ContentParked||!owner.Surface.IsVisible)return;
                if(owner.Opacity<=.01){maskedFrames++;return;}
                if(!owner.PanelMotion.Active){if(owner.Surface.Opacity>.01)flash=true;else maskedFrames++;return;}
                if(!owner.PanelMotion.Angles.Any(a=>a<83))return;
                System.Windows.Media.CompositionTarget.Rendering-=rendered;frame.TrySetResult(watch.Elapsed.TotalMilliseconds);
            };
            System.Windows.Media.CompositionTarget.Rendering+=rendered;
            SendMessage(new System.Windows.Interop.WindowInteropHelper(owner).Handle,0x112,(IntPtr)0xf120,IntPtr.Zero);
            double showMs=watch.Elapsed.TotalMilliseconds;
            var winner=await Task.WhenAny(frame.Task,Task.Delay(1500));System.Windows.Media.CompositionTarget.Rendering-=rendered;
            if(winner!=frame.Task)throw new Exception("恢复后未收到绘制帧。");
            rows.Add(new{stage="wake-timing",showCallMs=showMs,firstShutterRenderingCallbackMs=await frame.Task,maskedFrames,flash});
            if(flash)throw new Exception("原生恢复先显示了未遮挡的整块面板。");
            await Task.Delay(650);Sample("restored");
            if(owner.ContentParked||owner.WindowState!=WindowState.Normal||owner.VisibleCount!=58||Math.Abs(owner.IconsScroll.VerticalOffset-scroll)>.1||LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))!=hash)throw new Exception("恢复后的视图、滚动或索引核验失败。");
            for(int n=0;n<3;n++)
            {
                owner.BeginHidePanel();await Task.Delay(6200);Sample("repeat-idle-"+n);
                if(!owner.ContentParked||owner.PanelMotion.BufferedBytes!=0)throw new Exception("重复闲置未释放绘制资源。");
                SendMessage(new System.Windows.Interop.WindowInteropHelper(owner).Handle,0x112,(IntPtr)0xf120,IntPtr.Zero);await Task.Delay(700);Sample("repeat-wake-"+n);
                if(owner.ContentParked||owner.WindowState!=WindowState.Normal||Math.Abs(owner.IconsScroll.VerticalOffset-scroll)>.1)throw new Exception("重复原生恢复丢失状态。");
            }
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{owner.PanelMotion.InvalidationObserver=null;File.WriteAllText(Path.Combine(root,"residency-verification.json"),JsonSerializer.Serialize(new{failure,forcedGc=false,rows},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure.Length==0?0:1);}
    }
}

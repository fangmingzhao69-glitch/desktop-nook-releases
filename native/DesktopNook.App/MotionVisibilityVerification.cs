using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;
internal static class MotionVisibilityVerification
{
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)]private struct Bounds{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out Bounds bounds);
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int width,int height);
    [DllImport("gdi32.dll")]private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")]private static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint flags);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(IntPtr h);
    private static void Capture(IntPtr window,string path,Bounds? fixedBounds=null)
    {
        if(!GetWindowRect(window,out var b))throw new IOException("无法读取测试窗口边界。");
        if(fixedBounds.HasValue)b=fixedBounds.Value;
        var screen=GetDC(IntPtr.Zero);var dc=CreateCompatibleDC(screen);var bitmap=CreateCompatibleBitmap(screen,b.Right-b.Left,b.Bottom-b.Top);var old=SelectObject(dc,bitmap);
        try
        {
            if(!BitBlt(dc,0,0,b.Right-b.Left,b.Bottom-b.Top,screen,b.Left,b.Top,0x40CC0020))throw new IOException("屏幕帧采集失败。");
            var image=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);png.Save(file);
        }
        finally{SelectObject(dc,old);DeleteObject(bitmap);DeleteDC(dc);ReleaseDC(IntPtr.Zero,screen);}
    }
    internal static async Task Run(MainWindow owner,string root)
    {
        var rows=new List<object>();var events=new List<object>();var clock=Stopwatch.StartNew();string failure="";Window? backdrop=null;
        try
        {
            if(!App.IsSandbox)throw new InvalidOperationException("可见性检查仅允许沙箱。");
            owner.HoldVisibleForMotionProbe=true;owner.KeepOpenForVerification=false;UiMotion.Configure("on");
            for(int n=0;n<6;n++){var path=Path.Combine(App.Store.DesktopRoots[0],$"演示入口 {n+1}.url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(path,n<3?"office":"dev");}owner.Render();
            await Task.Delay(1200);
            backdrop=new Window{Width=owner.ActualWidth+100,Height=owner.ActualHeight+100,Left=owner.Left-50,Top=owner.Top-50,WindowStartupLocation=WindowStartupLocation.Manual,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Background=new SolidColorBrush(Color.FromRgb(218,225,234)),ShowInTaskbar=false,ShowActivated=false,Topmost=true};backdrop.Show();
            UiMotion.ProbeObserver=(stage,target)=>{if(ReferenceEquals(target,owner.Surface))events.Add(new{ms=clock.ElapsedMilliseconds,stage,stack=new StackTrace(1,false).ToString()});};
            for(int cycle=0;cycle<4;cycle++)
            {
                int intermediate=0;
                UiMotion.Configure(cycle==3?"off":"on");
                owner.WindowState=WindowState.Minimized;await Task.Delay(400);long start=clock.ElapsedMilliseconds;
                owner.ShowPanel();var handle=new WindowInteropHelper(owner).Handle;SetWindowPos(handle,(IntPtr)(-1),0,0,0,0,0x43);SetForegroundWindow(handle);
                if(cycle==1){await Task.Delay(750);owner.BeginHidePanel();start=clock.ElapsedMilliseconds;}
                GetWindowRect(handle,out var bounds);
                for(int n=0;n<28;n++){await Task.Delay(16);var angles=owner.PanelMotion.Angles;double opacity=owner.Surface.Opacity;rows.Add(new{cycle,ms=clock.ElapsedMilliseconds-start,opacity,angles,bytes=owner.PanelMotion.SnapshotBytes,clocks=owner.PanelMotion.ActiveClocks,state=owner.WindowState.ToString(),owner.Left,owner.Top});if(angles.Any(a=>a>1&&a<83))intermediate++;if(cycle==3&&(opacity!=1||owner.PanelMotion.Active))throw new Exception("关闭动画仍出现过渡帧。");if(cycle<2)Capture(handle,Path.Combine(root,$"{(cycle==0?"screen":"close")}-{n:D2}.png"),bounds);}
                if(cycle<3&&intermediate<3)throw new Exception("实际恢复路径未采到足够的过渡中间帧。");
                await Task.Delay(600);if(owner.PanelMotion.Active||owner.PanelMotion.SnapshotBytes!=0||owner.PanelMotion.Failure is not null)throw new Exception("百叶资源未释放或图形失败："+owner.PanelMotion.Failure);
            }
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            UiMotion.ProbeObserver=null;UiMotion.CancelAll();backdrop?.Close();
            File.WriteAllText(Path.Combine(root,"motion-visibility.json"),JsonSerializer.Serialize(new{failure,events,rows},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(failure.Length==0?0:1);
        }
    }
}

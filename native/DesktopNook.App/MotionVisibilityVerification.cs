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
    private sealed class NeutralBackdrop : IDisposable
    {
        private delegate IntPtr WindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        private static readonly WindowProc Proc=DefWindowProc;
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass{public uint Size,Style;public IntPtr Procedure;public int ClassExtra,WindowExtra;public IntPtr Instance,Icon,Cursor,Background;public string? Menu,Name;public IntPtr SmallIcon;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool UnregisterClass(string name,IntPtr instance);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowEx(uint ex,string name,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr h);
        [DllImport("gdi32.dll")]private static extern IntPtr CreateSolidBrush(uint color);
        private readonly string _name="Qingxia.ProbeBackdrop."+Guid.NewGuid().ToString("N");
        private readonly IntPtr _instance=GetModuleHandle(null),_brush=CreateSolidBrush(218u|(225u<<8)|(234u<<16));
        internal IntPtr Handle {get;}
        internal NeutralBackdrop(Rect area,double dpi)
        {
            var cls=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=Marshal.GetFunctionPointerForDelegate(Proc),Instance=_instance,Background=_brush,Name=_name};
            if(RegisterClassEx(ref cls)==0){DeleteObject(_brush);throw new IOException("无法建立测试背景。");}
            Handle=CreateWindowEx(0x08000088,_name,"轻匣测试背景",0x90000000,(int)(area.Left*dpi),(int)(area.Top*dpi),(int)(area.Width*dpi),(int)(area.Height*dpi),IntPtr.Zero,IntPtr.Zero,_instance,IntPtr.Zero);
            if(Handle==IntPtr.Zero){UnregisterClass(_name,_instance);throw new IOException("无法显示测试背景。");}
        }
        public void Dispose(){DestroyWindow(Handle);UnregisterClass(_name,_instance);}
    }
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
        var rows=new List<object>();var events=new List<object>();var clock=Stopwatch.StartNew();string failure="";NeutralBackdrop? backdrop=null;
        try
        {
            if(!App.IsSandbox)throw new InvalidOperationException("可见性检查仅允许沙箱。");
            owner.HoldVisibleForMotionProbe=true;owner.KeepOpenForVerification=false;UiMotion.Configure("on");
            for(int n=0;n<6;n++){var path=Path.Combine(App.Store.DesktopRoots[0],$"演示入口 {n+1}.url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(path,n<3?"office":"dev");}owner.Render();
            await Task.Delay(1200);
            owner.ShowPanel();await Task.Delay(650);
            var work=TaskbarPlacement.WorkArea(owner);
            backdrop=new NeutralBackdrop(work,VisualTreeHelper.GetDpi(owner).DpiScaleX);
            UiMotion.ProbeObserver=(stage,target)=>{if(ReferenceEquals(target,owner.Surface))events.Add(new{ms=clock.ElapsedMilliseconds,stage,stack=new StackTrace(1,false).ToString()});};
            owner.WindowState=WindowState.Minimized;await Task.Delay(5600);
            for(int cycle=0;cycle<4;cycle++)
            {
                int intermediate=0;
                UiMotion.Configure(cycle==3?"off":"on");
                owner.WindowState=WindowState.Minimized;await Task.Delay(400);long start=clock.ElapsedMilliseconds;
                owner.ShowPanel();var handle=new WindowInteropHelper(owner).Handle;SetWindowPos(handle,(IntPtr)(-1),0,0,0,0,0x43);SetForegroundWindow(handle);
                SetWindowPos(backdrop.Handle,handle,0,0,0,0,0x13);
                if(cycle==1){await Task.Delay(750);owner.BeginHidePanel();start=clock.ElapsedMilliseconds;}
                GetWindowRect(handle,out var bounds);
                for(int n=0;n<28;n++){await Task.Delay(16);var angles=owner.PanelMotion.Angles;double opacity=owner.Surface.Opacity;rows.Add(new{cycle,ms=clock.ElapsedMilliseconds-start,opacity,angles,bytes=owner.PanelMotion.SnapshotBytes,captures=owner.PanelMotion.BufferCaptures,allocations=owner.PanelMotion.BufferAllocations,clocks=owner.PanelMotion.ActiveClocks,state=owner.WindowState.ToString(),owner.Left,owner.Top});if(angles.Any(a=>a>1&&a<83))intermediate++;if(cycle==3&&(opacity!=1||owner.PanelMotion.Active))throw new Exception("关闭动画仍出现过渡帧。");if(cycle<2)Capture(handle,Path.Combine(root,$"{(cycle==0?"screen":"close")}-{n:D2}.png"),bounds);}
                if(cycle<3&&intermediate<3)throw new Exception("实际恢复路径未采到足够的过渡中间帧。");
                await Task.Delay(600);if(owner.PanelMotion.Active||owner.PanelMotion.SnapshotBytes!=0||owner.PanelMotion.Failure is not null)throw new Exception("百叶资源未释放或图形失败："+owner.PanelMotion.Failure);
            }
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            UiMotion.ProbeObserver=null;UiMotion.CancelAll();backdrop?.Dispose();
            File.WriteAllText(Path.Combine(root,"motion-visibility.json"),JsonSerializer.Serialize(new{failure,events,rows},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(failure.Length==0?0:1);
        }
    }
}

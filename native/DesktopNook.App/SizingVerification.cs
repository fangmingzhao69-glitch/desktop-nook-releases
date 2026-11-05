using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DesktopNook.App;
internal static class SizingVerification
{
    [StructLayout(LayoutKind.Sequential)]private struct CursorPoint{public int X,Y;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    internal static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();string failure="";
        void Check(bool result,string name){if(!result)throw new Exception(name);checks.Add(name);}
        void Snapshot(string name)
        {
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(root,name+".png"));png.Save(file);
        }
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);window.UpdateLayout();}
        IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
        {
            for(int n=0;n<VisualTreeHelper.GetChildrenCount(parent);n++){var child=VisualTreeHelper.GetChild(parent,n);if(child is T match)yield return match;foreach(var descendant in Find<T>(child))yield return descendant;}
        }
        double IconWidth(){var icon=Find<ShellIcon>(window.Icons).First();return icon.TransformToAncestor(window).TransformBounds(new Rect(0,0,icon.ActualWidth,icon.ActualHeight)).Width;}
        try
        {
            if(!App.IsSandbox)throw new Exception("尺寸自检只能在隔离目录运行。");window.KeepOpenForVerification=true;
            var key=@"Software\DesktopNook\SizingVerification\"+Guid.NewGuid().ToString("N");
            try
            {
                var settings=new StartupOptions(key+@"\Run",key+@"\Settings");
                Check(settings.ReadWindowSize()==new WindowSizePreference(),"旧版缺省设置为固定 100%");
                var preference=new WindowSizePreference{Free=true,Width=900,Height=420,Locked=false};settings.WriteWindowSize(preference);
                Check(settings.ReadWindowSize()==preference,"自定义宽高保存并重读");
                using(var registry=Registry.CurrentUser.CreateSubKey(key+@"\Settings"))registry.SetValue("WindowSize","broken json");
                Check(settings.ReadWindowSize()==new WindowSizePreference(),"损坏偏好安全恢复默认");
                Check(new WindowSizePreference{Width=double.NaN,Height=-20,Zoom=9}.Validated().Width==716.8,"非法尺寸数值校验");
            }
            finally{Registry.CurrentUser.DeleteSubKeyTree(key,false);}
            for(int n=1;n<=12;n++){var file=Path.Combine(App.Store.DesktopRoots[0],$"示例应用 {n}.url");File.WriteAllText(file,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");App.Store.Collect(file,"office");}window.Render();await Layout();
            double originalIconWidth=IconWidth();
            foreach(var scale in WindowSizePreference.Presets)
            {
                window.SelectWindowSize(scale);await Layout();
                Check(Math.Abs(window.ActualWidth-716.8*scale)<2&&Math.Abs(window.ActualHeight-627.2*scale)<2,$"固定 {scale*100}% 尺寸");
                Check(Math.Abs(IconWidth()-originalIconWidth)<.25,$"{scale*100}% 窗口下图标尺寸不变（{IconWidth():F2}）");
                Snapshot("preset-"+(int)(scale*100));
            }
            window.SelectWindowSize(reset:true);window.SelectWindowSize();window.Width=1000;window.Height=430;await Layout();
            Check(window.IconColumns>6&&Math.Abs(window.Surface.Width-window.ActualWidth/1.12)<1,"自由模式宽面板自动增加列数且保持内容比例");Snapshot("free-wide");
            Check(Math.Abs(IconWidth()-originalIconWidth)<.25,"自由模式图标尺寸不变");
            window.Width=560;window.Height=700;await Layout();Check(window.IconColumns<6,"窄窗口自动减少列数");Snapshot("free-narrow");
            window.SelectWindowSize(locked:true);await Layout();Check(Math.Abs(window.ActualWidth/window.ActualHeight-8d/7)<.01,"锁定宽高比恢复 8:7");
            Check(Math.Abs(IconWidth()-originalIconWidth)<.25,"锁定窗口宽高比时图标尺寸不变");
            var start=new Rect(100,100,716.8,627.2);var work=new Rect(0,0,1920,1040);
            foreach(string edge in new[]{"L","R","T","B","LT","RT","LB","RB"})
            {
                foreach(bool locked in new[]{false,true})
                {
                    var resized=MainWindow.CalculateResize(start,edge,200,120,locked,work);
                    Check(work.Contains(resized)&&resized.Width>=560&&resized.Height>=360&&(!locked||Math.Abs(resized.Width/resized.Height-8d/7)<.001),$"{edge} 边缘拖动，锁定={locked}");
                }
            }
            var huge=MainWindow.CalculateResize(start,"RB",5000,5000,false,work);Check(work.Contains(huge),"超大尺寸限制在屏幕内");
            var tiny=MainWindow.CalculateResize(start,"RB",-5000,-5000,false,work);Check(tiny.Width==560&&tiny.Height==360,"最小尺寸保护");
            // Exercise the same routed events used by actual mouse capture.
            window.SelectWindowSize(reset:true);window.SelectWindowSize();await Layout();
            var handle=window.ResizeHandles.Children.OfType<Thumb>().Single(t=>(string)t.Tag=="RB");
            handle.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            window.Width=850;window.Height=460;await Layout();
            handle.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
            Check(Math.Abs(App.Options.ReadWindowSize().Width-850)<2,"拖动结束后保存尺寸");
            handle.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});window.Width=950;await Layout();handle.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});
            Check(Math.Abs(window.Width-850)<2,"取消拖动恢复原尺寸");
            GetCursorPos(out var originalCursor);
            try
            {
                window.SelectWindowSize(reset:true);window.SelectWindowSize();window.Left=100;window.Top=100;window.Activate();await Layout();await Task.Delay(200);
                var position=window.PointToScreen(new Point(window.ActualWidth-4,window.ActualHeight-4));var dpi=VisualTreeHelper.GetDpi(window);
                SetCursorPos((int)position.X,(int)position.Y);mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(100);
                for(int step=1;step<=4;step++){SetCursorPos((int)(position.X+60*dpi.DpiScaleX*step/4),(int)(position.Y+40*dpi.DpiScaleY*step/4));await Task.Delay(60);}
                mouse_event(4,0,0,0,UIntPtr.Zero);await Layout();
                Check(Math.Abs(window.ActualWidth-776.8)<4&&Math.Abs(window.ActualHeight-667.2)<4,$"真实鼠标拖动右下角改变窗口宽高（实际 {window.ActualWidth:F1}×{window.ActualHeight:F1}）");
                Check(Math.Abs(App.Options.ReadWindowSize().Width-window.ActualWidth)<2,"真实拖动尺寸持久保存");Snapshot("mouse-resize");
            }
            finally{mouse_event(4,0,0,0,UIntPtr.Zero);SetCursorPos(originalCursor.X,originalCursor.Y);}
            Check(window.UpdateMenuText.Contains(MainWindow.CurrentVersionLabel),"更新入口显示当前版本");
            var download=new UpdateDownloadWindow(window,new OnlineRelease(new Version(9,9,9),new Uri("https://github.com/"+OnlineUpdater.Repository+"/releases/download/v9.9.9/"+OnlineUpdater.AssetName),1,new string('0',64),"test"),root);
            string Text(DependencyObject parent){var result=parent is System.Windows.Controls.TextBlock text?text.Text:"";foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())result+=" "+Text(child);return result;}
            download.Measure(new Size(430,600));download.Arrange(new Rect(0,0,430,600));
            Check(Text(download).Contains(MainWindow.CurrentVersionLabel)&&Text(download).Contains("v9.9.9"),"下载弹窗显示当前与目标版本");
            window.SelectWindowSize(reset:true);await Layout();Check(!window.SizePreference.Free&&window.IconColumns==6,"一键恢复默认尺寸及六列布局");
            Snapshot("default");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{File.WriteAllText(Path.Combine(root,"sizing-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,success=failure=="",failure,checks},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure==""?0:1);}
    }
}

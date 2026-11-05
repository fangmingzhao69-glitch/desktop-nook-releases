using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class OrderingVerification
{
    [StructLayout(LayoutKind.Sequential)]private struct CursorPoint{public int X,Y;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
    internal static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();string failure="";GetCursorPos(out var original);Window? host=null;
        void Check(bool result,string name){if(!result)throw new Exception(name);checks.Add(name);}
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
        string[] Visible()=>window.Icons.Items.Cast<MainWindow.IconEntry>().Select(i=>i.Id).ToArray();
        Point Center(string id){var button=window.IconButton(id);return button.PointToScreen(new Point(button.ActualWidth/2,button.ActualHeight/2));}
        bool captured=false;
        async Task Drag(Point from,Point to,bool cancel=false)
        {
            SetCursorPos((int)from.X,(int)from.Y);mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(100);
            await Task.Run(()=>{try{for(int n=1;n<=18;n++){SetCursorPos((int)(from.X+(to.X-from.X)*n/18),(int)(from.Y+(to.Y-from.Y)*n/18));Thread.Sleep(35);}Thread.Sleep(100);
                if(!captured){window.Dispatcher.Invoke(()=>{window.UpdateLayout();var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(root,"direct-ordering.png"));png.Save(file);});captured=true;}
                if(cancel){keybd_event(0x1B,0,0,UIntPtr.Zero);Thread.Sleep(60);keybd_event(0x1B,0,2,UIntPtr.Zero);Thread.Sleep(60);}
            }finally{mouse_event(4,0,0,0,UIntPtr.Zero);}});await Layout();
        }
        try
        {
            if(!App.IsSandbox)throw new Exception("排序检查必须使用隔离目录。");window.KeepOpenForVerification=true;window.Left=80;window.Top=80;
            var items=new List<NookItem>();
            for(int n=0;n<6;n++){string path=Path.Combine(App.Store.DesktopRoots[0],$"演示入口 {n+1}.url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");items.Add(App.Store.Collect(path,n is 1 or 4?"dev":"office"));}
            window.Render();await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"全部按分类分段汇总");
            App.Store.OrderCategory("dev",-1);window.Render();await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"分类移动同步全部页签");
            window.SelectCategory("office");await Layout();var before=App.Store.OrderedItems("office").Select(i=>i.Id).ToArray();
            Check(window.CollectButton.IsEnabled&&window.Tabs.IsEnabled,"无需进入模式，收纳和分类随时可用");
            window.Activate();await Task.Delay(250);
            var targetButton=window.IconButton(before[2]);var target=targetButton.PointToScreen(new Point(targetButton.ActualWidth-12,targetButton.ActualHeight/2));
            await Drag(Center(before[0]),target);
            var expected=new[]{before[1],before[2],before[0],before[3]};
            Check(Visible().SequenceEqual(expected),"分类页签直接拖动排序");
            App.ReloadStore();window.Render();await Layout();Check(Visible().SequenceEqual(expected),"松手自动保存，重读后顺序保留");
            string savedState=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));await Drag(Center(expected[0]),Center(expected[2]),true);Check(LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))==savedState,"Esc 取消拖动，不保存未完成顺序");
            window.SelectCategory(null);await Layout();
            targetButton=window.IconButton(expected[0]);target=targetButton.PointToScreen(new Point(12,targetButton.ActualHeight/2));
            await Drag(Center(expected[3]),target);expected=new[]{expected[3],expected[0],expected[1],expected[2]};
            Check(App.Store.OrderedItems("office").Select(i=>i.Id).SequenceEqual(expected),"全部页签随时调整同分类内部顺序");
            Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"全部继续按分类顺序汇总");
            string state=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));var other=App.Store.OrderedItems("dev")[0];
            await Drag(Center(expected[0]),Center(other.Id));
            Check(LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))==state,"全部跨分类图标落点不改分类或顺序");
            host=new Window{Title="拖动取消验证",Width=180,Height=100,Left=850,Top=80,Topmost=true,ShowInTaskbar=false,Content=new TextBlock{Text="无效落点",Margin=new Thickness(15)}};host.Show();window.Activate();await Task.Delay(150);
            await Drag(Center(expected[0]),host.PointToScreen(new Point(70,50)));
            Check(LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))==state&&App.Store.State.Items.Count==6,"无效窗口落点取消，不误移动文件");
            host.Close();host=null;
            var tab=window.Tabs.Children.OfType<Button>().Single(b=>b.Tag as string=="dev");var tabPoint=tab.PointToScreen(new Point(tab.ActualWidth/2,tab.ActualHeight/2));
            await Drag(Center(expected[0]),tabPoint);
            Check(App.Store.State.Items.Single(i=>i.Id==expected[0]).CategoryId=="dev"&&App.Store.OrderedItems("dev")[^1].Id==expected[0],"拖到分类标签仍可移动分类");
            window.SelectCategory(null);await Layout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(root,"ordering-preview.png")))png.Save(output);
            window.SelectWindowSize(.8);await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"窗口缩放只换行、不改变顺序");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{mouse_event(4,0,0,0,UIntPtr.Zero);SetCursorPos(original.X,original.Y);host?.Close();File.WriteAllText(Path.Combine(root,"ordering-verification.json"),JsonSerializer.Serialize(new{success=failure=="",passed=checks.Count,failure,checks},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure==""?0:1);}
    }
}

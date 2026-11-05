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
    internal static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();string failure="";GetCursorPos(out var original);Window? host=null;
        void Check(bool result,string name){if(!result)throw new Exception(name);checks.Add(name);}
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
        string[] Visible()=>window.Icons.Items.Cast<MainWindow.IconEntry>().Select(i=>i.Id).ToArray();
        Point Center(string id){var button=window.IconButton(id);return button.PointToScreen(new Point(button.ActualWidth/2,button.ActualHeight/2));}
        async Task Drag(Point from,Point to)
        {
            SetCursorPos((int)from.X,(int)from.Y);mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(100);
            await Task.Run(()=>{try{for(int n=1;n<=18;n++){SetCursorPos((int)(from.X+(to.X-from.X)*n/18),(int)(from.Y+(to.Y-from.Y)*n/18));Thread.Sleep(35);}Thread.Sleep(100);}finally{mouse_event(4,0,0,0,UIntPtr.Zero);}});await Layout();
        }
        try
        {
            if(!App.IsSandbox)throw new Exception("排序检查必须使用隔离目录。");window.KeepOpenForVerification=true;window.Left=80;window.Top=80;
            var items=new List<NookItem>();
            for(int n=0;n<6;n++){string path=Path.Combine(App.Store.DesktopRoots[0],$"演示入口 {n+1}.url");File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");items.Add(App.Store.Collect(path,n is 1 or 4?"dev":"office"));}
            window.Render();await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"全部按分类分段汇总");
            App.Store.OrderCategory("dev",-1);window.Render();await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"分类移动同步全部页签");
            window.SelectCategory("office");window.BeginIconOrdering();await Layout();var before=App.Store.OrderedItems("office").Select(i=>i.Id).ToArray();
            Check(window.IsOrdering&&!window.CollectButton.IsEnabled&&!window.Tabs.IsEnabled,"排序模式隔离收纳与分类切换");
            window.ShowIconMenu(window.IconButton(before[0]),App.Store.State.Items.Single(i=>i.Id==before[0]));Check(window.LastMenu is null,"排序模式阻止图标菜单，含键盘快捷入口");
            string state=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));window.Activate();await Task.Delay(250);
            var targetButton=window.IconButton(before[2]);var target=targetButton.PointToScreen(new Point(targetButton.ActualWidth-12,targetButton.ActualHeight/2));
            await Drag(Center(before[0]),target);
            Check(Visible().SequenceEqual(new[]{before[1],before[2],before[0],before[3]}),"真实鼠标拖动调整分类内顺序："+string.Join(",",Visible().Select(id=>Array.IndexOf(before,id))));
            Check(LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))==state,"完成前不写入排序配置");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(root,"ordering-preview.png")))png.Save(output);
            host=new Window{Title="排序取消验证",Width=180,Height=100,Left=850,Top=80,Topmost=true,ShowInTaskbar=false,Content=new TextBlock{Text="窗口外松开仅取消",Margin=new Thickness(15)}};host.Show();window.Activate();await Task.Delay(150);
            var draft=Visible();await Drag(Center(before[1]),host.PointToScreen(new Point(70,50)));
            Check(Visible().SequenceEqual(draft)&&App.Store.State.Items.Count==6&&App.Store.State.Items.All(i=>File.Exists(App.Store.ManagedPath(i))&&!File.Exists(i.OriginalPath)),"拖到窗口外不移回桌面、不改变顺序");
            host.Close();host=null;window.EndIconOrdering(false);await Layout();Check(Visible().SequenceEqual(before),"取消恢复原顺序");
            window.BeginIconOrdering();window.MoveOrderDraft(before[3],before[0],false);window.EndIconOrdering(true);await Layout();
            App.ReloadStore();window.Render();await Layout();Check(Visible().SequenceEqual(new[]{before[3],before[0],before[1],before[2]}),"完成后保存并重读顺序");
            window.SelectCategory(null);await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"全部沿用保存后的分类内顺序");
            window.SelectWindowSize(.8);await Layout();Check(Visible().SequenceEqual(App.Store.OrderedItems().Select(i=>i.Id)),"窗口缩放只换行、不改变顺序");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{mouse_event(4,0,0,0,UIntPtr.Zero);SetCursorPos(original.X,original.Y);host?.Close();File.WriteAllText(Path.Combine(root,"ordering-verification.json"),JsonSerializer.Serialize(new{success=failure=="",passed=checks.Count,failure,checks},new JsonSerializerOptions{WriteIndented=true}));Application.Current.Shutdown(failure==""?0:1);}
    }
}

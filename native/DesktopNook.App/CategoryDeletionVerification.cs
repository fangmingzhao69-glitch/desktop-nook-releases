using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;
internal static class CategoryDeletionVerification
{
    internal static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();string failure="";
        void Check(bool result,string name){if(!result)throw new Exception(name);checks.Add(name);}
        IEnumerable<DependencyObject> Children(DependencyObject parent){foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()){yield return child;foreach(var nested in Children(child))yield return nested;}}
        async Task Layout(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
        void ConfirmDelete(bool confirm)
        {
            var tab=window.Tabs.Children.OfType<Button>().Single(b=>b.Tag as string=="uncategorized");window.CategoryMenu(tab,"uncategorized");
            var delete=window.LastMenu!.Items.OfType<MenuItem>().Single(i=>i.Header as string=="删除分类");Check(delete.IsEnabled,"未分类右键菜单提供删除入口");window.LastMenu.IsOpen=false;
            window.Dispatcher.BeginInvoke(()=>{
                var dialog=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="删除分类");
                string text=string.Join(" ",Children(dialog).OfType<TextBlock>().Select(t=>t.Text));Check(text.Contains("未分类")&&text.Contains("办公效率")&&text.Contains("不会删除文件"),"确认窗口说明图标接收分类及内容保留");
                if(confirm){dialog.UpdateLayout();var image=new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),(int)Math.Ceiling(dialog.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(dialog);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(root,"delete-category-confirm.png"));png.Save(file);}
                var button=Children(dialog).OfType<Button>().Single(b=>b.Content as string==(confirm?"确定":"取消"));button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            },DispatcherPriority.ContextIdle);
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        try
        {
            if(!App.IsSandbox)throw new Exception("分类删除检查必须使用隔离目录。");window.KeepOpenForVerification=true;
            var source=Path.Combine(App.Store.DesktopRoots[0],"演示入口.url");File.WriteAllText(source,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");var item=App.Store.Collect(source,"uncategorized");
            window.SelectCategory("uncategorized");await Layout();var before=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));ConfirmDelete(false);
            Check(LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))==before,"取消确认不改变任何分类或图标");
            ConfirmDelete(true);await Layout();App.ReloadStore();window.Render();await Layout();
            Check(!App.Store.State.Categories.Any(c=>c.Id=="uncategorized")&&App.Store.State.Items.Single().CategoryId=="office"&&File.Exists(App.Store.ManagedPath(item))&&!File.Exists(source),"实际确认后删除分类，内容保留并迁入接收分类");
            Check(window.ActiveCategory=="office","删除当前分类后显示接收分类");
            var picker=new CollectWindow(App.Store,App.Store.DefaultCategoryId);Check(picker.CategoryId=="office","删除后收纳对话框仍有有效默认分类");picker.Close();
            Check(window.Tabs.Children.OfType<Button>().All(b=>b.Tag as string!="uncategorized"),"刷新后不重建未分类标签");
            App.Store.DeleteCategory("design");App.Store.DeleteCategory("dev");window.SelectCategory("office");await Layout();
            window.CategoryMenu(window.Tabs.Children.OfType<Button>().Single(b=>b.Tag as string=="office"),"office");
            Check(!window.LastMenu!.Items.OfType<MenuItem>().Single(i=>i.Header as string=="删除分类").IsEnabled,"最后一个分类的删除入口有保护提示");window.LastMenu.IsOpen=false;
        }
        catch(Exception ex){failure=ex.ToString();}
        finally{File.WriteAllText(Path.Combine(root,"category-deletion-verification.json"),JsonSerializer.Serialize(new{success=failure=="",passed=checks.Count,failure,checks},new JsonSerializerOptions{WriteIndented=true}));int code=failure==""?0:1;Application.Current.Shutdown(code);Environment.Exit(code);}
    }
}

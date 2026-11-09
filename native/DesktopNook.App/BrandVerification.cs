using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;

internal static class BrandVerification
{
    internal static async Task Run(MainWindow owner,string root,List<string> checks)
    {
        if(!App.IsSandbox)throw new InvalidOperationException("Brand checks require isolated data.");
        if(owner.Title!="轻匣")throw new Exception("窗口名称未更新。");
        var info=FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
        if(info.ProductName!="DesktopNook"||!(info.FileDescription??"").Contains("轻匣"))throw new Exception("显示名称或旧版更新兼容标识异常。");
        using(var resource=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico")).Stream)
        {
            var decoder=new IconBitmapDecoder(resource,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
            if(!new[]{16,20,24,32,40,48,64,96,128,256}.SequenceEqual(decoder.Frames.Select(f=>f.PixelWidth)))throw new Exception("图标尺寸不完整。");
        }
        checks.Add("Qingxia display name preserves DesktopNook update identity and ten native icon sizes");
        var folder=Path.Combine(root,"brand-shortcuts");Directory.CreateDirectory(folder);
        var legacy=Path.Combine(folder,"桌面小筑.lnk");
        var type=Type.GetTypeFromProgID("WScript.Shell")!;dynamic shell=Activator.CreateInstance(type)!;
        try
        {
            void Legacy(string target)
            {
                dynamic link=shell.CreateShortcut(legacy);
                try{link.TargetPath=target;link.Save();}finally{Marshal.FinalReleaseComObject(link);}
            }
            Legacy(Environment.ProcessPath!);
            var renamed=ShortcutInstaller.Install(folder);
            if(Path.GetFileName(renamed)!="轻匣.lnk"||!File.Exists(renamed)||File.Exists(legacy))throw new Exception("同目标快捷方式未迁移。");
            Legacy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"));
            ShortcutInstaller.Install(folder);
            if(!File.Exists(legacy))throw new Exception("错误移除了其他目标的快捷方式。");
        }
        finally{Marshal.FinalReleaseComObject(shell);}
        checks.Add("Start-menu rename migrates only matching legacy target and preserves unrelated links");
        File.WriteAllText(Path.Combine(App.Store.DesktopRoots[0],"轻匣收纳示例.txt"),"isolated fixture");
        var hash=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));
        var collect=new CollectWindow(App.Store,App.Store.DefaultCategoryId){Owner=owner};
        try
        {
            collect.Show();await collect.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var category=Find<ComboBox>(collect).Single();
            category.IsDropDownOpen=true;await collect.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if(category.SelectedValue as string!=App.Store.DefaultCategoryId||category.ItemContainerGenerator.ContainerFromIndex(0) is not ComboBoxItem)throw new Exception("分类下拉框未正确显示。");
            category.IsDropDownOpen=false;collect.UpdateLayout();if(!Find<TextBlock>(category).Any(t=>t.Text==App.Store.State.Categories.Single(c=>c.Id==App.Store.DefaultCategoryId).Name))throw new Exception("分类下拉框未显示可见名称。");
            var checkbox=Find<CheckBox>(collect).First();checkbox.IsChecked=true;collect.UpdateLayout();
            if(collect.Selection.Count!=1||!Find<Button>(collect).Single(b=>b.IsDefault).IsEnabled)throw new Exception("勾选后收纳按钮未启用。");
            Save(collect,Path.Combine(root,"collect-brand-preview.png"));
        }
        finally{collect.Close();}
        var edit=new TextDialog("分类名称","为分类起一个名字","常用工具"){Owner=owner};
        try
        {
            edit.Show();await edit.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var input=Find<TextBox>(edit).Single();input.Text="设计与文件";
            if(edit.Value!="设计与文件")throw new Exception("新输入框无法编辑。");
            Save(edit,Path.Combine(root,"edit-brand-preview.png"));
        }
        finally{edit.Close();}
        if(hash!=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json")))throw new Exception("预览弹窗修改了收纳配置。");
        checks.Add("Branded collect, checkboxes, category dropdown and rename dialog work without changing collection data");
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)yield return value;foreach(var nested in Find<T>(child))yield return nested;}
    }
    private static void Save(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);
    }
}

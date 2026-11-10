using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            var pinned=Path.Combine(root,"pinned-fixture");Directory.CreateDirectory(pinned);
            var ownedPath=Path.Combine(pinned,"桌面小筑.lnk");dynamic pin=shell.CreateShortcut(ownedPath);
            string args="--sandbox \""+root+"\"";
            try{pin.TargetPath=Environment.ProcessPath!;pin.Arguments=args;pin.WorkingDirectory=root;pin.IconLocation=Environment.ProcessPath+",0";pin.Save();}finally{Marshal.FinalReleaseComObject(pin);}
            var otherPath=Path.Combine(pinned,"other.lnk");dynamic other=shell.CreateShortcut(otherPath);
            try{other.TargetPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");other.IconLocation=other.TargetPath+",0";other.Save();}finally{Marshal.FinalReleaseComObject(other);}
            var otherHash=LocalUpdater.Hash(otherPath);var stateHash=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));
            var reg=@"Software\DesktopNook\BrandVerification\"+Guid.NewGuid().ToString("N");
            try
            {
                using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(reg))key.SetValue("InstallLocation",Path.GetDirectoryName(Environment.ProcessPath!)!);
                var publicIcons=Path.Combine(root,"public-icon-cache");BrandMigration.Refresh(App.Store.DataDirectory,folder,pinned,reg,publicIcons);
                dynamic updatedPin=shell.CreateShortcut(ownedPath);
                try{if((string)updatedPin.TargetPath!=Environment.ProcessPath||(string)updatedPin.Arguments!=args||(string)updatedPin.WorkingDirectory!=root||!((string)updatedPin.IconLocation).Contains(publicIcons+"\\Qingxia-"+MainWindow.CurrentVersionLabel[1..]+".ico"))throw new Exception("固定入口迁移未保留目标或参数。");}
                finally{Marshal.FinalReleaseComObject(updatedPin);}
                if((File.GetAttributes(Path.Combine(publicIcons,"Qingxia-"+MainWindow.CurrentVersionLabel[1..]+".ico"))&FileAttributes.Encrypted)!=0)throw new Exception("公开图标仍被 EFS 加密。");using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(reg))if(key?.GetValue("DisplayName") as string!="轻匣")throw new Exception("安装显示名未迁移。");
                if(LocalUpdater.Hash(otherPath)!=otherHash||!File.Exists(legacy)||LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"))!=stateHash)throw new Exception("迁移影响了其他入口或收纳配置。");
            }
            finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(reg,false);}
            checks.Add("Pinned icon migration uses versioned ICO, preserves pin path/target/arguments and other shortcuts, and refreshes install branding");
        }
        finally{Marshal.FinalReleaseComObject(shell);}
        checks.Add("Start-menu rename migrates only matching legacy target and preserves unrelated links");
        File.WriteAllText(Path.Combine(App.Store.DesktopRoots[0],"轻匣收纳示例.txt"),"isolated fixture");
        var hash=LocalUpdater.Hash(Path.Combine(App.Store.DataDirectory,"state.json"));
        var collect=new CollectWindow(App.Store,App.Store.DefaultCategoryId){Owner=owner};
        try
        {
            collect.Show();await collect.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            void Fits(CollectWindow dialog){if(dialog.ActualWidth>owner.ActualWidth*.9||dialog.ActualHeight>owner.ActualHeight*.9)throw new Exception("添加窗口超过主面板比例边界。");}
            Fits(collect);
            var category=Find<ComboBox>(collect).Single();
            category.IsDropDownOpen=true;await collect.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if(category.SelectedValue as string!=App.Store.DefaultCategoryId||category.ItemContainerGenerator.ContainerFromIndex(0) is not ComboBoxItem)throw new Exception("分类下拉框未正确显示。");
            category.IsDropDownOpen=false;collect.UpdateLayout();if(!Find<TextBlock>(category).Any(t=>t.Text==App.Store.State.Categories.Single(c=>c.Id==App.Store.DefaultCategoryId).Name))throw new Exception("分类下拉框未显示可见名称。");
            var tiles=Find<ToggleButton>(collect).Where(t=>t.Tag is DesktopEntry).Take(2).ToArray();
            void Toggle(ToggleButton tile){var peer=new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(tile);((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();}
            Toggle(tiles[0]);Toggle(tiles[1]);collect.UpdateLayout();
            if(collect.Selection.Count!=2||!Find<Button>(collect).Single(b=>b.IsDefault).IsEnabled)throw new Exception("图标多选后添加按钮未启用。");
            Toggle(tiles[0]);if(collect.Selection.Count!=1||tiles[1].IsChecked!=true)throw new Exception("取消一个选择影响了其他项目。");Toggle(tiles[0]);
            if(Find<CheckBox>(collect).Any())throw new Exception("添加窗口仍有可见勾选框控件。");
            foreach(var tile in tiles)if(Find<Border>(tile).Single(b=>b.Name=="Tile").Background is not SolidColorBrush background||background.Color!=Color.FromRgb(221,242,255))throw new Exception("选中项目未高亮。");
            checks.Add("Collect tiles highlight without checkbox glyphs; independent toggles retain multi-selection and deselect only the clicked item");
            if(collect.ViewMode!="icons")throw new Exception("新用户默认视图不是图标。");
            await collect.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);await Task.Delay(1000);
            Save(collect,Path.Combine(root,"collect-brand-preview.png"));
            var selected=collect.Selection.ToArray();var categoryBefore=collect.CategoryId;
            collect.SetView("details");collect.UpdateLayout();
            await collect.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);await Task.Delay(1000);
            if(!collect.Selection.SequenceEqual(selected)||collect.CategoryId!=categoryBefore)throw new Exception("切换视图丢失选择或分类。");
            Save(collect,Path.Combine(root,"collect-details-preview.png"));
            Find<Button>(collect).Single(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="CollectView-SaveDefault").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool closeBefore=App.Options.CloseEndsProcess;App.Options.CloseEndsProcess=!closeBefore;App.Options.CloseEndsProcess=closeBefore;
            App.Options.WriteWindowSize(App.Options.ReadWindowSize());App.Options.PersistForUpdate();
            var reopened=new CollectWindow(App.Store,categoryBefore){Owner=owner};
            try{if(reopened.ViewMode!="details")throw new Exception("默认视图没有跨窗口保存。");}finally{reopened.Close();}
            collect.SetView("icons");collect.UpdateLayout();
            if(!collect.Selection.SequenceEqual(selected))throw new Exception("切回图标后丢失选择。");
            App.Options.DefaultCollectView="icons";
            checks.Add("Collect icons/details switch preserves selection/category, defaults to icons and persists a chosen default through preference writes");
            double width=owner.Width,height=owner.Height;
            try
            {
                owner.Width=width*.8;owner.Height=height*.8;await owner.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
                var smaller=new CollectWindow(App.Store,categoryBefore){Owner=owner};
                try{smaller.Show();await smaller.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);Fits(smaller);var accept=Find<Button>(smaller).Single(b=>b.IsDefault);if(accept.TranslatePoint(new Point(0,accept.ActualHeight),smaller).Y>smaller.ActualHeight)throw new Exception("缩小后的添加按钮超出窗口。");Save(smaller,Path.Combine(root,"collect-small-owner-preview.png"));}finally{smaller.Close();}
            }
            finally{owner.Width=width;owner.Height=height;}
            checks.Add("Collect window stays within 90 percent of normal/smaller owner dimensions with its action footer visible");
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

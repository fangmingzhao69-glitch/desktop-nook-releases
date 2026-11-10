using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

internal static class IconVerification
{
    // Read-only Shell extraction checks: no collection or desktop changes.
    internal static int Run(string output,IEnumerable<string> paths)
    {
        Directory.CreateDirectory(output);
        var results=new List<object>();
        try
        {
            var cache=new ShellIntegration.IconCache();
            var small=BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,new byte[32*32*4],32*4);small.Freeze();
            for(int n=0;n<300;n++)cache.Add(n.ToString(),small);
            if(cache.Count>256||cache.TryGetValue("0",out _)||!cache.TryGetValue("299",out _))throw new Exception("图标缓存数量或最近使用淘汰异常。");
            var large=BitmapSource.Create(1024,1024,96,96,PixelFormats.Pbgra32,null,new byte[1024*1024*4],1024*4);large.Freeze();
            for(int n=0;n<10;n++)cache.Add("large-"+n,large);
            if(cache.Bytes>ShellIntegration.IconCache.Limit||!cache.TryGetValue("large-9",out _))throw new Exception("图标缓存超过 8 MiB 像素预算。");
            results.Add(new{cachePixelBudget=ShellIntegration.IconCache.Limit,cacheEntryLimit=256,cacheEvictionVerified=true});
            foreach(double dpi in new[]{1d,1.25,1.5,2,3})
                if(ShellIcon.PixelSize(28,1.12,dpi)<28*1.12*dpi)throw new Exception("DPI 像素请求不足。");
            int index=0;
            foreach(string path in paths)
            {
                var resolved=SharpIcons.ResolveIconPath(path);
                foreach(int size in new[]{32,40,48,64,96,128})
                {
                    var icon=SharpIcons.Load(path,size) as BitmapSource??throw new Exception("未提取到图标："+Path.GetFileName(path));
                    if(!icon.IsFrozen||icon.PixelWidth<size||icon.PixelHeight<size)throw new Exception("图标像素尺寸不足："+Path.GetFileName(path));
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(icon));
                    using(var file=File.Create(Path.Combine(output,$"icon-{index}-{size}.png")))encoder.Save(file);
                    results.Add(new{name=Path.GetFileName(path),resolved,size,width=icon.PixelWidth,height=icon.PixelHeight});
                }
                index++;
            }
            var firstPath=paths.FirstOrDefault();
            if(firstPath is not null)
            {
                var icon=new ShellIcon{IconPath=firstPath,Width=28,Height=28,LayoutTransform=new System.Windows.Media.ScaleTransform(1.12,1.12)};
                var window=new Window{Content=icon,Width=180,Height=180,Left=-32000,Top=-32000,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false,SizeToContent=SizeToContent.WidthAndHeight};
                window.Show();
                foreach(double scale in new[]{1.12,2.24,3.36})
                {
                    icon.LayoutTransform=new System.Windows.Media.ScaleTransform(scale,scale);window.UpdateLayout();
                    var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(icon);int required=ShellIcon.PixelSize(28,scale,dpi.DpiScaleX);
                    var frame=new System.Windows.Threading.DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(10);
                    var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
                    timer.Tick+=(_,_)=>{if((icon.Source is BitmapSource image&&!ReferenceEquals(image,ShellIntegration.AppIcon)&&image.PixelWidth>=required)||DateTime.UtcNow>deadline)frame.Continue=false;};
                    timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);timer.Stop();
                    if(icon.Source is not BitmapSource rendered||ReferenceEquals(rendered,ShellIntegration.AppIcon)||rendered.PixelWidth<required)throw new Exception("异步控件没有刷新到对应像素尺寸。");
                    results.Add(new{controlScale=scale,dpi=dpi.DpiScaleX,required,actual=rendered.PixelWidth});
                }
                window.Hide();
                var first=new ShellIcon{IconPath=firstPath,Width=28,Height=28};
                var last=new ShellIcon{IconPath=firstPath,Width=28,Height=28};
                var stack=new System.Windows.Controls.StackPanel();stack.Children.Add(first);stack.Children.Add(new System.Windows.Controls.Border{Height=1000});stack.Children.Add(last);
                var scroll=new System.Windows.Controls.ScrollViewer{Content=stack,VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto};
                window.Content=scroll;window.SizeToContent=SizeToContent.Manual;window.Width=180;window.Height=180;window.Show();window.UpdateLayout();
                void WaitFor(Func<bool> ready)
                {
                    var frame=new System.Windows.Threading.DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(10);
                    var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(50)};
                    timer.Tick+=(_,_)=>{if(ready()||DateTime.UtcNow>deadline)frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);timer.Stop();
                    if(!ready())throw new Exception("滚动后可见图标未完成加载。");
                }
                WaitFor(()=>first.Source is BitmapSource&&!ReferenceEquals(first.Source,ShellIntegration.AppIcon));
                if(last.Source is not null)throw new Exception("屏幕外图标不应提前加载。");
                scroll.ScrollToBottom();window.UpdateLayout();
                WaitFor(()=>last.Source is BitmapSource&&!ReferenceEquals(last.Source,ShellIntegration.AppIcon));
                if(first.Source is not null)throw new Exception("离开可视区域后应释放控件的图标引用。");
                scroll.ScrollToTop();window.UpdateLayout();
                WaitFor(()=>first.Source is BitmapSource&&!ReferenceEquals(first.Source,ShellIntegration.AppIcon));
                results.Add(new{viewportLazyLoading=true,scrollBackRestoresIcon=true});window.Close();
            }
            File.WriteAllText(Path.Combine(output,"icons.json"),JsonSerializer.Serialize(new{passed=true,scales=new[]{100,125,150,200,300},results},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());return 1;}
    }
}

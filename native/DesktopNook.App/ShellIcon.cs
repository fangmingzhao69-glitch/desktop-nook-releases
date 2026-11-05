using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNook.App;

// Request physical pixels after both the panel's Viewbox and monitor DPI have
// been applied. Moving to another monitor or resizing reloads the matching size.
public sealed class ShellIcon : Image
{
    public static readonly DependencyProperty IconPathProperty=DependencyProperty.Register(nameof(IconPath),typeof(string),typeof(ShellIcon),new PropertyMetadata("",Changed));
    public static readonly DependencyProperty IsRecycleBinProperty=DependencyProperty.Register(nameof(IsRecycleBin),typeof(bool),typeof(ShellIcon),new PropertyMetadata(false,Changed));
    public string IconPath {get=>(string)GetValue(IconPathProperty);set=>SetValue(IconPathProperty,value);}
    public bool IsRecycleBin {get=>(bool)GetValue(IsRecycleBinProperty);set=>SetValue(IsRecycleBinProperty,value);}
    private int _pixels;
    private string? _key;
    public ShellIcon()
    {
        SnapsToDevicePixels=true;UseLayoutRounding=true;
        RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
        Loaded+=(_,_)=>{ShellIntegration.IconReady+=Ready;LayoutUpdated+=LayoutChanged;Refresh();};
        Unloaded+=(_,_)=>{ShellIntegration.IconReady-=Ready;LayoutUpdated-=LayoutChanged;};
    }
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs e){var icon=(ShellIcon)sender;icon._key=null;if(icon.IsLoaded)icon.Refresh();}
    private void LayoutChanged(object? sender,EventArgs e)=>Refresh();
    private void Ready()=>Dispatcher.BeginInvoke(()=>{if(IsLoaded)Refresh(true);});
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);_key=null;}
    internal static int PixelSize(double logicalSize,double transformScale,double dpiScale)=>Math.Clamp((int)Math.Ceiling(logicalSize*transformScale*dpiScale),16,1024);
    private void Refresh(bool force=false)
    {
        var dpi=VisualTreeHelper.GetDpi(this);double width=ActualWidth,height=ActualHeight;
        if(Window.GetWindow(this) is Window window)
        {
            var bounds=TransformToAncestor(window).TransformBounds(new Rect(0,0,width,height));width=bounds.Width;height=bounds.Height;
        }
        int pixels=PixelSize(Math.Max(width,height),1,Math.Max(dpi.DpiScaleX,dpi.DpiScaleY));
        string key=(IsRecycleBin?"::recycle":IconPath);
        if(!force&&_pixels==pixels&&_key==key)return;
        _pixels=pixels;_key=key;
        Source=ShellIntegration.IconFor(IconPath,IsRecycleBin,pixels);
    }
}

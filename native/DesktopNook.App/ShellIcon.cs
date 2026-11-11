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
    private ScrollViewer? _scroll;
    private bool _paused;
    private WeakReference<ImageSource>? _lastImage;
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==SourceProperty&&e.NewValue is ImageSource image)
        {
            bool changed=_lastImage is null||!_lastImage.TryGetTarget(out var previous)||!ReferenceEquals(previous,image);
            _lastImage=new(image);
            if(changed&&Window.GetWindow(this) is MainWindow main)main.PanelMotion.InvalidateSnapshot();
        }
    }
    internal void Pause(){_paused=true;LayoutUpdated-=LayoutChanged;_key=null;Source=null;}
    internal void Resume(){if(!IsLoaded)return;_paused=false;LayoutUpdated-=LayoutChanged;LayoutUpdated+=LayoutChanged;Refresh();}
    internal void PrimeForReveal(MainWindow window)
    {
        if(Source is not null)return;
        if(_scroll is not null&&_scroll.IsAncestorOf(this)&&_scroll.ViewportHeight>0)
        {
            var bounds=TransformToAncestor(_scroll).TransformBounds(new Rect(RenderSize));
            if(!bounds.IntersectsWith(new Rect(0,0,_scroll.ViewportWidth,_scroll.ViewportHeight)))return;
        }
        var dpi=VisualTreeHelper.GetDpi(window);_pixels=PixelSize(Math.Max(double.IsFinite(Width)?Width:ActualWidth,double.IsFinite(Height)?Height:ActualHeight),MainWindow.ContentScale,Math.Max(dpi.DpiScaleX,dpi.DpiScaleY));
        _key=IsRecycleBin?"::recycle":IconPath;Source=ShellIntegration.IconFor(IconPath,IsRecycleBin,_pixels);
    }
    public ShellIcon()
    {
        SnapsToDevicePixels=true;UseLayoutRounding=true;
        RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
        Loaded+=(_,_)=>{ShellIntegration.IconReady+=Ready;for(var parent=VisualTreeHelper.GetParent(this);parent is not null;parent=VisualTreeHelper.GetParent(parent))if(parent is ScrollViewer scroll){_scroll=scroll;scroll.ScrollChanged+=ScrollChanged;break;}if(Window.GetWindow(this)?.WindowState==WindowState.Minimized)Pause();else Resume();};
        Unloaded+=(_,_)=>{ShellIntegration.IconReady-=Ready;LayoutUpdated-=LayoutChanged;if(_scroll is not null)_scroll.ScrollChanged-=ScrollChanged;_scroll=null;_key=null;Source=null;};
    }
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs e){var icon=(ShellIcon)sender;icon._key=null;if(icon.IsLoaded)icon.Refresh();}
    private void LayoutChanged(object? sender,EventArgs e)=>Refresh();
    private void ScrollChanged(object sender,ScrollChangedEventArgs e)=>Refresh();
    private void Ready(string path,int pixels,ImageSource image,int generation)
    {
        string key=path=="::"+WindowsRecycleBin.RecycleId?"::recycle":path;
        bool Matches()=>_pixels==pixels && string.Equals(_key,key,StringComparison.OrdinalIgnoreCase);
        // Deliver only to the matching control; loading N icons must not schedule N*N refreshes.
        if(Matches())Dispatcher.BeginInvoke(()=>{if(IsLoaded&&!_paused&&generation==ShellIntegration.IconGeneration&&Matches())Source=image;});
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);_key=null;}
    internal static int PixelSize(double logicalSize,double transformScale,double dpiScale)=>Math.Clamp((int)Math.Ceiling(logicalSize*transformScale*dpiScale),16,1024);
    private void Refresh(bool force=false)
    {
        // Items can leave the visual tree during a category switch before Unloaded is delivered.
        if(!IsLoaded || _paused || (_scroll is not null&&!_scroll.IsAncestorOf(this)))return;
        if(_scroll is not null && _scroll.ViewportHeight>0)
        {
            var bounds=TransformToAncestor(_scroll).TransformBounds(new Rect(RenderSize));
            if(!bounds.IntersectsWith(new Rect(0,0,_scroll.ViewportWidth,_scroll.ViewportHeight))){_key=null;Source=null;return;}
        }
        var dpi=VisualTreeHelper.GetDpi(this);double width=ActualWidth,height=ActualHeight;
        if(Window.GetWindow(this) is Window window)
        {
            if(window.WindowState==WindowState.Minimized)return;
            if(!window.IsAncestorOf(this))return;
            if(window is MainWindow){width=(double.IsFinite(Width)?Width:width)*MainWindow.ContentScale;height=(double.IsFinite(Height)?Height:height)*MainWindow.ContentScale;}
            else{var bounds=TransformToAncestor(window).TransformBounds(new Rect(0,0,width,height));width=bounds.Width;height=bounds.Height;}
        }
        int pixels=PixelSize(Math.Max(width,height),1,Math.Max(dpi.DpiScaleX,dpi.DpiScaleY));
        string key=(IsRecycleBin?"::recycle":IconPath);
        if(!force&&_pixels==pixels&&_key==key)return;
        _pixels=pixels;_key=key;
        Source=ShellIntegration.IconFor(IconPath,IsRecycleBin,pixels);
    }
}

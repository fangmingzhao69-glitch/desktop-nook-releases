using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

// Orthographic X rotation projects to cos(angle) height. Use five 2D strips,
// sharing a mutable screenshot buffer, without allocating a new 3D scene per play.
internal sealed class ShutterMotion(FrameworkElement surface,Grid overlay)
{
    internal const int StripCount=5;
    internal const int DurationMs=500;
    private sealed class StripView : Border
    {
        internal readonly ScaleTransform Scale=new(1,1);
        internal static readonly DependencyProperty AngleProperty=DependencyProperty.Register(nameof(Angle),typeof(double),typeof(StripView),new PropertyMetadata(0d,(d,e)=>((StripView)d).Scale.ScaleY=Math.Cos((double)e.NewValue*Math.PI/180)));
        internal double Angle {get=>(double)GetValue(AngleProperty);set=>SetValue(AngleProperty,value);}
    }
    private sealed record Strip(StripView View,TranslateTransform Translation,ImageBrush Brush);
    private readonly List<Strip> _strips=[];
    private readonly List<Strip> _pool=[];
    private Canvas? _canvas;
    private RenderTargetBitmap? _snapshot;
    private RenderTargetBitmap? _buffer;
    private int _bufferAllocations;
    private bool _dirty=true;
    internal int BufferCaptures {get;private set;}
    internal Action<string>? InvalidationObserver {get;set;}
    internal void InvalidateSnapshot([System.Runtime.CompilerServices.CallerMemberName]string reason=""){_dirty=true;InvalidationObserver?.Invoke(reason);}
    private int _revision;
    private double _originalOpacity=1;
    internal bool Active=>_strips.Count!=0;
    internal bool Closing {get;private set;}
    internal long SnapshotBytes=>_snapshot is null?0:(long)_snapshot.PixelWidth*_snapshot.PixelHeight*4;
    internal double[] Angles=>_strips.Select(s=>s.View.Angle).ToArray();
    internal long BufferedBytes=>_buffer is null?0:(long)_buffer.PixelWidth*_buffer.PixelHeight*4;
    internal int BufferAllocations=>_bufferAllocations;
    internal void ReleaseBuffer()
    {
        if(Active)return;
        foreach(var strip in _pool)strip.Brush.ImageSource=null;
        _pool.Clear();_canvas=null;overlay.Children.Clear();_buffer=null;_dirty=true;
    }
    internal int[] LastStartOrder {get;private set;}=[];
    internal int ActiveClocks=>_strips.Count*3;
    internal string? Failure {get;private set;}
    internal void Stop()
    {
        _revision++;
        foreach(var strip in _strips){strip.View.BeginAnimation(StripView.AngleProperty,null);strip.Translation.BeginAnimation(TranslateTransform.XProperty,null);strip.View.BeginAnimation(UIElement.OpacityProperty,null);}
        _strips.Clear();overlay.Visibility=Visibility.Collapsed;
        surface.Opacity=_originalOpacity;_snapshot=null;Closing=false;
    }
    internal void Play(bool opening,Action? completed=null)
    {
        var previous=_strips.Select(s=>(s.View.Angle,s.Translation.X,s.View.Opacity)).ToArray();
        var image=_snapshot;bool continuing=Active;
        Stop();
        if(!UiMotion.Enabled||!surface.IsLoaded||!surface.IsVisible||surface.ActualWidth<=0||surface.ActualHeight<=0){completed?.Invoke();return;}
        int revision=_revision;Closing=!opening;Failure=null;
        _originalOpacity=surface.Opacity;
        surface.UpdateLayout();double width=surface.ActualWidth,height=surface.ActualHeight;
        if(_canvas is not null&&(_canvas.Width!=width||_canvas.Height!=height))_dirty=true;
        try
        {
            if(image is null)
            {
                var dpi=VisualTreeHelper.GetDpi(surface);double scale=dpi.DpiScaleX;
                if(Window.GetWindow(surface) is Window window){var bounds=surface.TransformToAncestor(window).TransformBounds(new Rect(surface.RenderSize));scale*=bounds.Width/width;}
                double budget=8L*1024*1024;scale=Math.Min(scale,Math.Sqrt(budget/(width*height*4)));
                int pixelsX=Math.Max(1,(int)Math.Floor(width*scale)),pixelsY=Math.Max(1,(int)Math.Floor(height*scale));
                if(_buffer is null||_buffer.PixelWidth!=pixelsX||_buffer.PixelHeight!=pixelsY||Math.Abs(_buffer.DpiX-96*scale)>.01)
                {_buffer=new RenderTargetBitmap(pixelsX,pixelsY,96*scale,96*scale,PixelFormats.Pbgra32);_bufferAllocations++;_dirty=true;}
                image=_buffer;
                if(_dirty){_dirty=false;image.Clear();image.Render(surface);BufferCaptures++;}
            }
            _snapshot=image;
            if(_canvas is null||_canvas.Width!=width||_canvas.Height!=height)
            {
                foreach(var old in _pool)old.Brush.ImageSource=null;
                _pool.Clear();overlay.Children.Clear();_canvas=new Canvas{Width=width,Height=height,IsHitTestVisible=false,ClipToBounds=true};overlay.Children.Add(_canvas);
            }
            overlay.Visibility=Visibility.Visible;overlay.IsHitTestVisible=false;
            surface.Opacity=0;
            LastStartOrder=opening?Enumerable.Range(0,StripCount).Reverse().ToArray():Enumerable.Range(0,StripCount).ToArray();
            for(int i=0;i<StripCount;i++)
            {
                double top=height*i/StripCount,bottom=height*(i+1)/StripCount;
                double foldedX=i%2==0?-20:20;
                if(i>=_pool.Count)
                {
                    var newBrush=new ImageBrush(image){Stretch=Stretch.Fill,Viewbox=new Rect(0,(double)i/StripCount,1,1d/StripCount),ViewboxUnits=BrushMappingMode.RelativeToBoundingBox};
                    var newView=new StripView{Width=width,Height=bottom-top,Background=newBrush};var move=new TranslateTransform();
                    newView.Scale.CenterX=width/2;newView.Scale.CenterY=(bottom-top)/2;
                    newView.RenderTransform=new TransformGroup{Children=new TransformCollection{newView.Scale,move}};
                    Canvas.SetTop(newView,top);_canvas.Children.Add(newView);_pool.Add(new(newView,move,newBrush));
                }
                var strip=_pool[i];var view=strip.View;var translation=strip.Translation;var brush=strip.Brush;
                if(!ReferenceEquals(brush.ImageSource,image))brush.ImageSource=image;
                view.Opacity=continuing?previous[i].Opacity:opening?0:1;view.Angle=continuing?previous[i].Angle:opening?84:0;
                translation.X=continuing?previous[i].X:opening?foldedX:0;_strips.Add(strip);
                int rank=opening?StripCount-1-i:i;var delay=TimeSpan.FromMilliseconds(continuing?0:rank*35);
                DoubleAnimation Animate(double from,double to)=>new(from,to,TimeSpan.FromMilliseconds(continuing?DurationMs:360)){BeginTime=delay,EasingFunction=new QuadraticEase{EasingMode=opening?EasingMode.EaseOut:EasingMode.EaseInOut},FillBehavior=FillBehavior.HoldEnd};
                var angle=Animate(view.Angle,opening?0:84);
                int last=continuing?StripCount-1:opening?0:StripCount-1;
                if(i==last)angle.Completed+=(_,_)=>{if(revision!=_revision)return;Stop();completed?.Invoke();};
                view.BeginAnimation(StripView.AngleProperty,angle);
                translation.BeginAnimation(TranslateTransform.XProperty,Animate(translation.X,opening?0:foldedX));
                view.BeginAnimation(UIElement.OpacityProperty,Animate(view.Opacity,opening?1:0));
            }
        }
        catch(Exception ex)
        {
            Failure=ex.ToString();Stop();ReleaseBuffer();completed?.Invoke();
            // Graphics/capture failures degrade to an immediate state change, never block hiding.
        }
    }
}

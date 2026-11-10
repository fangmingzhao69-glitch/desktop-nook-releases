using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DesktopNook.App;

// One bounded snapshot shared by five planes; no permanent screenshot cache.
internal sealed class ShutterMotion(FrameworkElement surface,Grid overlay)
{
    internal const int StripCount=5;
    internal const int DurationMs=500;
    private sealed record Strip(AxisAngleRotation3D Rotation,TranslateTransform3D Translation,ImageBrush Brush);
    private readonly List<Strip> _strips=[];
    private RenderTargetBitmap? _snapshot;
    private int _revision;
    private double _originalOpacity=1;
    internal bool Active=>_strips.Count!=0;
    internal bool Closing {get;private set;}
    internal long SnapshotBytes=>_snapshot is null?0:(long)_snapshot.PixelWidth*_snapshot.PixelHeight*4;
    internal double[] Angles=>_strips.Select(s=>s.Rotation.Angle).ToArray();
    internal int[] LastStartOrder {get;private set;}=[];
    internal int ActiveClocks=>_strips.Count*3;
    internal string? Failure {get;private set;}
    internal void Stop()
    {
        _revision++;
        foreach(var strip in _strips){strip.Rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty,null);strip.Translation.BeginAnimation(TranslateTransform3D.OffsetXProperty,null);strip.Brush.BeginAnimation(Brush.OpacityProperty,null);}
        _strips.Clear();overlay.Children.Clear();overlay.Visibility=Visibility.Collapsed;
        surface.Opacity=_originalOpacity;_snapshot=null;Closing=false;
    }
    internal void Play(bool opening,Action? completed=null)
    {
        var previous=_strips.Select(s=>(s.Rotation.Angle,s.Translation.OffsetX,s.Brush.Opacity)).ToArray();
        var image=_snapshot;bool continuing=Active;
        Stop();
        if(!UiMotion.Enabled||!surface.IsLoaded||!surface.IsVisible||surface.ActualWidth<=0||surface.ActualHeight<=0){completed?.Invoke();return;}
        int revision=_revision;Closing=!opening;Failure=null;
        _originalOpacity=surface.Opacity;
        surface.UpdateLayout();double width=surface.ActualWidth,height=surface.ActualHeight;
        try
        {
            if(image is null)
            {
                var dpi=VisualTreeHelper.GetDpi(surface);double scale=dpi.DpiScaleX;
                if(Window.GetWindow(surface) is Window window){var bounds=surface.TransformToAncestor(window).TransformBounds(new Rect(surface.RenderSize));scale*=bounds.Width/width;}
                double budget=8L*1024*1024;scale=Math.Min(scale,Math.Sqrt(budget/(width*height*4)));
                int pixelsX=Math.Max(1,(int)Math.Ceiling(width*scale)),pixelsY=Math.Max(1,(int)Math.Ceiling(height*scale));
                image=new RenderTargetBitmap(pixelsX,pixelsY,96*scale,96*scale,PixelFormats.Pbgra32);image.Render(surface);image.Freeze();
            }
            _snapshot=image;
            var viewport=new Viewport3D{Width=width,Height=height,IsHitTestVisible=false,ClipToBounds=true,Camera=new OrthographicCamera(new Point3D(width/2,height/2,1000),new Vector3D(0,0,-1),new Vector3D(0,1,0),width)};
            var models=new Model3DGroup();models.Children.Add(new AmbientLight(Colors.White));viewport.Children.Add(new ModelVisual3D{Content=models});
            overlay.Children.Add(viewport);overlay.Visibility=Visibility.Visible;overlay.IsHitTestVisible=false;
            surface.Opacity=0;
            LastStartOrder=opening?Enumerable.Range(0,StripCount).Reverse().ToArray():Enumerable.Range(0,StripCount).ToArray();
            for(int i=0;i<StripCount;i++)
            {
                double top=height*i/StripCount,bottom=height*(i+1)/StripCount,yTop=height-top,yBottom=height-bottom;
                var mesh=new MeshGeometry3D{Positions=new Point3DCollection{new(0,yBottom,0),new(width,yBottom,0),new(width,yTop,0),new(0,yTop,0)},TriangleIndices=new Int32Collection{0,1,2,0,2,3},TextureCoordinates=new PointCollection{new(0,1),new(1,1),new(1,0),new(0,0)}};mesh.Freeze();
                double foldedX=i%2==0?-20:20;
                var rotation=new AxisAngleRotation3D(new Vector3D(1,0,0),continuing?previous[i].Angle:opening?84:0);
                var translation=new TranslateTransform3D(continuing?previous[i].OffsetX:opening?foldedX:0,0,0);
                var brush=new ImageBrush(image){Opacity=continuing?previous[i].Opacity:opening?0:1,Stretch=Stretch.Fill,Viewbox=new Rect(0,(double)i/StripCount,1,1d/StripCount),ViewboxUnits=BrushMappingMode.RelativeToBoundingBox};
                var material=new DiffuseMaterial(brush);
                var transform=new Transform3DGroup();transform.Children.Add(new RotateTransform3D(rotation,new Point3D(width/2,(yTop+yBottom)/2,0)));transform.Children.Add(translation);
                models.Children.Add(new GeometryModel3D(mesh,material){BackMaterial=material,Transform=transform});_strips.Add(new(rotation,translation,brush));
                int rank=opening?StripCount-1-i:i;var delay=TimeSpan.FromMilliseconds(continuing?0:rank*35);
                DoubleAnimation Animate(double from,double to)=>new(from,to,TimeSpan.FromMilliseconds(continuing?DurationMs:360)){BeginTime=delay,EasingFunction=new QuadraticEase{EasingMode=opening?EasingMode.EaseOut:EasingMode.EaseInOut},FillBehavior=FillBehavior.HoldEnd};
                var angle=Animate(rotation.Angle,opening?0:84);
                int last=continuing?StripCount-1:opening?0:StripCount-1;
                if(i==last)angle.Completed+=(_,_)=>{if(revision!=_revision)return;Stop();completed?.Invoke();};
                rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty,angle);
                translation.BeginAnimation(TranslateTransform3D.OffsetXProperty,Animate(translation.OffsetX,opening?0:foldedX));
                brush.BeginAnimation(Brush.OpacityProperty,Animate(brush.Opacity,opening?1:0));
            }
        }
        catch(Exception ex)
        {
            Failure=ex.ToString();Stop();completed?.Invoke();
            // Graphics/capture failures degrade to an immediate state change, never block hiding.
        }
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesktopNook.App;
internal sealed class BilliardsTray(BilliardsSurface scene,int side) : FrameworkElement
{
    private static Brush Solid(string value){var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));b.Freeze();return b;}
    private static Brush Gradient(params string[] colors)
    {
        var b=new LinearGradientBrush{StartPoint=new(0,0),EndPoint=new(1,0)};for(int n=0;n<colors.Length;n++)b.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(colors[n]),n/(double)(colors.Length-1)));b.Freeze();return b;
    }
    private static readonly Brush Wood=Gradient("#362016","#8B5833","#56351F","#9C6A3D","#352117"),Gold=Gradient("#59422B","#D9C698","#9D7945","#F0DCAC","#58432D");
    private static readonly Brush Lining=Gradient("#061719","#173C3E","#123031","#081A1C"),Channel=Gradient("#020808","#102527","#1E4142","#071112");
    private static readonly Brush Ink=Solid("#E6D8B8"),Muted=Solid("#A99573"),Shadow=Solid("#88000000"),Plate=Solid("#182020");
    private static readonly Pen Grain=new(Solid("#33432B19"),1),Inset=new(Solid("#140A06"),1.5),MetalEdge=new(Solid("#C7AD77"),.7),Highlight=new(Solid("#577D79"),1);
    private DrawingGroup? _frame;
    private Size _frameSize;
    private int _version=-1,_player=-1;
    private double _dpi;
    internal int Side=>side;
    private int Capacity=>scene.Round.Match.Players==2?scene.Round.Total:side==0?(scene.Round.Total+1)/2:scene.Round.Total/2;
    internal void Refresh()
    {
        int player=scene.Round.Match.CurrentPlayer;double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if(_version!=scene.Round.TrayVersion||_player!=player||_dpi!=dpi||scene.Round.Balls.Any(b=>b.Sunk&&b.ExitAge<.65))
        {_version=scene.Round.TrayVersion;_player=player;_dpi=dpi;InvalidateVisual();}
    }
    private DrawingGroup Frame()
    {
        if(_frame is not null&&_frameSize==RenderSize)return _frame;
        _frameSize=RenderSize;var frame=new DrawingGroup();double w=ActualWidth,h=ActualHeight;
        using(var dc=frame.Open())
        {
            dc.DrawRoundedRectangle(Shadow,null,new(5,8,Math.Max(0,w-9),Math.Max(0,h-12)),13,13);
            dc.DrawRoundedRectangle(Wood,Inset,new(3,3,Math.Max(0,w-9),Math.Max(0,h-12)),12,12);
            for(int n=0;n<7;n++){double x=8+n*(w-22)/6;dc.DrawLine(Grain,new(x,17),new(x+Math.Sin(n*2)*2,h-23));}
            dc.DrawRoundedRectangle(Gold,MetalEdge,new(10,12,Math.Max(0,w-23),Math.Max(0,h-32)),8,8);
            dc.DrawRoundedRectangle(Lining,Inset,new(14,16,Math.Max(0,w-31),Math.Max(0,h-40)),6,6);
            double spacing=(w-36)/2;
            for(int column=0;column<2;column++)
            {
                double x=18+column*spacing;dc.DrawRoundedRectangle(Channel,Inset,new(x,93,Math.Max(0,spacing-4),Math.Max(0,h-133)),13,13);
                dc.DrawLine(Highlight,new(x+3,108),new(x+3,h-56));
            }
            foreach(var p in new[]{new Point(9,12),new(w-15,12),new(9,h-21),new(w-15,h-21)}){dc.DrawEllipse(Gold,Inset,p,2.3,2.3);dc.DrawLine(Inset,new(p.X-1.2,p.Y),new(p.X+1.2,p.Y));}
            dc.DrawRoundedRectangle(Plate,MetalEdge,new(20,28,Math.Max(0,w-43),26),4,4);
            dc.DrawRoundedRectangle(Gold,Inset,new(14,h-48,Math.Max(0,w-31),10),4,4);
        }
        frame.Freeze();return _frame=frame;
    }
    private void Text(DrawingContext dc,string value,double y,double size,Brush brush)
    {
        var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text,new((ActualWidth-6-text.Width)/2,y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        if(ActualWidth<45||ActualHeight<160)return;dc.DrawDrawing(Frame());
        var all=scene.Round.PocketOrder.Select(id=>scene.Round.Balls[id]);bool dual=scene.Round.Match.Players==2;
        var balls=(dual?all.Where(b=>b.PottedBy==side):side==0?all.Take((scene.Round.Total+1)/2):all.Skip((scene.Round.Total+1)/2)).ToList();
        Text(dc,dual?$"玩家 {side+1}":side==0?"左收球盘":"右收球盘",33,11,dual&&scene.Round.Match.CurrentPlayer==side?Brushes.White:Ink);
        Text(dc,$"已入 {balls.Count} 球",64,10,Muted);
        int rows=(Capacity+1)/2;double spaceX=(ActualWidth-36)/2,step=Math.Min(35,Math.Max(12,(ActualHeight-150)/Math.Max(1,rows))),r=Math.Min(16,Math.Min((spaceX-5)/2,(step-2)/2));
        for(int n=0;n<balls.Count;n++)
        {
            var ball=balls[n];double x=18+spaceX*(n%2+.5)-2,y=ActualHeight-56-r-(n/2)*step;
            double enter=Math.Clamp(ball.ExitAge/.62,0,1),scale=r/ball.Radius*enter;
            dc.DrawEllipse(Shadow,null,new(x+1,y+3),r+1,r+1);
            dc.PushTransform(new MatrixTransform(scale,0,0,scale,x,y));dc.DrawDrawing(scene.BallArt(ball));dc.Pop();
            dc.DrawLine(Highlight,new(x-r-1,y+r*.85),new(x+r+1,y+r*.85));
        }
    }
}

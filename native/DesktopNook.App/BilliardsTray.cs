using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesktopNook.App;
internal sealed class BilliardsTray(BilliardsSurface scene) : FrameworkElement
{
    private static readonly Brush Back=new SolidColorBrush(Color.FromRgb(14,48,61)),Empty=new SolidColorBrush(Color.FromRgb(23,68,78)),Ink=new SolidColorBrush(Color.FromRgb(189,224,234));
    private static readonly Pen Edge=new(new SolidColorBrush(Color.FromRgb(69,114,126)),1);
    private int _version=-1;
    internal void Refresh()
    {
        Height=scene.Round.Match.Players==2?88:52;
        if(_version!=scene.Round.TrayVersion||scene.Round.Balls.Any(b=>b.Sunk&&b.ExitAge<.62)){_version=scene.Round.TrayVersion;InvalidateVisual();}
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Back,Edge,new(8,3,Math.Max(0,ActualWidth-16),Math.Max(0,ActualHeight-8)),9,9);
        int rows=scene.Round.Match.Players;double spacing=Math.Min(30,Math.Max(12,(ActualWidth-140)/scene.Round.Total)),radius=Math.Min(11,(spacing-4)/2);
        for(int row=0;row<rows;row++)
        {
            var potted=scene.Round.PocketOrder.Select(id=>scene.Round.Balls[id]).Where(b=>rows==1||b.PottedBy==row).ToList();double y=rows==1?26:24+row*37;
            string label=rows==1?$"已下球 {potted.Count}/{scene.Round.Total}":$"玩家 {row+1}  ·  {potted.Count} 球";
            dc.DrawText(new FormattedText(label,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),11,Ink,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(22,y-8));
            for(int n=0;n<scene.Round.Total;n++)
            {
                double x=132+spacing*n;dc.DrawEllipse(Empty,Edge,new(x,y),radius,radius);
                if(n>=potted.Count)continue;var ball=potted[n];double scale=radius/ball.Radius*Math.Clamp(ball.ExitAge/.62,.15,1);
                dc.PushTransform(new MatrixTransform(scale,0,0,scale,x,y));dc.DrawDrawing(scene.BallArt(ball));dc.Pop();
            }
        }
    }
}

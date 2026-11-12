using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopNook.Games.Core;

namespace DesktopNook.App;
internal static class BilliardsBallDrawing
{
    private static Brush Brush(string color){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));brush.Freeze();return brush;}
    private static readonly Brush Ivory=Brush("#FFFDF6"),Ink=Brush("#152536");
    private static readonly Pen Rim=new(Brush("#95E9F8F4"),.8);
    private static readonly RadialGradientBrush Shine=new(new GradientStopCollection{new(Color.FromArgb(115,255,255,255),0),new(Colors.Transparent,.65),new(Color.FromArgb(90,0,0,0),1)}){GradientOrigin=new(.28,.22),Center=new(.45,.4),RadiusX=.75,RadiusY=.75};
    static BilliardsBallDrawing(){Shine.Freeze();Rim.Freeze();}
    internal static DrawingGroup Create(Ball ball,ImageSource? icon,double dpi)
    {
        double r=ball.Radius;var art=new DrawingGroup();using(var dc=art.Open())
        {
            dc.PushClip(new EllipseGeometry(new(0,0),r,r));var color=Brush(ball.Style.Color);
            dc.DrawEllipse(ball.Style.Stripe?Ivory:color,null,new(0,0),r,r);
            if(ball.Style.Stripe)dc.DrawRectangle(color,null,new(-r,-r*.52,r*2,r*1.04));
            dc.DrawEllipse(Shine,null,new(0,0),r,r);
            if(icon is not null)
            {
                double badge=r*.48;var center=new Point(-r*.08,-r*.08);dc.DrawEllipse(Ivory,null,center,badge+1,badge+1);
                dc.PushClip(new EllipseGeometry(center,badge,badge));dc.DrawImage(icon,new(center.X-badge,center.Y-badge,badge*2,badge*2));dc.Pop();
            }
            if(ball.Style.Number>0)
            {
                double size=r*.3;var center=new Point(r*.48,r*.49);dc.DrawEllipse(Ivory,null,center,size,size);
                var text=new FormattedText(ball.Style.Number.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),r*.46,Ink,dpi){TextAlignment=TextAlignment.Center};
                dc.DrawText(text,new(center.X,center.Y-text.Height/2));
            }
            dc.DrawEllipse(null,Rim,new(0,0),r-.4,r-.4);dc.Pop();
        }
        art.Freeze();return art;
    }
}
internal static class BilliardsPlacement
{
    internal static Rect Initial(Rect screen)=>new(screen.Left+screen.Width/4,screen.Top+screen.Height/4,screen.Width/2,screen.Height/2);
}

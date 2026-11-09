using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class MenuLayout
{
    public static readonly DependencyProperty BodyHeightProperty=DependencyProperty.RegisterAttached("BodyHeight",typeof(double),typeof(MenuLayout),new FrameworkPropertyMetadata(480d,FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty BodyWidthProperty=DependencyProperty.RegisterAttached("BodyWidth",typeof(double),typeof(MenuLayout),new FrameworkPropertyMetadata(302d,FrameworkPropertyMetadataOptions.Inherits));
    public static double GetBodyHeight(DependencyObject target)=>(double)target.GetValue(BodyHeightProperty);
    public static void SetBodyHeight(DependencyObject target,double value)=>target.SetValue(BodyHeightProperty,value);
    public static double GetBodyWidth(DependencyObject target)=>(double)target.GetValue(BodyWidthProperty);
    public static void SetBodyWidth(DependencyObject target,double value)=>target.SetValue(BodyWidthProperty,value);

    internal static void Adapt(ContextMenu menu,Window owner)
    {
        // Keep text in native DIP coordinates. PerMonitorV2 supplies the current
        // popup monitor's DPI; scaling an effect-composited menu blurs its text.
        var work=TaskbarPlacement.WorkArea(owner);
        double width=Math.Max(156,Math.Min(owner.ActualWidth,work.Width)-24);
        double height=Math.Max(112,Math.Min(owner.ActualHeight,work.Height)-24);
        menu.FontSize=12;
        menu.LayoutTransform=Transform.Identity;
        menu.MaxWidth=Math.Min(358,width);
        menu.MinWidth=Math.Min(202,menu.MaxWidth);
        menu.MaxHeight=height;
        SetBodyWidth(menu,Math.Max(100,menu.MaxWidth-36));
        SetBodyHeight(menu,Math.Max(60,height-64));
    }
}

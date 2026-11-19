using System.Windows;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class DesignGlyphs
{
    internal const string Add = "M8,2 V14 M2,8 H14";
    internal const string Close = "M4,4 L12,12 M12,4 L4,12";
    internal const string More = "M3,8 H3.6 M7.7,8 H8.3 M12.4,8 H13";

    internal static FrameworkElement Path(string geometry)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry), Width = 16, Height = 16, StrokeThickness = 1.75,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false, Stretch = Stretch.None
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentInkBrush");
        return path;
    }
}

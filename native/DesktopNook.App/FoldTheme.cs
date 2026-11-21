using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopNook.App;

internal sealed record FoldTheme(string Id, string Name, FoldTheme.Paper Light, FoldTheme.Paper Dark, string MarkLight, string Stud)
{
    internal sealed record Paper(string Canvas, string Surface, string Grid, string Line, string Ink,
        string Muted, string Strip, string Accent, string Coral, string Mint, string Edge);

    internal static IReadOnlyList<FoldTheme> All { get; } =
    [
        new("grape", "葡萄气泡",
            new("#F2F4FC", "#F8F9FF", "#E4E8F5", "#DDE2F1", "#1E2340", "#8A92AF", "#CDD6EB", "#6C5CE7", "#FF6B8A", "#3AC47D", "#AEB9D8"),
            new("#171A2B", "#21263C", "#242A42", "#323854", "#ECEFFB", "#8A92AF", "#101423", "#9A8BFF", "#FF8AA2", "#4FD69A", "#3A4166"),
            "#9A8BFF", "#FFC93F"),
        new("melon", "西瓜冰棍",
            new("#FFF6F9", "#FFFFFF", "#FCE4ED", "#F7DCE6", "#3A1526", "#B0728D", "#FBD9E5", "#FF4D79", "#FF8A3D", "#34D399", "#F4B8CD"),
            new("#2A1220", "#3A1B2D", "#3D1D2F", "#5A2C43", "#FFEAF2", "#C08AA5", "#1C0A14", "#FF7FA0", "#FFA96B", "#4FD69A", "#6E3550"),
            "#FF80A0", "#FFD23F"),
        new("lime", "青柠电波",
            new("#F2FBF8", "#FFFFFF", "#DFEFEA", "#D5EDE7", "#0F2E2A", "#6FA099", "#D3EDE6", "#10BFA0", "#FF7A59", "#84CC16", "#A9D8CE"),
            new("#0E2422", "#163530", "#1A3E38", "#28534C", "#E6FBF5", "#7FB3AB", "#081816", "#4FE0BE", "#FF9E82", "#B2E63D", "#2E5E56"),
            "#5FE0C0", "#FDE047"),
        new("berry", "蓝莓苏打",
            new("#F4F7FE", "#FFFFFF", "#E3EAF9", "#DCE5F7", "#16224A", "#7E8CB5", "#D9E3F7", "#4F6DF5", "#FB7185", "#34D399", "#B4C6EE"),
            new("#111A33", "#1B2647", "#212D52", "#34406B", "#E8EEFF", "#8C9BC7", "#0A1024", "#8AA6FF", "#FDA4AF", "#4FD69A", "#3B4A7A"),
            "#8AA6FF", "#FBBF24")
    ];

    internal static FoldTheme Find(string? id) => All.FirstOrDefault(theme => theme.Id == id) ?? All[0];
    private static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
    private static Color Blend(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x * (1 - t) + y * t);
        return Color.FromRgb(Mix(a.R,b.R), Mix(a.G,b.G), Mix(a.B,b.B));
    }
    private static Color LabelOn(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return luminance > .18 ? Parse("#101422") : Colors.White;
    }

    internal Dictionary<string, Color> ColorsFor(bool dark)
    {
        var p = dark ? Dark : Light;
        var canvas=Parse(p.Canvas); var surface=Parse(p.Surface); var ink=Parse(p.Ink);
        var muted=Parse(p.Muted); var primary=Parse(p.Accent); var coral=Parse(p.Coral);
        var mint=Parse(p.Mint); var line=Parse(p.Line); var onPrimary=LabelOn(primary);
        var danger=Parse(dark ? "#FF9FAE" : "#A13349");
        var navigation=Blend(surface,primary,dark ? .09 : .11);
        return new()
        {
            ["CanvasBrush"]=canvas, ["CanvasSubtleBrush"]=Blend(canvas,line,.2), ["SurfaceBrush"]=surface,
            ["SurfaceSubtleBrush"]=Blend(surface,line,.22), ["TextPrimaryBrush"]=ink,
            ["TextSecondaryBrush"]=Blend(muted,ink,dark ? .14 : .38), ["TextFaintBrush"]=muted,
            ["LineBrush"]=line, ["OutlineBrush"]=Blend(line,ink,dark ? .28 : .38),
            ["PrimaryBrush"]=primary, ["PrimaryHoverBrush"]=Blend(primary,onPrimary,.08),
            ["PrimaryPressedBrush"]=Blend(primary,onPrimary,.16), ["OnPrimaryBrush"]=onPrimary,
            ["SettingsNavigationBrush"]=navigation,
            ["SettingsNavigationHoverBrush"]=Blend(surface,primary,dark ? .16 : .17),
            ["SettingsNavigationSelectedBrush"]=Blend(surface,primary,dark ? .27 : .23),
            ["SettingsNavigationGlyphBrush"]=Blend(surface,primary,dark ? .25 : .16),
            ["SettingsNavigationBorderBrush"]=Blend(line,primary,dark ? .30 : .35),
            ["AccentInkBrush"]=dark ? primary : Blend(primary,ink,.32),
            ["SelectionBrush"]=Blend(canvas,primary,dark ? .23 : .15), ["SelectionHoverBrush"]=Blend(canvas,primary,dark ? .16 : .09),
            ["PeachBrush"]=coral, ["PeachSoftBrush"]=Blend(canvas,coral,.15),
            ["SuccessBrush"]=dark ? mint : Blend(mint,ink,.54), ["WarningBrush"]=Parse(dark ? "#FFD08B" : "#8A5A12"),
            ["DangerBrush"]=danger, ["DangerSoftBrush"]=Blend(canvas,danger,.12),
            ["DisabledBrush"]=Blend(canvas,ink,.07), ["DisabledInkBrush"]=Blend(muted,ink,.2),
            ["TooltipBrush"]=ink, ["OnTooltipBrush"]=canvas,
            ["FoldGridLineBrush"]=Parse(p.Grid), ["FoldTitleBrush"]=Parse(p.Strip), ["FoldEdgeBrush"]=Parse(p.Edge),
            ["FoldCoralBrush"]=coral, ["OnCoralBrush"]=LabelOn(coral), ["FoldMintBrush"]=mint, ["OnMintBrush"]=LabelOn(mint),
            ["FoldCreaseShadeBrush"]=Color.FromArgb(dark ? (byte)40 : (byte)28,primary.R,primary.G,primary.B),
            ["FoldCreaseLightBrush"]=Parse(dark ? "#35FFFFFF" : "#B8FFFFFF")
        };
    }

    private static readonly Lazy<DrawingGroup> MarkDrawing = new(() =>
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIconDrawing.xaml"));
        using var stream = resource.Stream;
        var drawing = (DrawingGroup)XamlReader.Load(stream);
        drawing.Freeze();
        return drawing;
    });
    private static readonly Dictionary<string, DrawingImage> Marks = new();

    internal static DrawingImage Mark(FoldTheme theme)
    {
        if (Marks.TryGetValue(theme.Id, out var ready)) return ready;
        var art = MarkDrawing.Value.Clone();
        var gradient = (LinearGradientBrush)((GeometryDrawing)art.Children[0]).Brush;
        gradient.GradientStops[0].Color = Parse(theme.Light.Accent);
        gradient.GradientStops[1].Color = Parse(theme.MarkLight);
        ((SolidColorBrush)((GeometryDrawing)art.Children[^1]).Brush).Color = Parse(theme.Stud);
        var mark = new DrawingImage(art);
        mark.Freeze();
        Marks[theme.Id] = mark;
        return mark;
    }
}

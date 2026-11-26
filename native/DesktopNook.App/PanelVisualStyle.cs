using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class PanelVisualStyle
{
    internal static readonly (string Id, string Name)[] Choices = [("paper", "经典纸页"), ("mist", "柔雾纸页"), ("glass", "通透玻璃"), ("crystal", "晶透玻璃")];
    internal static string Selected { get; private set; } = "paper";
    internal static void Select(string style)
    {
        Selected = Choices.Any(choice => choice.Id == style) ? style : "paper";
        if (Application.Current.MainWindow is MainWindow main) Apply(main);
    }
    internal static void Apply(MainWindow main)
    {
        bool glassTheme = ThemePalette.Family.IsGlass && !SystemParameters.HighContrast;
        bool paper = (!glassTheme && Selected == "paper") || SystemParameters.HighContrast;
        bool glass = (Selected is "glass" or "crystal" || glassTheme) && !SystemParameters.HighContrast;
        bool crystal = (Selected == "crystal" || glassTheme) && !SystemParameters.HighContrast;
        var canvas = ThemePalette.Brush("CanvasBrush").Color;
        var title = ThemePalette.Brush("FoldTitleBrush").Color;
        // An accent on a per-pixel WPF window becomes opaque on current builds.
        // Clear that accent for crystal glass and keep the real alpha surface.
        bool materialReady = SetBlur(new WindowInteropHelper(main).Handle, !paper && !crystal, glass);
        if (crystal) materialReady = true;
        // Unsupported composition keeps the readable opaque surface.
        byte surfaceAlpha = glassTheme ? ThemePalette.IsDark ? (byte)64 : (byte)76 : crystal ? (byte)150 : glass ? (byte)170 : (byte)226;
        byte titleAlpha = glassTheme ? (byte)28 : glass ? (byte)130 : (byte)255;
        main.ApplyPanelSurface(new SolidColorBrush(Color.FromArgb(!paper && materialReady && !glassTheme ? surfaceAlpha : (byte)255, canvas.R, canvas.G, canvas.B)),
            new SolidColorBrush(Color.FromArgb(glass && materialReady ? titleAlpha : (byte)255, title.R, title.G, title.B)), paper ? 1 : glass ? 0 : .32,
            paper ? 1 : glass ? 0 : .38);
        main.ApplyGlassDetails(crystal && materialReady);
        main.PanelMotion.InvalidateSnapshot();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accent { public int State, Flags, Color, Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct Composition { public int Attribute; public nint Data; public int Size; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowCompositionAttribute(nint hwnd, ref Composition data);
    private static bool SetBlur(nint hwnd, bool enabled, bool glass)
    {
        if (hwnd == 0) return false;
        var accent = new Accent { State = enabled ? 3 : 0, Flags = 2, Color = glass ? 0x12FFFFFF : 0x32FFFFFF };
        nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<Accent>());
        try
        {
            Marshal.StructureToPtr(accent, memory, false);
            var data = new Composition { Attribute = 19, Data = memory, Size = Marshal.SizeOf<Accent>() };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }
}

public partial class MainWindow
{
    private GlassMaterialSurface? _glassMaterial;
    internal GlassMaterialSurface? GlassMaterial => _glassMaterial;
    internal void SetGlassBackdrop(ImageSource? image, Thickness margin)
    {
        GlassBackdropImage.Source = image;
        GlassBackdropImage.Margin = margin;
        GlassBackdropLayer.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        if (ThemePalette.Family.IsGlass && !SystemParameters.HighContrast)
            Surface.Background = image is null ? ThemePalette.Brush("CanvasBrush") : Brushes.Transparent;
    }
    internal void ApplyGlassDetails(bool enabled)
    {
        GlassSheen.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        bool clearGlass = enabled && ThemePalette.Family.IsGlass;
        GlassSheen.Opacity = clearGlass ? .7 : 1;
        Surface.BorderBrush = clearGlass
            ? new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(180,255,255,255),0),
                new(Color.FromArgb(45,255,255,255),.4), new(Color.FromArgb(42,0,0,0),.78), new(Color.FromArgb(85,255,255,255),1) },new Point(0,0),new Point(.6,1))
            : enabled ? new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)) : ThemePalette.Brush("FoldEdgeBrush");
        Brush Tint(string key, byte alpha)
        {
            var color = ThemePalette.Brush(key).Color;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
        Resources["NoteSurfaceBrush"] = clearGlass ? Tint("SurfaceBrush", 30) : enabled ? Tint("SurfaceBrush", 210) : ThemePalette.Brush("SurfaceBrush");
        Resources["NoteBorderBrush"] = clearGlass ? Tint("TextPrimaryBrush", 24) : ThemePalette.Brush("LineBrush");
        Resources["NoteAccentThickness"] = clearGlass ? new Thickness(0) : new Thickness(3,0,0,0);
        Resources["NoteMetaBrush"] = clearGlass ? Tint("SurfaceBrush", 25) : ThemePalette.Brush("SurfaceSubtleBrush");
        Resources["IconPlateBrush"] = Brushes.Transparent;
        Resources["IconCaptionBrush"] = Brushes.Transparent;
        Resources["GlassReadingSurfaceBrush"] = clearGlass ? Tint("CanvasBrush", ThemePalette.IsDark ? (byte)72 : (byte)95) : Brushes.Transparent;
        if (clearGlass)
        {
            Resources["FoldCreaseShadeBrush"] = Tint("TextPrimaryBrush", 12);
            Resources["FoldCreaseLightBrush"] = Tint("SurfaceBrush", 25);
        }
        else { Resources.Remove("FoldCreaseShadeBrush"); Resources.Remove("FoldCreaseLightBrush"); }
        // Bound the background's luminance even over black/white content, so
        // unplated labels remain readable while the continuous blur shows through.
        GlassBackdropTint.Fill = Tint("CanvasBrush", ThemePalette.IsDark ? (byte)190 : (byte)185);
        if (clearGlass) (_glassMaterial ??= new GlassMaterialSurface(this)).Enable(true);
        else _glassMaterial?.Enable(false);
    }

    internal void ApplyPanelSurface(Brush surface, Brush title, double gridOpacity, double rulesOpacity)
    {
        Surface.Background = surface;
        TitleSurface.Background = title;
        PaperGrid.Opacity = gridOpacity;
        PaperRules.Opacity = rulesOpacity;
    }
}

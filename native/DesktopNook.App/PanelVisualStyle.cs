using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class PanelVisualStyle
{
    internal static readonly (string Id, string Name)[] Choices = [("paper", "经典纸页"), ("mist", "柔雾纸页"), ("glass", "通透玻璃")];
    internal static string Selected { get; private set; } = "paper";
    internal static void Select(string style)
    {
        Selected = Choices.Any(choice => choice.Id == style) ? style : "paper";
        if (Application.Current.MainWindow is MainWindow main) Apply(main);
    }
    internal static void Apply(MainWindow main)
    {
        bool paper = Selected == "paper" || SystemParameters.HighContrast;
        bool glass = Selected == "glass" && !SystemParameters.HighContrast;
        var canvas = ThemePalette.Brush("CanvasBrush").Color;
        var title = ThemePalette.Brush("FoldTitleBrush").Color;
        bool blur = SetBlur(new WindowInteropHelper(main).Handle, !paper, glass);
        // Unsupported composition keeps the readable opaque surface.
        main.ApplyPanelSurface(new SolidColorBrush(Color.FromArgb(!paper && blur ? glass ? (byte)170 : (byte)226 : (byte)255, canvas.R, canvas.G, canvas.B)),
            new SolidColorBrush(Color.FromArgb(glass && blur ? (byte)130 : (byte)255, title.R, title.G, title.B)), paper ? 1 : glass ? 0 : .32,
            paper ? 1 : glass ? 0 : .38);
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
    internal void ApplyPanelSurface(Brush surface, Brush title, double gridOpacity, double rulesOpacity)
    {
        Surface.Background = surface;
        TitleSurface.Background = title;
        PaperGrid.Opacity = gridOpacity;
        PaperRules.Opacity = rulesOpacity;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class ThemePalette
{
    private static Dictionary<string, Color>? _light;
    private static readonly Dictionary<string, ColorSource> ColorsByKey = new();

    private sealed class ColorSource : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(Color), typeof(ColorSource));
        public Color Value
        {
            get => (Color)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }
    }
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["CanvasBrush"] = "#1B2235", ["CanvasSubtleBrush"] = "#1E273C", ["SurfaceBrush"] = "#242D43",
        ["SurfaceSubtleBrush"] = "#2E3A55", ["TextPrimaryBrush"] = "#F1F5FF", ["TextSecondaryBrush"] = "#B0BAD0",
        ["TextFaintBrush"] = "#8996AF", ["LineBrush"] = "#3A4762", ["OutlineBrush"] = "#7F90B1",
        ["PrimaryBrush"] = "#9DBBF7", ["PrimaryHoverBrush"] = "#AECAFF", ["PrimaryPressedBrush"] = "#87A7E8",
        ["OnPrimaryBrush"] = "#17233B", ["AccentInkBrush"] = "#B9D2FF", ["SelectionBrush"] = "#314A73",
        ["SelectionHoverBrush"] = "#293A5C", ["PeachBrush"] = "#FFAB98", ["PeachSoftBrush"] = "#553938",
        ["SuccessBrush"] = "#8BDAB1", ["WarningBrush"] = "#FFD08B", ["DangerBrush"] = "#FF9FAE",
        ["DangerSoftBrush"] = "#552E40", ["DisabledBrush"] = "#303B54", ["DisabledInkBrush"] = "#A4AEC3",
        ["TooltipBrush"] = "#E8EFFF", ["OnTooltipBrush"] = "#17233B"
    };
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["PanelBrush"] = "CanvasBrush", ["HeaderBrush"] = "SurfaceBrush", ["SoftAccentBrush"] = "SelectionBrush",
        ["AccentBrush"] = "PrimaryBrush", ["Ink"] = "TextPrimaryBrush", ["Muted"] = "TextSecondaryBrush",
        ["MenuSurfaceBrush"] = "SurfaceBrush", ["MenuHoverBrush"] = "SelectionHoverBrush"
    };
    internal static string Mode { get; private set; } = "light";
    internal static bool IsDark { get; private set; }

    internal static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.FindResource(key);

    internal static void Apply(string mode)
    {
        if (_light is null)
        {
            _light = Dark.Keys.ToDictionary(key => key, key => Brush(key).Color);
            foreach (var (key, color) in _light)
            {
                // WPF can freeze shared resources. The binding keeps each brush live
                // for code-created controls as well as XAML DynamicResource references.
                var source = new ColorSource { Value = color };
                var brush = new SolidColorBrush();
                BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
                    new Binding { Path = new PropertyPath(ColorSource.ValueProperty), Source = source, Mode = BindingMode.OneWay });
                ColorsByKey.Add(key, source);
                Application.Current.Resources[key] = brush;
            }
            foreach (var (alias, key) in Aliases) Application.Current.Resources[alias] = Brush(key);
        }
        Mode = mode;
        IsDark = mode == "dark" || (mode == "system" && UsesDarkTheme());
        bool changed = false;
        foreach (var (key, light) in _light)
        {
            var color = SystemParameters.HighContrast ? AccessibleColor(key) :
                IsDark ? (Color)ColorConverter.ConvertFromString(Dark[key]) : light;
            var source = ColorsByKey[key];
            if (source.Value == color) continue;
            changed = true;
            source.Value = color;
        }
        if (changed)
            foreach (Window window in Application.Current.Windows)
                RefreshControls(window);
    }

    private static void RefreshControls(DependencyObject parent)
    {
        if (parent is Control control) UiMotion.RefreshFeedback(control);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            RefreshControls(VisualTreeHelper.GetChild(parent, index));
    }

    internal static void Watch(Application app)
    {
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) Refresh();
        }
        void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args)
        {
            if (Mode == "system" && args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Refresh();
        }
        void Refresh()
        {
            if (app.Dispatcher.HasShutdownStarted) return;
            _ = app.Dispatcher.BeginInvoke(() =>
            {
                Apply(Mode);
                if (app.MainWindow is MainWindow main) main.PanelMotion.InvalidateSnapshot();
            });
        }

        SystemParameters.StaticPropertyChanged += Changed;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        app.Exit += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= Changed;
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        };
    }

    private static bool UsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Color AccessibleColor(string key) => key switch
    {
        "PrimaryBrush" or "PrimaryHoverBrush" or "PrimaryPressedBrush" or "SelectionBrush" or "SelectionHoverBrush" => SystemColors.HighlightColor,
        "OnPrimaryBrush" or "OnTooltipBrush" => SystemColors.HighlightTextColor,
        "TextPrimaryBrush" or "TextSecondaryBrush" or "AccentInkBrush" or "LineBrush" or "OutlineBrush" or "DangerBrush" or "WarningBrush" or "SuccessBrush" => SystemColors.WindowTextColor,
        "DisabledInkBrush" or "TextFaintBrush" => SystemColors.GrayTextColor,
        "TooltipBrush" => SystemColors.HighlightColor,
        _ => SystemColors.WindowColor
    };
}

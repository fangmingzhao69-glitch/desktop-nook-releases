using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class NamedColorPalette
{
    internal static readonly (string Name, string Hex)[] Standard =
    [
        ("白色", "#FFFFFF"), ("浅灰", "#E5E7EB"), ("灰色", "#9CA3AF"), ("石墨", "#4B5563"), ("深灰", "#252A34"), ("黑色", "#111111"),
        ("红色", "#E5484D"), ("珊瑚", "#F07878"), ("橙色", "#F28C28"), ("琥珀", "#E8B339"), ("黄色", "#F4D35E"), ("奶油", "#FFF0A6"),
        ("青柠", "#A3C644"), ("绿色", "#46A758"), ("森林", "#287F5D"), ("薄荷", "#89D8B2"), ("青色", "#12A594"), ("湖蓝", "#00A2C7"),
        ("天蓝", "#73BFF0"), ("蓝色", "#3E63DD"), ("靛蓝", "#5151CD"), ("藏蓝", "#152D42"), ("紫色", "#8E5CD9"), ("薰衣草", "#C4B0EE"),
        ("粉色", "#EF7DB0"), ("玫红", "#D6409F"), ("桃色", "#F7B7A3"), ("棕色", "#93654D"), ("米色", "#E8D9BE"), ("暖灰", "#B9ADA1")
    ];
    internal static readonly (string Name, string Hex)[] Markers =
    [
        ("黄色", "#FFF0A6"), ("绿色", "#CBEFCB"), ("蓝色", "#CDE8FF"), ("粉色", "#FFD7E5"),
        ("紫色", "#E3D6FF"), ("橙色", "#FFD8AF"), ("珊瑚色", "#FFC9C4"), ("薄荷色", "#BBEEDF"),
        ("青色", "#BDEDF2"), ("青柠色", "#E4EFA9"), ("米色", "#EEDFC5"), ("灰色", "#DFE3EB")
    ];

    internal static WrapPanel Create((string Name, string Hex)[] colors, int columns, Action<string> select)
    {
        var panel = new WrapPanel { Width = columns * 50, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (name, hex) in colors)
        {
            var content = new StackPanel();
            content.Children.Add(new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = ThemePalette.Brush("OutlineBrush"), BorderThickness = new Thickness(1) });
            content.Children.Add(new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
                Foreground = ThemePalette.Brush("TextPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = content, Width = 46, Height = 52, Padding = new Thickness(3),
                Margin = new Thickness(2), ToolTip = name + " " + hex, Tag = hex, BorderThickness = new Thickness(1) };
            AutomationProperties.SetName(button, name + " " + hex);
            AutomationProperties.SetAutomationId(button, "ColorSwatch-" + hex[1..]);
            button.Click += (_, _) => { Select(panel, hex); select(hex); };
            panel.Children.Add(button);
        }
        Select(panel, null);
        return panel;
    }

    internal static void Select(Panel panel, string? color)
    {
        foreach (var button in panel.Children.OfType<Button>())
        {
            bool selected = string.Equals(button.Tag as string, color, StringComparison.OrdinalIgnoreCase);
            button.SetResourceReference(Control.BorderBrushProperty, selected ? "AccentInkBrush" : "LineBrush");
            button.BorderThickness = new Thickness(selected ? 2 : 1);
            AutomationProperties.SetItemStatus(button, selected ? "已选颜色" : "");
        }
    }
}

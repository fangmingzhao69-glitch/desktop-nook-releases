using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNook.App;

internal static class TaskbarColorPicker
{
    internal static string? Choose(Window owner, string initial)
    {
        var dialog = new Window
        {
            Owner = owner, Title = "选择任务栏颜色", Width = 340, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize
        };
        var body = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
        var swatch = new Border { Height = 42, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8) };
        var hex = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(swatch);
        body.Children.Add(hex);

        body.Children.Add(new TextBlock { Text = "标准颜色", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        var standards = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        body.Children.Add(standards);

        int parsed = initial.Length == 7 && initial[0] == '#' &&
            int.TryParse(initial.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb) ? rgb : 0x152D42;
        var channels = new[] { ("红色", (parsed >> 16) & 255), ("绿色", (parsed >> 8) & 255), ("蓝色", parsed & 255) };
        var sliders = new List<Slider>();
        foreach (var (name, value) in channels)
        {
            var label = new TextBlock { Margin = new Thickness(0, 7, 0, 0) };
            var slider = new Slider
            {
                Minimum = 0, Maximum = 255, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1
            };
            AutomationProperties.SetName(slider, name);
            label.Text = $"{name}：{value}";
            slider.ValueChanged += (_, _) => { label.Text = $"{name}：{slider.Value:0}"; Refresh(); };
            sliders.Add(slider);
            body.Children.Add(label);
            body.Children.Add(slider);
        }

        foreach (var (name, value) in new (string, string)[]
        {
            ("白色", "#FFFFFF"), ("浅灰", "#E5E7EB"), ("灰色", "#9CA3AF"), ("石墨", "#4B5563"), ("深灰", "#252A34"), ("黑色", "#111111"),
            ("红色", "#E5484D"), ("珊瑚", "#F07878"), ("橙色", "#F28C28"), ("琥珀", "#E8B339"), ("黄色", "#F4D35E"), ("奶油", "#FFF0A6"),
            ("青柠", "#A3C644"), ("绿色", "#46A758"), ("森林", "#287F5D"), ("薄荷", "#89D8B2"), ("青色", "#12A594"), ("湖蓝", "#00A2C7"),
            ("天蓝", "#73BFF0"), ("蓝色", "#3E63DD"), ("靛蓝", "#5151CD"), ("藏蓝", "#152D42"), ("紫色", "#8E5CD9"), ("薰衣草", "#C4B0EE"),
            ("粉色", "#EF7DB0"), ("玫红", "#D6409F"), ("桃色", "#F7B7A3"), ("棕色", "#93654D"), ("米色", "#E8D9BE"), ("暖灰", "#B9ADA1")
        })
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var sample = new Button
            {
                Width = 42, Height = 30, Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 6), ToolTip = name + " " + value,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new Border { Width = 30, Height = 18, Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(3),
                    BorderBrush = ThemePalette.Brush("OutlineBrush"), BorderThickness = new Thickness(1) }
            };
            AutomationProperties.SetName(sample, name + " " + value);
            sample.Click += (_, _) => { sliders[0].Value = color.R; sliders[1].Value = color.G; sliders[2].Value = color.B; };
            standards.Children.Add(sample);
        }

        void Refresh()
        {
            if (sliders.Count != 3) return;
            var color = Color.FromRgb((byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value);
            swatch.Background = new SolidColorBrush(color);
            hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 6, 0) };
        var accept = new Button { Content = "使用颜色", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        body.Children.Add(actions);
        dialog.Content = body;
        UiDialogs.Chrome(dialog, compact: true);
        Refresh();
        return dialog.ShowDialog() == true ? hex.Text : null;
    }
}

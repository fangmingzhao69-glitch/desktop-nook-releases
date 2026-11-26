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
            Owner = owner, Title = "选择任务栏颜色", Width = 360, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize
        };
        var body = new StackPanel { Margin = new Thickness(20, 12, 20, 16) };
        var swatch = new Border { Height = 42, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8) };
        var hex = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(swatch);
        body.Children.Add(hex);

        body.Children.Add(new TextBlock { Text = "标准颜色", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        WrapPanel? standards = null;

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

        standards = NamedColorPalette.Create(NamedColorPalette.Standard, 6, value =>
        {
            var chosen = (Color)ColorConverter.ConvertFromString(value);
            sliders[0].Value = chosen.R; sliders[1].Value = chosen.G; sliders[2].Value = chosen.B;
        });
        body.Children.Insert(3, standards);

        void Refresh()
        {
            if (sliders.Count != 3) return;
            var color = Color.FromRgb((byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value);
            swatch.Background = new SolidColorBrush(color);
            hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (standards is not null) NamedColorPalette.Select(standards, hex.Text);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 6, 0) };
        var accept = new Button { Content = "使用颜色", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        body.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(280, TaskbarPlacement.WorkArea(owner).Height - 100) };
        UiDialogs.Chrome(dialog, compact: true);
        Refresh();
        return dialog.ShowDialog() == true ? hex.Text : null;
    }
}

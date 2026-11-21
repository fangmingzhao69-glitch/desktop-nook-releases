using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class FloatingTaskbarPreview : FrameworkElement
{
    internal FloatingTaskbarSettings Settings { get; set; } = new();
    private double _pointer = -1000;
    protected override void OnMouseMove(MouseEventArgs e) { _pointer = e.GetPosition(this).X; InvalidateVisual(); base.OnMouseMove(e); }
    protected override void OnMouseLeave(MouseEventArgs e) { _pointer = -1000; InvalidateVisual(); base.OnMouseLeave(e); }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14);
        dc.PushClip(clip);
        var wallpaper = new LinearGradientBrush(Color.FromRgb(231, 177, 135), Color.FromRgb(113, 159, 151), 55);
        dc.DrawRectangle(wallpaper, null, new Rect(0, 0, w, h));
        dc.PushTransform(new RotateTransform(-16, w / 2, h / 2));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(180, 250, 224, 185)), null, new Rect(-70, 25, w + 140, 39), 20, 20);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(115, 190, 98, 60)), null, new Rect(-70, 0, w + 140, 27), 20, 20);
        dc.Pop();
        bool floating = Settings.Mode == "floating";
        double height = Math.Clamp(Settings.Height * .6, 30, 52), gap = floating ? Math.Min(22, Settings.BottomGap * .65 + 8) : 0;
        var bounds = new Rect(floating ? 14 : 0, h - height - gap, w - (floating ? 28 : 0), height);
        Color tint;
        try { tint = (Color)ColorConverter.ConvertFromString(Settings.Tint); } catch { tint = Colors.White; }
        double transparency = Settings.Material == "solid" ? 0 : Settings.Transparency;
        var glass = new LinearGradientBrush(Color.FromArgb((byte)(255 * (1 - transparency * .6)), tint.R, tint.G, tint.B),
            Color.FromArgb((byte)(255 * (1 - transparency * .8)), tint.R, tint.G, tint.B), 90);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), null, new Rect(bounds.X, bounds.Y + 4, bounds.Width, bounds.Height), floating ? height / 2 : 0, floating ? height / 2 : 0);
        dc.DrawRoundedRectangle(glass, new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1), bounds, floating ? height / 2 : 0, floating ? height / 2 : 0);
        string[] icons = ["⊞", "▰", "折", "✉", "♫", "▣"];
        string[] names = ["开始", "文件", "折页", "邮件", "音乐", "照片"];
        Color[] colors = [Colors.DodgerBlue, Colors.Goldenrod, Color.FromRgb(108, 92, 231), Colors.DodgerBlue, Colors.HotPink, Colors.Coral];
        double size = Math.Clamp(Settings.IconSize * .6, 15, 28), step = Math.Min(43, (w - 135) / 6);
        for (int i = 0; i < icons.Length; ++i)
        {
            double x = bounds.Left + 23 + step * i;
            double distance = Math.Abs(_pointer - x), scale = floating ? 1 + Math.Max(0, 1 - distance / 65) * (Settings.Magnification - 1) : 1;
            var rect = new Rect(x - size * scale / 2, bounds.Y + height / 2 - size * scale / 2 - (scale - 1) * 9, size * scale, size * scale);
            dc.DrawRoundedRectangle(new SolidColorBrush(colors[i]), null, rect, 5, 5);
            Text(dc, icons[i], rect.X + rect.Width * .17, rect.Y + rect.Height * .07, rect.Height * .66, Brushes.White);
            if (i is 2 or 4) dc.DrawEllipse(Brushes.DimGray, null, new Point(x, bounds.Bottom - 4), 1.6, 1.6);
            if (distance < step / 2 && floating)
            {
                var label = new Rect(x - 22, rect.Y - 24, 44, 19);
                dc.DrawRoundedRectangle(glass, new Pen(Brushes.White, .7), label, 9, 9);
                Text(dc, names[i], label.X + 9, label.Y + 2, 10, Brushes.Black);
            }
        }
        double right = bounds.Right - 10;
        if (Settings.ShowClock) { Text(dc, "10:24", right - 44, bounds.Y + height * .16, 13, Brushes.Black); right -= 54; }
        if (Settings.ShowStatus) { Text(dc, "◉", right - 17, bounds.Y + height * .24, 16, Brushes.Black); right -= 28; }
        if (Settings.ShowTray) Text(dc, "⌃", right - 15, bounds.Y + height * .27, 15, Brushes.Black);
        Text(dc, floating ? "悬浮任务栏 · 布局示意" : "Windows 系统任务栏 · 布局示意", 12, 9, 11, Brushes.White);
        dc.Pop();
    }
    private void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush) =>
        dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;

internal static class YichuThemeVerification
{
    internal static async Task Run(MainWindow main, string root, List<string> checks)
    {
        if (!App.IsSandbox) throw new InvalidOperationException("主题检查仅允许沙箱。");
        var selected = App.Options.ThemeMode;
        var settings = new SettingsWindow(main);
        try
        {
            main.FinishPanelMotion();
            main.UpdateLayout();
            await Save(main, "yichu-main-light.png");
            settings.Show();
            foreach (var page in new[] { "常规", "外观", "任务栏", "数据与恢复", "关于与更新" })
            {
                settings.SelectPage(page);
                settings.UpdateLayout();
                Check(Find<TextBlock>(settings).Any(text => text.Text == page && text.FontSize >= 17),
                    "设置页可打开：" + page);
            }
            settings.SelectPage("外观");
            settings.UpdateLayout();
            SelectTheme("dark");
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(ThemePalette.IsDark && (main.Surface.Background as SolidColorBrush)?.Color ==
                ThemePalette.Brush("CanvasBrush").Color, "深色设置即时更新主面板的画布画刷");
            Check(((Border)settings.Content).Background is SolidColorBrush settingsBackground &&
                settingsBackground.Color == ThemePalette.Brush("SurfaceBrush").Color &&
                Find<TextBlock>(main.NotesHost).Any(text => text.Text == "便签与待办" &&
                    (text.Foreground as SolidColorBrush)?.Color == ThemePalette.Brush("TextPrimaryBrush").Color),
                "深色同步更新代码生成的设置背景和便签文字");
            var reopened = new StartupOptions(preferencesPath: Path.Combine(App.Store.DataDirectory, "preferences.json"), fileOnly: true);
            Check(reopened.ThemeMode == "dark" && reopened.MotionMode == App.Options.MotionMode &&
                reopened.CloseEndsProcess == App.Options.CloseEndsProcess, "主题保存与开合动画及关闭偏好相互独立");
            await Save(settings, "yichu-settings-dark.png");
            await Save(main, "yichu-main-dark.png");
            SelectTheme("system");
            Check(ThemePalette.Mode == "system" && reopened.ThemeMode == "system", "跟随系统选项可保存并重新读取");
            SelectTheme("light");
            await Save(settings, "yichu-settings-light.png");
        }
        finally
        {
            settings.Close();
            App.Options.ThemeMode = selected;
            ThemePalette.Apply(selected);
            main.PanelMotion.InvalidateSnapshot();
        }

        void SelectTheme(string mode)
        {
            settings.UpdateLayout();
            var button = Find<Button>(settings).Single(item =>
                AutomationProperties.GetAutomationId(item) == "Settings-Theme-" + mode);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        async Task Save(Window source, string name)
        {
            await Task.Delay(250);
            source.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(source.ActualWidth),
                (int)Math.Ceiling(source.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(source);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, name));
            encoder.Save(output);
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}

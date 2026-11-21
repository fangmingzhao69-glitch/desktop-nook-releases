using System.IO;
using System.Text.Json;
using System.Windows;

namespace DesktopNook.App;

internal static class TrayVerification
{
    internal static async Task Run(MainWindow window, string root)
    {
        var checks = new List<string>();
        string failure = "";
        try
        {
            if (!App.IsSandbox) throw new InvalidOperationException("托盘验证仅允许沙箱。");
            window.KeepOpenForVerification = true;
            UiMotion.Configure("off");
            var tray = window.TrayForVerification;
            await Check(() => tray.Registered && tray.HasShellBounds, "Windows 注册托盘入口并返回其位置");
            await Check(() => window.WindowState == WindowState.Minimized, "最小化启动时托盘仍可用");
            var icons = new HashSet<string>();
            var initialTheme = ThemePalette.Family;
            foreach (var family in FoldTheme.All.Where(theme => theme != initialTheme).Append(initialTheme))
            {
                var previous = tray.CurrentIcon;
                ThemePalette.Apply(ThemePalette.Mode, family.Id);
                window.ApplyShellTheme(refreshPins: true);
                await Check(() => tray.CurrentIcon != previous && tray.Registered && tray.HasShellBounds &&
                    ReferenceEquals(window.Icon, FoldTheme.Mark(family)) &&
                    window.ShellIconLocation is { } location && File.Exists(location[..^2]),
                    family.Name + "同步更新最小化窗口图标和 Windows 托盘图标");
                icons.Add(window.ShellIconLocation!);
            }
            await Check(() => icons.Count == FoldTheme.All.Count, "四套配色分别使用各自的 Windows ICO");
            window.ReleaseIdleResources();
            await Check(() => window.ContentParked && tray.HasShellBounds, "释放窗口绘制资源时仍保留托盘");
            if (!tray.PostActivation()) throw new InvalidOperationException("无法发送沙箱托盘激活消息。");
            await Check(() => window.WindowState == WindowState.Normal && !window.ContentParked, "托盘激活可唤醒已收起的折页");
            if (!tray.PostActivation()) throw new InvalidOperationException("无法发送第二次沙箱托盘激活消息。");
            await Check(() => window.WindowState == WindowState.Minimized && tray.HasShellBounds, "再次激活可收起折页并保留托盘");
            if (!tray.RestoreRegistrationForVerification()) throw new InvalidOperationException("无法模拟通知区域重建。");
            await Check(() => tray.Registered && tray.HasShellBounds, "只向沙箱窗口发送任务栏重建消息即可恢复托盘入口");
            tray.Dispose();
            await Check(() => !tray.Registered && !tray.HasShellBounds, "注销后 Windows 不再返回托盘入口");
        }
        catch (Exception error) { failure = error.ToString(); }
        File.WriteAllText(Path.Combine(root, "tray-verification.json"),
            JsonSerializer.Serialize(new { passed = checks.Count, failure, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);

        async Task Check(Func<bool> condition, string caption)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (condition())
                {
                    checks.Add(caption);
                    return;
                }
                await Task.Delay(80);
            }
            throw new InvalidOperationException(caption);
        }
    }
}

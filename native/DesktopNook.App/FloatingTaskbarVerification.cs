using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using DesktopNook.Core;

namespace DesktopNook.App;

internal static class FloatingTaskbarVerification
{
    internal static async Task Run(MainWindow main, string root)
    {
        var checks = new List<string>(); string failure = "";
        var timings = new List<object>();
        void Check(bool condition, string label) { if (!condition) throw new IOException(label); checks.Add(label); }
        var originalStyle = App.Options.VisualStyle;
        var originalMode = App.Options.ThemeMode;
        var originalColor = App.Options.ColorTheme;
        var snapshot = JsonSerializer.Serialize(App.Store.State);
        Window? backdrop = null;
        main.KeepOpenForVerification = true;
        try
        {
            if (!App.IsSandbox) throw new IOException("悬浮任务栏检查仅允许独立沙箱。");
            main.FinishPanelMotion();
            Check(main.FloatingTaskbar.Saved.Mode == "system", "旧配置默认使用系统任务栏");
            foreach (var style in PanelVisualStyle.Choices)
            {
                App.Options.VisualStyle = style.Id; PanelVisualStyle.Select(style.Id);
                foreach (var dark in new[] { "light", "dark" })
                    foreach (var family in FoldTheme.All)
                    {
                        ThemePalette.Apply(dark, family.Id);
                        await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        Check(PanelVisualStyle.Selected == style.Id && ThemePalette.Family.Id == family.Id,
                            $"风格与配色独立：{style.Id}/{dark}/{family.Id}");
                    }
                App.Options.PersistForUpdate();
                Check(App.Options.VisualStyle == style.Id, "更新偏好快照保留风格：" + style.Id);
            }
            ThemePalette.Apply("light", "grape");
            PanelVisualStyle.Select("mist");
            main.FinishPanelMotion();
            await Task.Delay(200);
            Save(main, Path.Combine(root, "zheye-mist.png"));
            PanelVisualStyle.Select("glass");
            main.FinishPanelMotion();
            await Task.Delay(200);
            Save(main, Path.Combine(root, "zheye-glass.png"));
            var settings = new SettingsWindow(main);
            settings.Show(); settings.SelectPage(SettingsWindow.TaskbarPage); settings.UpdateLayout();
            var mode = Find<ComboBox>(settings).Single(control => AutomationProperties.GetAutomationId(control) == "Settings-TaskbarMode");
            mode.SelectedIndex = 1;
            await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Find<ComboBox>(settings).Any(control => AutomationProperties.GetAutomationId(control) == "Dock-Material"), "设置页包含可切换模式与材质");
            Check(!Find<CheckBox>(settings).Any(control => AutomationProperties.GetAutomationId(control) == "Dock-HideSystem"),
                "替换系统任务栏由模式决定，设置页无叠加开关");
            using (var production = new FloatingTaskbarClient(App.Store.DataDirectory, 0, sandbox: false))
            {
                Check(production.EngineSettings(new() { Mode = "floating" })["hideTaskbar"]!.GetValue<bool>()
                    && !production.EngineSettings(new())["hideTaskbar"]!.GetValue<bool>(),
                    "正式悬浮模式请求替换系统任务栏，系统模式保留原栏");
            }
            Save(settings, Path.Combine(root, "zheye-taskbar-settings.png"));
            mode.SelectedIndex = 0;
            await settings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            settings.Close();
            var trialSettings = new SettingsWindow(main);
            trialSettings.Show(); trialSettings.SelectPage(SettingsWindow.TaskbarPage); trialSettings.UpdateLayout();
            Find<ComboBox>(trialSettings).Single(control => AutomationProperties.GetAutomationId(control) == "Settings-TaskbarMode").SelectedIndex = 1;
            await trialSettings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Find<Button>(trialSettings).Single(button => Equals(button.Content, "试用 12 秒")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var trialDeadline = Stopwatch.StartNew();
            while ((!main.FloatingTaskbar.HasPreview || main.FloatingTaskbar.Current.Mode != "floating") && trialDeadline.Elapsed < TimeSpan.FromSeconds(35)) await Task.Delay(100);
            Check(main.FloatingTaskbar.HasPreview && main.FloatingTaskbar.Current.Mode == "floating", "设置页原生试用启动成功");
            trialDeadline.Restart();
            while ((main.FloatingTaskbar.Running || main.FloatingTaskbar.Current.Mode != "system") && trialDeadline.Elapsed < TimeSpan.FromSeconds(22)) await Task.Delay(100);
            Check(!main.FloatingTaskbar.Running && main.FloatingTaskbar.Current.Mode == "system", "设置页实际 12 秒计时到期自动退出悬浮任务栏");
            await Find<TaskbarSettingsPage>(trialSettings).Single().DiscardAsync();
            await trialSettings.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            trialSettings.Close();
            var work = SystemParameters.WorkArea;
            backdrop = new Window { Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Topmost = true, Background = new LinearGradientBrush(Color.FromRgb(226, 160, 107), Color.FromRgb(104, 162, 155), 30) };
            backdrop.Show();
            await Task.Delay(200);
            var desired = new FloatingTaskbarSettings { Mode = "floating", AutoHide = "never", RemoteCompatible = false,
                ShowNotifications = false, ShowStatus = false, ShowTray = false };
            var stopwatch = Stopwatch.StartNew();
            await main.FloatingTaskbar.ApplyAsync(desired, save: false);
            Check(main.FloatingTaskbar.Running, "实际原生任务栏已启动并完成身份校验和协议握手");
            timings.Add(new { operation = "start", milliseconds = stopwatch.ElapsedMilliseconds });
            var state = await main.FloatingTaskbar.Client.RequestAsync(new() { ["op"] = "get" });
            var current = state["current"]!;
            Check(current["hideTaskbar"]!.GetValue<bool>() == false && current["trayHost"]!.GetValue<bool>() == false,
                "沙箱不隐藏系统任务栏也不接管托盘");
            Check(!current["widgets"]!.GetValue<bool>() && !current["stageManager"]!.GetValue<bool>(), "未启用无关桌面模块");
            foreach (var material in new[] { "liquid", "frosted", "solid" })
            {
                await main.FloatingTaskbar.ApplyAsync(desired with { Material = material }, save: false);
                state = await main.FloatingTaskbar.Client.RequestAsync(new() { ["op"] = "get" });
                Check(state["current"]!["refraction"]!.GetValue<double>() == (material == "liquid" ? 30 : 0), "原生材质实际生效：" + material);
                await Task.Delay(500);
                CaptureDock(main.FloatingTaskbar.Client.ProcessId, Path.Combine(root, "dock-real-" + material + ".png"));
            }
            backdrop.Close(); backdrop = null;
            await main.FloatingTaskbar.CancelPreviewAsync();
            Check(!main.FloatingTaskbar.Running && main.FloatingTaskbar.Current.Mode == "system", "撤销预览后原生进程退出并恢复系统模式");
            await main.FloatingTaskbar.ApplyAsync(desired, save: true);
            Check(new FloatingTaskbarStore(App.Store.DataDirectory).Load() == desired, "悬浮模式持久化可重新读取");
            await main.FloatingTaskbar.ApplyAsync(desired with { Mode = "system" }, save: false);
            await main.FloatingTaskbar.CancelPreviewAsync();
            Check(main.FloatingTaskbar.Running && main.FloatingTaskbar.Current.Mode == "floating", "试用系统模式后可恢复已保存悬浮模式");
            for (int i = 0; i < 3; i++)
            {
                await main.FloatingTaskbar.ApplyAsync(desired with { Mode = "system" }, save: true);
                await main.FloatingTaskbar.ApplyAsync(desired, save: true);
            }
            Check(main.FloatingTaskbar.Running, "多次真实启停后仍可连接");
            await main.FloatingTaskbar.ApplyAsync(new(), save: true);
            Check(!main.FloatingTaskbar.Running, "最终切回系统模式并释放进程");
            var childRoot = Path.Combine(root, "ParentProbe"); Directory.CreateDirectory(childRoot);
            var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            childStart.ArgumentList.Add("--sandbox"); childStart.ArgumentList.Add(childRoot); childStart.ArgumentList.Add("--floating-taskbar-parent-probe");
            using var child = Process.Start(childStart) ?? throw new IOException("无法启动父进程探针。");
            try
            {
                var readyFile = Path.Combine(childRoot, "dock-parent-ready.json");
                for (int i = 0; !File.Exists(readyFile) && i < 100; i++) { if (child.HasExited) break; await Task.Delay(100); }
                Check(File.Exists(readyFile), "受控沙箱父进程已启动原生任务栏");
                int dockPid = JsonDocument.Parse(File.ReadAllText(readyFile)).RootElement.GetProperty("pid").GetInt32();
                using var dock = Process.GetProcessById(dockPid);
                child.Kill(); await child.WaitForExitAsync();
                var deadline = Stopwatch.StartNew();
                while (!dock.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(100);
                Check(dock.HasExited, "沙箱父进程异常结束后原生任务栏自行退出");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            Check(JsonSerializer.Serialize(App.Store.State) == snapshot, "切换风格和任务栏不改变收纳分类与项目");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            backdrop?.Close();
            main.FloatingTaskbar.Dispose();
            App.Options.VisualStyle = originalStyle; App.Options.ThemeMode = originalMode; App.Options.ColorTheme = originalColor;
            ThemePalette.Apply(originalMode, originalColor); PanelVisualStyle.Select(originalStyle);
            File.WriteAllText(Path.Combine(root, "floating-taskbar-verification.json"), JsonSerializer.Serialize(new { passed = checks.Count, failure, checks, timings }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint value);
    private static void CaptureDock(int pid, string path)
    {
        nint handle = 0;
        while ((handle = FindWindowEx(0, handle, "AtriumBarVisual", null)) != 0)
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == pid) break;
        }
        if (handle == 0 || !GetWindowRect(handle, out var b)) throw new IOException("未找到原生任务栏绘制窗口。");
        var screen = GetDC(0); var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, dc == 0 ? 1 : b.Right - b.Left, b.Bottom - b.Top); var old = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, b.Right - b.Left, b.Bottom - b.Top, screen, b.Left, b.Top, 0x40CC0020)) throw new IOException("任务栏屏幕采集失败。");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var file = File.Create(path); encoder.Save(file);
        }
        finally { SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(0, screen); }
    }
    internal static async Task ParentProbe(MainWindow main, string root)
    {
        try
        {
            main.KeepOpenForVerification = true;
            await main.FloatingTaskbar.ApplyAsync(new() { Mode = "floating", AutoHide = "never", RemoteCompatible = true }, save: false);
            File.WriteAllText(Path.Combine(root, "dock-parent-ready.json"), JsonSerializer.Serialize(new { pid = main.FloatingTaskbar.Client.ProcessId }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "dock-parent-error.txt"), ex.ToString()); Application.Current.Shutdown(1); }
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

internal static class GroupPreviewVerification
{
    private static readonly List<Window> Probes = [];
    internal static void ShowProbe(string root)
    {
        Directory.CreateDirectory(root);
        ShellIntegration.SetAppId($"DesktopNook.GroupVerification.{Environment.ProcessId}");
        var handles = new List<long>();
        for (int i = 1; i <= 3; i++)
        {
            var window = new Window
            {
                Title = $"折页隔离测试窗口 {i}", Width = 270, Height = 150,
                Left = 100 + i * 80, Top = 100 + i * 50, ShowActivated = false,
                Content = new TextBlock { Text = $"多窗口预览测试 {i}", Margin = new Thickness(24), FontSize = 18 }
            };
            window.Show();
            Probes.Add(window);
            var handle = new WindowInteropHelper(window).Handle;
            ShortcutInstaller.SetProbeWindowId(handle, $"DesktopNook.GroupVerification.{Environment.ProcessId}");
            handles.Add(handle.ToInt64());
        }
        File.WriteAllText(Path.Combine(root, "group-windows.json"), JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, handles }));
    }

    internal static async Task Run(string root)
    {
        var checks = new List<string>(); string? failure = null;
        using var recovery = new NativeTaskbarGroupPreview();
        GetCursorPos(out var original);
        try
        {
            nint bar = FindWindowEx(0, 0, "Shell_TrayWnd", null);
            Check(bar != 0, "找到现有 Windows 任务栏");
            string id = $"DesktopNook.GroupVerification.{Environment.ProcessId}";
            var candidates = await Task.Run(() => NativeTaskbarGroupPreview.WindowsFor(id));
            Check(candidates.Count == 3 && candidates.All(item => Probes.Any(window => new WindowInteropHelper(window).Handle == item.Handle)),
                "分组只匹配三个隔离测试窗口");
            NativeTaskbarContents.Measurement? contents = null; int index = -1;
            for (int attempt = 0; attempt < 30 && index < 0; attempt++)
            {
                await Task.Delay(150);
                contents = await Task.Run(() => NativeTaskbarContents.Measure(bar));
                if (contents.HasValue) index = Array.IndexOf(contents.Value.AppIds, id);
            }
            Check(index >= 0, "Windows 将测试窗口归为同一任务栏分组");
            await Task.Delay(700);
            contents = await Task.Run(() => NativeTaskbarContents.Measure(bar));
            index = contents.HasValue ? Array.IndexOf(contents.Value.AppIds, id) : -1;
            Check(index >= 0, "胶囊完成伸缩后重新确认分组位置");
            recovery.Update(bar, contents!.Value);
            nint target = new WindowInteropHelper(Probes[1]).Handle;
            ShowWindowAsync(target, 6);
            await Task.Delay(200);
            Check(IsIconic(target), "目标窗口已最小化");
            var button = contents.Value.AppButtons[index];
            await Click(new Point(button.Left + button.Width / 2, button.Top + button.Height / 2));
            for (int wait = 0; wait < 45 && recovery.Popup is null; wait++) await Task.Delay(100);
            if (recovery.Popup is null) throw new Exception("窗口预览未出现：" + recovery.Diagnostic);
            Check(true, "真实鼠标点击分组图标后弹出窗口选择");
            var popup = recovery.Popup!;
            popup.UpdateLayout();
            var choices = Find<Button>(popup).ToList();
            Check(choices.Count == 3, "三个窗口各有独立可点击预览");
            Snapshot(popup, Path.Combine(root, "group-preview.png"));
            var choice = choices.Single(item => AutomationProperties.GetAutomationId(item) == "TaskbarWindow-" + target);
            var center = choice.PointToScreen(new Point(choice.ActualWidth / 2, choice.ActualHeight / 2));
            await Click(center);
            for (int wait = 0; wait < 30 && (GetForegroundWindow() != target || IsIconic(target)); wait++) await Task.Delay(100);
            Check(GetForegroundWindow() == target && !IsIconic(target), "真实鼠标点击缩略图后还原并激活对应窗口");
            Check(recovery.Popup is null, "选择窗口后释放预览");
            recovery.Dispose();
            Check(FindWindowEx(0, 0, "Shell_TrayWnd", null) == bar, "验收全程保留原 Explorer 任务栏");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            recovery.Dispose();
            SetCursorPos(original.X, original.Y);
            File.WriteAllText(Path.Combine(root, "group-preview-verification.json"), JsonSerializer.Serialize(new { success = failure is null, passed = checks.Count, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failure is null ? 0 : 1);
        }
        void Check(bool value, string text) { if (!value) throw new Exception(text); checks.Add(text); }
        static async Task Click(Point point)
        {
            if (!SetCursorPos((int)point.X, (int)point.Y)) throw new InvalidOperationException("无法定位测试鼠标。");
            mouse_event(2, 0, 0, 0, 0); await Task.Delay(90); mouse_event(4, 0, 0, 0, 0); await Task.Delay(100);
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) yield return target;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    internal static void Snapshot(Window window, string path)
    {
        // Capture the compositor, including DWM live thumbnails.
        var origin = window.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)(window.ActualWidth * dpi.DpiScaleX), height = (int)(window.ActualHeight * dpi.DpiScaleY);
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, (int)origin.X, (int)origin.Y, 0x40CC0020);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var file = File.Create(path); encoder.Save(file);
        }
        finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }
    internal static void PlaceBehind(Window window, Window foreground) => SetWindowPos(new WindowInteropHelper(window).Handle,
        new WindowInteropHelper(foreground).Handle, 0, 0, 0, 0, 0x13);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
}

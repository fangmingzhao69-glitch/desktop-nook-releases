using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using DesktopNook.Core;

namespace DesktopNook.App;

internal static class NativeTaskbarShapeVerification
{
    internal static async Task<int> RunAsync(string root, bool parentProbe)
    {
        Directory.CreateDirectory(root);
        if (parentProbe)
        {
            var shape = new NativeTaskbarShape();
            await shape.StartAsync();
            File.WriteAllText(Path.Combine(root, "ready"), "ready");
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        var checks = new List<string>();
        string? failure = null;
        Rect before = default, floating = default, expanded = default;
        double? trayGap = null;
        var store = new TaskbarAppearanceStore(root);
        var session = new TaskbarAppearanceSession(store, new NativeTaskbarBackend(store.DirectoryPath, sandbox: false));
        try
        {
            nint taskbar = FindWindowEx(0, 0, "Shell_TrayWnd", null);
            nint bridge = FindWindowEx(taskbar, 0, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            Check(taskbar != 0 && bridge != 0 && IsWindowVisible(taskbar) && IsWindowVisible(bridge),
                "The Explorer taskbar and its native controls are visible.");
            Check(GetWindowRect(bridge, out before), "Recorded the original geometry.");

            await session.PreviewAsync(new TaskbarAppearanceSettings { Enabled = true, Style = "normal", FloatingLayout = true }, allowDownload: false);
            Check(FindWindowEx(0, 0, "Shell_TrayWnd", null) == taskbar && IsWindowVisible(taskbar),
                "The same Explorer taskbar remains visible in capsule mode.");
            var centered = await WaitForBounds(rect => rect.Left > before.Left && rect.Right < before.Right &&
                rect.Top == before.Top && rect.Bottom == before.Bottom && IsCentered(rect, before));
            Check(centered.HasValue && GetWindowRect(bridge, out floating),
                "The native capsule is inset horizontally and spans the full taskbar height.");
            Check(centered!.Value.Right - centered.Value.Left < (before.Right - before.Left) - 100,
                "The capsule packs its contents and centers on the screen.");
            nint region = CreateRectRgn(0, 0, 0, 0);
            Rect clip = default;
            try
            {
                Check(region != 0 && GetWindowRgn(bridge, region) != 0 &&
                    PtInRegion(region, (floating.Right - floating.Left) / 2, (floating.Bottom - floating.Top) / 2) &&
                    GetRgnBox(region, out clip) != 0 && clip.Top == 0 && clip.Bottom == before.Bottom - before.Top,
                    "The capsule window region reaches both original vertical edges.");
            }
            finally { if (region != 0) DeleteObject(region); }
            await Task.Delay(350);
            var packed = await Task.Run(() => NativeTaskbarContents.Measure(taskbar));
            trayGap = packed?.TrayGap;
            double visibleMiddle = floating.Top + (clip.Top + clip.Bottom) / 2d;
            Check(packed is { } layout && layout.AppButtons.Length > 0 &&
                Math.Abs(layout.AppButtons[0].Top + layout.AppButtons[0].Height / 2 - visibleMiddle) <= 2,
                "The native icon row is centered vertically inside the visible capsule.");
            Check(packed!.Value is { HasOverflow: false, TrayGap: >= 0 } &&
                TryGetFace(bridge, out floating) && packed.Value.Right <= floating.Right + 2,
                "The application strip and tray both fit without overflowing.");
            Check(IsCentered(floating, before) && trayGap <= Math.Max(20, (floating.Bottom - floating.Top) / 2),
                "Only a narrow separator gap remains after compacting the native tray.");
            Check(GetProp(bridge, NativeTaskbarShape.SmoothProperty) != 0,
                "Explorer activated the antialiased Composition clip.");
            Check(GetProp(bridge, "DesktopNook.NativeTaskbarShape.DividerVisible") != 0,
                "Explorer drew the separator between the application strip and tray.");
            if (!packed.Value.OverflowButton.IsEmpty)
            {
                var arrow = packed.Value.OverflowButton;
                Check(ClickOverflow(taskbar, arrow), "Clicked the chevron inside the original Explorer taskbar.");
                try
                {
                    var wait = Stopwatch.StartNew();
                    bool aligned = false;
                    do
                    {
                        await Task.Delay(120);
                        var popup = FindWindowEx(0, 0, "TopLevelWindowForOverflowXamlIsland", null);
                        if (popup == 0) popup = FindWindowEx(0, 0, "NotifyIconOverflowWindow", null);
                        aligned = popup != 0 && IsWindowVisible(popup) && GetWindowRect(popup, out var flyout) &&
                            Math.Abs((flyout.Left + flyout.Right) / 2d - (arrow.Left + arrow.Width / 2)) <= 14;
                    } while (!aligned && wait.Elapsed < TimeSpan.FromSeconds(4));
                    Check(aligned, "The hidden-icons flyout centers above the capsule tray arrow.");
                }
                finally
                {
                    var popup = FindWindowEx(0, 0, "TopLevelWindowForOverflowXamlIsland", null);
                    if (popup == 0) popup = FindWindowEx(0, 0, "NotifyIconOverflowWindow", null);
                    if (popup != 0 && IsWindowVisible(popup)) await Task.Run(() => InvokeOverflow(taskbar, arrow));
                }
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Check(SetWindowPos(bridge, 0, 0, 0, before.Right - before.Left, before.Bottom - before.Top, 0x4214),
                    "Queued an Explorer-style request to restore full width.");
                for (int sample = 0; sample < 4; sample++)
                {
                    await Task.Delay(24);
                    if (!TryGetFace(bridge, out var retained) || !retained.Equals(floating))
                        throw new IOException($"Native geometry changed from {floating.Left}..{floating.Right} to " +
                            $"{retained.Left}..{retained.Right} on a full-width request (trim " +
                            $"{GetProp(bridge, NativeTaskbarShape.TrimAppliedProperty)}).");
                }
            }
            checks.Add("The geometry stays compact during native full-width requests.");

            var iconRoot = Path.Combine(root, "icon-probe");
            var launchIcon = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in new[] { "--sandbox", iconRoot, "--native-taskbar-capsule-icon-probe" })
                launchIcon.ArgumentList.Add(arg);
            using (var icon = Process.Start(launchIcon) ?? throw new IOException("Could not add the isolated application button."))
            {
                try
                {
                    int previousWidth = floating.Right - floating.Left;
                    var grown = await WaitForBounds(rect =>
                    {
                        int width = rect.Right - rect.Left;
                        if (width < previousWidth - 3)
                            throw new IOException("The capsule contracted while an application was opening.");
                        if (!IsCentered(rect, before))
                            throw new IOException("The capsule left the screen center during app startup.");
                        previousWidth = Math.Max(previousWidth, width);
                        return width >= floating.Right - floating.Left + 20 &&
                            rect.Top == floating.Top && rect.Bottom == floating.Bottom &&
                            width < before.Right - before.Left - 100;
                    });
                    Check(grown.HasValue && !icon.HasExited, "Opening a separate application grows the centered capsule.");
                    expanded = grown!.Value;
                    var groups = await Task.Run(() => NativeTaskbarContents.Measure(taskbar));
                    Check(groups is { } content && content.Left >= expanded.Left - 2 && content.Right <= expanded.Right + 2,
                        "Both the application buttons and system tray fit inside the expanded capsule.");
                    for (int sample = 0; sample < 12; sample++)
                    {
                        await Task.Delay(160);
                        if (!TryGetFace(bridge, out var stable) || Math.Abs(stable.Left - expanded.Left) > 3 ||
                            Math.Abs(stable.Right - expanded.Right) > 3)
                            throw new IOException("The expanded capsule moved again without another taskbar change.");
                    }
                    checks.Add("The expanded capsule stays still across successive layout scans.");
                    var appId = $"DesktopNook.CapsuleVerification.{icon.Id}";
                    int probeIndex = Array.FindIndex(groups!.Value.AppIds, id => id == appId);
                    Check(probeIndex >= 0, "Identified the separate probe process by its native taskbar app ID.");
                    var button = groups.Value.AppButtons[probeIndex];
                    var pointer = new NativePoint { X = (int)(button.X + button.Width / 2), Y = (int)(button.Y + button.Height / 2) };
                    if (!GetCursorPos(out var saved) || !SetCursorPos(pointer.X, pointer.Y)) throw new IOException("Cannot position the test pointer.");
                    try
                    {
                        icon.Refresh();
                        PostMessage(icon.MainWindowHandle, ShowProbePopup, 0, 0);
                        var limit = Stopwatch.StartNew();
                        bool positioned = false;
                        do
                        {
                            await Task.Delay(100);
                            nint popup = FindWindowEx(0, 0, "Static", appId);
                            positioned = popup != 0 && GetWindowRect(popup, out var surface) &&
                                Math.Abs((surface.Left + surface.Right) / 2 - pointer.X) <= 3 && surface.Bottom <= button.Top;
                        } while (!positioned && limit.ElapsedMilliseconds < 3000);
                        Check(positioned, "A non-Explorer popup from the probe process aligns above its own taskbar icon.");
                    }
                    finally
                    {
                        if (GetCursorPos(out var current) && current.Equals(pointer)) SetCursorPos(saved.X, saved.Y);
                    }
                }
                finally { if (!icon.HasExited) { icon.Kill(); await icon.WaitForExitAsync(); } }
            }
            Check((await WaitForBounds(rect => Math.Abs((rect.Right - rect.Left) - (floating.Right - floating.Left)) <= 4 &&
                IsCentered(rect, before))).HasValue, "Closing the application shrinks the capsule back.");

            await session.CancelPreviewAsync();
            Check(await WaitUntilRestored(), "Cancelling restores the original native taskbar geometry.");

            var probe = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--sandbox", Path.Combine(root, "guard-probe"), "--native-taskbar-shape-parent-probe" })
                probe.ArgumentList.Add(arg);
            using var process = Process.Start(probe) ?? throw new IOException("Could not start the isolated recovery probe.");
            try
            {
                var timeout = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "guard-probe", "ready")) && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(12))
                    await Task.Delay(100);
                Check(!process.HasExited && (await WaitForBounds(rect => rect.Equals(floating))).HasValue,
                    "An isolated application process applied the native capsule.");
                process.Kill();
                await process.WaitForExitAsync();
                Check(await WaitUntilRestored(), "The guard restores the original geometry after host termination.");
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }

            async Task<bool> WaitUntilRestored()
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(4))
                {
                    if (GetWindowRect(bridge, out var restored) && restored.Equals(before)) return true;
                    await Task.Delay(100);
                }
                return false;
            }

            async Task<Rect?> WaitForBounds(Func<Rect, bool> predicate)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(9))
                {
                    if (TryGetFace(bridge, out var rect) && predicate(rect)) return rect;
                    await Task.Delay(150);
                }
                return null;
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        finally { session.Stop(); }
        File.WriteAllText(Path.Combine(root, "native-taskbar-shape.json"), JsonSerializer.Serialize(
            new { passed = checks.Count, failure, checks, original = before, capsule = floating, expanded, trayGap },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        return failure is null ? 0 : 1;

        void Check(bool condition, string description)
        {
            if (!condition) throw new IOException(description);
            checks.Add(description);
        }
    }

    private static bool IsCentered(Rect rect, Rect before) => Math.Abs(rect.Left + rect.Right - before.Left - before.Right) <= 2;
    private static bool TryGetFace(nint bridge, out Rect rect)
    {
        if (!GetWindowRect(bridge, out rect)) return false;
        rect.Right -= (int)GetProp(bridge, NativeTaskbarShape.TrimAppliedProperty);
        return true;
    }

    private static bool ClickOverflow(nint taskbar, System.Windows.Rect button)
    {
        var center = new NativePoint { X = (int)(button.Left + button.Width / 2), Y = (int)(button.Top + button.Height / 2) };
        nint hit = WindowFromPoint(center);
        if ((hit != taskbar && GetAncestor(hit, 2) != taskbar) || !GetCursorPos(out var original) ||
            !SetCursorPos(center.X, center.Y))
            return false;
        try
        {
            if (!GetCursorPos(out var actual) || !actual.Equals(center)) return false;
            mouse_event(2, 0, 0, 0, 0);
            mouse_event(4, 0, 0, 0, 0);
            return true;
        }
        finally
        {
            if (GetCursorPos(out var actual) && actual.Equals(center)) SetCursorPos(original.X, original.Y);
        }
    }

    private static bool InvokeOverflow(nint taskbar, System.Windows.Rect button)
    {
        try
        {
            var center = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2);
            var candidates = AutomationElement.FromHandle(taskbar).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton"));
            foreach (AutomationElement item in candidates)
            {
                if (!item.Current.BoundingRectangle.Contains(center) ||
                    (!item.Current.Name.Contains("显示隐藏的图标", StringComparison.Ordinal) &&
                    !item.Current.Name.Contains("顯示隱藏的圖示", StringComparison.Ordinal) &&
                    !item.Current.Name.Contains("show hidden icons", StringComparison.OrdinalIgnoreCase))) continue;
                if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    ((InvokePattern)invoke).Invoke();
                    return true;
                }
                if (item.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
                {
                    ((TogglePattern)toggle).Toggle();
                    return true;
                }
            }
            return false;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        { return false; }
    }

    internal static void ShowIconProbe(string root)
    {
        Directory.CreateDirectory(root);
        ShellIntegration.SetAppId($"DesktopNook.CapsuleVerification.{Environment.ProcessId}");
        var window = new Window
        {
            Title = "折页任务栏验收",
            Width = 180,
            Height = 72,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        Application.Current.MainWindow = window;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        string id = $"DesktopNook.CapsuleVerification.{Environment.ProcessId}";
        ShortcutInstaller.SetProbeWindowId(handle, id);
        System.Windows.Interop.HwndSource.FromHwnd(handle)!.AddHook((nint hwnd, int message, nint w, nint l, ref bool handled) =>
        {
            if (message == ShowProbePopup && GetCursorPos(out var point))
            {
                CreateWindowEx(0x08000080, "Static", id, 0x90000000, point.X + 120, point.Y - 90, 150, 56, hwnd, 0, 0, 0);
                handled = true;
            }
            return 0;
        });
        window.Show();
    }
    private const int ShowProbePopup = 0x8104;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, int msg, nint w, nint l);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nint extra);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Rect bounds);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}

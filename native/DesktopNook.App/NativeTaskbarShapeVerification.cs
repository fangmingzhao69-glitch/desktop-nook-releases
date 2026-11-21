using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
        Rect before = default, floating = default;
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
            Check(GetWindowRect(bridge, out floating) && floating.Left > before.Left && floating.Right < before.Right && floating.Bottom < before.Bottom,
                "The original native composition child moves inward, with left, right and bottom gaps.");
            nint region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Check(region != 0 && GetWindowRgn(bridge, region) != 0 && !PtInRegion(region, 0, 0) &&
                    PtInRegion(region, (floating.Right - floating.Left) / 2, (floating.Bottom - floating.Top) / 2),
                    "The visible silhouette of the native child has rounded corners.");
            }
            finally { if (region != 0) DeleteObject(region); }
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
                Check(!process.HasExited && GetWindowRect(bridge, out var probeBounds) && probeBounds.Equals(floating),
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
        }
        catch (Exception error) { failure = error.ToString(); }
        finally { session.Stop(); }
        File.WriteAllText(Path.Combine(root, "native-taskbar-shape.json"), JsonSerializer.Serialize(
            new { passed = checks.Count, failure, checks, original = before, capsule = floating },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        return failure is null ? 0 : 1;

        void Check(bool condition, string description)
        {
            if (!condition) throw new IOException(description);
            checks.Add(description);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}

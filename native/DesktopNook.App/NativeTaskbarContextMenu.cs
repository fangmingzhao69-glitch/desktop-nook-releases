using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace DesktopNook.App;

// Modern jump lists are immersive CoreWindows and can be absent from EnumWindows.
// Follow an actual right-click and validate SystemItemList before moving the menu.
internal sealed class NativeTaskbarContextMenu : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private int _generation;
    internal nint LastMenu { get; private set; }
    internal bool Aligned { get; private set; }
    internal string Diagnostic { get; private set; } = "idle";

    internal void Track(nint taskbar, Rect button)
    {
        int generation = ++_generation;
        LastMenu = 0; Aligned = false;
        Diagnostic = "right-click tracked";
        _dispatcher.BeginInvoke(() => _ = WatchAsync(taskbar, button, generation));
    }

    private async Task WatchAsync(nint taskbar, Rect button, int generation)
    {
        try
        {
            var deadline = Environment.TickCount64 + 1800;
            while (generation == _generation && Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
                nint menu = GetForegroundWindow();
                if (!IsWindowVisible(menu) || !IsShellMenuWindow(menu)) continue;
                Diagnostic = "shell foreground found";
                bool jumpList = await Task.Run(() => HasSystemItems(menu)).WaitAsync(TimeSpan.FromMilliseconds(400));
                Diagnostic = jumpList ? "system items found" : "system items not found";
                if (!jumpList) continue;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt != 0) await Task.Delay(attempt * 60);
                    if (generation != _generation || GetForegroundWindow() != menu || !IsWindowVisible(menu)) return;
                    var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                    if (!GetWindowRect(menu, out var bounds) || !GetWindowRect(taskbar, out var shell) ||
                        !GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref info)) return;
                    if (bounds.Right - bounds.Left < 80 || bounds.Bottom - bounds.Top < 40) continue;
                    double scale = Math.Max(1, GetDpiForWindow(taskbar) / 96d);
                    var position = NativeTaskbarHover.Place(button, shell.ToRect(), info.Work.ToRect(),
                        new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), scale);
                    if (Math.Abs(position.X - bounds.Left) > 1 || Math.Abs(position.Y - bounds.Top) > 1)
                        SetWindowPos(menu, 0, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, 0x4215);
                    LastMenu = menu;
                }
                if (generation == _generation && LastMenu != 0 && GetWindowRect(menu, out var final))
                {
                    Aligned = Math.Abs(final.Left + (final.Right - final.Left) / 2d - (button.Left + button.Width / 2)) <= 3 ||
                        final.Left <= button.Left && final.Right >= button.Right;
                    Diagnostic = Aligned ? "aligned" : "menu moved back";
                }
                return;
            }
            Diagnostic = "no jump list observed: " + Diagnostic;
        }
        catch (Exception error) when (error is TimeoutException or COMException or Win32Exception or InvalidOperationException or ArgumentException)
        { Diagnostic = error.GetType().Name; }
    }

    private static bool IsShellMenuWindow(nint window)
    {
        var name = new StringBuilder(128);
        if (GetClassName(window, name, name.Capacity) == 0 || name.ToString() != "Windows.UI.Core.CoreWindow") return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return false;
        using var process = Process.GetProcessById((int)pid);
        return process.ProcessName is "ShellExperienceHost" or "ShellHost";
    }

    private static bool HasSystemItems(nint window)
    {
        try
        {
            return AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemItemList")) is not null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    public void Dispose() { _generation++; LastMenu = 0; Aligned = false; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

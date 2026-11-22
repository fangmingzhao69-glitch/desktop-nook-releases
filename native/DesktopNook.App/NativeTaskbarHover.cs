using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace DesktopNook.App;

// The shell still creates its own flyouts. Only app previews and app tooltips are reanchored.
internal sealed class NativeTaskbarHover : IDisposable
{
    private const uint ObjectDestroy = 0x8001;
    private const uint ObjectShow = 0x8002;
    private const uint ObjectHide = 0x8003;
    private const uint ObjectLocationChange = 0x800B;
    private readonly Callback _callback;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<nint, (Rect Shell, Rect Capsule, Rect[] Buttons, double Scale)> _bars = new();
    private readonly Dictionary<nint, long> _pending = new();
    private readonly Dictionary<nint, HoverAnchor> _anchored = new();
    private HoverAnchor? _recent;
    private uint _processId;
    private nint _hook;
    private long _sequence;

    internal NativeTaskbarHover() => _callback = OnWindowEvent;

    internal void Update(nint taskbar, Rect shell, Rect capsule, Rect[] icons, uint dpi)
    {
        GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid != _processId || _hook == 0)
        {
            Dispose();
            if (pid == 0) return;
            _hook = SetWinEventHook(ObjectDestroy, ObjectLocationChange, 0, _callback, pid, 0, 0);
            _processId = pid;
        }
        _bars[taskbar] = (shell, capsule, icons, dpi == 0 ? 1 : dpi / 96d);
        if (GetCursorPos(out var pointer))
            ObserveButton(new System.Windows.Point(pointer.X, pointer.Y));
    }

    internal void Remove(nint taskbar) => _bars.Remove(taskbar);

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
        _processId = 0;
        _pending.Clear();
        _anchored.Clear();
        _recent = null;
        _bars.Clear();
    }

    private void OnWindowEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (hook != _hook || window == 0 || objectId is not (0 or -4) || childId != 0 ||
            eventId is not (ObjectDestroy or ObjectShow or ObjectHide or ObjectLocationChange)) return;
        if (eventId is ObjectHide or ObjectDestroy) { _pending.Remove(window); _anchored.Remove(window); return; }
        if (!GetCursorPos(out var pointer)) return;

        var point = new System.Windows.Point(pointer.X, pointer.Y);
        var className = new StringBuilder(128);
        if (GetClassName(window, className, className.Capacity) == 0) return;
        string name = className.ToString();
        if (name is not ("TaskListThumbnailWnd" or "tooltips_class32" or "Xaml_WindowedPopupClass")) return;
        if (_pending.ContainsKey(window)) return;
        var anchor = ObserveButton(point);
        if (anchor is null && GetWindowRect(window, out var surface) && surface.Contains(point))
        {
            if (_anchored.TryGetValue(window, out var current)) anchor = current;
            else if (_recent is { } recent && Environment.TickCount64 - recent.Observed <= 650) anchor = recent;
        }
        if (anchor is not { } target || !_bars.TryGetValue(target.Taskbar, out var bar) ||
            (name != "TaskListThumbnailWnd" && GetAncestor(window, 3) != target.Taskbar)) return;

        long sequence = ++_sequence;
        _pending[window] = sequence;
        _dispatcher.BeginInvoke(() => _ = RepositionAsync(window, target, bar.Shell, bar.Scale, name, sequence));
    }

    private HoverAnchor? ObserveButton(System.Windows.Point point)
    {
        foreach (var (taskbar, bar) in _bars)
        {
            if (!bar.Capsule.Contains(point)) continue;
            var button = bar.Buttons.FirstOrDefault(rect => rect.Contains(point));
            // Moving across the tray clears app-hover context. Its flyouts use the system position.
            return _recent = button.Width > 0 ? new(taskbar, button, Environment.TickCount64) : null;
        }
        return null;
    }

    private async Task RepositionAsync(nint window, HoverAnchor anchor, Rect shell, double scale, string name, long sequence)
    {
        try
        {
            if (name == "Xaml_WindowedPopupClass" && !_anchored.ContainsKey(window) &&
                !await Task.Run(() => IsAppFlyout(window)).WaitAsync(TimeSpan.FromMilliseconds(300)))
                return;
            nint taskbar = anchor.Taskbar;
            if (!_pending.TryGetValue(window, out long current) || current != sequence ||
                !_bars.ContainsKey(taskbar) || !IsWindowVisible(window) || !GetWindowRect(window, out var bounds))
                return;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return;
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0 || pid != _processId || !GetCursorPos(out var pointer)) return;
            var point = new System.Windows.Point(pointer.X, pointer.Y);
            if (!anchor.Button.Contains(point) && !bounds.Contains(point)) return;
            nint monitor = MonitorFromWindow(taskbar, 2);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;
            var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            var placement = Place(anchor.Button, shell, info.Work.ToRect(), size, scale);
            _anchored[window] = anchor;
            if (Math.Abs(placement.X - bounds.Left) <= 2 && Math.Abs(placement.Y - bounds.Top) <= 2) return;
            SetWindowPos(window, 0, (int)Math.Round(placement.X), (int)Math.Round(placement.Y), 0, 0, 0x4215);
        }
        catch (TimeoutException) { }
        finally { if (_pending.TryGetValue(window, out long current) && current == sequence) _pending.Remove(window); }
    }

    internal static System.Windows.Point Place(Rect icon, Rect taskbar, Rect work, Size flyout, double scale)
    {
        double x = icon.Left + (icon.Width - flyout.Width) / 2;
        double y = taskbar.Bottom <= work.Top + 1
            ? icon.Bottom + 5 * scale
            : icon.Top - flyout.Height - 5 * scale;
        return new(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - flyout.Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - flyout.Height)));
    }

    private static bool IsAppFlyout(nint window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var walker = TreeWalker.RawViewWalker;
            int remaining = 40;
            return ContainsPreview(root, 0);

            bool ContainsPreview(AutomationElement item, int depth)
            {
                if (depth > 6 || remaining-- <= 0) return false;
                var info = item.Current;
                if (info.ControlType == ControlType.ToolTip || info.AutomationId == "TaskItemThumbnailListRepeater" ||
                    info.ClassName.StartsWith("Taskbar.TaskItemThumbnail", StringComparison.Ordinal))
                    return true;
                for (var child = walker.GetFirstChild(item); child is not null && remaining > 0; child = walker.GetNextSibling(child))
                    if (ContainsPreview(child, depth + 1)) return true;
                return false;
            }
        }
        catch (Exception error) when (error is ElementNotAvailableException or ArgumentException or COMException or InvalidOperationException)
        { return false; }
    }

    private readonly record struct HoverAnchor(nint Taskbar, Rect Button, long Observed);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
        public readonly bool Contains(System.Windows.Point point) =>
            point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate void Callback(nint eventHook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, Callback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

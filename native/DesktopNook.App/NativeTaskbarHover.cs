using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace DesktopNook.App;

// Reanchor Explorer's app previews, app tooltips, and hidden-icons flyout.
internal sealed class NativeTaskbarHover : IDisposable
{
    private const uint ObjectDestroy = 0x8001;
    private const uint ObjectShow = 0x8002;
    private const uint ObjectHide = 0x8003;
    private const uint ObjectLocationChange = 0x800B;
    private readonly Callback _callback;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<nint, (Rect Shell, Rect Capsule, Rect[] Buttons, Rect TrayOverflow, double Scale,
        Rect[] TrayButtons, string[] AppIds)> _bars = new();
    private readonly Dictionary<nint, long> _pending = new();
    private readonly Dictionary<nint, HoverAnchor> _anchored = new();
    private HoverAnchor? _recent;
    private uint _processId;
    private nint _hook;
    private nint _showHook;
    private long _sequence;

    internal NativeTaskbarHover() => _callback = OnWindowEvent;

    internal void Update(nint taskbar, Rect shell, Rect capsule, NativeTaskbarContents.Measurement contents, uint dpi)
    {
        GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid != _processId || _hook == 0)
        {
            Dispose();
            if (pid == 0) return;
            _hook = SetWinEventHook(ObjectDestroy, ObjectLocationChange, 0, _callback, pid, 0, 0);
            _showHook = SetWinEventHook(ObjectShow, ObjectShow, 0, _callback, 0, 0, 2); // Skip our process.
            _processId = pid;
        }
        _bars[taskbar] = (shell, capsule, contents.AppButtons, contents.OverflowButton, dpi == 0 ? 1 : dpi / 96d,
            contents.TrayButtons, contents.AppIds);
        if (GetCursorPos(out var pointer))
            ObserveButton(new System.Windows.Point(pointer.X, pointer.Y));
    }

    internal void Remove(nint taskbar) => _bars.Remove(taskbar);

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        if (_showHook != 0) UnhookWinEvent(_showHook);
        _showHook = 0;
        _hook = 0;
        _processId = 0;
        _pending.Clear();
        _anchored.Clear();
        _recent = null;
        _bars.Clear();
    }

    private void OnWindowEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (hook == _showHook && _showHook != 0)
        {
            if (objectId == 0 && childId == 0 && window != 0 && GetAncestor(window, 2) == window)
                PositionApplicationPopup(window);
            return;
        }
        if (hook != _hook || window == 0 || objectId is not (0 or -4) || childId != 0 ||
            eventId is not (ObjectDestroy or ObjectShow or ObjectHide or ObjectLocationChange)) return;
        if (eventId is ObjectHide or ObjectDestroy) { _pending.Remove(window); _anchored.Remove(window); return; }
        if (!GetCursorPos(out var pointer)) return;

        var point = new System.Windows.Point(pointer.X, pointer.Y);
        var className = new StringBuilder(128);
        if (GetClassName(window, className, className.Capacity) == 0) return;
        string name = className.ToString();
        bool overflow = name is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";
        if (!overflow && name is not ("TaskListThumbnailWnd" or "tooltips_class32" or "Xaml_WindowedPopupClass")) return;
        if (_pending.ContainsKey(window)) return;
        var anchor = overflow ? FindOverflowAnchor(window, point) : ObserveButton(point);
        if (!overflow && anchor is null && GetWindowRect(window, out var surface) && surface.Contains(point))
        {
            if (_anchored.TryGetValue(window, out var current)) anchor = current;
            else if (_recent is { } recent && Environment.TickCount64 - recent.Observed <= 650) anchor = recent;
        }
        if (anchor is not { } target || !_bars.TryGetValue(target.Taskbar, out var bar) ||
            (name == "tooltips_class32" && GetAncestor(window, 3) != target.Taskbar)) return;

        long sequence = ++_sequence;
        _pending[window] = sequence;
        _dispatcher.BeginInvoke(() => _ = RepositionAsync(window, target, bar.Shell, bar.Scale, name, sequence));
    }

    private void PositionApplicationPopup(nint window)
    {
        if (!GetCursorPos(out var pointer) || !GetWindowRect(window, out var bounds) ||
            bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return;
        var point = new Point(pointer.X, pointer.Y);
        foreach (var (taskbar, bar) in _bars)
        {
            if (!bar.Capsule.Contains(point) || MonitorFromWindow(window, 2) != MonitorFromWindow(taskbar, 2)) continue;
            int index = Array.FindIndex(bar.Buttons, button => button.Contains(point));
            var icon = index >= 0 ? bar.Buttons[index] : bar.TrayButtons.FirstOrDefault(button => button.Contains(point));
            if (icon.Width <= 0) return;
            GetWindowThreadProcessId(window, out uint pid);
            long style = GetWindowLongPtr(window, -16).ToInt64();
            if (pid == 0 || pid == _processId || (style & 0x80000000L) == 0 ||
                (style & 0x00C00000L) == 0x00C00000L) return;
            var cls = new StringBuilder(128);
            GetClassName(window, cls, cls.Capacity);
            bool standard = cls.ToString() is "tooltips_class32" or "#32768";
            if (!standard && (GetWindowLongPtr(window, -20).ToInt64() & 0x08000080L) == 0) return;
            // A transient window must originate at the taskbar edge, on this display.
            if (Math.Min(Math.Abs(bounds.Bottom - icon.Top), Math.Abs(bounds.Top - icon.Bottom)) > 60 * bar.Scale) return;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName is "ShellHost" or "ShellExperienceHost" or "StartMenuExperienceHost" or "TextInputHost") return;
                string? id = index >= 0 ? bar.AppIds[index] : null;
                bool owned = !string.IsNullOrEmpty(id) && MatchesApplication(window, process, id);
                // For tray icons without a public GUID, pair standard tooltips/menus with
                // the icon's pre-capsule position, never just the presence of a popup.
                double legacyX = icon.Left + icon.Width / 2 + bar.Shell.Right - bar.Capsule.Right;
                if (!owned && !(index < 0 && standard && legacyX >= bounds.Left - 8 * bar.Scale &&
                    legacyX <= bounds.Right + 8 * bar.Scale)) return;
                var position = NativePlacement(taskbar, icon, bar.Shell, bounds, bar.Scale);
                if (position.HasValue && IsWindowVisible(window))
                    SetWindowPos(window, 0, position.Value.X, position.Value.Y, 0, 0, 0x4215);
            }
            catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException or COMException) { }
            return;
        }
    }

    private static bool MatchesApplication(nint popup, Process process, string id)
    {
        nint owner = GetAncestor(popup, 3);
        if (string.Equals(id, ShortcutInstaller.WindowApplicationId(owner), StringComparison.OrdinalIgnoreCase))
            return true;
        if (System.IO.Path.IsPathFullyQualified(id))
            return string.Equals(id, process.MainModule?.FileName, StringComparison.OrdinalIgnoreCase);
        var identity = new StringBuilder(256);
        uint length = (uint)identity.Capacity;
        return GetApplicationUserModelId(process.Handle, ref length, identity) == 0 &&
            string.Equals(id, identity.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static NativePoint? NativePlacement(nint taskbar, Rect icon, Rect shell, NativeRect bounds, double scale)
    {
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref monitor)) return null;
        var location = Place(icon, shell, monitor.Work.ToRect(), new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), scale);
        return new NativePoint { X = (int)Math.Round(location.X), Y = (int)Math.Round(location.Y) };
    }

    private HoverAnchor? FindOverflowAnchor(nint window, Point pointer)
    {
        nint monitor = MonitorFromWindow(window, 2);
        foreach (var (taskbar, bar) in _bars)
            if (!bar.TrayOverflow.IsEmpty && bar.TrayOverflow.Contains(pointer))
                return new(taskbar, bar.TrayOverflow, Environment.TickCount64, true);
        foreach (var (taskbar, bar) in _bars)
            if (!bar.TrayOverflow.IsEmpty && MonitorFromWindow(taskbar, 2) == monitor)
                return new(taskbar, bar.TrayOverflow, Environment.TickCount64, true);
        return null;
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
            if (!anchor.Tray && !anchor.Button.Contains(point) && !bounds.Contains(point)) return;
            if (NativePlacement(taskbar, anchor.Button, shell, bounds, scale) is not { } placement) return;
            _anchored[window] = anchor;
            if (Math.Abs(placement.X - bounds.Left) <= 2 && Math.Abs(placement.Y - bounds.Top) <= 2) return;
            SetWindowPos(window, 0, placement.X, placement.Y, 0, 0, 0x4215);
        }
        catch (TimeoutException) { }
        finally { if (_pending.TryGetValue(window, out long current) && current == sequence) _pending.Remove(window); }
    }

    internal static System.Windows.Point Place(Rect icon, Rect taskbar, Rect work, Size flyout, double scale)
    {
        double x = icon.Left + (icon.Width - flyout.Width) / 2;
        bool above = taskbar.Bottom > work.Top + 1;
        double anchorY = above ? icon.Top - 5 * scale : icon.Bottom + 5 * scale;
        double y = above ? anchorY - flyout.Height : anchorY;
        var anchor = new NativePoint { X = (int)Math.Round(icon.Left + icon.Width / 2), Y = (int)Math.Round(anchorY) };
        var size = new NativePoint { X = (int)Math.Ceiling(flyout.Width), Y = (int)Math.Ceiling(flyout.Height) };
        var exclude = new NativeRect { Left = (int)icon.Left, Right = (int)Math.Ceiling(icon.Right),
            Top = (int)taskbar.Top, Bottom = (int)taskbar.Bottom };
        const uint centerVerticalWorkArea = 0x10044;
        if (CalculatePopupWindowPosition(ref anchor, ref size, centerVerticalWorkArea | (above ? 0x20u : 0), ref exclude, out var placed))
        {
            x = placed.Left;
            y = placed.Top;
        }
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
                if (info.ControlType == ControlType.ToolTip || info.ClassName == "ToolTip" ||
                    info.AutomationId == "TaskItemThumbnailListRepeater" ||
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

    private readonly record struct HoverAnchor(nint Taskbar, Rect Button, long Observed, bool Tray = false);
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);
    [DllImport("user32.dll")] private static extern bool CalculatePopupWindowPosition(ref NativePoint anchor, ref NativePoint size,
        uint flags, ref NativeRect exclude, out NativeRect result);
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

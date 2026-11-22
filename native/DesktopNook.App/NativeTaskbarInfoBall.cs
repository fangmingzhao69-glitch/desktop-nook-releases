using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopNook.App;

// Tencent's information ball is a separate tool window beside the old tray coordinates.
// Only adopt that specific adjacent window and only when Windows allows ordinary moves.
internal sealed class NativeTaskbarInfoBall
{
    private const string OwnerProperty = "DesktopNook.Capsule.InfoBallOwner";
    private const string OriginProperty = "DesktopNook.Capsule.InfoBallOrigin";
    private const uint MoveFlags = 0x4215;
    private nint _window;
    private nint _taskbar;
    private nint _refusedWindow;
    private long _nextSearch;

    internal int WidthFor(nint taskbar)
    {
        if (_window != 0)
        {
            if (IsWindowVisible(_window) && GetWindowRect(_window, out var frame) &&
                GetProp(_window, OwnerProperty) == (nint)Environment.ProcessId)
                return taskbar == _taskbar ? frame.Right - frame.Left : 0;
            Restore(_window, Environment.ProcessId);
            _window = _taskbar = 0;
        }
        if (Environment.TickCount64 < _nextSearch) return 0;
        _nextSearch = Environment.TickCount64 + 2000;
        if (ClassOf(taskbar) != "Shell_TrayWnd") return 0;
        var tray = FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
        if (tray == 0 || !GetWindowRect(tray, out var nativeTray)) return 0;

        int width = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var frame) ||
                frame.Right - frame.Left is < 22 or > 90 || frame.Bottom - frame.Top is < 22 or > 90 ||
                Math.Abs(frame.Right - nativeTray.Left) > 16 || frame.Top < nativeTray.Top - 12 ||
                frame.Bottom > nativeTray.Bottom + 12 || !IsTencentInfoBall(window)) return true;
            if (window == _refusedWindow || GetProp(window, OwnerProperty) != 0) return true;
            if (!SetWindowPos(window, 0, frame.Left, frame.Top, 0, 0, MoveFlags))
            {
                _refusedWindow = window;
                return true;
            }

            long packedOrigin = (long)(uint)frame.Left | ((long)frame.Top << 32);
            if (!SetProp(window, OriginProperty, unchecked((nint)packedOrigin))) return true;
            if (!SetProp(window, OwnerProperty, Environment.ProcessId))
            {
                RemoveProp(window, OriginProperty);
                return true;
            }
            _window = window;
            _taskbar = taskbar;
            width = frame.Right - frame.Left;
            return false;
        }, 0);
        return width;
    }

    internal void Align(nint taskbar, double trayLeft, double leftEnd, int capsuleTop, int capsuleBottom)
    {
        if (_window == 0 || _taskbar != taskbar || !GetWindowRect(_window, out var frame) ||
            GetProp(_window, OwnerProperty) != (nint)Environment.ProcessId) return;
        int width = frame.Right - frame.Left;
        int x = (int)Math.Round(trayLeft) - width - 2;
        int y = (capsuleTop + capsuleBottom - (frame.Bottom - frame.Top)) / 2;
        if (x >= leftEnd + 3 && (x != frame.Left || y != frame.Top))
            SetWindowPos(_window, 0, x, y, 0, 0, MoveFlags);
    }

    internal void Stop()
    {
        if (_window != 0) Restore(_window, Environment.ProcessId);
        _window = _taskbar = _refusedWindow = 0;
        _nextSearch = 0;
    }

    internal static void RestoreOwned(int pid) => EnumWindows((window, _) => { Restore(window, pid); return true; }, 0);

    private static void Restore(nint window, int pid)
    {
        if (GetProp(window, OwnerProperty) != pid || !IsTencentInfoBall(window)) return;
        long packed = GetProp(window, OriginProperty);
        if (SetWindowPos(window, 0, unchecked((int)packed), (int)(packed >> 32), 0, 0, MoveFlags))
        {
            RemoveProp(window, OwnerProperty);
            RemoveProp(window, OriginProperty);
        }
    }

    private static bool IsTencentInfoBall(nint window)
    {
        string cls = ClassOf(window);
        if (!cls.StartsWith("Qt", StringComparison.Ordinal) || !cls.EndsWith("QWindowToolSaveBits", StringComparison.Ordinal)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("MiniHomePagePro", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private static string ClassOf(nint window)
    {
        var text = new StringBuilder(128);
        return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace DesktopNook.App;

// The shell still owns the taskbar, its appbar reservation, and all its controls.
// Only its native composition child moves and receives a rounded window region.
internal sealed class NativeTaskbarShape
{
    private const string BridgeClass = "Windows.UI.Composition.DesktopWindowContentBridge";
    private const string OwnerProperty = "DesktopNook.NativeTaskbarShape.Owner";
    private const string PositionProperty = "DesktopNook.NativeTaskbarShape.Position";
    private const string GeometryLockProperty = "DesktopNook.NativeTaskbarShape.GeometryLock";
    private const uint MoveFlags = 0x4214; // async, no activation, no z-order changes
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<nint, Placement> _owned = new();
    private readonly Dictionary<nint, int> _pendingWidths = new();
    private readonly NativeTaskbarHover _hover = new();
    private Task<Dictionary<nint, NativeTaskbarContents.Measurement>>? _pendingMeasure;
    private int _revision;
    private bool _refreshing;
    private EventWaitHandle? _guardStop;

    internal NativeTaskbarShape()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    internal static bool IsCapsule(nint taskbar)
    {
        nint bridge = FindWindowEx(taskbar, 0, BridgeClass, null);
        return bridge != 0 && GetProp(bridge, OwnerProperty) != 0;
    }

    internal async Task StartAsync()
    {
        if (_timer.IsEnabled) return;
        string key = "Local\\DesktopNook.NativeTaskbarShape." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".ready");
        _guardStop = new EventWaitHandle(false, EventResetMode.ManualReset, key + ".stop");
        try
        {
            using var owner = Process.GetCurrentProcess();
            var launch = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "--watch-native-taskbar-shape", key, owner.Id.ToString(), owner.StartTime.ToFileTimeUtc().ToString() })
                launch.ArgumentList.Add(arg);
            using var guard = Process.Start(launch) ?? throw new IOException("无法启动原生任务栏恢复组件。");
            if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(8))) || guard.HasExited)
                throw new IOException("原生任务栏恢复组件未就绪。");
            await RefreshAsync();
            var deadline = Stopwatch.StartNew();
            while (!_owned.Any(entry => IsInPosition(entry.Key, entry.Value.Parent, entry.Value.Shape)) &&
                deadline.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(50);
            if (!_owned.Any(entry => IsInPosition(entry.Key, entry.Value.Parent, entry.Value.Shape)))
                throw new IOException("未能调整当前 Windows 原生任务栏。请确认任务栏处于水平方向，且没有其他工具正在修改它。");
            _timer.Start();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Stop()
    {
        _timer.Stop();
        _revision++;
        _hover.Dispose();
        _pendingWidths.Clear();
        RestoreOwned(Environment.ProcessId);
        if (_owned.Count != 0) TaskbarPlacement.Invalidate();
        _owned.Clear();
        _guardStop?.Set();
        _guardStop?.Dispose();
        _guardStop = null;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        int revision = _revision;
        try
        {
            var bridges = Bridges().ToArray();
            if (_pendingMeasure is { IsCompleted: false }) return;
            _pendingMeasure = Task.Run(() =>
            {
                var measured = new Dictionary<nint, NativeTaskbarContents.Measurement>();
                foreach (var (taskbar, bridge) in bridges)
                    if (NativeTaskbarContents.Measure(taskbar) is { } contents)
                        measured[bridge] = contents;
                return measured;
            });
            var results = await _pendingMeasure.WaitAsync(TimeSpan.FromSeconds(2));
            if (revision != _revision) return;
            ApplyMeasurements(bridges, results);
        }
        catch (TimeoutException) { }
        finally { _refreshing = false; }
    }

    private void ApplyMeasurements(
        (nint Taskbar, nint Bridge)[] bridges,
        Dictionary<nint, NativeTaskbarContents.Measurement> measurements)
    {
        var owner = (nint)Environment.ProcessId;
        var found = new HashSet<nint>();
        foreach (var (taskbar, bridge) in bridges)
        {
            found.Add(bridge);
            if (GetProp(bridge, GeometryLockProperty) == 0 ||
                !GetClientRect(taskbar, out var bounds) || bounds.Right < 500 || bounds.Right > ushort.MaxValue ||
                bounds.Bottom < 32 || bounds.Right < bounds.Bottom * 3)
            {
                Restore(taskbar, bridge, owner);
                _owned.Remove(bridge);
                _pendingWidths.Remove(bridge);
                _hover.Remove(taskbar);
                continue;
            }
            if (!measurements.TryGetValue(bridge, out var contents)) continue;
            _owned.TryGetValue(bridge, out var previous);
            int padding = Math.Max(22, (int)Math.Round(bounds.Bottom * .55));
            int maximum = bounds.Right - 8;
            // When the new button cannot fit Windows may compress every app button at once.
            // Remember the regular slot from the unconstrained/native layout for this DPI.
            double normalSlot = previous.Shape.Height == bounds.Bottom
                ? Math.Max(previous.NormalAppSlot, contents.AppSlot)
                : contents.AppSlot;
            bool squeezed = contents.AppSlot > 0 && contents.AppSlot < normalSlot * .9;
            double normalChrome = squeezed && previous.ChromeCount == contents.ChromeCount
                ? Math.Max(previous.NormalChromeWidth, contents.ChromeWidth)
                : contents.ChromeWidth;
            int squeezedSpace = squeezed ? (int)Math.Ceiling((normalSlot - contents.AppSlot) * contents.AppButtons.Length +
                normalChrome - contents.ChromeWidth) : 0;
            int measuredWidth = Math.Min(maximum, contents.PackedWidth + squeezedSpace);
            int minimum = Math.Abs(previous.PackedWidth - measuredWidth) <= 2 ? previous.MinimumWidth : 0;
            bool laidOut = previous.Shape.Width > 0 && IsInPosition(bridge, taskbar, previous.Shape);
            if (laidOut && GetWindowRect(bridge, out var position) &&
                (contents.HasOverflow || contents.Left < position.Left - 2 || contents.Right > position.Right + 2))
                minimum = Math.Max(minimum, previous.Shape.Width + padding);
            else if (previous.Shape.Width == 0 && contents.HasOverflow)
                minimum = maximum;

            int width = Math.Clamp(Math.Max(measuredWidth + padding, minimum), Math.Min(220, maximum), maximum);
            if (previous.Shape.Width > 0 && Math.Abs(previous.Shape.Width - width) <= 3)
                width = previous.Shape.Width;
            if (previous.Shape.Width > 0 && previous.Shape.Width != width &&
                (!_pendingWidths.TryGetValue(bridge, out int observed) || Math.Abs(observed - width) > 3))
            {
                // Explorer can report an intermediate set of button bounds during reflow.
                // Commit a resize only when the next scan agrees with the proposed width.
                _pendingWidths[bridge] = width;
                if (laidOut) UpdateHover(taskbar, bridge, contents);
                continue;
            }
            _pendingWidths.Remove(bridge);
            // Clip around the middle of the original icon row. Shrinking the native control's
            // height can leave its normal-height contents sitting too low in the capsule.
            int inset = Math.Clamp((int)Math.Round(bounds.Bottom * .09), 3, 8);
            int gap = Math.Clamp((int)Math.Round(bounds.Bottom * .14), inset, 12);
            var desired = new Layout((bounds.Right - width) / 2, inset - gap, width, bounds.Bottom, inset);
            if (GetProp(bridge, OwnerProperty) != owner)
            {
                // A non-default geometry or region may belong to another customizer.
                if (GetProp(bridge, OwnerProperty) != 0 || !IsInPosition(bridge, taskbar, new(0, 0, bounds.Right, bounds.Bottom, 0)) ||
                    HasRegion(bridge) || !SetProp(bridge, OwnerProperty, owner))
                    continue;
            }
            bool unchanged = previous.Shape == desired && IsInPosition(bridge, taskbar, desired);
            if (!unchanged && !Apply(bridge, desired))
            {
                Restore(taskbar, bridge, owner);
                _owned.Remove(bridge);
                _hover.Remove(taskbar);
                continue;
            }
            _owned[bridge] = new(taskbar, desired, measuredWidth, minimum, normalSlot, contents.ChromeCount, normalChrome);
            if (!unchanged) TaskbarPlacement.Invalidate();
            else UpdateHover(taskbar, bridge, contents);
        }
        foreach (nint bridge in _owned.Keys.Where(window => !found.Contains(window)).ToArray())
        {
            nint taskbar = _owned[bridge].Parent;
            Restore(taskbar, bridge, owner);
            _hover.Remove(taskbar);
            _pendingWidths.Remove(bridge);
            _owned.Remove(bridge);
        }
    }

    private void UpdateHover(nint taskbar, nint bridge, NativeTaskbarContents.Measurement contents)
    {
        if (GetWindowRect(taskbar, out var shell) && GetWindowRect(bridge, out var shape))
            _hover.Update(taskbar, shell.ToRect(), shape.ToRect(), contents.AppButtons, GetDpiForWindow(taskbar));
    }

    private static bool Apply(nint bridge, Layout shape)
    {
        int visibleHeight = shape.Height - 2 * shape.Inset;
        nint region = CreateRoundRectRgn(1, shape.Inset, shape.Width - 1, shape.Height - shape.Inset, visibleHeight, visibleHeight);
        if (region == 0) return false;
        // Share the four coordinates atomically with Explorer's WINDOWPOS callback.
        ulong packed = (ushort)shape.X | (ulong)(ushort)shape.Y << 16 |
            (ulong)(ushort)shape.Width << 32 | (ulong)(ushort)shape.Height << 48;
        if (SetProp(bridge, PositionProperty, unchecked((nint)packed)) &&
            SetWindowPos(bridge, 0, shape.X, shape.Y, shape.Width, shape.Height, MoveFlags) &&
            SetWindowRgn(bridge, region, true) != 0)
            return true; // Windows owns the region after SetWindowRgn succeeds.
        DeleteObject(region);
        return false;
    }

    private static bool HasRegion(nint bridge)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return true;
        try { return GetWindowRgn(bridge, region) != 0; }
        finally { DeleteObject(region); }
    }

    private static bool IsInPosition(nint bridge, nint taskbar, Layout shape)
    {
        if (!GetWindowRect(bridge, out var window)) return false;
        var origin = new Point();
        return ClientToScreen(taskbar, ref origin) &&
            window.Left - origin.X == shape.X && window.Top - origin.Y == shape.Y &&
            window.Right - window.Left == shape.Width && window.Bottom - window.Top == shape.Height;
    }

    private static IEnumerable<(nint Taskbar, nint Bridge)> Bridges()
    {
        GetWindowThreadProcessId(GetShellWindow(), out uint explorer);
        if (explorer == 0) yield break;
        foreach (var name in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            nint taskbar = 0;
            while ((taskbar = FindWindowEx(0, taskbar, name, null)) != 0)
            {
                GetWindowThreadProcessId(taskbar, out uint owner);
                if (owner != explorer) continue;
                var bridge = FindWindowEx(taskbar, 0, BridgeClass, null);
                if (bridge != 0 && GetWindowThreadProcessId(bridge, out uint bridgeOwner) != 0 && bridgeOwner == explorer)
                    yield return (taskbar, bridge);
            }
        }
    }

    private static void RestoreOwned(int pid)
    {
        foreach (var (taskbar, bridge) in Bridges()) Restore(taskbar, bridge, pid);
    }

    private static void Restore(nint taskbar, nint bridge, nint owner)
    {
        if (GetProp(bridge, OwnerProperty) != owner || GetParent(bridge) != taskbar || !GetClientRect(taskbar, out var bounds))
            return;
        RemoveProp(bridge, PositionProperty);
        if (SetWindowRgn(bridge, 0, true) != 0 && SetWindowPos(bridge, 0, 0, 0, bounds.Right, bounds.Bottom, MoveFlags))
            RemoveProp(bridge, OwnerProperty);
    }

    internal static int RunGuard(string key, int pid, long creationTime)
    {
        const string prefix = "Local\\DesktopNook.NativeTaskbarShape.";
        if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(key.AsSpan(prefix.Length), "N", out _) || pid <= 0)
            return 64;
        try
        {
            using var ready = EventWaitHandle.OpenExisting(key + ".ready");
            using var stop = EventWaitHandle.OpenExisting(key + ".stop");
            using var owner = Process.GetProcessById(pid);
            if (owner.StartTime.ToFileTimeUtc() != creationTime) return 64;
            using var finished = new ManualResetEvent(false);
            finished.SafeWaitHandle = new SafeWaitHandle(owner.Handle, ownsHandle: false);
            ready.Set();
            WaitHandle.WaitAny([stop, finished]);
            RestoreOwned(pid);
            return 0;
        }
        catch (ArgumentException) { return 64; }
        catch (WaitHandleCannotBeOpenedException) { return 64; }
    }

    private readonly record struct Layout(int X, int Y, int Width, int Height, int Inset);
    private readonly record struct Placement(nint Parent, Layout Shape, int PackedWidth, int MinimumWidth,
        double NormalAppSlot, int ChromeCount, double NormalChromeWidth);

    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly System.Windows.Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}

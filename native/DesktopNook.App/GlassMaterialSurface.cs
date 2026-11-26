using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNook.App;

internal sealed class GlassMaterialSurface : IDisposable
{
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _timer;
    private GlassBackdropCapture? _capture;
    private ImageSource? _frame;
    private bool _enabled, _unavailable, _busy, _stopPending, _resetPending;
    private int _stillFrames, _failures;
    internal bool Running => _timer.IsEnabled;
    internal bool HasFrame => _frame is not null;
    internal int UpdatedFrames { get; private set; }
    internal long BufferBytes => _capture?.BufferBytes ?? 0;
    internal string Status { get; private set; } = "stopped";

    internal GlassMaterialSurface(MainWindow owner)
    {
        _owner = owner;
        _timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => CaptureFrame();
        owner.StateChanged += VisibilityChanged;
        owner.IsVisibleChanged += VisibleChanged;
        owner.LocationChanged += GeometryChanged;
        owner.SizeChanged += SizeChanged;
        owner.Closed += Closed;
    }

    internal void Enable(bool enabled)
    {
        _enabled = enabled;
        RefreshVisibility();
    }

    internal void RefreshVisibility()
    {
        if (!_enabled || !_owner.IsVisible || _owner.WindowState == WindowState.Minimized || _owner.ContentParked)
        { Stop(); return; }
        if (!_timer.IsEnabled) { _unavailable = false; _failures = 0; _stillFrames = 0; _timer.Start(); }
        CaptureFrame();
    }

    internal void ResetDisplay()
    {
        if (_busy) { _resetPending = true; return; }
        _capture?.Dispose(); _capture = null; _unavailable = false; _failures = 0;
        _frame = null; _owner.SetGlassBackdrop(null, default);
        if (_enabled) _timer.Interval = TimeSpan.FromMilliseconds(200);
    }

    private void CaptureFrame()
    {
        if (_busy || !_enabled || _owner.WindowState == WindowState.Minimized || !_owner.IsVisible || _owner.ContentParked || _owner.PanelMotion.Active) return;
        _busy = true;
        try
        {
            if (_unavailable) return;
            _capture ??= new GlassBackdropCapture();
            if (!_capture.Supported) { _unavailable = true; Status = _capture.Status; _capture.Dispose(); _capture = null; return; }
            nint handle = new WindowInteropHelper(_owner).Handle;
            if (!GetWindowRect(handle, out var ownerRect)) return;
            var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
            double dpi = VisualTreeHelper.GetDpi(_owner).DpiScaleX;
            int padding = (int)Math.Ceiling(30 * dpi);
            var rect = new GlassBackdropCapture.NativeRect
            {
                Left = Math.Max(monitor.Monitor.Left, ownerRect.Left - padding), Top = Math.Max(monitor.Monitor.Top, ownerRect.Top - padding),
                Right = Math.Min(monitor.Monitor.Right, ownerRect.Right + padding), Bottom = Math.Min(monitor.Monitor.Bottom, ownerRect.Bottom + padding)
            };
            var excluded = new List<nint> { handle };
            var seen = new HashSet<nint> { handle };
            for (nint above = GetWindow(handle, 3); above != 0 && seen.Add(above) && seen.Count < 128; above = GetWindow(above, 3))
                if (IsWindowVisible(above)) excluded.Add(above);
            var frame = _capture.Capture(rect, excluded.ToArray());
            if (_stopPending || !_enabled || _owner.WindowState == WindowState.Minimized) return;
            Status = _capture.Status;
            if (frame is null)
            {
                if (Status != "capture suspended" && ++_failures >= 3)
                { _unavailable = true; _capture.Dispose(); _capture = null; _frame = null; _owner.SetGlassBackdrop(null, default); }
                return;
            }
            _failures = 0;
            var margin = new Thickness(-(ownerRect.Left - rect.Left) / dpi, -(ownerRect.Top - rect.Top) / dpi,
                -(rect.Right - ownerRect.Right) / dpi, -(rect.Bottom - ownerRect.Bottom) / dpi);
            if (!ReferenceEquals(_frame, frame))
            {
                _frame = frame; UpdatedFrames++; _stillFrames = 0;
                _owner.SetGlassBackdrop(frame, margin);
                _owner.PanelMotion.InvalidateSnapshot();
                _timer.Interval = TimeSpan.FromMilliseconds(200);
            }
            else
            {
                _owner.SetGlassBackdrop(frame, margin);
                if (++_stillFrames >= 3) _timer.Interval = TimeSpan.FromMilliseconds(800);
            }
        }
        finally
        {
            _busy = false;
            if (_stopPending) { _stopPending = false; Stop(); }
            if (_resetPending) { _resetPending = false; ResetDisplay(); }
        }
    }

    private void GeometryChanged(object? sender, EventArgs args) { if (_enabled) _timer.Interval = TimeSpan.FromMilliseconds(200); }
    private void SizeChanged(object sender, SizeChangedEventArgs args) => GeometryChanged(sender, args);
    private void VisibilityChanged(object? sender, EventArgs args) => RefreshVisibility();
    private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshVisibility();
    private void Closed(object? sender, EventArgs args) => Dispose();
    private void Stop()
    {
        if (_busy) { _timer.Stop(); _stopPending = true; return; }
        _timer.Stop(); _capture?.Dispose(); _capture = null; _frame = null;
        _owner.SetGlassBackdrop(null, default); Status = "stopped";
    }
    public void Dispose()
    {
        _enabled = false; Stop();
        _owner.StateChanged -= VisibilityChanged; _owner.IsVisibleChanged -= VisibleChanged;
        _owner.LocationChanged -= GeometryChanged; _owner.SizeChanged -= SizeChanged; _owner.Closed -= Closed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

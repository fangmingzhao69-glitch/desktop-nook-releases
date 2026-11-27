using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

// A private, filtered capture surface. Captured pixels stay in memory and the
// application remains present in ordinary screenshots and screen sharing.
internal sealed class GlassBackdropCapture : IDisposable
{
    internal const int PixelBudget = 8 * 1024 * 1024;
    private readonly ScalingCallback _callback;
    private nint _host, _magnifier;
    private bool _initialized;
    private bool _suspended = true;
    private byte[]? _pixels;
    private int _width, _height, _stride;
    private bool _reading, _hasFrame;
    private BitmapSource? _image;
    private ulong _signature;
    private nint[] _filter = [];
    private int _hostWidth, _hostHeight;
    internal long BufferBytes => (_pixels?.LongLength ?? 0) + (_image is null ? 0 : (long)_image.PixelWidth * _image.PixelHeight * 4);
    internal string Status { get; private set; } = "not initialized";
    internal Guid Format { get; private set; }
    internal bool Supported => _magnifier != 0;
    internal bool Suspended => _suspended;

    internal GlassBackdropCapture()
    {
        _callback = Receive;
        if (!Environment.Is64BitProcess || GetSystemMetrics(80) != 1) { Status = "capture unavailable for this display configuration"; return; }
        try
        {
            if (!MagInitialize()) { Status = "capture initialization unavailable"; return; }
            _initialized = true;
            _host = CreateWindowEx(0x080800A0, "Static", "DesktopNook private backdrop", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
            if (_host == 0) { Status = "capture host unavailable"; return; }
            _magnifier = CreateWindowEx(0, "Magnifier", "", 0x50000000, 0, 0, 1, 1, _host, 0, 0, 0);
            if (_magnifier == 0 || !MagSetImageScalingCallback(_magnifier, _callback)) { _magnifier = 0; Status = "capture callback unavailable"; return; }
            var identity = new Transform { M11 = 1, M22 = 1, M33 = 1 };
            MagSetWindowTransform(_magnifier, ref identity);
            SetLayeredWindowAttributes(_host, 0, 1, 2);
            Status = "ready";
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { Status = "capture API unavailable"; }
    }

    internal BitmapSource? Capture(NativeRect rect, nint[] excluded)
    {
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (GetSystemMetrics(80) != 1) { Status = "capture unavailable for this display configuration"; return null; }
        if (_magnifier == 0 || width <= 0 || height <= 0 || (long)width * height * 4 > PixelBudget) return null;
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > GetSystemMetrics(0) || rect.Bottom > GetSystemMetrics(1)) return null;
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4) { Status = "capture suspended"; return null; }
        if (_suspended) { ShowWindow(_host, 8); _suspended = false; }
        var filter = excluded.Append(_host).Distinct().ToArray();
        if (!_filter.SequenceEqual(filter))
        {
            if (!MagSetWindowFilterList(_magnifier, 0, filter.Length, filter)) return null;
            _filter = filter;
        }
        if (_hostWidth != width || _hostHeight != height)
        {
            if (!SetWindowPos(_host, 1, 0, 0, width, height, 0x50) || !SetWindowPos(_magnifier, 0, 0, 0, width, height, 0x14)) return null;
            _hostWidth = width; _hostHeight = height;
        }
        _hasFrame = false; _reading = true;
        try
        {
            if (!MagSetWindowSource(_magnifier, rect)) return null;
            InvalidateRect(_magnifier, 0, false); UpdateWindow(_magnifier);
        }
        finally { _reading = false; }
        if (!_hasFrame || _pixels is null)
        { Status = "no backdrop frame"; return null; }
        ulong signature = 14695981039346656037;
        for (int p = 0; p + 3 < _pixels.Length; p += 148)
            signature = unchecked((signature ^ BitConverter.ToUInt32(_pixels, p)) * 1099511628211);
        if (_image is not null && _image.PixelWidth == _width && _image.PixelHeight == _height && signature == _signature)
        { Status = "unchanged"; return _image; }
        var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, _pixels, _stride);
        image.Freeze();
        _image = image; _signature = signature;
        Status = "captured";
        return image;
    }

    private bool Receive(nint window, nint source, ImageHeader header, nint destination, ImageHeader output, NativeRect raw, NativeRect clipped, nint region)
    {
        try
        {
            if (!_reading) return true;
            long bytes = (long)header.Stride * header.Height;
            if (header.Format != new Guid("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9") &&
                header.Format != new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f")) return true;
            if (source == 0 || header.Width == 0 || header.Height == 0 || header.Stride < header.Width * 4 ||
                bytes <= 0 || bytes > PixelBudget || header.Size < (nuint)bytes + header.Offset) return true;
            var pixels = _pixels is { } existing && existing.Length == bytes ? existing : new byte[(int)bytes];
            Marshal.Copy(source + checked((int)header.Offset), pixels, 0, pixels.Length);
            for (int y = 0; y < header.Height; y++)
                for (int x = 0; x < header.Width; x++)
                {
                    int p = checked(y * (int)header.Stride + x * 4);
                    pixels[p + 3] = 255;
                }
            _pixels = pixels; _width = (int)header.Width; _height = (int)header.Height; _stride = (int)header.Stride; Format = header.Format; _hasFrame = true;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { Status = "invalid backdrop frame"; }
        return true;
    }

    // Keep one native magnifier for this panel's lifetime. Repeated runtime
    // initialize/uninitialize retains native allocations on current Windows.
    // Suspending stops paints and releases our pixel/bitmap ownership instead.
    internal void Suspend()
    {
        _reading = false; _hasFrame = false;
        if (_host != 0 && !_suspended) ShowWindow(_host, 0);
        _suspended = true; _pixels = null; _image = null;
        _width = _height = _stride = 0; _signature = 0;
        Status = "suspended";
    }

    public void Dispose()
    {
        Suspend();
        if (_magnifier != 0)
        {
            MagSetImageScalingCallback(_magnifier, null);
            DestroyWindow(_magnifier);
        }
        if (_host != 0) DestroyWindow(_host);
        _host = 0; _magnifier = 0; _filter = [];
        if (_initialized) MagUninitialize();
        _initialized = false;
        GC.KeepAlive(_callback);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ImageHeader { public uint Width, Height; public Guid Format; public uint Stride, Offset; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct Transform { public float M11, M12, M13, M21, M22, M23, M31, M32, M33; }
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool ScalingCallback(nint window, nint source, ImageHeader header,
        nint destination, ImageHeader output, NativeRect raw, NativeRect clipped, nint region);
    [DllImport("Magnification.dll")] private static extern bool MagInitialize();
    [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
    [DllImport("Magnification.dll")] private static extern bool MagSetImageScalingCallback(nint window, ScalingCallback? callback);
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowSource(nint window, NativeRect rect);
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowFilterList(nint window, uint mode, int count, nint[] windows);
    [DllImport("Magnification.dll")] private static extern bool MagSetWindowTransform(nint window, ref Transform transform);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string cls, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}

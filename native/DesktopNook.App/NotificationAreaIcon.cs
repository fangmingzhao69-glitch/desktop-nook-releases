using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopNook.App;

internal sealed class NotificationAreaIcon : IDisposable
{
    private const uint Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4;
    private const uint NotifyMessage = 1, NotifyIcon = 2, NotifyTip = 4, NotifyGuid = 0x20, ShowTip = 0x80;
    private const int Callback = 0x804a, ContextMenu = 0x007b, LeftButtonUp = 0x0202,
        RightButtonUp = 0x0205, Select = 0x0400, KeySelect = 0x0401;
    private const int IconId = 1;
    private const uint ReturnMenuCommand = 0x0100, NoMenuNotification = 0x0080, RightMenuButton = 0x0002;
    private const uint MenuSeparator = 0x0800;
    private const int Toggle = 1, Settings = 2, Exit = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref IconData data);
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref Identifier id, out NativeRect bounds);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string path, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr window, int msg, IntPtr wParam, IntPtr lParam);

    private readonly HwndSource _source;
    private readonly Func<bool> _panelShown, _canAct;
    private readonly Action _toggle, _settings, _exit;
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(1) };
    private IconData _data;
    private int _attempts;
    private bool _registered, _modern, _disposed, _menuOpen;

    internal NotificationAreaIcon(HwndSource source, string? sandbox, Func<bool> panelShown, Func<bool> canAct,
        Action toggle, Action settings, Action exit)
    {
        _source = source;
        _panelShown = panelShown;
        _canAct = canAct;
        _toggle = toggle;
        _settings = settings;
        _exit = exit;

        string path = Environment.ProcessPath!;
        ExtractIconExW(path, 0, IntPtr.Zero, out var icon, 1);
        var identity = "DesktopNook.NotificationArea|" + path.ToUpperInvariant() + "|" + sandbox?.ToUpperInvariant();
        _data = new IconData
        {
            Size = (uint)Marshal.SizeOf<IconData>(), Window = source.Handle, Id = IconId,
            Flags = NotifyMessage | NotifyIcon | NotifyTip | NotifyGuid | ShowTip, CallbackMessage = Callback,
            Icon = icon, Tip = sandbox is null ? "折页" : "折页 · 隔离验证", Info = "", InfoTitle = "",
            Guid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16))
        };

        if (icon == IntPtr.Zero) return;
        source.AddHook(WndProc);
        source.Disposed += OnSourceDisposed;
        _retry.Tick += (_, _) => RegisterIcon();
        RegisterIcon();
    }

    private void RegisterIcon()
    {
        if (_disposed) return;
        _registered = Shell_NotifyIconW(Add, ref _data) || Shell_NotifyIconW(Modify, ref _data);
        if (_registered)
        {
            _data.Version = 4;
            _modern = Shell_NotifyIconW(SetVersion, ref _data);
            _retry.Stop();
        }
        else if (++_attempts < 8) _retry.Start();
        else _retry.Stop();
    }

    private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            _attempts = 0;
            _source.Dispatcher.BeginInvoke(RegisterIcon);
        }
        if (message != Callback || !_registered) return IntPtr.Zero;

        int packed = unchecked((int)lParam.ToInt64());
        int id = _modern ? (packed >> 16) & 0xffff : unchecked((int)wParam.ToInt64());
        if (id != IconId) return IntPtr.Zero;
        handled = true;
        int action = packed & 0xffff;
        if (action is Select or KeySelect || (!_modern && action == LeftButtonUp))
            _source.Dispatcher.BeginInvoke(() => Invoke(Toggle));
        else if (action == ContextMenu || (!_modern && action == RightButtonUp))
            _source.Dispatcher.BeginInvoke(ShowMenu);
        return IntPtr.Zero;
    }

    private Identifier Identity() => new()
    {
        Size = (uint)Marshal.SizeOf<Identifier>(), Window = _data.Window, Id = IconId, Guid = _data.Guid
    };

    internal bool PointerOverIcon()
    {
        if (!_registered || !GetCursorPos(out var point)) return false;
        var id = Identity();
        return Shell_NotifyIconGetRect(ref id, out var bounds) == 0 &&
            point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }

    private void ShowMenu()
    {
        if (_disposed || !_canAct() || _menuOpen) return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        int choice = 0;
        _menuOpen = true;
        try
        {
            AppendMenu(menu, 0, (UIntPtr)Toggle, _panelShown() ? "收起折页" : "打开折页");
            AppendMenu(menu, 0, (UIntPtr)Settings, "设置");
            AppendMenu(menu, MenuSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, 0, (UIntPtr)Exit, "退出并结束进程");
            var id = Identity();
            if (!GetCursorPos(out var point)) return;
            if (Shell_NotifyIconGetRect(ref id, out var bounds) == 0 &&
                (point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom))
                point = new NativePoint { X = bounds.Left + (bounds.Right - bounds.Left) / 2, Y = bounds.Top };
            SetForegroundWindow(_data.Window);
            choice = TrackPopupMenuEx(menu, ReturnMenuCommand | NoMenuNotification | RightMenuButton,
                point.X, point.Y, _data.Window, IntPtr.Zero);
            PostMessage(_data.Window, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
        }
        if (choice != 0) Invoke(choice);
        else if (!_disposed) Shell_NotifyIconW(SetFocus, ref _data);
    }

    private void Invoke(int choice)
    {
        if (_disposed || !_canAct() || _menuOpen) return;
        switch (choice)
        {
            case Toggle: _toggle(); break;
            case Settings: _settings(); break;
            case Exit: _exit(); break;
        }
    }

    private void OnSourceDisposed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry.Stop();
        if (_registered) Shell_NotifyIconW(Delete, ref _data);
        _registered = false;
        if (_data.Icon != IntPtr.Zero)
        {
            _source.Disposed -= OnSourceDisposed;
            if (!_source.IsDisposed) _source.RemoveHook(WndProc);
            DestroyIcon(_data.Icon);
        }
    }

#if NOOK_UI_VERIFICATION
    internal bool Registered => _registered;
    internal bool HasShellBounds
    {
        get
        {
            var id = Identity();
            return Shell_NotifyIconGetRect(ref id, out var bounds) == 0 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        }
    }

    internal bool PostActivation() => _modern
        ? PostMessage(_data.Window, Callback, IntPtr.Zero, (IntPtr)((IconId << 16) | Select))
        : PostMessage(_data.Window, Callback, (IntPtr)IconId, (IntPtr)LeftButtonUp);

    internal bool RestoreRegistrationForVerification() =>
        Shell_NotifyIconW(Delete, ref _data) && PostMessage(_data.Window, _taskbarCreated, IntPtr.Zero, IntPtr.Zero);
#endif
}

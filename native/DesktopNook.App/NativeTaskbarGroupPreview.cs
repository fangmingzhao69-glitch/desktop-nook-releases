using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNook.App;

// Some Explorer builds do not open a grouped-window flyout after their native
// composition child is repositioned. Recover only that failed group click.
internal sealed class NativeTaskbarGroupPreview : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<nint, (Rect[] Buttons, string[] Ids)> _bars = new();
    private readonly MouseCallback _callback;
    private nint _hook;
    private int _revision;
    private Window? _popup;
    private string? _shownId;
    internal Window? Popup => _popup;
    internal string Diagnostic { get; private set; } = "idle";

    internal NativeTaskbarGroupPreview() => _callback = OnMouse;

    internal void Update(nint taskbar, NativeTaskbarContents.Measurement contents)
    {
        _bars[taskbar] = (contents.AppButtons, contents.AppIds);
        if (_hook == 0) { _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0); Diagnostic = "hook:" + _hook; }
    }

    internal void Remove(nint taskbar)
    {
        _bars.Remove(taskbar);
        if (_bars.Count == 0) Dispose();
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message == 0x202) // WM_LBUTTONUP; never swallow native input.
        {
            Diagnostic = "mouse-up";
            var input = Marshal.PtrToStructure<MouseInput>(data);
            var point = new Point(input.Point.X, input.Point.Y);
            foreach (var (taskbar, bar) in _bars)
            {
                int index = Array.FindIndex(bar.Buttons, rectangle => rectangle.Contains(point));
                Diagnostic = "index:" + index;
                if (index < 0 || index >= bar.Ids.Length || string.IsNullOrEmpty(bar.Ids[index])) continue;
                if (GetAncestor(WindowFromPoint(input.Point), 2) != taskbar) { Diagnostic = "different-window"; break; }
                var id = bar.Ids[index];
                var button = bar.Buttons[index];
                int revision = ++_revision;
                _dispatcher.BeginInvoke(() => _ = RecoverAsync(taskbar, button, id, revision));
                break;
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private async Task RecoverAsync(nint taskbar, Rect button, string id, int revision)
    {
        try
        {
            if (_shownId == id && _popup is not null) { ClosePopup(); return; }
            ClosePopup();
            var windows = await Task.Run(() => WindowsFor(id)).WaitAsync(TimeSpan.FromSeconds(2));
            Diagnostic = "windows:" + windows.Count;
            if (windows.Count < 2) return;
            await Task.Delay(300);
            if (revision != _revision || !_bars.ContainsKey(taskbar) || !GetCursorPos(out var pointer) ||
                !button.Contains(new Point(pointer.X, pointer.Y))) { Diagnostic = "stale"; return; }
            // An available native flyout keeps all its normal Windows behavior.
            if (NativePreviewVisible(taskbar, button)) { Diagnostic = "native-visible"; return; }
            Show(taskbar, button, id, windows);
        }
        catch (Exception error) when (error is TimeoutException or Win32Exception or COMException or InvalidOperationException) { Diagnostic = error.ToString(); }
    }

    internal static List<(nint Handle, string Title)> WindowsFor(string id)
    {
        var windows = new List<(nint Handle, string Title)>();
        var processes = new Dictionary<uint, bool>();
        EnumWindows((window, _) =>
        {
            try
            {
                if (!IsWindowVisible(window) || GetAncestor(window, 2) != window ||
                    (GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0 ||
                    (GetWindow(window, 4) != 0 && (GetWindowLongPtr(window, -20).ToInt64() & 0x40000) == 0)) return true;
                if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(512);
                if (GetWindowText(window, title, title.Capacity) == 0) return true;
                string? windowId = ShortcutInstaller.WindowApplicationId(window);
                bool matches = string.Equals(id, windowId, StringComparison.OrdinalIgnoreCase);
                // An explicit window identity is authoritative (profiles and PWAs can share a process).
                if (!matches && string.IsNullOrEmpty(windowId))
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    if (!processes.TryGetValue(pid, out matches))
                    {
                        try
                        {
                            using var process = Process.GetProcessById((int)pid);
                            if (Path.IsPathFullyQualified(id))
                                matches = string.Equals(process.MainModule?.FileName, id, StringComparison.OrdinalIgnoreCase);
                            else
                            {
                                var identity = new StringBuilder(256); uint length = (uint)identity.Capacity;
                                matches = GetApplicationUserModelId(process.Handle, ref length, identity) == 0 &&
                                    string.Equals(identity.ToString(), id, StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { matches = false; }
                        processes[pid] = matches;
                    }
                    if (!matches && id == "Microsoft.Windows.Explorer")
                    {
                        var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
                        matches = name.ToString() is "CabinetWClass" or "ExploreWClass";
                    }
                }
                if (matches) windows.Add((window, title.ToString()));
                return windows.Count < 128;
            }
            catch (Exception error) when (error is COMException or Win32Exception or ArgumentException or InvalidOperationException)
            { return true; }
        }, 0);
        return windows;
    }

    private static bool NativePreviewVisible(nint taskbar, Rect button)
    {
        GetWindowThreadProcessId(taskbar, out uint explorer);
        bool visible = false;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var bounds) ||
                bounds.Right - bounds.Left < 80 || bounds.Bottom - bounds.Top < 50) return true;
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != explorer) return true;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() is not ("TaskListThumbnailWnd" or "XamlExplorerHostIslandWindow")) return true;
            if (button.Left > bounds.Right || button.Right < bounds.Left || bounds.Top >= button.Bottom) return true;
            visible = true; return false;
        }, 0);
        return visible;
    }

    private void Show(nint taskbar, Rect button, string id, List<(nint Handle, string Title)> windows)
    {
        double scale = Math.Max(1, GetDpiForWindow(taskbar) / 96d);
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref monitor)) return;
        var work = monitor.Work.ToRect();
        double cardWidth = 196;
        var cards = new WrapPanel();
        var popup = new Window
        {
            Title = "选择窗口", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.Height,
            Width = Math.Min(work.Width / scale, Math.Min(windows.Count, 4) * cardWidth + 24),
            MaxHeight = Math.Max(120, work.Height / scale - 24),
            Background = ThemePalette.Brush("CanvasBrush"), Foreground = ThemePalette.Brush("TextPrimaryBrush"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFontFamily"), UseLayoutRounding = true
        };
        AutomationProperties.SetAutomationId(popup, "TaskbarGroupPreview");
        var thumbnails = new List<nint>();
        var live = new List<(nint Thumbnail, Border Slot, NativePoint Size)>();
        var slots = new List<(nint Source, Border Slot)>();
        foreach (var (handle, title) in windows)
        {
            var slot = new Border { Height = 108, Background = ThemePalette.Brush("CanvasSubtleBrush"),
                Child = new TextBlock { Text = "窗口预览", HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, Foreground = ThemePalette.Brush("TextSecondaryBrush") } };
            var content = new StackPanel();
            content.Children.Add(slot);
            content.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 8, 2, 2), FontSize = 12 });
            var choice = new Button { Width = cardWidth - 8, Margin = new Thickness(4), Padding = new Thickness(8),
                Content = content, ToolTip = title, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, title);
            AutomationProperties.SetAutomationId(choice, "TaskbarWindow-" + handle);
            choice.Click += (_, _) => { ClosePopup(); if (IsWindow(handle)) { if (IsIconic(handle)) ShowWindowAsync(handle, 9); SetForegroundWindow(handle); } };
            cards.Children.Add(choice); slots.Add((handle, slot));
        }
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        popup.Content = new Border { BorderBrush = ThemePalette.Brush("OutlineBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(8), Child = scroll };
        popup.Deactivated += (_, _) => ClosePopup();
        popup.PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; ClosePopup(); } };
        popup.Closed += (_, _) =>
        {
            foreach (var thumbnail in thumbnails) DwmUnregisterThumbnail(thumbnail);
            if (_popup == popup) { _popup = null; _shownId = null; }
        };
        _popup = popup; _shownId = id;
        popup.Left = Math.Clamp((button.Left + button.Width / 2) / scale - popup.Width / 2, work.Left / scale,
            Math.Max(work.Left / scale, work.Right / scale - popup.Width));
        popup.Top = Math.Max(work.Top / scale, button.Top / scale - 190);
        popup.Show(); popup.UpdateLayout();
        var position = NativeTaskbarHover.Place(button, new Rect(button.Left, button.Top, button.Width, button.Height), work,
            new Size(popup.ActualWidth * scale, popup.ActualHeight * scale), scale);
        popup.Left = position.X / scale; popup.Top = position.Y / scale;
        var destination = new WindowInteropHelper(popup).Handle;
        foreach (var (source, slot) in slots)
        {
            if (DwmRegisterThumbnail(destination, source, out nint thumbnail) != 0) continue;
            thumbnails.Add(thumbnail);
            if (DwmQueryThumbnailSourceSize(thumbnail, out var size) != 0 || size.X <= 0 || size.Y <= 0) continue;
            live.Add((thumbnail, slot, size));
        }
        RefreshThumbnails();
        scroll.ScrollChanged += (_, _) => RefreshThumbnails();
        popup.SizeChanged += (_, _) => RefreshThumbnails();
        ((Button)cards.Children[0]).Focus();

        void RefreshThumbnails()
        {
            if (_popup != popup) return;
            double pixels = VisualTreeHelper.GetDpi(popup).DpiScaleX;
            var viewport = new Rect(scroll.TranslatePoint(new Point(), popup), new Size(scroll.ActualWidth, scroll.ActualHeight));
            foreach (var (thumbnail, slot, size) in live)
            {
                double ratio = Math.Min(slot.ActualWidth / size.X, slot.ActualHeight / size.Y);
                if (ratio <= 0) continue;
                var origin = slot.TranslatePoint(new Point((slot.ActualWidth - size.X * ratio) / 2,
                    (slot.ActualHeight - size.Y * ratio) / 2), popup);
                var target = new Rect(origin, new Size(size.X * ratio, size.Y * ratio));
                var clipped = Rect.Intersect(target, viewport);
                var properties = new ThumbnailProperties { Flags = 1 | 2 | 4 | 8 | 16, Opacity = 255, Visible = !clipped.IsEmpty };
                if (!clipped.IsEmpty)
                {
                    properties.Destination = new NativeRect { Left = (int)(clipped.Left * pixels), Top = (int)(clipped.Top * pixels),
                        Right = (int)(clipped.Right * pixels), Bottom = (int)(clipped.Bottom * pixels) };
                    properties.Source = new NativeRect { Left = (int)((clipped.Left - target.Left) / ratio), Top = (int)((clipped.Top - target.Top) / ratio),
                        Right = Math.Min(size.X, (int)Math.Ceiling((clipped.Right - target.Left) / ratio)),
                        Bottom = Math.Min(size.Y, (int)Math.Ceiling((clipped.Bottom - target.Top) / ratio)) };
                }
                if (DwmUpdateThumbnailProperties(thumbnail, ref properties) == 0) slot.Child = null;
            }
        }
    }

    private void ClosePopup() { var popup = _popup; _popup = null; _shownId = null; popup?.Close(); }
    public void Dispose()
    {
        _revision++;
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _hook = 0; _bars.Clear(); ClosePopup();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public NativePoint Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    {
        public uint Flags; public NativeRect Destination, Source; public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientOnly;
    }
    private delegate nint MouseCallback(int code, nint message, nint data);
    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SetWindowsHookEx(int id, MouseCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder title, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativePoint size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
}

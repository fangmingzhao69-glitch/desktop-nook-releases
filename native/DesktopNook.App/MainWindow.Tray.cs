using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopNook.App;

public partial class MainWindow
{
    private NotificationAreaIcon? _tray;
    internal string? ShellIconLocation { get; private set; }
#if NOOK_UI_VERIFICATION
    internal NotificationAreaIcon TrayForVerification => _tray!;
#endif

    internal void InitializeTray(string? sandbox)
    {
        SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source is not null)
                _tray = new NotificationAreaIcon(source, sandbox,
                    () => IsVisible && WindowState == WindowState.Normal && !_hidePending,
                    () => !_dragging && _interactions == 0 && !_storageMoving && !_allowExit,
                    TogglePanel, () => { ShowPanel(); OpenSettings(); }, Quit);
        };
        Closed += (_, _) => _tray?.Dispose();
    }

    internal void ApplyShellTheme(bool initialize = false, bool refreshPins = false)
    {
        if (App.IsSandbox && _tray is null) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var folder = App.IsSandbox ? Path.Combine(App.SandboxRoot!, "ShellIconCache") : null;
            var path = ThemeShellIcon.FileFor(ThemePalette.Family, folder);
            var location = path + ",0";
            if (refreshPins && !App.IsSandbox) BrandMigration.RefreshPinnedAppearance(location);
            if (!App.IsSandbox)
                ShortcutInstaller.SetWindowIdentity(handle, Environment.ProcessPath!, location, attachCleanup: initialize,
                    notifyShellIcon: initialize || refreshPins);
            if (ShellIconLocation != location) _tray?.SetThemeIcon(path);
            ShellIconLocation = location;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            SetStatus("任务栏图标未完成刷新，可在设置中刷新程序入口。");
        }
    }
}

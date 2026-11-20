using System.Windows;
using System.Windows.Interop;

namespace DesktopNook.App;

public partial class MainWindow
{
    private NotificationAreaIcon? _tray;
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
}

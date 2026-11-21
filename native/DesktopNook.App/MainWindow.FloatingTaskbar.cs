using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopNook.App;

public partial class MainWindow
{
    private FloatingTaskbarSession? _floatingTaskbar;
    internal FloatingTaskbarSession FloatingTaskbar => _floatingTaskbar ??= new(this);
    private static readonly uint DockSettingsMessage = RegisterWindowMessage("DesktopNook.Dock.Settings");
    private static readonly uint DockPanelMessage = RegisterWindowMessage("DesktopNook.Dock.Panel");
    private static readonly uint DockSystemMessage = RegisterWindowMessage("DesktopNook.Dock.System");
    private void InitializeFloatingTaskbar()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source is null) return;
        source.AddHook(DockMessage);
        Closed += (_, _) => { source.RemoveHook(DockMessage); _floatingTaskbar?.Dispose(); };
    }
    private async Task StartFloatingTaskbarAsync()
    {
        try
        {
            if (FloatingTaskbar.Saved.Mode == "floating") await FloatingTaskbar.ApplyAsync(FloatingTaskbar.Saved, save: false);
        }
        catch (Exception ex) { SetStatus("悬浮任务栏未启动，系统任务栏保持可用：" + ex.Message); }
    }
    private nint DockMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == DockSystemMessage)
        {
            handled = true;
            Dispatcher.BeginInvoke(async () =>
            {
                try { await FloatingTaskbar.ApplyAsync(FloatingTaskbar.Saved with { Mode = "system" }, save: true); }
                catch (Exception ex) { SetStatus("任务栏切换：" + ex.Message); }
            });
            return 0;
        }
        if ((uint)message == DockPanelMessage)
        {
            handled = true; Dispatcher.BeginInvoke(TogglePanel); return 0;
        }
        if ((uint)message != DockSettingsMessage) return 0;
        handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            var existing = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault(window => window.IsVisible);
            if (existing is not null) { existing.SelectPage(SettingsWindow.TaskbarPage); existing.Activate(); }
            else { ShowPanel(); OpenSettings(SettingsWindow.TaskbarPage); }
        });
        return 0;
    }
    internal bool PauseSystemTaskbarAppearance() => StopTaskbarAppearance();
    internal Task ResumeSystemTaskbarAppearanceAsync() => RestoreSavedTaskbarAppearanceAsync();
    internal bool FloatingTaskbarSelected => _floatingTaskbar?.Current.Mode == "floating";
    internal bool StopFloatingTaskbar()
    {
        if (_floatingTaskbar?.Stop() != false) return true;
        SetStatus("正在切换任务栏，请稍后重试。");
        return false;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}

using System.Windows;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow
{
    private TaskbarAppearanceSession? _taskbarAppearance;
    private bool _startingTaskbarAppearance;

    private void InitializeTaskbarAppearance()
    {
        if (App.IsSandbox) return;
        SessionEndingCancelEventHandler ending = (_, args) =>
        {
            if (!args.Cancel) _taskbarAppearance?.Stop();
        };
        Application.Current.SessionEnding += ending;
        Closed += (_, _) => Application.Current.SessionEnding -= ending;
        _ = RestoreSavedTaskbarAppearanceAsync();
    }

    private TaskbarAppearanceSession TaskbarAppearance()
    {
        if (_taskbarAppearance is not null) return _taskbarAppearance;
        var store = new TaskbarAppearanceStore(Store.DataDirectory);
        var package = new TaskbarAppearancePackage(store.DirectoryPath, useCompatibilityPackage: Environment.OSVersion.Version.Build == 26340);
        var backend = new TranslucentTaskbarBackend(package, App.IsSandbox);
        return _taskbarAppearance = new TaskbarAppearanceSession(store, backend);
    }

    private async Task RestoreSavedTaskbarAppearanceAsync()
    {
        if (App.IsSandbox || _startingTaskbarAppearance) return;
        _startingTaskbarAppearance = true;
        try
        {
            var session = TaskbarAppearance();
            if (session.Saved.UsesComponent) await session.StartSavedAsync();
        }
        catch (Exception ex) { SetStatus("任务栏外观：" + ex.Message); }
        finally { _startingTaskbarAppearance = false; }
    }

    private void OpenTaskbarAppearance()
    {
        if (_startingTaskbarAppearance)
        {
            SetStatus("正在启动任务栏外观组件，请稍后打开设置。");
            return;
        }
        _interactions++;
        try { new TaskbarAppearanceWindow(this, TaskbarAppearance()).ShowDialog(); }
        catch (Exception ex) { ShowFailure(ex); }
        finally { _interactions--; }
    }

    private bool StopTaskbarAppearance()
    {
        if (_startingTaskbarAppearance)
        {
            SetStatus("正在启动任务栏外观组件，请稍后重试。");
            return false;
        }
        if (_taskbarAppearance?.Stop() != false) return true;
        SetStatus("任务栏外观组件尚未退出，请稍后重试。");
        return false;
    }
}

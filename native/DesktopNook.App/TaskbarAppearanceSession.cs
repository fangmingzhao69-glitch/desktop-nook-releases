using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DesktopNook.Core;

namespace DesktopNook.App;

internal interface ITaskbarAppearanceBackend
{
    string? Restriction { get; }
    bool NeedsDownload { get; }
    bool Running { get; }
    bool UsesCompatibilityPackage { get; }
    Task ImportCompatibilityPackageAsync(string source);
    Task ApplyAsync(TaskbarAppearanceSettings settings, bool allowDownload);
    bool Stop();
}

internal sealed class TaskbarAppearanceSession(TaskbarAppearanceStore store, ITaskbarAppearanceBackend backend)
{
    internal TaskbarAppearanceSettings Saved { get; private set; } = store.Load();
    internal bool HasPreview { get; private set; }
    internal string? Restriction => backend.Restriction;
    internal bool NeedsDownload => backend.NeedsDownload;
    internal bool Running => backend.Running;
    internal bool UsesCompatibilityPackage => backend.UsesCompatibilityPackage;
    internal Task ImportCompatibilityPackageAsync(string source) => backend.ImportCompatibilityPackageAsync(source);

    internal Task StartSavedAsync() => backend.ApplyAsync(Saved, allowDownload: false);

    internal async Task PreviewAsync(TaskbarAppearanceSettings settings, bool allowDownload)
    {
        settings = settings.Validate();
        HasPreview = true;
        await backend.ApplyAsync(settings, allowDownload);
    }

    internal async Task ApplyAsync(TaskbarAppearanceSettings settings, bool allowDownload)
    {
        settings = settings.Validate();
        HasPreview = true;
        await backend.ApplyAsync(settings, allowDownload);
        store.Save(settings);
        Saved = settings;
        HasPreview = false;
    }

    internal async Task CancelPreviewAsync()
    {
        if (!HasPreview) return;
        try
        {
            await StartSavedAsync();
            HasPreview = false;
        }
        catch (Exception ex)
        {
            if (!backend.Stop()) throw;
            HasPreview = false;
            throw new IOException("无法重新应用已保存的外观，已关闭轻匣的外观组件。", ex);
        }
    }

    internal bool Stop() => backend.Stop();
}

internal sealed class TranslucentTaskbarBackend(TaskbarAppearancePackage package, bool sandbox) : ITaskbarAppearanceBackend
{
    private Process? _owned;
    private string? _config;

    public bool Running => _owned is { HasExited: false };
    public bool NeedsDownload => !package.UsesCompatibilityPackage && !package.HasArchive;
    public bool UsesCompatibilityPackage => package.UsesCompatibilityPackage;

    public Task ImportCompatibilityPackageAsync(string source) =>
        Task.Run(() => package.ImportCompatibilityPackage(source));

    public string? Restriction
    {
        get
        {
            if (sandbox) return "隔离模式只验证设置界面。任务栏外观在独立桌面环境中验收。";
            var version = Environment.OSVersion.Version;
            if (RuntimeInformation.OSArchitecture != Architecture.X64 || version.Build is < 22621 or > 26340)
                return $"当前 Windows 构建 {version.Build} 没有可用的已选版本组件。可继续使用 Windows 默认外观或独立版。";
            if (UsesCompatibilityPackage && !package.HasArchive)
                return "当前 Windows 构建需先导入预览版随附的 26340 兼容组件。";
            if (IndependentBackendExists())
                return "已有 TranslucentTB 正在管理任务栏。可先导入设置，再从系统托盘退出独立版。";
            return null;
        }
    }

    public async Task ApplyAsync(TaskbarAppearanceSettings settings, bool allowDownload)
    {
        if (!settings.UsesComponent)
        {
            if (!Stop()) throw new IOException("外观组件尚未退出，请稍后重试。");
            return;
        }
        if (Restriction is { } reason) throw new IOException(reason);
        if (Running)
        {
            TaskbarAppearanceStore.WriteComponentSettings(_config!, settings);
            return;
        }
        _owned?.Dispose();
        _owned = null;
        string directory = await package.PrepareAsync(allowDownload);
        if (Restriction is { } changed) throw new IOException(changed);
        string path = Path.Combine(directory, "TranslucentTB.exe");
        _config = Path.Combine(directory, "settings.json");
        TaskbarAppearanceStore.WriteComponentSettings(_config, settings);
        _owned = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
            CreateNoWindow = true
        }) ?? throw new IOException("无法启动任务栏外观组件。");
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (_owned.HasExited) throw new IOException("外观组件启动后退出。请检查独立版冲突及其运行依赖。");
            if (FindOwnedWindow() != IntPtr.Zero) return;
            await Task.Delay(100);
        }
        throw new IOException("外观组件尚未就绪。任务栏外观需要在组件就绪后重新应用。");
    }

    public bool Stop()
    {
        if (_owned is null) return true;
        try
        {
            if (!_owned.HasExited)
            {
                var window = FindOwnedWindow();
                if (window == IntPtr.Zero || !PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero) || !_owned.WaitForExit(3000))
                    return false;
            }
            _owned.Dispose();
            _owned = null;
            _config = null;
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private IntPtr FindOwnedWindow()
    {
        if (_owned is null || _owned.HasExited) return IntPtr.Zero;
        var window = FindWindow("TrayWindow", "TranslucentTB");
        if (window == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(window, out var pid);
        return pid == _owned.Id && !_owned.HasExited ? window : IntPtr.Zero;
    }

    private bool IndependentBackendExists()
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("TranslucentTB");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == current.SessionId && (!Running || process.Id != _owned!.Id))
                        return true;
                }
                catch (InvalidOperationException) { } // Process ended while enumerating.
                catch (Win32Exception) { return true; }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

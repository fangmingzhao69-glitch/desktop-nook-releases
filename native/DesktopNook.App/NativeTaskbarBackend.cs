using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class NativeTaskbarBackend(string storePath, bool sandbox, bool dryRun = false) : ITaskbarAppearanceBackend
{
    private readonly string _configuration = Path.Combine(storePath, "Native", "RoamingState", "settings.json");
    private readonly TaskbarAppearancePackage _package = new(storePath);
    private NativeTaskbarModule? _module;
    private Worker? _worker;
    private bool _starting;

    public bool Running => _worker is { Running: true };
    public bool NeedsDownload => !_package.HasArchive;

    public string? Restriction
    {
        get
        {
            if (sandbox && !dryRun) return "隔离模式只验证设置界面。";
            if (!dryRun && Environment.OSVersion.Version.Build is < 22621 or > 26340)
                return "此 Windows 版本暂不受任务栏外观组件支持。";
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return "此任务栏外观组件需要 x64 进程。";
            if (!dryRun && IndependentBackendExists())
                return "已有 TranslucentTB 正在管理任务栏。请从系统托盘退出独立版，再启用折页的任务栏外观。";
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

        if (_starting) throw new IOException("任务栏外观组件正在启动。");
        if (Restriction is { } reason) throw new IOException(reason);

        Directory.CreateDirectory(Path.GetDirectoryName(_configuration)!);
        TaskbarAppearanceStore.WriteComponentSettings(_configuration, settings);
        if (Running) return;
        if (!Stop()) throw new IOException("无法清理已退出的外观组件。");

        _starting = true;
        try
        {
            if (_module is null)
            {
                string directory = await _package.PrepareAsync(allowDownload);
                _module = await Task.Run(() => NativeTaskbarModule.Load(directory));
            }
            if (Restriction is { } changed) throw new IOException(changed);
            var worker = new Worker(_module, Path.Combine(storePath, "Native"), dryRun);
            _worker = worker;
            await worker.WaitForStartAsync();
        }
        catch
        {
            StopWorker();
            throw;
        }
        finally { _starting = false; }
    }

    public bool Stop() => !_starting && StopWorker();

    private bool StopWorker()
    {
        if (_worker is null) return true;
        if (!_worker.TryStop(out bool stopped)) return false;
        _worker = null;
        return stopped;
    }

    private static bool IndependentBackendExists()
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("TranslucentTB");
        try
        {
            foreach (var process in processes)
            {
                try { if (process.SessionId == current.SessionId) return true; }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { return true; }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private sealed class Worker
    {
        private readonly ManualResetEvent _stop = new(false);
        private readonly ManualResetEvent _ready = new(false);
        private readonly ManualResetEvent _finished = new(false);
        private readonly Thread _thread;
        private int _result;
        private Exception? _exception;

        internal Worker(NativeTaskbarModule module, string storage, bool dry)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    string mutexName = dry ? "Local\\DesktopNook-TaskbarAppearanceTest" : "Local\\DesktopNook-TaskbarAppearance";
                    using var ownership = new Mutex(true, mutexName, out bool first);
                    if (!first) throw new IOException("当前会话已有折页管理任务栏外观。");
                    try
                    {
                        _result = module.Run(storage, _stop.SafeWaitHandle.DangerousGetHandle(),
                            _ready.SafeWaitHandle.DangerousGetHandle(), dry);
                    }
                    finally { ownership.ReleaseMutex(); }
                }
                catch (Exception error) { _exception = error; }
                finally { _finished.Set(); }
            }) { IsBackground = true, Name = "Taskbar appearance" };
            try
            {
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            catch
            {
                DisposeSignals();
                throw;
            }
        }

        internal bool Running => _thread.IsAlive;

        internal async Task WaitForStartAsync()
        {
            int result = await Task.Run(() => WaitHandle.WaitAny([_finished, _ready], TimeSpan.FromSeconds(8)));
            if (result == 1 && !_finished.WaitOne(0)) return;
            if (result == WaitHandle.WaitTimeout) throw new TimeoutException("任务栏外观组件尚未就绪。");
            if (_exception is not null) throw new IOException(_exception.Message, _exception);
            Marshal.ThrowExceptionForHR(_result);
            throw new IOException("任务栏线程提前退出。");
        }

        internal bool TryStop(out bool successful)
        {
            successful = false;
            _stop.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(3))) return false;
            DisposeSignals();
            successful = _exception is null && _result >= 0;
            return true;
        }

        private void DisposeSignals()
        {
            _stop.Dispose();
            _ready.Dispose();
            _finished.Dispose();
        }
    }
}

internal sealed class NativeTaskbarModule
{
    private const string Filename = "QingxiaTaskbarCore.dll";
    private static readonly Dictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase)
    {
        [Filename] = "91F2D6CBECE6F120415F004D974300708859FA2E9D88D865E4CD630B8A26336B",
        ["ProgramLog.dll"] = "0EC63B02AA66BA4D3CC66CD724223E03A7160BAD7118ADBEC1403FEDF99BFF80",
        ["ExplorerTAP.dll"] = "6D8D2E025EC30911DA7DE9EA04F171F03B658196FAAE737E15D0DCC0C6739844",
        ["ExplorerHooks.dll"] = "896727368CE616E6F4962083CD11295F54DCBA35EC72B3FC6FB2DFCF120A1BBC"
    };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint GetAbiVersion();

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    internal delegate int RunTaskbar(string settingsRoot, IntPtr stop, IntPtr ready, [MarshalAs(UnmanagedType.Bool)] bool dryRun);

    internal RunTaskbar Run { get; }

    private NativeTaskbarModule(RunTaskbar run) => Run = run;

    internal static NativeTaskbarModule Load(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(directory).Any(path =>
                !TaskbarAppearancePackage.Files.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
            throw new IOException("任务栏外观组件目录包含未校验的内容。");

        var verifiedFiles = new List<FileStream>();
        try
        {
            foreach (var (name, hash) in Hashes)
            {
                string path = Path.Combine(directory, name);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"任务栏外观组件文件不能使用链接：{name}");
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                verifiedFiles.Add(file);
                if (Convert.ToHexString(SHA256.HashData(file)) != hash)
                    throw new IOException($"任务栏外观组件校验失败：{name}");
            }

            const uint searchDllDirectoryAndSystem32 = 0x00000100 | 0x00000800;
            IntPtr module = LoadLibraryExW(Path.Combine(directory, Filename), IntPtr.Zero, searchDllDirectoryAndSystem32);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var version = Marshal.GetDelegateForFunctionPointer<GetAbiVersion>(NativeLibrary.GetExport(module, "QingxiaTaskbarAbiVersion"));
                if (version() != 1) throw new IOException("任务栏外观组件接口版本不匹配。");
                // Keep the module loaded until process exit while native callback cleanup is validated.
                return new NativeTaskbarModule(Marshal.GetDelegateForFunctionPointer<RunTaskbar>(
                    NativeLibrary.GetExport(module, "QingxiaRunTaskbar")));
            }
            catch { NativeLibrary.Free(module); throw; }
        }
        finally { foreach (var file in verifiedFiles) file.Dispose(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
}

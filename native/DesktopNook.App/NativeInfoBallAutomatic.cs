using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DesktopNook.App;

// A fixed, protected helper can be started by this user without elevating the UI.
// It accepts no caller-controlled command, script, executable or window operation.
internal static class NativeInfoBallAutomatic
{
    private const string Resource = "DesktopNook.InfoBallHelper";
    private static readonly Lazy<byte[]> Payload = new(() =>
    {
        using var source = typeof(NativeInfoBallAutomatic).Assembly.GetManifestResourceStream(Resource)
            ?? throw new IOException("缺少信息球定位组件。");
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    });
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private static string TaskName => "DesktopNook.InfoBall." + Sid;
    private static string BaseRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DesktopNook.InfoBallHelper");
    private static string Root => Path.Combine(BaseRoot, Sid);
    private static string Hash => Convert.ToHexString(SHA256.HashData(Payload.Value));
    private static string HelperDirectory => Path.Combine(Root, Hash);
    private static string HelperPath => Path.Combine(HelperDirectory, "DesktopNook.InfoBallHelper.exe");
    private static string TaskSecurity => $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{Sid})";
    private static long _nextAttempt;
    private static int _starting;
    private static int _configuring;

    internal static bool Enabled
    {
        get { try { return WithTask(task => IsExpectedTask(task)); } catch (Exception error) when (Recoverable(error)) { return false; } }
    }
    internal static bool HasAuthorization => TaskExists();

    internal static void TryStart()
    {
        if (App.IsSandbox || Volatile.Read(ref _configuring) != 0 || Environment.TickCount64 < Interlocked.Read(ref _nextAttempt) ||
            Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return;
        Interlocked.Exchange(ref _nextAttempt, Environment.TickCount64 + 5000);
        _ = Task.Run(() =>
        {
            try
            {
                if (Volatile.Read(ref _configuring) != 0) return;
                WithTask(task =>
                {
                    if (!IsExpectedTask(task)) { Interlocked.Exchange(ref _nextAttempt, Environment.TickCount64 + 60000); return false; }
                    object running = task.Run(null);
                    Marshal.FinalReleaseComObject(running);
                    return true;
                });
            }
            catch (Exception error) when (Recoverable(error))
            {
                if (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F))
                    Interlocked.Exchange(ref _nextAttempt, Environment.TickCount64 + 60000);
            }
            finally { Interlocked.Exchange(ref _starting, 0); }
        });
    }

    internal static async Task<string> ConfigureAsync(bool enable)
    {
        Interlocked.Increment(ref _configuring);
        try { return await ConfigureCoreAsync(enable).ConfigureAwait(false); }
        finally
        {
            Interlocked.Decrement(ref _configuring);
            if (enable) { Interlocked.Exchange(ref _nextAttempt, 0); TryStart(); }
        }
    }

    private static async Task<string> ConfigureCoreAsync(bool enable)
    {
        if (App.IsSandbox) throw new InvalidOperationException("隔离模式不登记管理员定位任务。");
        if (enable && Enabled) { Interlocked.Exchange(ref _nextAttempt, 0); TryStart(); return "已启用自动连接，折页重启后也会自动定位。"; }
        if (!enable && !TaskExists()) return "自动连接已关闭。";
        using var current = Process.GetCurrentProcess();
        var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--configure-info-ball-auto", current.Id.ToString(), current.StartTime.ToFileTimeUtc().ToString(), enable ? "enable" : "disable" })
            launch.ArgumentList.Add(argument);
        using var setup = Process.Start(launch) ?? throw new IOException("无法打开 Windows 授权窗口。");
        await setup.WaitForExitAsync().ConfigureAwait(false);
        if (setup.ExitCode != 0) throw new IOException($"自动定位授权未完成（{setup.ExitCode:x8}），请使用当前 Windows 账户授权后重试。");
        if (enable)
        {
            if (!Enabled) throw new IOException("自动定位组件校验未通过。");
            Interlocked.Exchange(ref _nextAttempt, 0);
            TryStart();
        }
        return enable ? "自动连接已启用，以后启动折页无需再次授权。" : "自动连接已关闭，定位任务已移除。";
    }

    internal static int ConfigureElevated(int parentId, long parentStart, bool enable)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 65;
            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToFileTimeUtc() != parentStart || parent.SessionId != Process.GetCurrentProcess().SessionId ||
                !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                !OpenProcessToken(parent.Handle, 8, out var token)) return 65;
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                if (owner.User?.Value != Sid) return 65;
            if (enable) Install(); else Remove();
            return 0;
        }
        catch (Exception error) when (Recoverable(error)) { return error.HResult; }
    }

    private static bool Recoverable(Exception error) => error is COMException or IOException or UnauthorizedAccessException or
        Win32Exception or InvalidOperationException or ArgumentException or JsonException or System.Security.SecurityException;

    private static T WithTask<T>(Func<dynamic, T> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null, task = null;
        try
        {
            service.Connect();
            folder = service.GetFolder("\\");
            task = ((dynamic)folder).GetTask(TaskName);
            return action((dynamic)task);
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    private static bool TaskExists()
    {
        try { return WithTask(_ => true); } catch (COMException) { return false; }
    }

    private static bool IsExpectedTask(dynamic task)
    {
        var definition = task.Definition;
        if (!task.Enabled || definition.Actions.Count != 1 || definition.Triggers.Count != 0 ||
            definition.Principal.RunLevel != 1 || definition.Principal.LogonType != 3) return false;
        string principal = definition.Principal.UserId;
        string principalSid = principal.StartsWith("S-1-", StringComparison.Ordinal) ? principal :
            ((SecurityIdentifier)new NTAccount(principal).Translate(typeof(SecurityIdentifier))).Value;
        var action = definition.Actions.Item(1);
        if (principalSid != Sid || action.Type != 0 || !string.Equals((string)action.Path, HelperPath, StringComparison.OrdinalIgnoreCase) ||
            (string)action.Arguments != "--run" || !File.Exists(HelperPath)) return false;
        using (var file = File.OpenRead(HelperPath))
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), SHA256.HashData(Payload.Value))) return false;
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(HelperDirectory, "owner.json")));
        return config.RootElement.GetProperty("Sid").GetString() == Sid &&
            string.Equals(config.RootElement.GetProperty("Program").GetString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private static dynamic Definition(dynamic service)
    {
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "折页：按需将腾讯电脑管家信息球放入胶囊；不启动主程序、不接受外部命令。";
        definition.Principal.UserId = Sid;
        definition.Principal.LogonType = 3; // Existing interactive token; no password is stored.
        definition.Principal.RunLevel = 1;
        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2;
        dynamic action = definition.Actions.Create(0);
        action.Path = HelperPath;
        action.Arguments = "--run";
        action.WorkingDirectory = HelperDirectory;
        return definition;
    }

    private static void Install()
    {
        // Program Files and every component directory must remain ordinary directories.
        string path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string component in new[] { "DesktopNook.InfoBallHelper", Sid, Hash })
        {
            path = Path.Combine(path, component);
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("定位组件目录不能是链接。");
            Directory.CreateDirectory(path);
        }
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (string directory in new[] { BaseRoot, Root, HelperDirectory })
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.SetOwner(administrators);
            foreach (var pair in new[] { (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
                (administrators, FileSystemRights.FullControl),
                (directory == BaseRoot ? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null) : new SecurityIdentifier(Sid),
                    FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize) })
                acl.AddAccessRule(new FileSystemAccessRule(pair.Item1, pair.Item2, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
        }
        if (!File.Exists(HelperPath)) File.WriteAllBytes(HelperPath, Payload.Value);
        using (var file = File.OpenRead(HelperPath))
            if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(Payload.Value))) throw new IOException("定位组件校验失败。");
        File.WriteAllText(Path.Combine(HelperDirectory, "owner.json"), JsonSerializer.Serialize(new { Sid, Program = Environment.ProcessPath }));
        foreach (string file in new[] { HelperPath, Path.Combine(HelperDirectory, "owner.json") })
        {
            var acl = new FileInfo(file).GetAccessControl();
            acl.SetOwner(administrators);
            new FileInfo(file).SetAccessControl(acl);
        }
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null, definition = null, task = null;
        try
        {
            service.Connect();
            folder = service.GetFolder("\\");
            definition = Definition(service);
            // Only administrators can edit the task. Its owner can read and run it.
            task = ((dynamic)folder).RegisterTaskDefinition(TaskName, definition, 6 | 16, Sid, null, 3, TaskSecurity);
        }
        finally
        {
            if (task is not null) Marshal.FinalReleaseComObject(task);
            if (definition is not null) Marshal.FinalReleaseComObject(definition);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }

    private static void Remove()
    {
        nint taskbar = FindWindowEx(0, 0, "Shell_TrayWnd", null);
        nint bridge = FindWindowEx(taskbar, 0, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        int helper = (int)GetProp(bridge, NativeTaskbarInfoBall.AssistedProcess);
        if (helper > 0) SetProp(bridge, NativeTaskbarInfoBall.AssistedStop, helper);
        if (TaskExists())
        {
            WithTask(task =>
            {
                for (int attempt = 0; attempt < 20 && task.State == 4; attempt++) Thread.Sleep(150);
                if (task.State == 4)
                {
                    if (helper > 0) NativeTaskbarInfoBall.RestoreOwned(helper);
                    task.Stop(0);
                }
                return true;
            });
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            object? folder = null;
            try { service.Connect(); folder = service.GetFolder("\\"); ((dynamic)folder).DeleteTask(TaskName, 0); }
            finally { if (folder is not null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
        }
        if (!Directory.Exists(Root)) return;
        string root = Path.GetFullPath(Root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            string resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(resolved).Length != 64 || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) continue;
            try { Directory.Delete(resolved, recursive: true); } catch (IOException) { /* A retiring helper can still hold its image. */ }
        }
    }

#if NOOK_UI_VERIFICATION
    internal static string DefinitionXmlForVerification()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? definition = null;
        try { service.Connect(); definition = Definition(service); return ((dynamic)definition).XmlText; }
        finally { if (definition is not null) Marshal.FinalReleaseComObject(definition); Marshal.FinalReleaseComObject(service); }
    }
    internal static byte[] PayloadForVerification => Payload.Value;
    internal static string TaskSecurityForVerification => TaskSecurity;
#endif
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint value);
}

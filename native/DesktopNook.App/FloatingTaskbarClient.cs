using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class FloatingTaskbarClient : IDisposable
{
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _requests = new(1);
    private readonly string _dataRoot;
    private readonly string _identity;
    private readonly nint _owner;
    private readonly bool _sandbox;
    internal bool Running => _process is { HasExited: false };
    internal int ProcessId => _process?.Id ?? 0;
    internal string EngineDirectory => Path.Combine(_dataRoot, "FloatingTaskbar", "Engine");

    internal FloatingTaskbarClient(string dataRoot, nint owner, bool sandbox)
    {
        _dataRoot = dataRoot; _owner = owner; _sandbox = sandbox;
        _identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24];
    }

    internal async Task StartAsync(FloatingTaskbarSettings settings)
    {
        if (Running && _pipe is { IsConnected: true }) return;
        if (Running) throw new IOException("悬浮任务栏连接已断开，请先切回系统模式再重试。");
        var exe = await Task.Run(() => FloatingTaskbarPackage.Ensure(_dataRoot));
        Directory.CreateDirectory(EngineDirectory);
        var configPath = Path.Combine(EngineDirectory, "settings.json");
        JsonObject config = File.Exists(configPath) ? JsonNode.Parse(await File.ReadAllTextAsync(configPath))?.AsObject() ?? new() : new();
        foreach (var pair in EngineSettings(settings)) config[pair.Key] = pair.Value?.DeepClone();
        // Unrelated modules and logon/update registration stay owned by the main app.
        config["widgets"] = false; config["stageManager"] = false; config["startOnLogin"] = false;
        config["hideBanners"] = false; config["language"] = "zh"; config["version"] = 2;
        config["notifyListener"] = false; config["notifyWeChat"] = false; config["notifyFeishu"] = false;
        config["notifyQQ"] = false; config["notifyWeCom"] = false; config["notifyDingTalk"] = false;
        config["winKeyStart"] = false; config["winNumbers"] = false;
        config["trayHost"] = !_sandbox;
        if (_sandbox)
            config["pins"] = new JsonArray(
                new JsonObject { ["name"] = "折页", ["path"] = Environment.ProcessPath!, ["args"] = "--sandbox \"" + App.SandboxRoot + "\"", ["aumid"] = "DesktopNook.DesktopNook" },
                new JsonObject { ["name"] = "文件", ["path"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ["args"] = "", ["aumid"] = "" },
                new JsonObject { ["name"] = "记事本", ["path"] = Path.Combine(Environment.SystemDirectory, "notepad.exe"), ["args"] = "", ["aumid"] = "" },
                new JsonObject { ["name"] = "计算器", ["path"] = Path.Combine(Environment.SystemDirectory, "calc.exe"), ["args"] = "", ["aumid"] = "" });
        var temp = configPath + ".tmp-host";
        await File.WriteAllTextAsync(temp, config.ToJsonString());
        File.Move(temp, configPath, true);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = EngineDirectory };
        start.Environment["ZHEYE_DOCK_DATA"] = EngineDirectory;
        start.Environment["ZHEYE_DOCK_ID"] = _identity;
        start.Environment["ZHEYE_DOCK_PARENT"] = Environment.ProcessId.ToString();
        start.Environment["ZHEYE_DOCK_OWNER"] = _owner.ToInt64().ToString();
        start.Environment["ZHEYE_DOCK_SANDBOX"] = _sandbox ? "1" : "0";
        if (_sandbox) start.ArgumentList.Add("--debug-capturable");
        _process = Process.Start(start) ?? throw new IOException("无法启动悬浮任务栏。");
        try
        {
            _pipe?.Dispose();
            _pipe = new NamedPipeClientStream(".", $"Zheye.Dock.{_identity}.{Process.GetCurrentProcess().SessionId}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out uint pid) || pid != _process.Id)
                throw new IOException("悬浮任务栏连接的进程身份不匹配。");
            using var server = Process.GetProcessById((int)pid);
            if (!string.Equals(server.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                throw new IOException("悬浮任务栏连接的程序路径不匹配。");
            _pipe.ReadMode = PipeTransmissionMode.Message;
            var state = await RequestAsync(new() { ["op"] = "get" });
            if (state["protocol"]?.GetValue<int>() != 1) throw new IOException("悬浮任务栏协议不兼容。");
            // Import Windows pins once in the native core; add ZHEYE without changing Windows pins.
            if (!_sandbox && state["current"]?["pins"] is JsonArray pins &&
                !pins.OfType<JsonObject>().Any(pin => string.Equals(pin["path"]?.GetValue<string>(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
            {
                var updated = (JsonArray)pins.DeepClone();
                updated.Insert(0, new JsonObject { ["name"] = "折页", ["path"] = Environment.ProcessPath!, ["args"] = "", ["aumid"] = "DesktopNook.DesktopNook" });
                await RequestAsync(new() { ["op"] = "commit", ["settings"] = new JsonObject { ["pins"] = updated } });
            }
        }
        catch { Stop(); throw; }
    }

    internal JsonObject EngineSettings(FloatingTaskbarSettings settings)
    {
        settings.Validate();
        bool solid = settings.Material == "solid", frosted = settings.Material == "frosted";
        return new()
        {
            ["bar"] = true, ["preset"] = solid ? "custom" : settings.Material,
            ["height"] = settings.Height, ["cornerRadius"] = settings.Height / 2,
            ["iconSize"] = settings.IconSize, ["margin"] = settings.BottomGap, ["width"] = settings.MaximumWidth,
            ["autoWidth"] = settings.AutoWidth, ["magnify"] = UiMotion.Enabled ? settings.Magnification : 1,
            ["transparency"] = solid ? 0 : settings.Transparency, ["blur"] = solid ? 0 : frosted ? Math.Max(.75, settings.Blur) : settings.Blur,
            ["refraction"] = solid || frosted ? 0 : settings.Refraction, ["edge"] = solid ? 0 : 24,
            ["dispersion"] = solid || frosted ? 0 : .3, ["highlight"] = solid ? 0 : settings.Highlight,
            ["shadow"] = .30, ["tint"] = settings.Tint, ["autoHide"] = settings.AutoHide,
            ["showClock"] = settings.ShowClock, ["showTray"] = settings.ShowTray, ["showStatus"] = settings.ShowStatus,
            ["showNotifications"] = settings.ShowNotifications, ["showDesktop"] = true,
            ["hoverRise"] = UiMotion.Enabled && settings.HoverRise, ["showRunning"] = !_sandbox,
            ["hideTaskbar"] = !_sandbox && settings.HideSystemTaskbar, ["remoteVisible"] = settings.RemoteCompatible,
            ["dockDisplayMode"] = settings.DisplayMode, ["dockDisplay"] = settings.Display,
            ["taskbarApps"] = "all", ["barEdge"] = "bottom"
        };
    }

    internal Task<JsonObject> ApplyAsync(FloatingTaskbarSettings settings, bool commit) =>
        RequestAsync(new() { ["op"] = commit ? "commit" : "preview", ["settings"] = EngineSettings(settings) });

    internal async Task<JsonObject> RequestAsync(JsonObject request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _requests.WaitAsync(timeout.Token);
        try
        {
            var pipe = _pipe ?? throw new IOException("悬浮任务栏连接已关闭。");
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
            await pipe.WriteAsync(bytes, timeout.Token);
            using var message = new MemoryStream();
            var buffer = new byte[16384];
            do
            {
                int read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0) throw new IOException("悬浮任务栏连接已中断。");
                await message.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                if (message.Length > 1024 * 1024) throw new IOException("悬浮任务栏回复过大。");
            } while (!pipe.IsMessageComplete);
            var response = JsonNode.Parse(message.ToArray())?.AsObject() ?? throw new IOException("悬浮任务栏回复为空。");
            if (response["op"]?.GetValue<string>() == "error") throw new IOException(response["message"]?.GetValue<string>() ?? "悬浮任务栏请求失败。");
            return response;
        }
        catch (OperationCanceledException) { _pipe?.Dispose(); _pipe = null; throw new IOException("悬浮任务栏请求超时，请切回系统模式重试。"); }
        finally { _requests.Release(); }
    }

    internal bool Stop()
    {
        _pipe?.Dispose(); _pipe = null;
        if (_process is not { } process) return true;
        try
        {
            if (process.HasExited) return true;
            var host = FindWindow("ZheyeDockHost." + _identity, null);
            if (host != 0 && GetWindowThreadProcessId(host, out uint pid) != 0 && pid == process.Id) PostMessage(host, 0x10, 0, 0);
            if (process.WaitForExit(4500)) return true;
            // The core's guard restores Windows after an abnormal termination.
            process.Kill();
            return process.WaitForExit(2500);
        }
        catch (InvalidOperationException) { return true; }
        finally { if (process.HasExited) { process.Dispose(); _process = null; } }
    }
    public void Dispose() => Stop();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? name);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}

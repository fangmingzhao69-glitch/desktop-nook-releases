using System.IO;
using System.Windows.Interop;
using DesktopNook.Core;

namespace DesktopNook.App;

internal sealed class FloatingTaskbarSession : IDisposable
{
    private readonly MainWindow _owner;
    private readonly FloatingTaskbarStore _store;
    private readonly FloatingTaskbarClient _client;
    internal FloatingTaskbarSettings Saved { get; private set; }
    internal FloatingTaskbarSettings Current { get; private set; }
    internal bool HasPreview { get; private set; }
    internal bool Busy { get; private set; }
    internal bool Running => _client.Running;
    internal FloatingTaskbarClient Client => _client;

    internal FloatingTaskbarSession(MainWindow owner)
    {
        _owner = owner; _store = new(App.Store.DataDirectory);
        Saved = Current = _store.Load();
        _client = new(App.Store.DataDirectory, new WindowInteropHelper(owner).Handle, App.IsSandbox);
    }
    internal async Task ApplyAsync(FloatingTaskbarSettings settings, bool save)
    {
        if (Busy) throw new IOException("正在切换任务栏，请稍后重试。");
        settings.Validate(); Busy = true;
        var previous = Current;
        try
        {
            await SwitchAsync(settings, commit: save);
            if (save) { _store.Save(settings); Saved = settings; }
            Current = settings; HasPreview = !save && settings != Saved;
        }
        catch
        {
            try { await SwitchAsync(previous, commit: false); }
            catch { _client.Stop(); await _owner.ResumeSystemTaskbarAppearanceAsync(); }
            throw;
        }
        finally { Busy = false; }
    }
    private async Task SwitchAsync(FloatingTaskbarSettings settings, bool commit)
    {
        if (settings.Mode == "floating")
        {
            if (!_owner.PauseSystemTaskbarAppearance()) throw new IOException("系统任务栏外观正在启动或尚未停止。");
            await _client.StartAsync(settings);
            await _client.ApplyAsync(settings, commit);
        }
        else
        {
            if (!await Task.Run(_client.Stop)) throw new IOException("悬浮任务栏尚未退出。");
            await _owner.ResumeSystemTaskbarAppearanceAsync();
        }
    }
    internal async Task CancelPreviewAsync()
    {
        if (!HasPreview) return;
        await ApplyAsync(Saved, save: false);
        HasPreview = false;
    }
    internal async Task AddPinAsync(NookItem item)
    {
        if (!Running) throw new IOException("请先启用悬浮任务栏。");
        if (item.Kind == "recycle-bin") throw new IOException("回收站请从折页打开。");
        var state = await _client.RequestAsync(new() { ["op"] = "get" });
        var pins = state["current"]?["pins"]?.DeepClone() as System.Text.Json.Nodes.JsonArray ?? new();
        string path = App.Store.ManagedPath(item);
        if (pins.OfType<System.Text.Json.Nodes.JsonObject>().Any(pin => string.Equals(pin["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase))) return;
        pins.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = item.Name, ["path"] = path, ["args"] = "", ["aumid"] = "" });
        await _client.RequestAsync(new() { ["op"] = "commit", ["settings"] = new System.Text.Json.Nodes.JsonObject { ["pins"] = pins } });
    }
    internal bool Stop() => !Busy && _client.Stop();
    public void Dispose() => _client.Dispose();
}

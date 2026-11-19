using System.IO;

using DesktopNook.Core;

namespace DesktopNook.App;

internal interface ITaskbarAppearanceBackend
{
    string? Restriction { get; }
    bool NeedsDownload { get; }
    bool Running { get; }
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
            throw new IOException("无法重新应用已保存的外观，已关闭折页的外观组件。", ex);
        }
    }

    internal bool Stop() => backend.Stop();
}

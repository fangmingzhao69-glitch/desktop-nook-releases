using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNook.Core;

public sealed record TaskbarAppearanceScene(string Key, string Name, bool AllowsRules = false);

public sealed record TaskbarAppearanceSettings : TaskbarAppearanceProfile
{
    public const string DesktopScene = "desktop_appearance";
    public static IReadOnlyList<TaskbarAppearanceScene> Scenes { get; } = Array.AsReadOnly(new TaskbarAppearanceScene[]
    {
        new(DesktopScene, "桌面"),
        new("visible_window_appearance", "有可见窗口", true),
        new("maximized_window_appearance", "窗口最大化", true),
        new("start_opened_appearance", "开始菜单打开"),
        new("search_opened_appearance", "搜索打开"),
        new("task_view_opened_appearance", "任务视图打开"),
        new("battery_saver_appearance", "节能模式")
    });

    public int Format { get; init; } = 1;
    public bool FloatingLayout { get; init; }
    public bool FollowScenes { get; init; } = true;
    public IReadOnlyDictionary<string, TaskbarAppearanceProfile>? Profiles { get; init; }
    public JsonElement? IgnoredWindows { get; init; }

    [JsonIgnore]
    public bool UsesComponent => Enabled && (FloatingLayout || UsesMaterialComponent);

    [JsonIgnore]
    public bool UsesMaterialComponent => Enabled && (Style != "normal" || ShowLine.HasValue || ShowPeek.HasValue ||
        FollowScenes && Profiles?.Values.Any(profile => profile.Enabled) == true);

    public TaskbarAppearanceSettings Validate()
    {
        if (Format != 1) throw new IOException("任务栏外观配置版本不受支持。");
        if (Profiles is not null)
        {
            foreach (var (key, profile) in Profiles)
            {
                var scene = Scenes.FirstOrDefault(scene => scene.Key == key && key != DesktopScene);
                if (scene is null || profile is null) throw new IOException("任务栏外观配置含有不受支持的场景。");
                profile.ValidateAppearance(scene.AllowsRules);
            }
        }
        if (IgnoredWindows is { } ignored) TaskbarAppearanceFilters.ValidateIgnoredWindows(ignored);
        return (TaskbarAppearanceSettings)ValidateAppearance(allowsRules: false);
    }

    public TaskbarAppearanceProfile ProfileFor(string scene) => scene == DesktopScene
        ? this
        : Profiles?.GetValueOrDefault(scene) ?? new TaskbarAppearanceProfile { Style = "normal" };

    // These are the renderer's defaults on Windows 11.
    public static (bool ShowLine, bool ShowPeek, double BlurRadius) SceneDefaultsFor(string scene) => scene switch
    {
        DesktopScene => (false, false, 9),
        "visible_window_appearance" or "battery_saver_appearance" => (false, true, 9),
        "maximized_window_appearance" or "start_opened_appearance" or "search_opened_appearance" => (true, true, 9),
        "task_view_opened_appearance" => (true, false, 9),
        _ => throw new IOException("当前场景不受支持。")
    };

    internal TaskbarAppearanceProfile ResolvedProfileFor(string scene)
    {
        var profile = ProfileFor(scene);
        var defaults = SceneDefaultsFor(scene);
        return profile with
        {
            BlurRadius = profile.BlurRadius ?? defaults.BlurRadius,
            ShowLine = profile.ShowLine ?? defaults.ShowLine,
            ShowPeek = profile.ShowPeek ?? defaults.ShowPeek
        };
    }

    public TaskbarAppearanceSettings WithProfile(string scene, TaskbarAppearanceProfile profile)
    {
        if (scene == DesktopScene)
        {
            profile = profile.ValidateAppearance(allowsRules: false);
            return this with { Style = profile.Style, Color = profile.Color, Alpha = profile.Alpha,
                BlurRadius = profile.BlurRadius, ShowLine = profile.ShowLine, ShowPeek = profile.ShowPeek };
        }
        var descriptor = Scenes.SingleOrDefault(item => item.Key == scene) ?? throw new IOException("当前场景不受支持。");
        var updated = Profiles is null ? new Dictionary<string, TaskbarAppearanceProfile>() : new(Profiles);
        updated[scene] = profile.ValidateAppearance(descriptor.AllowsRules);
        return this with { Profiles = updated };
    }

    public TaskbarAppearanceSettings ForUniformAppearance(string scene)
    {
        var style = ResolvedProfileFor(scene) with { Rules = null };
        return WithProfile(DesktopScene, style) with { FollowScenes = false };
    }

    public TaskbarAppearanceSettings ForPreview(string scene) =>
        ForUniformAppearance(scene) with { Profiles = null, IgnoredWindows = null };
}

public sealed class TaskbarAppearanceStore(string dataRoot)
{
    private const int MaximumSettingsBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string DirectoryPath { get; } = Path.Combine(dataRoot, "TaskbarAppearance");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public TaskbarAppearanceSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        using var stream = File.OpenRead(SettingsPath);
        if (stream.Length > MaximumSettingsBytes) throw new IOException("任务栏外观配置文件过大。");
        return (JsonSerializer.Deserialize<TaskbarAppearanceSettings>(stream, Json)
            ?? throw new IOException("任务栏外观配置为空。")).Validate();
    }

    public void Save(TaskbarAppearanceSettings settings) => Write(SettingsPath, settings.Validate());

    public static void WriteComponentSettings(string path, TaskbarAppearanceSettings settings)
    {
        settings = settings.Validate();
        var output = new Dictionary<string, object>
        {
            ["hide_tray"] = true,
            ["disable_saving"] = true,
            ["copy_dlls"] = false,
            ["floating_layout"] = settings.FloatingLayout,
            ["verbosity"] = "off"
        };
        foreach (var scene in TaskbarAppearanceSettings.Scenes)
        {
            bool dynamic = scene.Key != TaskbarAppearanceSettings.DesktopScene;
            var appearance = settings.ResolvedProfileFor(scene.Key);
            if (dynamic && !settings.FollowScenes) appearance = appearance with { Enabled = false };
            output[scene.Key] = appearance.ToEngine(dynamic);
        }
        if (settings.IgnoredWindows is { } ignored) output["ignored_windows"] = ignored;
        Write(path, output);
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                if (stream.Length > MaximumSettingsBytes) throw new IOException("请减少窗口规则或忽略列表，外观配置文件过大。");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

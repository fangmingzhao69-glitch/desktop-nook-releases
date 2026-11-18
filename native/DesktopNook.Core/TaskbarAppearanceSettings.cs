using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNook.Core;

public sealed record TaskbarAppearanceSettings
{
    public int Format { get; init; } = 1;
    public bool Enabled { get; init; }
    public string Style { get; init; } = "clear";
    public string Color { get; init; } = "#152D42";
    public byte Alpha { get; init; }

    [JsonIgnore]
    public bool UsesComponent => Enabled && Style != "normal";

    public TaskbarAppearanceSettings Validate()
    {
        if (Format != 1 || Style is not ("normal" or "clear" or "opaque") ||
            Color is not { Length: 7 } || Color[0] != '#' || !Color.Skip(1).All(Uri.IsHexDigit))
            throw new IOException("任务栏外观配置无效。颜色格式为 #RRGGBB。");
        return this with { Color = Color.ToUpperInvariant() };
    }

    public static TaskbarAppearanceSettings Import(string source)
    {
        using var file = File.OpenRead(source);
        if (file.Length > 128 * 1024) throw new IOException("外观配置文件过大。");
        using var json = JsonDocument.Parse(file, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32
        });
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("desktop_appearance", out var desktop) ||
            desktop.ValueKind != JsonValueKind.Object ||
            !desktop.TryGetProperty("accent", out var accent) || accent.ValueKind != JsonValueKind.String)
            throw new IOException("没有找到可导入的桌面任务栏外观。");
        var style = accent.GetString();
        if (style is not ("normal" or "clear" or "opaque"))
            throw new IOException("当前只导入系统默认、透明和纯色模式。");
        string color = "#00000000";
        if (desktop.TryGetProperty("color", out var tint))
        {
            if (tint.ValueKind != JsonValueKind.String || tint.GetString() is not { } value)
                throw new IOException("原外观的颜色无效。");
            color = value;
        }
        if (color.Length is 4 or 5 && color[0] == '#')
            color = "#" + string.Concat(color.Skip(1).Select(c => new string(c, 2)));
        if (color.Length is not (7 or 9) || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
            throw new IOException("原外观的颜色无效。");
        byte alpha = color.Length == 9 ? byte.Parse(color.AsSpan(7), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)255;
        return new TaskbarAppearanceSettings { Style = style, Color = color[..7], Alpha = alpha }.Validate();
    }
}

public sealed class TaskbarAppearanceStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath { get; } = Path.Combine(dataRoot, "TaskbarAppearance");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public TaskbarAppearanceSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        using var stream = File.OpenRead(SettingsPath);
        if (stream.Length > 16 * 1024) throw new IOException("任务栏外观配置文件过大。");
        return (JsonSerializer.Deserialize<TaskbarAppearanceSettings>(stream, Json)
            ?? throw new IOException("任务栏外观配置为空。")).Validate();
    }

    public void Save(TaskbarAppearanceSettings settings) => Write(SettingsPath, settings.Validate());

    public static void WriteComponentSettings(string path, TaskbarAppearanceSettings settings)
    {
        settings = settings.Validate();
        var desktop = new
        {
            accent = settings.Style,
            color = $"{settings.Color}{(settings.Style == "opaque" ? 255 : settings.Alpha):X2}"
        };
        var disabled = new { enabled = false };
        Write(path, new
        {
            desktop_appearance = desktop,
            visible_window_appearance = disabled,
            maximized_window_appearance = disabled,
            start_opened_appearance = disabled,
            search_opened_appearance = disabled,
            task_view_opened_appearance = disabled,
            battery_saver_appearance = disabled,
            hide_tray = true,
            disable_saving = true,
            copy_dlls = false,
            verbosity = "off"
        });
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

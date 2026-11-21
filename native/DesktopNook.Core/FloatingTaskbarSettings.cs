using System.Text.Json;

namespace DesktopNook.Core;

public sealed record FloatingTaskbarSettings
{
    public int Format { get; init; } = 1;
    public string Mode { get; init; } = "system";
    public string Material { get; init; } = "liquid";
    public double Height { get; init; } = 60;
    public double IconSize { get; init; } = 36;
    public double BottomGap { get; init; } = 12;
    public double MaximumWidth { get; init; } = 1200;
    public bool AutoWidth { get; init; } = true;
    public double Magnification { get; init; } = 1.5;
    public double Transparency { get; init; } = .90;
    public double Blur { get; init; } = .5;
    public double Refraction { get; init; } = 30;
    public double Highlight { get; init; } = .8;
    public string Tint { get; init; } = "#FFFFFF";
    public string AutoHide { get; init; } = "fullscreen";
    public string DisplayMode { get; init; } = "primary";
    public string Display { get; init; } = "";
    public bool HideSystemTaskbar { get; init; }
    public bool ShowClock { get; init; } = true;
    public bool ShowTray { get; init; } = true;
    public bool ShowStatus { get; init; } = true;
    public bool ShowNotifications { get; init; } = true;
    public bool RemoteCompatible { get; init; }
    public bool HoverRise { get; init; } = true;

    public FloatingTaskbarSettings Validate()
    {
        if (Format != 1 || Mode is not ("system" or "floating") || Material is not ("liquid" or "frosted" or "solid") ||
            AutoHide is not ("never" or "fullscreen" or "always") || DisplayMode is not ("primary" or "fixed" or "all" or "follow"))
            throw new IOException("悬浮任务栏配置包含不支持的选项。");
        Check(Height, 48, 88); Check(IconSize, 24, 56); Check(BottomGap, 0, 48);
        Check(MaximumWidth, 320, 4000); Check(Magnification, 1, 2); Check(Transparency, 0, 1);
        Check(Blur, 0, 1); Check(Refraction, 0, 60); Check(Highlight, 0, 1);
        if (IconSize > Height - 8) throw new IOException("图标大小应小于任务栏高度至少 8 DIP。");
        if (Display is null || Display.Length > 1024 || DisplayMode == "fixed" && string.IsNullOrWhiteSpace(Display))
            throw new IOException("请选择任务栏所在的显示器。");
        if (Tint is null || Tint.Length != 7 || Tint[0] != '#' || !uint.TryParse(Tint.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _)) throw new IOException("染色格式应为 #RRGGBB。");
        return this;
    }
    private static void Check(double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max) throw new IOException("悬浮任务栏参数超出有效范围。");
    }
}

public sealed class FloatingTaskbarStore(string dataRoot)
{
    public string DirectoryPath { get; } = Path.Combine(dataRoot, "FloatingTaskbar");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public FloatingTaskbarSettings Load()
    {
        bool exists = false;
        foreach (string path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            exists = true;
            try
            {
                if (new FileInfo(path).Length > 64 * 1024) continue;
                return (JsonSerializer.Deserialize<FloatingTaskbarSettings>(File.ReadAllText(path)) ?? throw new IOException("配置为空。")).Validate();
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        if (exists) throw new IOException("悬浮任务栏配置及备份均无法读取，系统任务栏保持可用。");
        return new();
    }
    public void Save(FloatingTaskbarSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            if (File.Exists(SettingsPath))
            {
                bool valid = false;
                try { valid = JsonSerializer.Deserialize<FloatingTaskbarSettings>(File.ReadAllText(SettingsPath))?.Validate() is not null; }
                catch (Exception ex) when (ex is IOException or JsonException) { }
                if (valid) File.Copy(SettingsPath, SettingsPath + ".bak", true);
                else File.Copy(SettingsPath, SettingsPath + ".corrupt-" + Guid.NewGuid().ToString("N"));
            }
            File.Move(temp, SettingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

using System.Text.Json;

namespace DesktopNook.Core;

public record TaskbarAppearanceProfile
{
    public bool Enabled { get; init; }
    public string Style { get; init; } = "clear";
    public string Color { get; init; } = "#152D42";
    public byte Alpha { get; init; }
    public double? BlurRadius { get; init; }
    public bool? ShowLine { get; init; }
    public bool? ShowPeek { get; init; }
    public JsonElement? Rules { get; init; }

    internal TaskbarAppearanceProfile ValidateAppearance(bool allowsRules)
    {
        if (!IsSupportedStyle(Style) ||
            Color is not { Length: 7 } || Color[0] != '#' || !Color.Skip(1).All(Uri.IsHexDigit))
            throw new IOException("任务栏外观样式或颜色无效。颜色格式为 #RRGGBB。");
        if (BlurRadius is double radius && (!double.IsFinite(radius) || radius is < 0 or > 750))
            throw new IOException("模糊半径必须在 0 至 750 之间。");
        if (Rules is { } rules)
        {
            if (!allowsRules) throw new IOException("只有窗口可见和最大化场景支持窗口规则。");
            TaskbarAppearanceFilters.ValidateRules(rules);
        }
        return this with { Color = Color.ToUpperInvariant() };
    }

    internal static bool IsSupportedStyle(string? style) => style is "normal" or "clear" or "opaque" or "blur" or "acrylic" or "liquid" or "frosted";

    internal static double? PresetBlurRadius(string style) => style switch
    {
        "liquid" => 6,
        "frosted" => 66,
        _ => null
    };

    internal Dictionary<string, object> ToEngine(bool dynamic)
    {
        double? preset = PresetBlurRadius(Style);
        var result = new Dictionary<string, object>
        {
            ["accent"] = preset.HasValue ? "blur" : Style,
            ["color"] = $"{Color}{Alpha:X2}"
        };
        if (dynamic) result["enabled"] = Enabled;
        if (Style == "liquid") result["liquid_glass"] = true;
        if ((preset ?? BlurRadius) is double radius) result["blur_radius"] = radius;
        if (ShowLine is bool line) result["show_line"] = line;
        if (ShowPeek is bool peek) result["show_peek"] = peek;
        if (Rules is { } rules) result["rules"] = rules;
        return result;
    }
}

public static class TaskbarAppearanceFilters
{
    public static void ValidateIgnoredWindows(JsonElement value)
    {
        foreach (var field in Fields(value))
        {
            if (field.Value.ValueKind != JsonValueKind.Array)
                throw new IOException("忽略窗口的每类条件必须为文字数组。");
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in field.Value.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 512 } name || !values.Add(name))
                    throw new IOException("忽略窗口条件必须为不重复的有效名称。");
        }
    }

    public static void ValidateRules(JsonElement value)
    {
        foreach (var field in Fields(value))
        {
            if (field.Value.ValueKind != JsonValueKind.Object)
                throw new IOException("窗口规则必须按窗口名称映射到外观设置。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in field.Value.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 512 || !names.Add(rule.Name))
                    throw new IOException("窗口规则的名称为空、过长或重复。");
                ValidateRule(rule.Value, allowInactive: true);
            }
        }
    }

    private static IEnumerable<JsonProperty> Fields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IOException("窗口条件必须为 JSON 对象。");
        var keys = new HashSet<string>();
        foreach (var field in value.EnumerateObject())
        {
            if (field.Name is not ("window_class" or "window_title" or "process_name") || !keys.Add(field.Name))
                throw new IOException("窗口条件只支持 window_class、window_title 和 process_name，且每类只能出现一次。");
            yield return field;
        }
    }

    private static void ValidateRule(JsonElement value, bool allowInactive)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IOException("窗口规则必须为 JSON 对象。");
        var keys = new HashSet<string>();
        foreach (var field in value.EnumerateObject())
        {
            if (!keys.Add(field.Name)) throw new IOException("单条窗口规则中存在重复字段。");
            switch (field.Name)
            {
                case "accent":
                    if (field.Value.ValueKind != JsonValueKind.String || !TaskbarAppearanceProfile.IsSupportedStyle(field.Value.GetString()))
                        throw new IOException("窗口规则中的外观样式无效。");
                    break;
                case "color":
                    if (field.Value.ValueKind != JsonValueKind.String || !IsRuleColor(field.Value.GetString()))
                        throw new IOException("窗口规则中的颜色无效。");
                    break;
                case "blur_radius":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out double radius) ||
                        !double.IsFinite(radius) || radius < 0)
                        throw new IOException("窗口规则中的模糊半径无效。");
                    break;
                case "show_line":
                case "show_peek":
                    if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new IOException($"窗口规则中的 {field.Name} 必须为开关。");
                    break;
                case "inactive" when allowInactive:
                    ValidateRule(field.Value, allowInactive: false);
                    break;
                default:
                    throw new IOException($"窗口规则中的字段不受支持：{field.Name}。");
            }
        }
    }

    private static bool IsRuleColor(string? color)
    {
        color = color?.Trim();
        return color is { Length: 4 or 5 or 7 or 9 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
    }
}

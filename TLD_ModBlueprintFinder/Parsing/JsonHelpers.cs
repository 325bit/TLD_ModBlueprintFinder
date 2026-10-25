using System.Globalization;
using System.Text.Json;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>
/// 宽容的 JSON 取值助手。
///
/// modcomponent 的蓝图字段在不同 mod、不同 ModComponent 版本之间差异很大，例如：
///   - 计数既可能写成数字 3，也可能写成字符串 "3"
///   - Count 可能缺失，改用 Quantity + Units 表达重量
///   - 旧版用 GunpowderRequired，新版用 GunpowderKGRequired
///   - 数组元素既可能是对象，也可能是纯字符串
/// 因此这里一律只做「尽力取值」，取不到返回 null，由调用方决定降级策略，
/// 绝不对字段类型做强假设（否则一个 mod 写错就把整次扫描拖崩）。
/// </summary>
internal static class JsonHelpers
{
    /// <summary>按优先级尝试多个属性名（大小写不敏感），返回第一个存在的值。</summary>
    public static bool TryGet(JsonElement obj, out JsonElement value, params string[] names)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>取字符串；数字/布尔会转成文本，对象/数组/Null 返回 null。</summary>
    public static string? GetString(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names)) return null;
        return AsString(v);
    }

    public static string? AsString(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            case JsonValueKind.Number:
                return v.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return null;
        }
    }

    /// <summary>取数字；字符串形式的数字也能解析（"0.1" / "3"）。</summary>
    public static double? GetDouble(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names)) return null;
        return AsDouble(v);
    }

    public static double? AsDouble(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetDouble(out var d) ? d : null;
            case JsonValueKind.String:
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
            default:
                return null;
        }
    }

    /// <summary>取布尔；兼容 "true"/"false" 字符串。</summary>
    public static bool? GetBool(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String:
                var s = v.GetString();
                if (bool.TryParse(s, out var b)) return b;
                if (s == "1") return true;
                if (s == "0") return false;
                return null;
            case JsonValueKind.Number:
                return AsDouble(v) is { } n && Math.Abs(n) > 1e-9;
            default:
                return null;
        }
    }

    /// <summary>
    /// 把一个「可能是数组、可能是单值、可能是 null」的字段读成 JsonElement 列表。
    /// 例：RequiredTool 多数写成字符串 ""，个别 mod 写成数组。
    /// </summary>
    public static IReadOnlyList<JsonElement> GetArrayLike(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names)) return Array.Empty<JsonElement>();

        switch (v.ValueKind)
        {
            case JsonValueKind.Array:
                var list = new List<JsonElement>(v.GetArrayLength());
                foreach (var item in v.EnumerateArray()) list.Add(item);
                return list;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return Array.Empty<JsonElement>();
            default:
                return new[] { v };
        }
    }
}

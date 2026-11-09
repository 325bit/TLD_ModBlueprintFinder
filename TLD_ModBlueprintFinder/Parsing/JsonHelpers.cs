using System.Globalization;
using System.Text.Json;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>
/// Lenient JSON accessors: every getter tries its best and returns null instead of throwing.
/// Fields differ across mods and ModComponent versions - a count may be 3 or "3", Count may be
/// replaced by Quantity + Units, array elements may be objects or plain strings. One malformed mod
/// must not abort a whole scan, so the caller decides how to degrade.
/// </summary>
internal static class JsonHelpers
{
    /// <summary>Tries several property names (case-insensitive) and returns the first hit.</summary>
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

    /// <summary>Reads a string; numbers and booleans are stringified, objects/arrays/null give null.</summary>
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

    /// <summary>Reads a number; numeric strings ("0.1", "3") are accepted.</summary>
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

    /// <summary>Reads a boolean; "true"/"false" strings are accepted.</summary>
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
    /// Reads a field that may be an array, a single value or null into a list.
    /// RequiredTool is usually a bare string and occasionally an array.
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

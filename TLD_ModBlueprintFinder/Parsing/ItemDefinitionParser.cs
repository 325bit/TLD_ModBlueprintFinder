using System.Text.Json;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>
/// Parses auto-mapped/*.json - the item definition itself.
/// Layout: auto-mapped/&lt;internal name&gt;.json; notes/, collectibles/ and ClothingItems/ hold the same
/// format and only differ by category. The root is a component dictionary over 40+ component types.
/// No prefab ID is stored (only localization keys), so the ID is inferred from the file name plus
/// recipe references. Trailing commas are common, hence AllowTrailingCommas.
/// JSON is deliberately left as text: component fields are too varied to model usefully.
/// </summary>
public static class ItemDefinitionParser
{
    /// <summary>Parse result. A non-null SkipReason means no content (empty file / read error).</summary>
    public readonly record struct ParseOutcome(ItemDefinition? Definition, string? SkipReason);

    public static ParseOutcome Parse(string modName, string containerPath, string entryPath, byte[] jsonBytes)
    {
        string text;
        try
        {
            text = BlueprintParser.DecodeText(jsonBytes);
        }
        catch (Exception ex)
        {
            return new ParseOutcome(null, $"文本解码失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
            return new ParseOutcome(null, "文件为空");

        var stem = Path.GetFileNameWithoutExtension(entryPath);
        var (components, warning) = ReadComponents(text);

        return new ParseOutcome(new ItemDefinition
        {
            ModName = modName,
            ContainerPath = containerPath,
            EntryPath = entryPath,
            Stem = stem,
            Components = components,
            JsonText = text,
            SizeBytes = jsonBytes.LongLength,
            ParseWarning = warning
        }, null);
    }

    /// <summary>
    /// Reads top-level property names as the component list. A parse failure keeps the raw text: for a
    /// modder a slightly malformed but complete JSON beats a blank panel.
    /// </summary>
    private static (IReadOnlyList<string> Components, string? Warning) ReadComponents(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
                return (root.EnumerateObject().Select(p => p.Name).ToList(), null);

            // Some packers put several definitions into one array.
            if (root.ValueKind == JsonValueKind.Array)
            {
                var first = root.EnumerateArray().FirstOrDefault();
                var names = first.ValueKind == JsonValueKind.Object
                    ? first.EnumerateObject().Select(p => p.Name).ToList()
                    : new List<string>();

                return (names, "顶层是数组：按第一项的组件列出，完整内容见原始 JSON");
            }

            return (Array.Empty<string>(), $"顶层不是对象（{root.ValueKind}）");
        }
        catch (JsonException ex)
        {
            return (Array.Empty<string>(), $"JSON 语法错误：{ex.Message}（原文仍可查看）");
        }
    }
}

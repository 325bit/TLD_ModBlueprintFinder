using System.Text.Json;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>
/// 解析 modcomponent 里 auto-mapped/*.json —— 也就是「物品定义」本身。
///
/// ── 实测结论（46 个容器 / 688 份定义）──
///   · 布局：auto-mapped/&lt;内部名&gt;.json，另有 auto-mapped/notes/、collectibles/、
///     collectibles/cards/、ClothingItems/ 四个子目录，子目录只是分类，不影响 ID 规则。
///   · 文件顶层直接是组件字典：{ "ModGenericComponent": {...}, "ModStackableBehaviour": {...} }
///     组件类型实测 40+ 种（ModFoodComponent / ModClothingComponent / ModHarvestableBehaviour …）。
///   · **文件里不写预制体 ID**，只有 DisplayNameLocalizationId 之类的本地化 key，
///     所以「这个 ID 对应哪份定义」只能靠文件名与配方引用反推。
///   · 部分文件带尾逗号（Bountiful_Foraging 等 mod 的导出习惯），
///     必须开 AllowTrailingCommas，否则会误报语法错误。
///
/// 这里刻意不把 JSON 转成强类型：物品组件字段太多且各 mod 自定义不一，
/// 详细展示阶段原样给出文本对玩家/制作者更有用（也能直接粘去改自己的 mod）。
/// </summary>
public static class ItemDefinitionParser
{
    /// <summary>解析结果。SkipReason 非空表示没拿到内容（文件为空 / 读取异常）。</summary>
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
    /// 只取顶层属性名当「组件清单」。
    /// 解析失败不丢内容：保留原文展示，另附一条提示——对二次开发的人，
    /// 一份语法有瑕疵但完整的 JSON 仍然比空白有用。
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

            // 少数打包器会把多份定义塞进一个数组
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

namespace TLD_ModBlueprintFinder.Models;

/// <summary>
/// modcomponent 里的一份物品定义（auto-mapped/*.json）。
///
/// 这类文件描述的是「物品本身」——重量、腐坏、可堆叠、能否烹饪等组件参数，
/// 与 blueprints/ 里的「怎么造它」是两回事，所以单独建模、单独展示。
///
/// 文件名（去掉 .json）就是物品的内部名，预制体 ID 一般是
/// <c>GEAR_</c> / <c>LIQUID_</c> / <c>POWDER_</c> + 这个名字；
/// 但文件里不写这个前缀，所以真正的 ID 要靠同名配方引用去反推，
/// 见 <see cref="Indexing.ItemDefinitionIndex"/>。
/// </summary>
public sealed class ItemDefinition
{
    /// <summary>所属 mod（modcomponent 文件名 / 解压目录名）。</summary>
    public string ModName { get; init; } = string.Empty;

    /// <summary>容器（.modcomponent 文件或目录）的完整路径。</summary>
    public string ContainerPath { get; init; } = string.Empty;

    /// <summary>容器内条目路径，如 auto-mapped/sugara.json。</summary>
    public string EntryPath { get; init; } = string.Empty;

    /// <summary>文件名主干（不含扩展名），即物品的内部名。</summary>
    public string Stem { get; init; } = string.Empty;

    /// <summary>文件里的顶层组件名，如 ModFoodComponent / ModStackableBehaviour。</summary>
    public IReadOnlyList<string> Components { get; init; } = Array.Empty<string>();

    /// <summary>原始 JSON 文本，原样保留（含原作者的空格、尾逗号）。</summary>
    public string JsonText { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>解析失败/异常时的说明，正常为 null。</summary>
    public string? ParseWarning { get; init; }

    public override string ToString() => $"{ModName} :: {EntryPath}";
}

/// <summary>物品 ID 的书写规则。蓝图与物品定义两边都要用，集中放一处。</summary>
public static class ItemIds
{
    private static readonly string[] Prefixes = { "GEAR_", "LIQUID_", "POWDER_", "TOOL_", "REC_" };

    /// <summary>去掉 GEAR_ / LIQUID_ / POWDER_ 之类的前缀，得到裸名字。</summary>
    public static string StripPrefix(string id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;

        foreach (var prefix in Prefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return id.Substring(prefix.Length);
        }

        return id;
    }
}

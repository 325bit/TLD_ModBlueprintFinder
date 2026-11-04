using System.Collections.ObjectModel;

namespace TLD_ModBlueprintFinder.Models;

/// <summary>原料条的类别。蓝图里的「原料」不止 Gear，还包括粉末、液体和工具。</summary>
public enum IngredientKind
{
    /// <summary>普通物品（RequiredGear[].Item）。</summary>
    Gear,
    /// <summary>粉末（RequiredPowder[].Powder），例如 POWDER_Flour。</summary>
    Powder,
    /// <summary>液体（RequiredLiquid[].Liquid），例如 LIQUID_WaterPotable。</summary>
    Liquid,
    /// <summary>工具（RequiredTool / OptionalTools / AllowedCookingPots）。</summary>
    Tool
}

/// <summary>工具在配方中的角色。</summary>
public enum ToolRole
{
    None = 0,
    /// <summary>RequiredTool：必须有。</summary>
    Required,
    /// <summary>OptionalTools：有其一即可。</summary>
    Optional,
    /// <summary>AllowedCookingPots：烹饪容器。</summary>
    CookingPot
}

/// <summary>
/// 一条原料引用。对应 modcomponent 蓝图 JSON 里的三种写法：
///   1. { "Item": "GEAR_X", "Count": 3 }                      —— 按件数
///   2. { "Item": "GEAR_X", "Quantity": 0.1, "Units": "Kilograms" } —— 按重量/体积
///   3. "GEAR_X"                                              —— 纯字符串简写
/// 三者必须统一建模，否则「以某物品为原料」的检索会漏。
/// </summary>
public sealed class Ingredient
{
    public string ItemId { get; init; } = string.Empty;
    public IngredientKind Kind { get; init; } = IngredientKind.Gear;
    public ToolRole Role { get; init; } = ToolRole.None;

    /// <summary>按件数计（Count 字段）。</summary>
    public double? Count { get; init; }

    /// <summary>按数值计（Quantity 字段，配合 Units）。</summary>
    public double? Quantity { get; init; }

    /// <summary>Quantity 的单位：Count / Kilograms / Litres。</summary>
    public string? Units { get; init; }

    public bool IsTool => Kind == IngredientKind.Tool;

    /// <summary>用于表格展示的用量文本。</summary>
    public string AmountText
    {
        get
        {
            if (IsTool)
            {
                return Role switch
                {
                    ToolRole.Required => "必需工具",
                    ToolRole.Optional => "可选工具",
                    ToolRole.CookingPot => "可用锅具",
                    _ => "工具"
                };
            }

            if (Quantity.HasValue)
            {
                var unit = NormalizeUnit(Units);
                return $"{Trim(Quantity.Value)} {unit}";
            }

            if (Count.HasValue)
                return $"x{Trim(Count.Value)}";

            return "x1";
        }
    }

    /// <summary>把 "Kilograms" / "Litres" 之类换成紧凑的展示单位。</summary>
    private static string NormalizeUnit(string? units)
    {
        if (string.IsNullOrWhiteSpace(units)) return "";
        var u = units.Trim();
        if (u.Equals("Kilograms", StringComparison.OrdinalIgnoreCase) || u.Equals("kg", StringComparison.OrdinalIgnoreCase)) return "kg";
        if (u.Equals("Litres", StringComparison.OrdinalIgnoreCase) || u.Equals("Liters", StringComparison.OrdinalIgnoreCase) || u.Equals("L", StringComparison.OrdinalIgnoreCase)) return "L";
        if (u.Equals("Count", StringComparison.OrdinalIgnoreCase)) return "件";
        return u;
    }

    /// <summary>数字去掉无意义的 .0，避免表格里出现 "3.000000"。</summary>
    private static string Trim(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString();
        return v.ToString("0.###");
    }

    public override string ToString() => $"{ItemId} {AmountText}";
}

/// <summary>蓝图来源：直接定义在 blueprints/ 下，还是包在 recipes/ 的食谱里。</summary>
public enum BlueprintSourceKind
{
    /// <summary>blueprints/*.json —— 一个文件就是一个 Blueprint 对象。</summary>
    Blueprint,
    /// <summary>recipes/*.json —— 一个文件是 Recipe 对象，蓝图嵌在 BlueprintData 里。</summary>
    Recipe
}

/// <summary>
/// 一条蓝图记录 = modcomponent 里的一份可制作配方。
/// 字段名与 modcomponent JSON 的映射见 <see cref="Parsing.BlueprintParser"/>。
/// </summary>
public sealed class BlueprintRecord
{
    /// <summary>所属 mod（modcomponent 文件名 / 解压目录名）。</summary>
    public string ModName { get; init; } = string.Empty;

    /// <summary>容器（.modcomponent 文件或目录）的完整路径。</summary>
    public string ContainerPath { get; init; } = string.Empty;

    /// <summary>容器内条目路径，如 blueprints/AcornShelled.json。</summary>
    public string EntryPath { get; init; } = string.Empty;

    public BlueprintSourceKind SourceKind { get; init; } = BlueprintSourceKind.Blueprint;

    /// <summary>蓝图 ID（JSON 的 Name）。这是蓝图在游戏里的唯一键。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>食谱的本地化 key（仅 recipes/ 形式有，如 GAMEPLAY_Broth）。</summary>
    public string? RecipeName { get; init; }

    /// <summary>食谱的本地化描述 key。</summary>
    public string? RecipeDescription { get; init; }

    /// <summary>产物物品 ID（CraftedResult）。这是「作为产物」检索的匹配字段。</summary>
    public string CraftedResult { get; init; } = string.Empty;

    /// <summary>产物数量（CraftedResultCount，缺省按 1 处理）。</summary>
    public double CraftedResultCount { get; init; } = 1;

    /// <summary>制作地点原始值（RequiredCraftingLocation）。</summary>
    public string? RawCraftingLocation { get; init; }

    /// <summary>制作地点归一化后的展示名。</summary>
    public string CraftingLocation => CraftingLocations.Normalize(RawCraftingLocation);

    public double DurationMinutes { get; init; }

    /// <summary>配方消耗的原料（Gear + Powder + Liquid）。</summary>
    public IReadOnlyList<Ingredient> Ingredients { get; init; } = Array.Empty<Ingredient>();

    /// <summary>配方涉及的工具（必需 / 可选 / 锅具）。</summary>
    public IReadOnlyList<Ingredient> Tools { get; init; } = Array.Empty<Ingredient>();

    /// <summary>额外条件：煤油、火药之类不落在 Gear/Powder/Liquid 三组里的需求。</summary>
    public IReadOnlyList<string> ExtraRequirements { get; init; } = Array.Empty<string>();

    /// <summary>解析过程中发现的字段问题（缺字段、类型异常）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>原始 JSON 文本，用于详情面板核对。</summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>表格里展示的简短名：优先食谱本地化 key，其次蓝图 ID。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? EntryPath : Name;

    /// <summary>产物的可读文本，如 "GEAR_AcornShelled x10"。</summary>
    public string ResultText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CraftedResult)) return "(未声明产物)";
            if (Math.Abs(CraftedResultCount - 1) < 1e-9) return CraftedResult;
            return $"{CraftedResult} x{CraftedResultCount:0.###}";
        }
    }

    public override string ToString() => $"{ModName} :: {Name} -> {CraftedResult}";
}

/// <summary>
/// 制作地点的归一化表。真实数据里同时存在 "Workbench" 与 "WorkBench" 两种拼写
/// （实测 68 次 Workbench / 1 次 WorkBench），不做归一化会让分组统计出错。
/// </summary>
public static class CraftingLocations
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Anywhere"] = "任意地点",
        ["Workbench"] = "工作台",
        ["WorkbenchMilling"] = "工作台(研磨)",
        ["Fire"] = "火堆",
        ["Forge"] = "锻造炉",
        ["AmmoWorkbench"] = "弹药工作台",
        ["FurnitureWorkbench"] = "家具工作台",
        ["Campfire"] = "营火"
    };

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "未声明";
        var key = raw.Trim();
        // 去掉内部空格再匹配，"Work Bench" 也能命中
        var compact = key.Replace(" ", "");
        return Map.TryGetValue(compact, out var name) ? name : key;
    }
}

/// <summary>一次扫描中某个容器的结果摘要，用于界面上说明「为什么没结果」。</summary>
public sealed class ContainerSummary
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
    public bool LoadFailed { get; set; }
    public string? FailureReason { get; set; }
    public int BlueprintCount { get; set; }

    /// <summary>这个容器里 auto-mapped/ 的物品定义份数。</summary>
    public int DefinitionCount { get; set; }

    public int EntryErrorCount { get; set; }
}

/// <summary>整体扫描报告。</summary>
public sealed class ScanReport
{
    public List<BlueprintRecord> Records { get; } = new();

    /// <summary>auto-mapped/ 里的物品定义（物品本身长什么样，不是配方）。</summary>
    public List<ItemDefinition> Definitions { get; } = new();

    public List<ContainerSummary> Containers { get; } = new();
    public List<string> Errors { get; } = new();
    public int JsonEntriesSeen { get; set; }
    public int JsonEntriesParsed { get; set; }
    public int JsonEntriesSkipped { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string ResolvedModRoot { get; set; } = string.Empty;

    public int WarningCount => Records.Sum(r => r.Warnings.Count);
}

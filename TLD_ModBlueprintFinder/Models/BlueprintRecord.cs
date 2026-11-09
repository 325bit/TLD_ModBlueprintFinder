using System.Collections.ObjectModel;

namespace TLD_ModBlueprintFinder.Models;

/// <summary>Kind of an ingredient line. Ingredients include powders, liquids and tools, not only gear.</summary>
public enum IngredientKind
{
    /// <summary>Plain gear item (RequiredGear[].Item).</summary>
    Gear,
    /// <summary>Powder (RequiredPowder[].Powder), e.g. POWDER_Flour.</summary>
    Powder,
    /// <summary>Liquid (RequiredLiquid[].Liquid), e.g. LIQUID_WaterPotable.</summary>
    Liquid,
    /// <summary>Tool (RequiredTool / OptionalTools / AllowedCookingPots).</summary>
    Tool
}

/// <summary>Role a tool plays in a recipe.</summary>
public enum ToolRole
{
    None = 0,
    /// <summary>RequiredTool: must be carried.</summary>
    Required,
    /// <summary>OptionalTools: any one of them is enough.</summary>
    Optional,
    /// <summary>AllowedCookingPots: cooking vessel.</summary>
    CookingPot
}

/// <summary>
/// One ingredient reference. The JSON has three shapes for the same field:
///   { "Item": "GEAR_X", "Count": 3 }                             - by item count
///   { "Item": "GEAR_X", "Quantity": 0.1, "Units": "Kilograms" }  - by weight or volume
///   "GEAR_X"                                                     - bare string
/// All three must map to this one model, or "used as an ingredient" searches silently miss entries.
/// </summary>
public sealed class Ingredient
{
    public string ItemId { get; init; } = string.Empty;
    public IngredientKind Kind { get; init; } = IngredientKind.Gear;
    public ToolRole Role { get; init; } = ToolRole.None;

    /// <summary>Counted in items (Count).</summary>
    public double? Count { get; init; }

    /// <summary>Measured amount (Quantity, read together with Units).</summary>
    public double? Quantity { get; init; }

    /// <summary>Unit for Quantity: Count / Kilograms / Litres.</summary>
    public string? Units { get; init; }

    public bool IsTool => Kind == IngredientKind.Tool;

    /// <summary>Amount text for table cells.</summary>
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

    /// <summary>Compacts "Kilograms" / "Litres" into the short display unit.</summary>
    private static string NormalizeUnit(string? units)
    {
        if (string.IsNullOrWhiteSpace(units)) return "";
        var u = units.Trim();
        if (u.Equals("Kilograms", StringComparison.OrdinalIgnoreCase) || u.Equals("kg", StringComparison.OrdinalIgnoreCase)) return "kg";
        if (u.Equals("Litres", StringComparison.OrdinalIgnoreCase) || u.Equals("Liters", StringComparison.OrdinalIgnoreCase) || u.Equals("L", StringComparison.OrdinalIgnoreCase)) return "L";
        if (u.Equals("Count", StringComparison.OrdinalIgnoreCase)) return "件";
        return u;
    }

    /// <summary>Drops trailing zeros so tables never show "3.000000".</summary>
    private static string Trim(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString();
        return v.ToString("0.###");
    }

    public override string ToString() => $"{ItemId} {AmountText}";
}

/// <summary>Where a record came from: a blueprint under blueprints/, or a recipe under recipes/.</summary>
public enum BlueprintSourceKind
{
    /// <summary>blueprints/*.json - the file is the blueprint object itself.</summary>
    Blueprint,
    /// <summary>recipes/*.json - the file is a recipe wrapper, the blueprint sits in BlueprintData.</summary>
    Recipe
}

/// <summary>
/// One craftable recipe inside a modcomponent. JSON field mapping: <see cref="Parsing.BlueprintParser"/>.
/// </summary>
public sealed class BlueprintRecord
{
    /// <summary>Owning mod (modcomponent file name or extracted folder name).</summary>
    public string ModName { get; init; } = string.Empty;

    /// <summary>Full path of the container (.modcomponent file or extracted folder).</summary>
    public string ContainerPath { get; init; } = string.Empty;

    /// <summary>Entry path inside the container, e.g. blueprints/AcornShelled.json.</summary>
    public string EntryPath { get; init; } = string.Empty;

    public BlueprintSourceKind SourceKind { get; init; } = BlueprintSourceKind.Blueprint;

    /// <summary>Blueprint ID (JSON "Name"). The blueprint's unique key in game.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Recipe localization key (recipes/ only), e.g. GAMEPLAY_Broth.</summary>
    public string? RecipeName { get; init; }

    /// <summary>Recipe description localization key.</summary>
    public string? RecipeDescription { get; init; }

    /// <summary>Crafted item ID (CraftedResult). What "as a product" searches match on.</summary>
    public string CraftedResult { get; init; } = string.Empty;

    /// <summary>Crafted amount (CraftedResultCount, defaults to 1).</summary>
    public double CraftedResultCount { get; init; } = 1;

    /// <summary>Raw crafting location (RequiredCraftingLocation).</summary>
    public string? RawCraftingLocation { get; init; }

    /// <summary>Crafting location after normalization, for display.</summary>
    public string CraftingLocation => CraftingLocations.Normalize(RawCraftingLocation);

    public double DurationMinutes { get; init; }

    /// <summary>Consumed ingredients (gear + powder + liquid).</summary>
    public IReadOnlyList<Ingredient> Ingredients { get; init; } = Array.Empty<Ingredient>();

    /// <summary>Tools referenced by the recipe (required / optional / cooking pot).</summary>
    public IReadOnlyList<Ingredient> Tools { get; init; } = Array.Empty<Ingredient>();

    /// <summary>Requirements outside the gear/powder/liquid groups, e.g. kerosene or gunpowder.</summary>
    public IReadOnlyList<string> ExtraRequirements { get; init; } = Array.Empty<string>();

    /// <summary>Field problems found while parsing (missing field, unexpected type).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Raw JSON text, shown in the detail panel for verification.</summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>Short name for tables: the blueprint ID, falling back to the entry path.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? EntryPath : Name;

    /// <summary>Readable product text, e.g. "GEAR_AcornShelled x10".</summary>
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
/// Normalization table for crafting locations. Real data contains both "Workbench" and "WorkBench",
/// so grouping breaks without it.
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
        /// <summary>Drops inner spaces before lookup so "Work Bench" matches too.</summary>
        var compact = key.Replace(" ", "");
        return Map.TryGetValue(compact, out var name) ? name : key;
    }
}

/// <summary>Per-container summary, used by the UI to explain why a search came back empty.</summary>
public sealed class ContainerSummary
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
    public bool LoadFailed { get; set; }
    public string? FailureReason { get; set; }
    public int BlueprintCount { get; set; }

    /// <summary>Number of auto-mapped item definitions in this container.</summary>
    public int DefinitionCount { get; set; }

    public int EntryErrorCount { get; set; }
}

/// <summary>Aggregate result of one scan.</summary>
public sealed class ScanReport
{
    public List<BlueprintRecord> Records { get; } = new();

    /// <summary>Item definitions from auto-mapped/ (what the item is, not how to make it).</summary>
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

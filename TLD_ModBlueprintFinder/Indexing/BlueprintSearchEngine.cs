using System.Text;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Indexing;

/// <summary>
/// One row of a result table. A row may aggregate several copies of the same blueprint from different
/// mods; <see cref="Records"/> keeps every source, <see cref="Primary"/> is the displayed one.
/// </summary>
public sealed class SearchRow : IDetailRow
{
    public SearchRow(IReadOnlyList<BlueprintRecord> records, MatchQuality quality, string matchNote)
    {
        if (records.Count == 0) throw new ArgumentException("records 不能为空", nameof(records));
        Records = records;
        Quality = quality;
        MatchNote = matchNote;
    }

    public IReadOnlyList<BlueprintRecord> Records { get; }

    public BlueprintRecord Primary => Records[0];

    public MatchQuality Quality { get; }

    /// <summary>Why this row matched, e.g. "product", "ingredient x10", "optional tool".</summary>
    public string MatchNote { get; }

    // Table columns (bound by DataPropertyName).

    public string BlueprintName => Primary.DisplayName;

    public string RecipeKey => Primary.RecipeName ?? "";

    public string ResultText => Primary.ResultText;

    public string ResultCountText => Primary.CraftedResultCount.ToString("0.###");

    public string LocationText => Primary.CraftingLocation;

    public string DurationText => FormatDuration(Primary.DurationMinutes);

    public string IngredientsText
    {
        get
        {
            if (Primary.Ingredients.Count == 0) return "(无)";
            return string.Join("、", Primary.Ingredients.Select(i => $"{i.ItemId} {i.AmountText}"));
        }
    }

    public string ToolsText
    {
        get
        {
            if (Primary.Tools.Count == 0) return "";
            return string.Join("、", Primary.Tools.Select(t => $"{t.ItemId}({ShortRole(t)})"));
        }
    }

    public string ExtraText => Primary.ExtraRequirements.Count == 0
        ? ""
        : string.Join("；", Primary.ExtraRequirements);

    public string ModSources => string.Join(" | ", Records
        .Select(r => r.ModName)
        .Distinct(StringComparer.OrdinalIgnoreCase));

    public string SourceKindText => Primary.SourceKind == BlueprintSourceKind.Recipe ? "食谱" : "蓝图";

    // Locating info for in-place editing (only the representative copy when records were merged).

    public string ContainerPath => Primary.ContainerPath;

    public string EntryPath => Primary.EntryPath;

    public string EditableJson => Primary.RawJson;

    public string EditTitle => $"{Primary.DisplayName}（{Primary.ModName}）";

    public string DuplicateText => Records.Count > 1 ? $"x{Records.Count}" : "";

    public string QualityText => Quality switch
    {
        MatchQuality.Exact => "精确",
        MatchQuality.Prefix => "前缀",
        MatchQuality.Contains => "包含",
        _ => ""
    };

    // Detail panel.

    public string BuildDetailText()
    {
        var sb = new StringBuilder();

        foreach (var bp in Records)
        {
            sb.AppendLine($"■ {bp.Name}" + (Records.Count > 1 ? $"   （来源副本：{bp.ModName}）" : ""));
            sb.AppendLine($"  来源      : {bp.ModName}  [{(bp.SourceKind == BlueprintSourceKind.Recipe ? "食谱 recipes/" : "蓝图 blueprints/")}]");
            sb.AppendLine($"  容器      : {bp.ContainerPath}");
            sb.AppendLine($"  条目      : {bp.EntryPath}");

            if (!string.IsNullOrWhiteSpace(bp.RecipeName))
                sb.AppendLine($"  食谱键    : {bp.RecipeName}");
            if (!string.IsNullOrWhiteSpace(bp.RecipeDescription))
                sb.AppendLine($"  描述键    : {bp.RecipeDescription}");

            sb.AppendLine($"  产物      : {bp.ResultText}");
            sb.AppendLine($"  制作地点  : {bp.CraftingLocation}" + (bp.RawCraftingLocation is null ? "" : $"（原始值 {bp.RawCraftingLocation}）"));
            sb.AppendLine($"  耗时      : {FormatDuration(bp.DurationMinutes)}");

            if (bp.ExtraRequirements.Count > 0)
                sb.AppendLine($"  额外条件  : {string.Join("；", bp.ExtraRequirements)}");

            sb.AppendLine("  原料      :");
            if (bp.Ingredients.Count == 0) sb.AppendLine("      (无)");
            foreach (var ing in bp.Ingredients)
                sb.AppendLine($"      · {ing.ItemId}  {ing.AmountText}   [{KindName(ing.Kind)}]");

            sb.AppendLine("  工具      :");
            if (bp.Tools.Count == 0) sb.AppendLine("      (无)");
            foreach (var tool in bp.Tools)
                sb.AppendLine($"      · {tool.ItemId}   [{RoleName(tool.Role)}]");

            if (bp.Warnings.Count > 0)
            {
                sb.AppendLine("  ⚠ 解析提示 :");
                foreach (var w in bp.Warnings) sb.AppendLine($"      · {w}");
            }

            sb.AppendLine();
        }

        // Raw JSON last, for field verification.
        sb.AppendLine("──────── 原始 JSON ────────");
        var raw = Records[0].RawJson;
        sb.AppendLine(raw.Length > 20000 ? raw.Substring(0, 20000) + "\n…（已截断）" : raw);

        return sb.ToString();
    }

    internal static string FormatDuration(double minutes)
    {
        if (minutes <= 0) return "";
        if (minutes < 60) return $"{minutes:0.##} 分钟";

        var h = Math.Floor(minutes / 60);
        var m = minutes - h * 60;
        return m < 0.01 ? $"{h:0} 小时" : $"{h:0} 小时 {m:0.##} 分";
    }

    private static string KindName(IngredientKind kind) => kind switch
    {
        IngredientKind.Gear => "物品",
        IngredientKind.Powder => "粉末",
        IngredientKind.Liquid => "液体",
        _ => "工具"
    };

    private static string RoleName(ToolRole role) => role switch
    {
        ToolRole.Required => "必需工具",
        ToolRole.Optional => "可选工具",
        ToolRole.CookingPot => "可用锅具",
        _ => "工具"
    };

    private static string ShortRole(Ingredient t) => t.Role switch
    {
        ToolRole.Required => "必需",
        ToolRole.Optional => "可选",
        ToolRole.CookingPot => "锅具",
        _ => "工具"
    };
}

/// <summary>Complete result of one query.</summary>
public sealed class SearchOutcome
{
    public string Query { get; init; } = string.Empty;
    public MatchMode Mode { get; init; }

    // Blueprints (blueprints/).
    public List<SearchRow> AsProduct { get; } = new();
    public List<SearchRow> AsIngredient { get; } = new();

    /// <summary>Tools are not split by kind: whether a pot fits or a hammer is needed is one question.</summary>
    public List<SearchRow> AsTool { get; } = new();

    // Recipes (recipes/).
    public List<SearchRow> AsRecipeProduct { get; } = new();
    public List<SearchRow> AsRecipeIngredient { get; } = new();

    /// <summary>The item's own JSON definition (auto-mapped); empty when it has none.</summary>
    public List<DefinitionRow> Definitions { get; } = new();

    /// <summary>Similar IDs suggested when nothing matched.</summary>
    public IReadOnlyList<string> Suggestions { get; set; } = Array.Empty<string>();

    /// <summary>Notes for the user, e.g. that only mod blueprints are covered.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Recipe hits across the five groups (definitions excluded). The groups are disjoint, so they add up.</summary>
    public int TotalHits =>
        AsProduct.Count + AsIngredient.Count + AsTool.Count +
        AsRecipeProduct.Count + AsRecipeIngredient.Count;

    /// <summary>No recipe matched and no item definition was found.</summary>
    public bool IsEmpty => TotalHits == 0 && Definitions.Count == 0;
}

/// <summary>
/// Two-way search service; every matching rule lives here and the UI makes no business decisions.
///   product: CraftedResult | ingredient: RequiredGear/Powder/Liquid | tool: RequiredTool/OptionalTools/Pots
/// Blueprints and recipes get separate product and ingredient groups but share the tool group, and the
/// five groups never overlap, so TotalHits adds up. Tools stay out of ingredients on purpose: a tool is
/// not consumed, and listing it there reads as "bring a hammer to burn it".
/// </summary>
public sealed class BlueprintSearchEngine
{
    public BlueprintIndex Index { get; }

    /// <summary>Item definition index. Null when this scan produced no auto-mapped content.</summary>
    public ItemDefinitionIndex? Definitions { get; }

    public BlueprintSearchEngine(BlueprintIndex index, ItemDefinitionIndex? definitions = null)
    {
        Index = index;
        Definitions = definitions;
    }

    public SearchOutcome Search(string rawQuery, MatchMode mode = MatchMode.Auto, bool mergeDuplicates = true)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        var outcome = new SearchOutcome { Query = query, Mode = mode };

        if (query.Length == 0)
        {
            outcome.Notes.Add("请输入物品 ID（例如 GEAR_AcornShelled）。");
            return outcome;
        }

        // Nothing is loaded only when there is no recipe at all and no definition either. Some mods ship
        // auto-mapped definitions with no recipes, and those must still be searchable.
        if (Index.Count == 0 && (Definitions is null || Definitions.Count == 0))
        {
            outcome.Notes.Add("尚未加载任何蓝图或物品定义，请先选择游戏路径并点击「扫描」。");
            return outcome;
        }

        // Product groups 1 and 4.
        CollectProducts(outcome.AsProduct, query, mode, mergeDuplicates, BlueprintSourceKind.Blueprint, "作为产物");
        CollectProducts(outcome.AsRecipeProduct, query, mode, mergeDuplicates, BlueprintSourceKind.Recipe, "食谱产物");

        // Ingredient groups 2 and 5.
        CollectIngredients(outcome.AsIngredient, query, mode, mergeDuplicates, BlueprintSourceKind.Blueprint);
        CollectIngredients(outcome.AsRecipeIngredient, query, mode, mergeDuplicates, BlueprintSourceKind.Recipe);

        // Tool group 3 (blueprints and recipes merged: a pot hangs off a recipe, a hammer off a blueprint).
        var toolGroups = new Dictionary<string, List<BlueprintRecord>>(StringComparer.OrdinalIgnoreCase);
        var toolQuality = new Dictionary<string, MatchQuality>(StringComparer.OrdinalIgnoreCase);
        var toolNote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (bp, quality, tool) in Index.SearchAsTool(query, mode))
        {
            var key = mergeDuplicates ? bp.Name : $"{bp.ModName}::{bp.EntryPath}";
            KeepBest(toolGroups, toolQuality, key, bp, quality);

            var note = tool.Role switch
            {
                ToolRole.Required => "必需工具",
                ToolRole.Optional => "可选工具",
                ToolRole.CookingPot => "可用锅具",
                _ => "工具"
            };
            if (!toolNote.ContainsKey(key)) toolNote[key] = note;
        }

        outcome.AsTool.AddRange(BuildRows(toolGroups, toolQuality,
            key => toolNote.TryGetValue(key, out var n) ? n : "作为工具"));

        // Item definitions (auto-mapped). Independent of recipe hits: definitions without recipes are common.
        if (Definitions is not null)
            outcome.Definitions.AddRange(Definitions.Search(query, mode));

        // Suggestions when nothing matched.
        if (outcome.IsEmpty)
        {
            var suggestions = Index.SuggestSimilarIds(query);
            outcome.Suggestions = suggestions;
            if (suggestions.Count > 0)
                outcome.Notes.Add("没有精确匹配，以下是相近的物品 ID：");
        }

        return outcome;
    }

    /// <summary>
    /// Collects the "query as product" group. The blueprint tab and the recipe tab run this same code and
    /// only swap <paramref name="kind"/> and the note text; writing it twice means fixing a dedup key or a
    /// sort order in one place and missing the other, so the two tab counts disagree.
    /// </summary>
    private void CollectProducts(
        List<SearchRow> target,
        string query,
        MatchMode mode,
        bool mergeDuplicates,
        BlueprintSourceKind kind,
        string note)
    {
        var groups = new Dictionary<string, List<BlueprintRecord>>(StringComparer.OrdinalIgnoreCase);
        var quality = new Dictionary<string, MatchQuality>(StringComparer.OrdinalIgnoreCase);

        foreach (var (bp, matchQuality, _) in Index.SearchAsProduct(query, mode, kind))
        {
            var key = mergeDuplicates ? bp.Name : $"{bp.ModName}::{bp.EntryPath}";
            KeepBest(groups, quality, key, bp, matchQuality);
        }

        target.AddRange(BuildRows(groups, quality, _ => note));
    }

    /// <summary>Collects the "query as ingredient" group, with the amount in the note.</summary>
    private void CollectIngredients(
        List<SearchRow> target,
        string query,
        MatchMode mode,
        bool mergeDuplicates,
        BlueprintSourceKind kind)
    {
        var groups = new Dictionary<string, List<BlueprintRecord>>(StringComparer.OrdinalIgnoreCase);
        var quality = new Dictionary<string, MatchQuality>(StringComparer.OrdinalIgnoreCase);
        var note = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (bp, matchQuality, ing) in Index.SearchAsIngredient(query, mode, kind))
        {
            var key = mergeDuplicates ? bp.Name : $"{bp.ModName}::{bp.EntryPath}";
            KeepBest(groups, quality, key, bp, matchQuality);

            // When one recipe matches at several ingredient positions, keep the note with the most information.
            var text = $"原料 {ing.AmountText}" + (ing.Kind == IngredientKind.Powder ? "（粉末）" :
                                                   ing.Kind == IngredientKind.Liquid ? "（液体）" : "");
            if (!note.TryGetValue(key, out var existing) || existing.Length < text.Length)
                note[key] = text;
        }

        target.AddRange(BuildRows(groups, quality,
            key => note.TryGetValue(key, out var n) ? n : "作为原料"));
    }

    /// <summary>
    /// Keeps one record per key when a group was hit several times, but keeps the best match quality.
    /// "Best" rather than "first" because the inverted tables enumerate in dictionary order, so the first
    /// hit may well be a Contains match.
    /// </summary>
    private static void KeepBest(
        Dictionary<string, List<BlueprintRecord>> groups,
        Dictionary<string, MatchQuality> qualities,
        string key,
        BlueprintRecord bp,
        MatchQuality quality)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            list = new List<BlueprintRecord>();
            groups[key] = list;
        }

        if (!list.Any(r => ReferenceEquals(r, bp)))
            list.Add(bp);

        if (!qualities.TryGetValue(key, out var existing) || quality < existing)
            qualities[key] = quality;
    }

    private static IEnumerable<SearchRow> BuildRows(
        Dictionary<string, List<BlueprintRecord>> groups,
        Dictionary<string, MatchQuality> qualities,
        Func<string, string> noteFactory)
    {
        return groups.Keys
            .Select(key => new SearchRow(
                groups[key]
                    .OrderBy(r => r.ModName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.EntryPath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                qualities.TryGetValue(key, out var q) ? q : MatchQuality.Contains,
                noteFactory(key)))
            .OrderBy(row => row.Quality)
            .ThenBy(row => row.BlueprintName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ModSources, StringComparer.OrdinalIgnoreCase);
    }
}

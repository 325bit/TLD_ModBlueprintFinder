using System.Text;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Indexing;

/// <summary>
/// 结果表里的一行。一个 <see cref="SearchRow"/> 可能聚合多份同名蓝图（不同 mod 的副本），
/// 用 <see cref="Records"/> 保存全部来源，<see cref="Primary"/> 作为展示代表。
/// </summary>
public sealed class SearchRow
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

    /// <summary>这一行为什么命中，例如「产物」「原料 x10」「可选工具」。</summary>
    public string MatchNote { get; }

    // ── 表格列（DataGridView 通过 DataPropertyName 绑定）──

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

    public string DuplicateText => Records.Count > 1 ? $"x{Records.Count}" : "";

    public string QualityText => Quality switch
    {
        MatchQuality.Exact => "精确",
        MatchQuality.Prefix => "前缀",
        MatchQuality.Contains => "包含",
        _ => ""
    };

    // ── 详情面板 ──

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

        // 原始 JSON 附在最后，方便核对字段
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

/// <summary>一次查询的完整结果。</summary>
public sealed class SearchOutcome
{
    public string Query { get; init; } = string.Empty;
    public MatchMode Mode { get; init; }
    public List<SearchRow> AsProduct { get; } = new();
    public List<SearchRow> AsIngredient { get; } = new();
    public List<SearchRow> AsTool { get; } = new();

    /// <summary>查无匹配时给出的相近 ID 建议。</summary>
    public IReadOnlyList<string> Suggestions { get; set; } = Array.Empty<string>();

    /// <summary>给用户看的备注（例如提示只覆盖 Mod 蓝图）。</summary>
    public List<string> Notes { get; } = new();

    public int TotalHits => AsProduct.Count + AsIngredient.Count + AsTool.Count;

    public bool IsEmpty => TotalHits == 0;
}

/// <summary>
/// 双向检索服务。
///
/// ── 判定条件（写死在这里，界面层不做任何业务判断）──
///   「作为产物」：blueprint.CraftedResult 与查询串匹配
///   「作为原料」：blueprint.RequiredGear[].Item / RequiredPowder[].Powder / RequiredLiquid[].Liquid 中任一项匹配
///   「作为工具」：RequiredTool / OptionalTools / AllowedCookingPots 中任一项匹配
///
/// 三者刻意分开：工具不是消耗品（不会被扣掉），把它混进「原料」会让玩家误以为要消耗一把锤子。
/// </summary>
public sealed class BlueprintSearchEngine
{
    public BlueprintIndex Index { get; }

    public BlueprintSearchEngine(BlueprintIndex index) => Index = index;

    public SearchOutcome Search(string rawQuery, MatchMode mode = MatchMode.Auto, bool mergeDuplicates = true)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        var outcome = new SearchOutcome { Query = query, Mode = mode };

        if (Index.Count == 0)
        {
            outcome.Notes.Add("尚未加载任何蓝图，请先选择游戏路径并点击「扫描」。");
            return outcome;
        }

        if (query.Length == 0)
        {
            outcome.Notes.Add("请输入物品 ID（例如 GEAR_AcornShelled）。");
            return outcome;
        }

        // ① 作为产物
        var productGroups = new Dictionary<string, List<BlueprintRecord>>(StringComparer.OrdinalIgnoreCase);
        var productQuality = new Dictionary<string, MatchQuality>(StringComparer.OrdinalIgnoreCase);

        foreach (var (bp, quality, _) in Index.SearchAsProduct(query, mode))
        {
            var key = mergeDuplicates ? bp.Name : $"{bp.ModName}::{bp.EntryPath}";
            KeepBest(productGroups, productQuality, key, bp, quality);
        }

        outcome.AsProduct.AddRange(BuildRows(productGroups, productQuality, q => "作为产物"));

        // ② 作为原料
        var ingredientGroups = new Dictionary<string, List<BlueprintRecord>>(StringComparer.OrdinalIgnoreCase);
        var ingredientQuality = new Dictionary<string, MatchQuality>(StringComparer.OrdinalIgnoreCase);
        var ingredientNote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (bp, quality, ing) in Index.SearchAsIngredient(query, mode))
        {
            var key = mergeDuplicates ? bp.Name : $"{bp.ModName}::{bp.EntryPath}";
            KeepBest(ingredientGroups, ingredientQuality, key, bp, quality);

            var note = $"原料 {ing.AmountText}" + (ing.Kind == IngredientKind.Powder ? "（粉末）" :
                                                  ing.Kind == IngredientKind.Liquid ? "（液体）" : "");
            if (!ingredientNote.TryGetValue(key, out var existing) || existing.Length < note.Length)
                ingredientNote[key] = note;
        }

        outcome.AsIngredient.AddRange(BuildRows(ingredientGroups, ingredientQuality,
            key => ingredientNote.TryGetValue(key, out var n) ? n : "作为原料"));

        // ③ 作为工具
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

        // ④ 无结果时给建议
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
    /// 同一个 key 命中多次时只保留一条记录，但质量取最好的一次。
    /// 之所以要「取最好」而不是「先到先得」：倒排表遍历顺序是字典序，先到的可能是 Contains 匹配。
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

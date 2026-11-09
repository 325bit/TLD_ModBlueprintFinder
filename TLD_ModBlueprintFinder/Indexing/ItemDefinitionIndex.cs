using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Indexing;

/// <summary>One detail row. Every result table and the definition table share the right-hand panel through it.</summary>
public interface IDetailRow
{
    MatchQuality Quality { get; }

    /// <summary>Display text for the match quality (exact / prefix / contains).</summary>
    string QualityText { get; }

    string BuildDetailText();

    // Locating info for in-place editing.

    /// <summary>Full path of the owning modcomponent (backups and writes key off it).</summary>
    string ContainerPath { get; }

    /// <summary>Entry path inside the container, e.g. blueprints/AcornShelled.json.</summary>
    string EntryPath { get; }

    /// <summary>Editable raw JSON; empty when this row has nothing to write.</summary>
    string EditableJson { get; }

    /// <summary>Short title for the status bar and dialogs, e.g. "AcornShelled (AHandyToolbox)".</summary>
    string EditTitle { get; }

    /// <summary>True for rows that come from the game itself: nothing to write back, so every edit stays off.</summary>
    bool IsVanilla => false;
}

/// <summary>Confidence in "which ID does this definition belong to".</summary>
public enum DefinitionConfidence
{
    /// <summary>A recipe in the same mod references this ID.</summary>
    SameMod,

    /// <summary>A recipe elsewhere (another mod, or another container) references this name.</summary>
    CrossMod,

    /// <summary>No recipe references it; the ID comes from the file name alone.</summary>
    FileNameOnly
}

/// <summary>
/// One row of the item definition table (= one JSON file under auto-mapped/).
/// </summary>
public sealed class DefinitionRow : IDetailRow
{
    internal DefinitionRow(
        ItemDefinition definition,
        string? resolvedId,
        DefinitionConfidence confidence,
        MatchQuality quality,
        bool caseMismatch)
    {
        Definition = definition;
        ResolvedId = resolvedId;
        Confidence = confidence;
        Quality = quality;
        CaseMismatch = caseMismatch;
    }

    public ItemDefinition Definition { get; }

    /// <summary>Inferred item ID; null means only the file name is known.</summary>
    public string? ResolvedId { get; }

    public DefinitionConfidence Confidence { get; }

    public MatchQuality Quality { get; }

    /// <summary>Matched, but the real ID differs from the query only by case.</summary>
    public bool CaseMismatch { get; }

    // Table columns.

    public string ItemId => ResolvedId ?? Definition.Stem;

    public string ConfidenceText
    {
        get
        {
            var basis = Confidence switch
            {
                DefinitionConfidence.SameMod => "同 mod 配方引用",
                DefinitionConfidence.CrossMod => "其他 mod 引用",
                _ => "仅文件名"
            };

            var quality = Quality switch
            {
                MatchQuality.Exact => "",
                MatchQuality.Prefix => "前缀 · ",
                _ => "包含 · "
            };

            return CaseMismatch ? $"{quality}{basis} · 大小写不同" : quality + basis;
        }
    }

    public string ComponentsText =>
        Definition.Components.Count == 0 ? "(未识别)" : string.Join("、", Definition.Components);

    public string ComponentCountText => Definition.Components.Count.ToString();

    public string ModName => Definition.ModName;

    public string EntryPath => Definition.EntryPath;

    public string ContainerPath => Definition.ContainerPath;

    public string EditableJson => Definition.JsonText;

    public string EditTitle => $"{ItemId}（{Definition.ModName}）";

    public string SizeText => Definition.SizeBytes < 1024
        ? $"{Definition.SizeBytes} B"
        : $"{Definition.SizeBytes / 1024.0:0.#} KB";

    public string QualityText => Quality switch
    {
        MatchQuality.Exact => "精确",
        MatchQuality.Prefix => "前缀",
        MatchQuality.Contains => "包含",
        _ => ""
    };

    public string BuildDetailText()
    {
        var d = Definition;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"■ {ItemId}     ← auto-mapped 物品定义");
        sb.AppendLine($"  来源      : {d.ModName}  [物品定义 auto-mapped/]");
        sb.AppendLine($"  容器      : {d.ContainerPath}");
        sb.AppendLine($"  条目      : {d.EntryPath}");
        sb.AppendLine($"  文件名    : {d.Stem}.json");
        sb.AppendLine($"  组件      : {ComponentsText}");
        sb.AppendLine($"  大小      : {SizeText}");
        sb.AppendLine($"  判定依据  : {Explain()}");

        if (CaseMismatch)
        {
            sb.AppendLine();
            sb.AppendLine($"  ⚠ 注意：你查的 ID 与这份定义的宿主 ID「{ResolvedId}」只是大小写不同。");
            sb.AppendLine("     游戏里的 ID 区分大小写，两者可能是不同的物品。");
        }

        if (!string.IsNullOrWhiteSpace(d.ParseWarning))
            sb.AppendLine($"  ⚠ 解析提示: {d.ParseWarning}");

        sb.AppendLine();
        sb.AppendLine("──────── JSON 定义 ────────");
        sb.AppendLine(d.JsonText);

        return sb.ToString();
    }

    private string Explain() => Confidence switch
    {
        DefinitionConfidence.SameMod => $"同一 mod 的配方文件里引用了 {ResolvedId}，按文件名对齐 —— 基本可确定",
        DefinitionConfidence.CrossMod => $"另一些 mod 的配方引用了 {ResolvedId}，本 mod 没有引用 —— 大概率是同一件物品",
        _ => "没有任何配方引用这个文件名，只能按名字推断（前缀无法确定）"
    };
}

/// <summary>
/// Item definition index.
/// Files under auto-mapped/ are named &lt;internal name&gt;.json and never store the prefab ID, so it is
/// inferred: same-mod recipe references first, then any mod's recipes, then the file name alone, each
/// labelled in the UI. Skipping this produces mismatches - sugara.json belongs to MoreMeds' GEAR_sugara,
/// while a recipe in the same mod references vanilla GEAR_SugarA. So the index tracks how many IDs differ
/// only by case, and refuses case fallback when there is more than one.
/// </summary>
public sealed class ItemDefinitionIndex
{
    private sealed record Resolved(ItemDefinition Definition, string? Id, DefinitionConfidence Confidence);

    private readonly List<Resolved> _entries = new();

    /// <summary>Lower-case file stem -&gt; entry indexes.</summary>
    private readonly Dictionary<string, List<int>> _byStem = new(StringComparer.Ordinal);

    /// <summary>Lower-case ID -&gt; every casing seen in the corpus.</summary>
    private readonly Dictionary<string, HashSet<string>> _caseVariants = new(StringComparer.Ordinal);

    public ItemDefinitionIndex(IEnumerable<ItemDefinition> definitions, IEnumerable<BlueprintRecord> records)
    {
        // Ordinal is required here: the corpus contains GEAR_sugara and GEAR_SugarA, and an OrdinalIgnoreCase
        // set would collapse them into one entry, losing exactly the distinction that matters.
        var idsByMod = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var allIds = new HashSet<string>(StringComparer.Ordinal);

        void Note(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            allIds.Add(id);

            var lower = id.ToLowerInvariant();
            if (!_caseVariants.TryGetValue(lower, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _caseVariants[lower] = set;
            }

            set.Add(id);
        }

        foreach (var bp in records)
        {
            Note(bp.CraftedResult);
            foreach (var ing in bp.Ingredients) Note(ing.ItemId);
            foreach (var tool in bp.Tools) Note(tool.ItemId);

            if (!idsByMod.TryGetValue(bp.ModName, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                idsByMod[bp.ModName] = set;
            }

            set.Add(bp.CraftedResult);
            foreach (var ing in bp.Ingredients) set.Add(ing.ItemId);
            foreach (var tool in bp.Tools) set.Add(tool.ItemId);
        }

        foreach (var def in definitions)
        {
            var (id, confidence) = Resolve(def, idsByMod, allIds);
            var index = _entries.Count;
            _entries.Add(new Resolved(def, id, confidence));

            var key = def.Stem.ToLowerInvariant();
            if (!_byStem.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _byStem[key] = list;
            }

            list.Add(index);
        }
    }

    public int Count => _entries.Count;

    public int ModCount => _entries.Select(e => e.Definition.ModName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>The same item ID appears in more than one casing.</summary>
    private bool HasCaseTwin(string id) =>
        _caseVariants.TryGetValue(id.ToLowerInvariant(), out var set) && set.Count > 1;

    private static (string? Id, DefinitionConfidence Confidence) Resolve(
        ItemDefinition def,
        Dictionary<string, HashSet<string>> idsByMod,
        HashSet<string> allIds)
    {
        var sameMod = PickCandidate(def, idsByMod.TryGetValue(def.ModName, out var set) ? set : null);
        if (sameMod is not null) return (sameMod, DefinitionConfidence.SameMod);

        var crossMod = PickCandidate(def, allIds);
        if (crossMod is not null) return (crossMod, DefinitionConfidence.CrossMod);

        return (null, DefinitionConfidence.FileNameOnly);
    }

    /// <summary>
    /// Picks the candidate ID corresponding to the file name: an exact-case match first (this separates
    /// sugara from SugarA), then a case-insensitive match only when it is unique.
    /// </summary>
    private static string? PickCandidate(ItemDefinition def, IEnumerable<string>? candidates)
    {
        if (candidates is null) return null;

        string? loose = null;
        var looseCount = 0;

        foreach (var id in candidates)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!ItemIds.StripPrefix(id).Equals(def.Stem, StringComparison.OrdinalIgnoreCase)) continue;

            if (ItemIds.StripPrefix(id).Equals(def.Stem, StringComparison.Ordinal))
                return id;   // exact case: most reliable, take it

            loose = id;
            looseCount++;
        }

        return looseCount == 1 ? loose : null;
    }

    /// <summary>
    /// Looks definitions up by item ID. Matching runs on the prefix-stripped name, so GEAR_sugara,
    /// LIQUID_xxx and a bare "sugara" all reach the same file.
    /// </summary>
    public IReadOnlyList<DefinitionRow> Search(string rawQuery, MatchMode mode)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        if (query.Length == 0) return Array.Empty<DefinitionRow>();

        var queryStem = ItemIds.StripPrefix(query);
        if (queryStem.Length == 0) return Array.Empty<DefinitionRow>();

        var rows = new List<DefinitionRow>();

        foreach (var (stem, indexes) in _byStem)
        {
            var quality = EvaluateStem(stem, queryStem, mode);
            if (quality == MatchQuality.None) continue;

            foreach (var index in indexes)
            {
                var entry = _entries[index];

                var caseMismatch = false;

                // On an exact hit the real ID must agree with the query; a different name is dropped.
                if (quality == MatchQuality.Exact && entry.Id is not null &&
                    !entry.Id.Equals(query, StringComparison.Ordinal))
                {
                    if (!entry.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Case-only difference: when another ID with the same lower-case form exists, these may be two items.
                    caseMismatch = HasCaseTwin(entry.Id);
                }

                rows.Add(new DefinitionRow(entry.Definition, entry.Id, entry.Confidence, quality, caseMismatch));
            }
        }

        return rows
            .OrderBy(r => r.Quality)
            .ThenBy(r => r.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Same three-tier semantics as <see cref="BlueprintIndex.Evaluate"/>, applied to file stems.</summary>
    private static MatchQuality EvaluateStem(string stem, string queryStem, MatchMode mode)
    {
        var isExact = stem.Equals(queryStem, StringComparison.OrdinalIgnoreCase);

        if (mode == MatchMode.Fuzzy)
        {
            if (isExact) return MatchQuality.None;
            return stem.Contains(queryStem, StringComparison.OrdinalIgnoreCase)
                ? MatchQuality.Contains
                : MatchQuality.None;
        }

        if (isExact) return MatchQuality.Exact;
        if (mode == MatchMode.ExactOnly) return MatchQuality.None;

        if (stem.StartsWith(queryStem, StringComparison.OrdinalIgnoreCase))
            return MatchQuality.Prefix;

        return stem.Contains(queryStem, StringComparison.OrdinalIgnoreCase)
            ? MatchQuality.Contains
            : MatchQuality.None;
    }
}

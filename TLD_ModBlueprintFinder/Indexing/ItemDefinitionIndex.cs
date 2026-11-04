using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Indexing;

/// <summary>一行详情。三个结果表 + 物品定义表共用右侧面板，靠这个接口统一渲染。</summary>
public interface IDetailRow
{
    MatchQuality Quality { get; }

    /// <summary>匹配方式的中文说明（精确/前缀/包含）。</summary>
    string QualityText { get; }

    string BuildDetailText();
}

/// <summary>「这份定义对应哪个 ID」的可信度。</summary>
public enum DefinitionConfidence
{
    /// <summary>同 mod 的配方文件里引用过这个 ID。</summary>
    SameMod,

    /// <summary>别的 mod（或同 mod 别的容器）的配方引用过这个名字。</summary>
    CrossMod,

    /// <summary>没有任何配方引用过，只能靠文件名推断。</summary>
    FileNameOnly
}

/// <summary>
/// 物品定义表的一行（= auto-mapped 里的一份 JSON）。
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

    /// <summary>反推出来的物品 ID；为 null 表示只有文件名可用。</summary>
    public string? ResolvedId { get; }

    public DefinitionConfidence Confidence { get; }

    public MatchQuality Quality { get; }

    /// <summary>命中了，但真实 ID 与查询只是大小写不同。</summary>
    public bool CaseMismatch { get; }

    // ── 表格列 ──

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
/// 物品定义索引。
///
/// ── 为什么需要「反推 ID」这一步 ──
/// auto-mapped 里的文件叫 &lt;内部名&gt;.json，文件内容只写组件参数，不写预制体 ID。
/// 而玩家查的是 GEAR_xxx 这种 ID，中间少了前缀这一环，只能这样补：
///   1. 先用同 mod 配方里出现过的 ID 去对文件名（最可信）；
///   2. 再退到全局（跨 mod）的 ID；
///   3. 都没有就只用文件名，并明确标注「仅文件名」。
///
/// 不这么做就会出现张冠李戴：`sugara.json` 是 MoreMeds 的 GEAR_sugara，
/// 但同一个 mod 的玫瑰果酱配方引用了原版的 GEAR_SugarA，两者只差大小写。
/// 所以还要记录「小写后同名的 ID 有几个」，多于一个时不做大小写兜底匹配。
/// </summary>
public sealed class ItemDefinitionIndex
{
    private sealed record Resolved(ItemDefinition Definition, string? Id, DefinitionConfidence Confidence);

    private readonly List<Resolved> _entries = new();

    /// <summary>文件名主干（小写）→ 条目下标。</summary>
    private readonly Dictionary<string, List<int>> _byStem = new(StringComparer.Ordinal);

    /// <summary>小写 ID → 实际出现过的各种大小写写法。</summary>
    private readonly Dictionary<string, HashSet<string>> _caseVariants = new(StringComparer.Ordinal);

    public ItemDefinitionIndex(IEnumerable<ItemDefinition> definitions, IEnumerable<BlueprintRecord> records)
    {
        // 这里必须用 Ordinal：语料里存在只差大小写的两个 ID（GEAR_sugara / GEAR_SugarA），
        // 用 OrdinalIgnoreCase 的集合会把它们折叠成一条，正好丢掉需要区分的信息。
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

    /// <summary>同名（忽略大小写）的物品 ID 有多种大小写写法。</summary>
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
    /// 从候选 ID 里挑出与文件名对应的那个：
    /// 先看「去掉前缀后大小写完全一致」的（区分 sugara / SugarA），
    /// 再退到忽略大小写且只有一个候选的情况。
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
                return id;   // 大小写都一致，最可信，直接用

            loose = id;
            looseCount++;
        }

        return looseCount == 1 ? loose : null;
    }

    /// <summary>
    /// 按物品 ID 查定义。匹配的是「去掉前缀后的名字」，因此
    /// GEAR_sugara / LIQUID_xxx / 裸名字 sugara 都能命中同一份文件。
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

                // 精确命中时，真实 ID 必须与查询对得上；名字不同就直接排除
                if (quality == MatchQuality.Exact && entry.Id is not null &&
                    !entry.Id.Equals(query, StringComparison.Ordinal))
                {
                    if (!entry.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 只是大小写不同：若语料里存在同形异写的另一个 ID，有可能是两件不同物品，必须标注
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

    /// <summary>与 <see cref="BlueprintIndex.Evaluate"/> 同一套三档语义，只是比较对象换成文件名。</summary>
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

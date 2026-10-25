using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Indexing;

/// <summary>匹配质量。数值越小越精确，用于结果排序与界面着色。</summary>
public enum MatchQuality
{
    None = 0,
    /// <summary>完全相等（忽略大小写）。</summary>
    Exact = 1,
    /// <summary>以查询串开头。</summary>
    Prefix = 2,
    /// <summary>包含查询串。</summary>
    Contains = 3
}

/// <summary>用户可选的匹配强度。三档互不重叠，避免「选了跟没选一样」。</summary>
public enum MatchMode
{
    /// <summary>自动：精确 &gt; 前缀 &gt; 包含，全部列出并按精确度排序。</summary>
    Auto,
    /// <summary>只出完全相等的结果。</summary>
    ExactOnly,
    /// <summary>只看「名字里包含查询串」，且排除完全相等的那条（用来捞一批同族 ID）。</summary>
    Fuzzy
}

/// <summary>
/// 蓝图索引。把 417 条记录预先摊成三张倒排表，让「产物 / 原料 / 工具」三条检索路径都变成 O(1) 查表。
/// 数据量不大（当前实机约 400 条），但倒排的意义在于：即使用户以后把原版蓝图也灌进来（数千条），
/// 匹配逻辑也不用改。
/// </summary>
public sealed class BlueprintIndex
{
    private readonly List<BlueprintRecord> _all;
    private readonly Dictionary<string, List<BlueprintRecord>> _byResult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(BlueprintRecord Blueprint, Ingredient Ingredient)>> _byIngredient = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(BlueprintRecord Blueprint, Ingredient Ingredient)>> _byTool = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownIds = new(StringComparer.OrdinalIgnoreCase);

    public BlueprintIndex(IEnumerable<BlueprintRecord> records)
    {
        _all = records.ToList();

        foreach (var bp in _all)
        {
            // 产物倒排
            if (!string.IsNullOrWhiteSpace(bp.CraftedResult))
            {
                Add(_byResult, bp.CraftedResult, bp);
                _knownIds.Add(bp.CraftedResult);
            }

            // 原料倒排（Gear / Powder / Liquid）
            foreach (var ing in bp.Ingredients)
            {
                if (string.IsNullOrWhiteSpace(ing.ItemId)) continue;
                Add(_byIngredient, ing.ItemId, (bp, ing));
                _knownIds.Add(ing.ItemId);
            }

            // 工具倒排
            foreach (var tool in bp.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.ItemId)) continue;
                Add(_byTool, tool.ItemId, (bp, tool));
                _knownIds.Add(tool.ItemId);
            }
        }
    }

    public IReadOnlyList<BlueprintRecord> All => _all;

    public int Count => _all.Count;

    public int ModCount => _all.Select(r => r.ModName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public int ItemIdCount => _knownIds.Count;

    public int RecipeCount => _all.Count(r => r.SourceKind == BlueprintSourceKind.Recipe);

    public IReadOnlyCollection<string> KnownItemIds => _knownIds;

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<T>();
            map[key] = list;
        }

        list.Add(value);
    }

    // ────────────────────────────────────────────────────────────────────
    // 三条检索路径
    // ────────────────────────────────────────────────────────────────────

    /// <summary>正向：以 query 为「产物」的蓝图。</summary>
    internal IEnumerable<(BlueprintRecord Blueprint, MatchQuality Quality, string MatchedId)> SearchAsProduct(
        string query, MatchMode mode)
    {
        foreach (var (id, list) in _byResult)
        {
            var q = Evaluate(id, query, mode);
            if (q == MatchQuality.None) continue;
            foreach (var bp in list) yield return (bp, q, id);
        }
    }

    /// <summary>反向：以 query 为「原料」（Gear/Powder/Liquid）的蓝图。</summary>
    internal IEnumerable<(BlueprintRecord Blueprint, MatchQuality Quality, Ingredient Ingredient)> SearchAsIngredient(
        string query, MatchMode mode)
    {
        foreach (var (id, list) in _byIngredient)
        {
            var q = Evaluate(id, query, mode);
            if (q == MatchQuality.None) continue;
            foreach (var pair in list) yield return (pair.Blueprint, q, pair.Ingredient);
        }
    }

    /// <summary>反向：以 query 为「工具」（必需/可选/锅具）的蓝图。</summary>
    internal IEnumerable<(BlueprintRecord Blueprint, MatchQuality Quality, Ingredient Ingredient)> SearchAsTool(
        string query, MatchMode mode)
    {
        foreach (var (id, list) in _byTool)
        {
            var q = Evaluate(id, query, mode);
            if (q == MatchQuality.None) continue;
            foreach (var pair in list) yield return (pair.Blueprint, q, pair.Ingredient);
        }
    }

    /// <summary>
    /// 匹配强度判定。
    /// 注意：物品 ID 本身是大小写敏感的符号，但玩家手输时不该被大小写惩罚，
    /// 所以一律用 OrdinalIgnoreCase 比较。
    ///
    ///   Auto      → Exact / Prefix / Contains 都算，靠 MatchQuality 分级排序
    ///   ExactOnly → 只有 Exact
    ///   Fuzzy     → 只有 Contains，且排除 Exact（否则搜 Water 会先撞上精确的那条）
    /// </summary>
    internal static MatchQuality Evaluate(string candidateId, string query, MatchMode mode)
    {
        if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(query))
            return MatchQuality.None;

        var isExact = candidateId.Equals(query, StringComparison.OrdinalIgnoreCase);

        if (mode == MatchMode.Fuzzy)
        {
            if (isExact) return MatchQuality.None;
            return candidateId.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? MatchQuality.Contains
                : MatchQuality.None;
        }

        if (isExact) return MatchQuality.Exact;

        if (mode == MatchMode.ExactOnly) return MatchQuality.None;

        if (candidateId.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return MatchQuality.Prefix;

        if (candidateId.Contains(query, StringComparison.OrdinalIgnoreCase))
            return MatchQuality.Contains;

        return MatchQuality.None;
    }

    /// <summary>
    /// 查无结果时的「你是不是想找」建议。
    /// 先做双向包含匹配，再退化为编辑距离，避免用户把 GEAR_Acorn 打成 Acorn 就一片空白。
    /// </summary>
    public IReadOnlyList<string> SuggestSimilarIds(string query, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || _knownIds.Count == 0) return Array.Empty<string>();

        var q = query.Trim();

        var scored = new List<(string Id, int Score, int Distance)>();

        foreach (var id in _knownIds)
        {
            if (id.Equals(q, StringComparison.OrdinalIgnoreCase)) continue;

            var score = 0;

            // 去掉 GEAR_/LIQUID_/POWDER_ 前缀后比较，玩家往往只记得裸名字
            var bare = StripPrefix(id);
            var bareQuery = StripPrefix(q);

            if (bare.Equals(bareQuery, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (bare.StartsWith(bareQuery, StringComparison.OrdinalIgnoreCase)) score += 60;
            else if (bare.Contains(bareQuery, StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (bareQuery.Contains(bare, StringComparison.OrdinalIgnoreCase)) score += 30;

            var distance = Levenshtein(bare.ToLowerInvariant(), bareQuery.ToLowerInvariant());
            if (distance <= 3) score += Math.Max(0, 25 - distance * 5);

            if (score > 0)
                scored.Add((id, score, distance));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Id)
            .ToList();
    }

    private static string StripPrefix(string id)
    {
        foreach (var prefix in new[] { "GEAR_", "LIQUID_", "POWDER_", "TOOL_", "REC_" })
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return id.Substring(prefix.Length);
        }

        return id;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}

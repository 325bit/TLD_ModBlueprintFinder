using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Indexing;

/// <summary>Match strength. Lower is more precise; used for ordering and for coloring rows.</summary>
public enum MatchQuality
{
    None = 0,
    /// <summary>Identical (case-insensitive).</summary>
    Exact = 1,
    /// <summary>Starts with the query.</summary>
    Prefix = 2,
    /// <summary>Contains the query.</summary>
    Contains = 3
}

/// <summary>Match modes offered to the user. The three are disjoint, so none behaves like another.</summary>
public enum MatchMode
{
    /// <summary>Auto: exact &gt; prefix &gt; contains, all listed and ordered by precision.</summary>
    Auto,
    /// <summary>Identical IDs only.</summary>
    ExactOnly,
    /// <summary>Only "contains", exact excluded - used to sweep a family of IDs.</summary>
    Fuzzy
}

/// <summary>
/// Blueprint index: one flat list plus three inverted tables (product / ingredient / tool), turning each
/// search path into a dictionary lookup. The inverted tables matter for growth, not for today's few
/// hundred records: feeding vanilla blueprints in later needs no change to the matching code.
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
            // Product inverted table
            if (!string.IsNullOrWhiteSpace(bp.CraftedResult))
            {
                Add(_byResult, bp.CraftedResult, bp);
                _knownIds.Add(bp.CraftedResult);
            }

            // Ingredient inverted table (gear / powder / liquid)
            foreach (var ing in bp.Ingredients)
            {
                if (string.IsNullOrWhiteSpace(ing.ItemId)) continue;
                Add(_byIngredient, ing.ItemId, (bp, ing));
                _knownIds.Add(ing.ItemId);
            }

            // Tool inverted table
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

    // Three search paths. All accept kindFilter: a blueprints/ blueprint and a recipes/ recipe are the
    // same model, but players ask different questions - "what can this meat cook into" is not "what tools
    // can this meat make". A null filter means both kinds.

    /// <summary>Forward: blueprints whose product is query.</summary>
    internal IEnumerable<(BlueprintRecord Blueprint, MatchQuality Quality, string MatchedId)> SearchAsProduct(
        string query, MatchMode mode, BlueprintSourceKind? kindFilter = null)
    {
        foreach (var (id, list) in _byResult)
        {
            var q = Evaluate(id, query, mode);
            if (q == MatchQuality.None) continue;
            foreach (var bp in list)
            {
                if (kindFilter.HasValue && bp.SourceKind != kindFilter.Value) continue;
                yield return (bp, q, id);
            }
        }
    }

    /// <summary>Reverse: blueprints consuming query as gear/powder/liquid.</summary>
    internal IEnumerable<(BlueprintRecord Blueprint, MatchQuality Quality, Ingredient Ingredient)> SearchAsIngredient(
        string query, MatchMode mode, BlueprintSourceKind? kindFilter = null)
    {
        foreach (var (id, list) in _byIngredient)
        {
            var q = Evaluate(id, query, mode);
            if (q == MatchQuality.None) continue;
            foreach (var pair in list)
            {
                if (kindFilter.HasValue && pair.Blueprint.SourceKind != kindFilter.Value) continue;
                yield return (pair.Blueprint, q, pair.Ingredient);
            }
        }
    }

    /// <summary>Reverse: blueprints needing or accepting query as a tool (required / optional / pot).</summary>
    internal IEnumerable<(BlueprintRecord Blueprint, MatchQuality Quality, Ingredient Ingredient)> SearchAsTool(
        string query, MatchMode mode, BlueprintSourceKind? kindFilter = null)
    {
        foreach (var (id, list) in _byTool)
        {
            var q = Evaluate(id, query, mode);
            if (q == MatchQuality.None) continue;
            foreach (var pair in list)
            {
                if (kindFilter.HasValue && pair.Blueprint.SourceKind != kindFilter.Value) continue;
                yield return (pair.Blueprint, q, pair.Ingredient);
            }
        }
    }

    /// <summary>
    /// Match strength. Item IDs are case-sensitive symbols, but a hand-typed query must not be punished for
    /// casing, so every comparison is OrdinalIgnoreCase.
    /// Fuzzy excludes exact matches, otherwise searching "Water" hits the exact row first and hides the family.
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
    /// "Did you mean" suggestions when nothing matched: bidirectional containment first, then edit distance,
    /// so typing "Acorn" for "GEAR_Acorn" does not return a blank screen.
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

            // Compare bare names too: players usually remember the name without the prefix.
            var bare = ItemIds.StripPrefix(id);
            var bareQuery = ItemIds.StripPrefix(q);

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

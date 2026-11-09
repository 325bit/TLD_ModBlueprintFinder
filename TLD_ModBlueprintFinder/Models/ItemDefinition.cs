namespace TLD_ModBlueprintFinder.Models;

/// <summary>
/// One item definition from auto-mapped/*.json: the item's own component parameters
/// (weight, decay, stack size, cookability), unrelated to blueprints/ which describe how it is made.
/// The file name is the internal name; the prefab ID adds a GEAR_/LIQUID_/POWDER_ prefix that the
/// file never stores, so the ID is inferred from recipes referencing it.
/// See <see cref="Indexing.ItemDefinitionIndex"/> and docs/DEVELOPMENT.md §2.4.
/// </summary>
public sealed class ItemDefinition
{
    /// <summary>Owning mod (modcomponent file name or extracted folder name).</summary>
    public string ModName { get; init; } = string.Empty;

    /// <summary>Full path of the container (.modcomponent file or extracted folder).</summary>
    public string ContainerPath { get; init; } = string.Empty;

    /// <summary>Entry path inside the container, e.g. auto-mapped/sugara.json.</summary>
    public string EntryPath { get; init; } = string.Empty;

    /// <summary>File name without extension: the item's internal name.</summary>
    public string Stem { get; init; } = string.Empty;

    /// <summary>Top-level component names, e.g. ModFoodComponent / ModStackableBehaviour.</summary>
    public IReadOnlyList<string> Components { get; init; } = Array.Empty<string>();

    /// <summary>Raw JSON text, preserved as written (spacing and trailing commas included).</summary>
    public string JsonText { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>Explains a failed or degraded parse; null when parsing succeeded.</summary>
    public string? ParseWarning { get; init; }

    public override string ToString() => $"{ModName} :: {EntryPath}";
}

/// <summary>Item ID conventions. Shared by blueprints and definitions, so the rules live in one place.</summary>
public static class ItemIds
{
    private static readonly string[] Prefixes = { "GEAR_", "LIQUID_", "POWDER_", "TOOL_", "REC_" };

    /// <summary>Strips the GEAR_ / LIQUID_ / POWDER_ prefix to get the bare name.</summary>
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

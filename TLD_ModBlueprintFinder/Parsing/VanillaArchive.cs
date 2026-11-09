using System.IO.Compression;
using System.Text.Json;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>The game's own recipes, as exported by <c>tools/export_vanilla.py</c>.</summary>
public sealed class VanillaExport
{
    public IReadOnlyList<BlueprintRecord> Records { get; init; } = Array.Empty<BlueprintRecord>();

    /// <summary>Game build the snapshot was taken from, e.g. "2.55 181032 S".</summary>
    public string GameVersion { get; init; } = string.Empty;

    public string ExportedAt { get; init; } = string.Empty;
    public int BlueprintCount { get; init; }
    public int RecipeCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool IsAvailable => Records.Count > 0;

    public string Summary => $"原版 {BlueprintCount} 蓝图 / {RecipeCount} 食谱（游戏 {GameVersion}）";
}

/// <summary>
/// Ships the vanilla snapshot inside the exe (<c>vanilla.zip</c>, ~63 KB) and parses it with the same
/// <see cref="BlueprintParser"/> the modcomponent files go through. Two reasons, not one:
///   - one parser means the two sources cannot drift apart in field handling;
///   - the JSON shape is ModComponent's, so the export script only has to emit that shape.
/// These records are read-only by construction: <see cref="BlueprintRecord.IsVanilla"/> makes every write path
/// refuse them, because there is no container file behind them to write to.
/// </summary>
public static class VanillaArchive
{
    /// <summary>Name shown in the "source" column for these rows.</summary>
    public const string DisplayName = "游戏原版";

    private const string ResourceName = "vanilla.zip";
    private const string Prefix = "vanilla/";

    private static VanillaExport? _cached;

    /// <summary>Parses once per process; a 63 KB zip is cheap but the parse is not free.</summary>
    public static VanillaExport Load() => _cached ??= LoadCore();

    private static VanillaExport LoadCore()
    {
        var errors = new List<string>();
        var records = new List<BlueprintRecord>();
        var version = string.Empty;
        var exportedAt = string.Empty;
        var blueprintCount = 0;
        var recipeCount = 0;

        try
        {
            using var raw = typeof(VanillaArchive).Assembly.GetManifestResourceStream(ResourceName);
            if (raw is null)
            {
                errors.Add($"内嵌数据 {ResourceName} 不存在（发布时未打包？）。");
                return new VanillaExport { Errors = errors };
            }

            using var zip = new ZipArchive(raw, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                using var buffer = new MemoryStream();
                using (var stream = entry.Open()) stream.CopyTo(buffer);

                var relative = path.Substring(Prefix.Length);

                if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    ReadManifest(buffer.ToArray(), ref version, ref exportedAt, ref blueprintCount, ref recipeCount, errors);
                    continue;
                }

                var kind = relative.StartsWith("recipes/", StringComparison.OrdinalIgnoreCase)
                    ? BlueprintSourceKind.Recipe
                    : BlueprintSourceKind.Blueprint;

                // ContainerPath stays empty: vanilla records have no container, and every write path in the UI
                // treats an empty container as "not editable".
                var parsed = BlueprintParser.ParseMany(DisplayName, string.Empty, relative, kind, buffer.ToArray(), errors, isVanilla: true);
                records.AddRange(parsed);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{ResourceName} 读取失败：{ex.Message}");
        }

        return new VanillaExport
        {
            Records = records,
            GameVersion = version,
            ExportedAt = exportedAt,
            BlueprintCount = blueprintCount > 0 ? blueprintCount : records.Count(r => r.SourceKind == BlueprintSourceKind.Blueprint),
            RecipeCount = recipeCount > 0 ? recipeCount : records.Count(r => r.SourceKind == BlueprintSourceKind.Recipe),
            Errors = errors
        };
    }

    private static void ReadManifest(
        byte[] bytes, ref string version, ref string exportedAt,
        ref int blueprintCount, ref int recipeCount, List<string> errors)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.TryGetProperty("gameVersion", out var v)) version = v.GetString() ?? string.Empty;
            if (root.TryGetProperty("exportedUtc", out var e)) exportedAt = e.GetString() ?? string.Empty;
            if (root.TryGetProperty("blueprints", out var b) && b.TryGetInt32(out var bc)) blueprintCount = bc;
            if (root.TryGetProperty("recipes", out var r) && r.TryGetInt32(out var rc)) recipeCount = rc;
        }
        catch (Exception ex)
        {
            errors.Add($"manifest.json 解析失败：{ex.Message}");
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>Scan options.</summary>
public sealed class ScanOptions
{
    /// <summary>Also look in subfolders (some players sort mods into folders).</summary>
    public bool IncludeSubFolders { get; set; } = true;

    /// <summary>Scan extracted-folder mods (Mods/&lt;ModName&gt;/blueprints/*.json).</summary>
    public bool IncludeExtractedFolders { get; set; } = true;

    /// <summary>
    /// Scan Mods/_ModComponentTemp. Off by default: ModComponent unpacks a copy of every .modcomponent
    /// there, so scanning it double-counts the same blueprints.
    /// </summary>
    public bool IncludeModComponentTemp { get; set; }

    /// <summary>Per-entry size cap, to survive malformed or zip-bomb containers.</summary>
    public long MaxEntryBytes { get; set; } = 16 * 1024 * 1024;
}

/// <summary>Scan progress report.</summary>
public readonly record struct ScanProgress(string Message, int Done, int Total);

/// <summary>
/// Read-only modcomponent scanner.
/// A .modcomponent is a ZIP; entries are enumerated in memory with ZipArchive, so the Mods folder is
/// never written to. Path resolution lives here too: the input may be a game root, a Mods folder or a
/// single .modcomponent.
/// </summary>
public sealed class ModScanner
{
    private static readonly string[] ExcludedSegments =
    {
        "auto-mapped", "auto_mapped", "gear-spawns", "gear_spawns",
        "localizations", "localization", "bundle", "bundles"
    };

    /// <summary>
    /// Scans the given path: a game root, a Mods folder, or a single .modcomponent/.zip file.
    /// </summary>
    public ScanReport Scan(
        string inputPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        var report = new ScanReport();
        var sw = Stopwatch.StartNew();

        var resolved = ResolveInput(inputPath, options);
        report.ResolvedModRoot = resolved.DisplayRoot;

        if (resolved.Containers.Count == 0)
        {
            report.Errors.Add($"在 {resolved.DisplayRoot} 下没有找到任何 .modcomponent 文件或解压形式的 mod 目录。");
            report.Elapsed = sw.Elapsed;
            return report;
        }

        var total = resolved.Containers.Count;
        var done = 0;

        foreach (var container in resolved.Containers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress($"正在解析 {container.Name} …", done, total));

            var summary = new ContainerSummary
            {
                Name = container.Name,
                Path = container.Path,
                IsFolder = container.IsFolder
            };

            try
            {
                if (container.IsFolder)
                    ScanFolder(container.Path, container.Name, summary, report, options, cancellationToken);
                else
                    ScanZip(container.Path, container.Name, summary, report, options, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.LoadFailed = true;
                summary.FailureReason = ex.Message;
                report.Errors.Add($"{container.Name} → 无法打开容器：{ex.Message}");
            }

            report.Containers.Add(summary);
            done++;
        }

        progress?.Report(new ScanProgress("解析完成", total, total));
        report.Elapsed = sw.Elapsed;
        return report;
    }

    // Path resolution: normalize any reasonable input into a list of containers

    internal sealed record ContainerSpec(string Name, string Path, bool IsFolder);

    internal sealed record ResolvedInput(string DisplayRoot, List<ContainerSpec> Containers);

    internal static ResolvedInput ResolveInput(string rawInput, ScanOptions? options = null)
    {
        options ??= new ScanOptions();
        var input = (rawInput ?? string.Empty).Trim().Trim('"').Trim();
        if (input.Length == 0)
            throw new ArgumentException("请先填写游戏路径（例如 F:\\SteamLibrary\\steamapps\\common\\TheLongDark）。");

        // 1. The user pointed straight at a modcomponent / zip file.
        if (File.Exists(input))
        {
            var ext = Path.GetExtension(input);
            if (ext.Equals(".modcomponent", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(input);
                return new ResolvedInput(
                    Path.GetDirectoryName(Path.GetFullPath(input)) ?? input,
                    new List<ContainerSpec> { new(name, Path.GetFullPath(input), false) });
            }

            throw new ArgumentException($"不支持的文件类型『{ext}』，请选择游戏根目录、Mods 目录或 .modcomponent 文件。");
        }

        if (!Directory.Exists(input))
            throw new ArgumentException($"路径不存在：{input}");

        var full = Path.GetFullPath(input);

        // 2. The folder is Mods itself (holds modcomponents or blueprints/ directly).
        var containers = CollectContainers(full, options);
        if (containers.Count > 0)
            return new ResolvedInput(full, containers);

        // 3. The user gave the game root: look for Mods underneath.
        var modsSub = Path.Combine(full, "Mods");
        if (Directory.Exists(modsSub))
        {
            var subContainers = CollectContainers(modsSub, options);
            if (subContainers.Count > 0)
                return new ResolvedInput(modsSub, subContainers);

            return new ResolvedInput(modsSub, new List<ContainerSpec>());
        }

        return new ResolvedInput(full, new List<ContainerSpec>());
    }

    private static List<ContainerSpec> CollectContainers(string modsRoot, ScanOptions options)
    {
        var list = new List<ContainerSpec>();
        if (!Directory.Exists(modsRoot)) return list;

        var search = options.IncludeSubFolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // .modcomponent / .zip containers
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(modsRoot, "*", search);
        }
        catch (UnauthorizedAccessException)
        {
            files = Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            files = Array.Empty<string>();
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".modcomponent", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            // Accept only real .modcomponent files; skip backups like .modcomponent.bak.20261020
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(new ContainerSpec(Path.GetFileNameWithoutExtension(file), file, false));
        }

        if (!options.IncludeExtractedFolders) return list;

        // Extracted folders: <dir>/blueprints/*.json
        foreach (var dir in SafeEnumerateDirectories(modsRoot, search))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.Equals("ModData", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("~ModData", StringComparison.OrdinalIgnoreCase))
                continue;

            var isTemp = dirName.Equals("_ModComponentTemp", StringComparison.OrdinalIgnoreCase);
            if (isTemp) continue;   // _ModComponentTemp is not a mod itself; the mods sit inside it

            if (!HasBlueprintFolder(dir)) continue;

            // Skip the folder when a .modcomponent of the same name was collected, or the mod counts twice
            if (list.Any(c => !c.IsFolder && c.Name.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                continue;

            list.Add(new ContainerSpec(dirName, dir, true));
        }

        // _ModComponentTemp is handled separately and is off by default
        if (options.IncludeModComponentTemp)
        {
            var tempRoot = Path.Combine(modsRoot, "_ModComponentTemp");
            if (Directory.Exists(tempRoot))
            {
                foreach (var dir in SafeEnumerateDirectories(tempRoot, SearchOption.TopDirectoryOnly))
                {
                    if (!HasBlueprintFolder(dir)) continue;
                    var dirName = Path.GetFileName(dir);
                    if (list.Any(c => !c.IsFolder && c.Name.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    list.Add(new ContainerSpec(dirName, dir, true));
                }
            }
        }

        return list
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, SearchOption search)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", search);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool HasBlueprintFolder(string dir)
    {
        foreach (var sub in new[] { "blueprints", "Blueprints", "recipes", "Recipes" })
        {
            if (Directory.Exists(Path.Combine(dir, sub))) return true;
        }
        return false;
    }

    // Container readers

    private static void ScanZip(
        string zipPath,
        string modName,
        ContainerSummary summary,
        ScanReport report,
        ScanOptions options,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry

            var isDefinition = IsDefinitionEntry(entry.FullName);
            var kind = isDefinition ? null : ClassifyEntry(entry.FullName);
            if (!isDefinition && kind is null) continue;

            report.JsonEntriesSeen++;

            if (entry.Length > options.MaxEntryBytes)
            {
                report.Errors.Add($"{modName} :: {entry.FullName} → 条目过大（{entry.Length / 1024} KB），已跳过");
                report.JsonEntriesSkipped++;
                summary.EntryErrorCount++;
                continue;
            }

            byte[] bytes;
            try
            {
                using var stream = entry.Open();
                using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{modName} :: {entry.FullName} → 读取条目失败：{ex.Message}");
                report.JsonEntriesSkipped++;
                summary.EntryErrorCount++;
                continue;
            }

            if (isDefinition)
            {
                AddDefinition(modName, zipPath, entry.FullName, bytes, summary, report);
                continue;
            }

            var before = report.Records.Count;
            var parsed = BlueprintParser.ParseMany(modName, zipPath, entry.FullName, kind!.Value, bytes, report.Errors);
            report.Records.AddRange(parsed);

            if (report.Records.Count > before)
            {
                report.JsonEntriesParsed++;
                summary.BlueprintCount += report.Records.Count - before;
            }
            else
            {
                report.JsonEntriesSkipped++;
            }
        }
    }

    private static void ScanFolder(
        string dir,
        string modName,
        ContainerSummary summary,
        ScanReport report,
        ScanOptions options,
        CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            report.Errors.Add($"{modName} → 枚举目录失败：{ex.Message}");
            summary.EntryErrorCount++;
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var isDefinition = IsDefinitionEntry(relative);
            var kind = isDefinition ? null : ClassifyEntry(relative);
            if (!isDefinition && kind is null) continue;

            report.JsonEntriesSeen++;

            var before = report.Records.Count;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > options.MaxEntryBytes)
                {
                    report.Errors.Add($"{modName} :: {relative} → 文件过大，已跳过");
                    report.JsonEntriesSkipped++;
                    summary.EntryErrorCount++;
                    continue;
                }

                var bytes = File.ReadAllBytes(file);   // read-only

                if (isDefinition)
                {
                    AddDefinition(modName, dir, relative, bytes, summary, report);
                    continue;
                }

                var parsed = BlueprintParser.ParseMany(modName, dir, relative, kind!.Value, bytes, report.Errors);
                report.Records.AddRange(parsed);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{modName} :: {relative} → 读取失败：{ex.Message}");
                report.JsonEntriesSkipped++;
                summary.EntryErrorCount++;
                continue;
            }

            if (report.Records.Count > before)
            {
                report.JsonEntriesParsed++;
                summary.BlueprintCount += report.Records.Count - before;
            }
            else
            {
                report.JsonEntriesSkipped++;
            }
        }
    }

    /// <summary>Collects one auto-mapped JSON into the report, keeping counts and errors in sync.</summary>
    private static void AddDefinition(
        string modName,
        string containerPath,
        string entryPath,
        byte[] bytes,
        ContainerSummary summary,
        ScanReport report)
    {
        var outcome = ItemDefinitionParser.Parse(modName, containerPath, entryPath, bytes);

        if (outcome.Definition is null)
        {
            report.Errors.Add($"{modName} :: {entryPath} → {outcome.SkipReason}");
            report.JsonEntriesSkipped++;
            summary.EntryErrorCount++;
            return;
        }

        report.Definitions.Add(outcome.Definition);
        report.JsonEntriesParsed++;
        summary.DefinitionCount++;
    }

    /// <summary>
    /// Decides whether an entry is an item definition under auto-mapped/.
    /// Only "first path segment is auto-mapped" counts: that is where ModComponent exports them, and a
    /// same-named JSON elsewhere would be misread.
    /// </summary>
    internal static bool IsDefinitionEntry(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return false;
        if (!entryPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        var slash = normalized.IndexOf('/');

        // Must live in a subfolder: auto-mapped/x.json or auto-mapped/collectibles/x.json
        if (slash <= 0) return false;

        var first = normalized.Substring(0, slash);
        return first.Equals("auto-mapped", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("auto_mapped", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides whether an entry belongs to blueprints/ or recipes/.
    /// Rule: a directory segment named blueprints/recipes plus a .json extension; anything under
    /// auto-mapped, gear-spawns, localizations or bundle is excluded.
    /// </summary>
    internal static BlueprintSourceKind? ClassifyEntry(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return null;
        if (!entryPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = entryPath.Split('/', '\\');

        // The file segment itself must be checked too (e.g. auto-mapped/blueprints.json)
        foreach (var seg in segments)
        {
            foreach (var excluded in ExcludedSegments)
            {
                if (seg.Equals(excluded, StringComparison.OrdinalIgnoreCase)) return null;
            }
        }

        var isBlueprint = false;
        var isRecipe = false;

        // Directory segments only; the last segment is the file name.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (seg.Equals("blueprints", StringComparison.OrdinalIgnoreCase) ||
                seg.Equals("blueprint", StringComparison.OrdinalIgnoreCase))
                isBlueprint = true;
            else if (seg.Equals("recipes", StringComparison.OrdinalIgnoreCase) ||
                     seg.Equals("recipe", StringComparison.OrdinalIgnoreCase))
                isRecipe = true;
        }

        if (isRecipe) return BlueprintSourceKind.Recipe;
        if (isBlueprint) return BlueprintSourceKind.Blueprint;
        return null;
    }
}

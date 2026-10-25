using System.Diagnostics;
using System.IO.Compression;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Parsing;

/// <summary>扫描选项。</summary>
public sealed class ScanOptions
{
    /// <summary>是否向 Mods 的子目录继续找 modcomponent（部分玩家会把 mod 分文件夹放）。</summary>
    public bool IncludeSubFolders { get; set; } = true;

    /// <summary>是否扫描「解压目录形式」的 component（Mods/&lt;ModName&gt;/blueprints/*.json）。</summary>
    public bool IncludeExtractedFolders { get; set; } = true;

    /// <summary>
    /// 是否扫描 Mods/_ModComponentTemp。
    /// 默认关闭：ModComponent 加载 .modcomponent 时会在这里解压一份副本，
    /// 扫了会和 zip 里的蓝图重复计数。
    /// </summary>
    public bool IncludeModComponentTemp { get; set; }

    /// <summary>单个 JSON 条目的大小上限，防御畸形/炸弹包。</summary>
    public long MaxEntryBytes { get; set; } = 16 * 1024 * 1024;
}

/// <summary>扫描进度回报。</summary>
public readonly record struct ScanProgress(string Message, int Done, int Total);

/// <summary>
/// modcomponent 扫描器 —— 只读。
///
/// .modcomponent 的本质是一个 ZIP 容器（与参考实现 ModComponentExtractor 的结论一致：
/// 它用 SharpZipLib 的 FastZip 把 .modcomponent 解压成目录来编辑，反之压回 zip）。
/// 本工具不解压到磁盘，而是用 System.IO.Compression.ZipArchive 在内存里枚举条目，
/// 因此对 Mods 目录 **零写入**。
/// </summary>
public sealed class ModScanner
{
    private static readonly string[] ExcludedSegments =
    {
        "auto-mapped", "auto_mapped", "gear-spawns", "gear_spawns",
        "localizations", "localization", "bundle", "bundles"
    };

    /// <summary>
    /// 扫描用户给定的路径。
    /// 路径可以是：游戏根目录、Mods 目录、单个 .modcomponent/.zip 文件。
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

    // ────────────────────────────────────────────────────────────────────
    // 路径解析：把用户输入的任意合理路径归一成「容器列表」
    // ────────────────────────────────────────────────────────────────────

    internal sealed record ContainerSpec(string Name, string Path, bool IsFolder);

    internal sealed record ResolvedInput(string DisplayRoot, List<ContainerSpec> Containers);

    internal static ResolvedInput ResolveInput(string rawInput, ScanOptions? options = null)
    {
        options ??= new ScanOptions();
        var input = (rawInput ?? string.Empty).Trim().Trim('"').Trim();
        if (input.Length == 0)
            throw new ArgumentException("请先填写游戏路径（例如 F:\\SteamLibrary\\steamapps\\common\\TheLongDark）。");

        // ① 用户直接给了一个 modcomponent / zip 文件
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

        // ② 目录本身就是 Mods（直接含 modcomponent 文件，或含 blueprints/ 子目录）
        var containers = CollectContainers(full, options);
        if (containers.Count > 0)
            return new ResolvedInput(full, containers);

        // ③ 用户给的是游戏根目录：往下找 Mods
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

        // .modcomponent / .zip 容器
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

            // 只认严格以 .modcomponent 结尾的，排除 .modcomponent.bak.20261020 这类备份
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(new ContainerSpec(Path.GetFileNameWithoutExtension(file), file, false));
        }

        if (!options.IncludeExtractedFolders) return list;

        // 解压目录形式：<dir>/blueprints/*.json
        foreach (var dir in SafeEnumerateDirectories(modsRoot, search))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.Equals("ModData", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("~ModData", StringComparison.OrdinalIgnoreCase))
                continue;

            var isTemp = dirName.Equals("_ModComponentTemp", StringComparison.OrdinalIgnoreCase);
            if (isTemp) continue;   // _ModComponentTemp 自身不是 mod，里面才是

            if (!HasBlueprintFolder(dir)) continue;

            // 若同名的 .modcomponent 已经收集过，跳过解压目录，避免同一 mod 被算两次
            if (list.Any(c => !c.IsFolder && c.Name.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                continue;

            list.Add(new ContainerSpec(dirName, dir, true));
        }

        // 单独处理 _ModComponentTemp（默认关闭）
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

    // ────────────────────────────────────────────────────────────────────
    // 容器读取
    // ────────────────────────────────────────────────────────────────────

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

            if (string.IsNullOrEmpty(entry.Name)) continue;   // 目录条目
            var kind = ClassifyEntry(entry.FullName);
            if (kind is null) continue;

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

            var before = report.Records.Count;
            var parsed = BlueprintParser.ParseMany(modName, zipPath, entry.FullName, kind.Value, bytes, report.Errors);
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
            var kind = ClassifyEntry(relative);
            if (kind is null) continue;

            report.JsonEntriesSeen++;

            int before = report.Records.Count;
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

                var bytes = File.ReadAllBytes(file);   // 只读，不写
                var parsed = BlueprintParser.ParseMany(modName, dir, relative, kind.Value, bytes, report.Errors);
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

    /// <summary>
    /// 判断一个条目路径是否属于蓝图/食谱。
    /// 规则：路径中出现名为 blueprints/recipes 的目录段，且以 .json 结尾；
    /// 位于 auto-mapped / gear-spawns / localizations / bundle 下的内容一律排除。
    /// </summary>
    internal static BlueprintSourceKind? ClassifyEntry(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return null;
        if (!entryPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = entryPath.Split('/', '\\');

        // 文件本身那一段也要检查排除（例如 auto-mapped/blueprints.json）
        foreach (var seg in segments)
        {
            foreach (var excluded in ExcludedSegments)
            {
                if (seg.Equals(excluded, StringComparison.OrdinalIgnoreCase)) return null;
            }
        }

        var isBlueprint = false;
        var isRecipe = false;

        // 只检查目录段（最后一段是文件名）
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

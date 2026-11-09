using System.Text;
using TLD_ModBlueprintFinder.Export;
using TLD_ModBlueprintFinder.Indexing;
using TLD_ModBlueprintFinder.Models;
using TLD_ModBlueprintFinder.Parsing;

namespace TLD_ModBlueprintFinder;

/// <summary>
/// Headless mode, for two purposes: scripted queries (archiving, batch verification) and exercising the
/// parser and search logic without a GUI.
///   TLD_ModBlueprintFinder.exe --cli --path "&lt;game root or Mods&gt;" --item GEAR_AcornShelled
/// Options: --mode auto|exact|fuzzy  --no-merge  --scan-only  --csv out.csv
/// </summary>
internal static class Cli
{
    public static int Run(string[] args)
    {
        // The console code page may be GBK, and a WinExe process has no console handle at all, which makes
        // Console.OutputEncoding throw. Fall back to a UTF-8 writer so redirected output stays readable.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
                {
                    AutoFlush = true
                });
            }
            catch
            {
                // No standard output at all: give up on encoding; the results themselves are unaffected
            }
        }

        var options = ParseArgs(args);

        if (!options.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("缺少 --path 参数。");
            PrintUsage();
            return 2;
        }

        var scanOptions = new ScanOptions
        {
            IncludeSubFolders = !options.ContainsKey("no-subfolders"),
            IncludeExtractedFolders = !options.ContainsKey("no-folders"),
            IncludeModComponentTemp = options.ContainsKey("temp")
        };

        ScanReport report;
        try
        {
            report = new ModScanner().Scan(path, scanOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"扫描失败：{ex.Message}");
            return 3;
        }

        PrintScanSummary(report);

        if (report.Records.Count == 0 && report.Definitions.Count == 0)
        {
            Console.Error.WriteLine("没有解析到任何蓝图或物品定义。");
            return 4;
        }

        if (options.ContainsKey("scan-only"))
            return 0;

        if (!options.TryGetValue("item", out var item) || string.IsNullOrWhiteSpace(item))
        {
            Console.Error.WriteLine("缺少 --item 参数（要查询的物品 ID）。");
            return 2;
        }

        var mode = MatchMode.Auto;
        if (options.TryGetValue("mode", out var modeText))
        {
            mode = modeText.ToLowerInvariant() switch
            {
                "exact" => MatchMode.ExactOnly,
                "fuzzy" => MatchMode.Fuzzy,
                _ => MatchMode.Auto
            };
        }

        var index = new BlueprintIndex(report.Records);
        var definitions = new ItemDefinitionIndex(report.Definitions, report.Records);
        var engine = new BlueprintSearchEngine(index, definitions);
        var outcome = engine.Search(item, mode, mergeDuplicates: !options.ContainsKey("no-merge"));

        PrintOutcome(outcome);

        if (options.TryGetValue("csv", out var csvPath) && !string.IsNullOrWhiteSpace(csvPath))
        {
            CsvExporter.Save(csvPath, CsvExporter.Build(outcome));
            Console.WriteLine($"CSV 已写入：{csvPath}");
        }

        return outcome.IsEmpty ? 1 : 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;

            var key = a.Substring(2);
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                map[key.Substring(0, eq)] = key.Substring(eq + 1);
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[i + 1];
                i++;
            }
            else
            {
                map[key] = "true";
            }
        }

        return map;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TLD 配方（蓝图）检索工具 · 命令行模式");
        Console.WriteLine("  --path <游戏根目录|Mods 目录|单个 modcomponent>");
        Console.WriteLine("  --item <物品 ID>");
        Console.WriteLine("  --mode auto|exact|fuzzy");
        Console.WriteLine("  --no-merge       不合并同名蓝图");
        Console.WriteLine("  --no-subfolders  不递归子目录");
        Console.WriteLine("  --no-folders     跳过解压目录形式");
        Console.WriteLine("  --temp           扫描 _ModComponentTemp");
        Console.WriteLine("  --scan-only      只扫描并打印统计");
        Console.WriteLine("  --csv <路径>     导出结果 CSV");
    }

    private static void PrintScanSummary(ScanReport report)
    {
        Console.WriteLine("══════════ 扫描结果 ══════════");
        Console.WriteLine($"Mods 根目录 : {report.ResolvedModRoot}");
        Console.WriteLine($"容器数量    : {report.Containers.Count}（成功 {report.Containers.Count(c => !c.LoadFailed)}，失败 {report.Containers.Count(c => c.LoadFailed)}）");
        Console.WriteLine($"JSON 条目   : 共 {report.JsonEntriesSeen}，解析 {report.JsonEntriesParsed}，跳过 {report.JsonEntriesSkipped}");
        Console.WriteLine($"蓝图记录    : {report.Records.Count}（其中食谱 {report.Records.Count(r => r.SourceKind == BlueprintSourceKind.Recipe)}）");
        Console.WriteLine($"物品定义    : {report.Definitions.Count} 份（auto-mapped）");
        Console.WriteLine($"涉及物品 ID : {report.Records.SelectMany(r => r.Ingredients.Select(i => i.ItemId)).Concat(report.Records.Select(r => r.CraftedResult)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
        Console.WriteLine($"解析警告    : {report.WarningCount}");
        Console.WriteLine($"耗时        : {report.Elapsed.TotalMilliseconds:0} ms");

        if (report.Errors.Count > 0)
        {
            Console.WriteLine($"── 错误 {report.Errors.Count} 条 ──");
            foreach (var e in report.Errors.Take(15)) Console.WriteLine("  · " + e);
            if (report.Errors.Count > 15) Console.WriteLine($"  …（另有 {report.Errors.Count - 15} 条）");
        }

        var warned = report.Records.Where(r => r.Warnings.Count > 0).ToList();
        if (warned.Count > 0)
        {
            Console.WriteLine($"── 字段警告（{warned.Sum(r => r.Warnings.Count)} 条，涉及 {warned.Count} 份蓝图）──");
            foreach (var r in warned.Take(10))
                Console.WriteLine($"  · {r.ModName} :: {r.EntryPath} → {string.Join("；", r.Warnings)}");
        }

        Console.WriteLine();
    }

    private static void PrintOutcome(SearchOutcome outcome)
    {
        Console.WriteLine($"══════════ 查询：{outcome.Query} ══════════");

        PrintSection("① 作为产物（蓝图 blueprints/）", outcome.AsProduct);
        PrintSection("② 作为原料（蓝图 blueprints/）", outcome.AsIngredient);
        PrintSection("③ 作为工具（蓝图 + 食谱）", outcome.AsTool);
        PrintSection("④ 作为产物（食谱 recipes/）", outcome.AsRecipeProduct);
        PrintSection("⑤ 作为原料（食谱 recipes/）", outcome.AsRecipeIngredient);
        PrintDefinitions(outcome.Definitions);

        if (outcome.IsEmpty)
        {
            Console.WriteLine("未找到任何匹配。");
            foreach (var note in outcome.Notes) Console.WriteLine("  " + note);
            foreach (var s in outcome.Suggestions) Console.WriteLine("  · " + s);
        }

        Console.WriteLine($"合计命中：配方 {outcome.TotalHits} 条 ｜ 物品定义 {outcome.Definitions.Count} 份");
    }

    private static void PrintDefinitions(List<DefinitionRow> rows)
    {
        Console.WriteLine();
        Console.WriteLine($"⑥ 物品定义（auto-mapped 里的 JSON）  ——  {rows.Count} 份");

        if (rows.Count == 0)
        {
            Console.WriteLine("  (无)");
            return;
        }

        foreach (var row in rows)
        {
            Console.WriteLine($"  ▸ {row.ItemId}   [{row.ConfidenceText}]");
            Console.WriteLine($"     文件: {row.EntryPath}   来源: {row.ModName}   大小: {row.SizeText}");
            Console.WriteLine($"     组件: {row.ComponentsText}");

            if (!string.IsNullOrWhiteSpace(row.Definition.ParseWarning))
                Console.WriteLine($"     ⚠ {row.Definition.ParseWarning}");

            Console.WriteLine("     ──── JSON 定义 ────");
            foreach (var line in row.Definition.JsonText.Replace("\r\n", "\n").Split('\n'))
                Console.WriteLine("     " + line);
        }
    }

    private static void PrintSection(string title, List<SearchRow> rows)
    {
        Console.WriteLine();
        Console.WriteLine($"{title}  ——  {rows.Count} 条");

        if (rows.Count == 0)
        {
            Console.WriteLine("  (无)");
            return;
        }

        foreach (var row in rows)
        {
            var p = row.Primary;
            Console.WriteLine($"  ▸ {row.BlueprintName}  →  {p.ResultText}   [{row.MatchNote}]  地点={row.LocationText}  耗时={row.DurationText}");
            Console.WriteLine($"     原料: {row.IngredientsText}");
            if (!string.IsNullOrEmpty(row.ToolsText)) Console.WriteLine($"     工具: {row.ToolsText}");
            if (!string.IsNullOrEmpty(row.ExtraText)) Console.WriteLine($"     附加: {row.ExtraText}");
            Console.WriteLine($"     来源: {row.ModSources} ({row.SourceKindText})  {p.EntryPath}");
        }
    }
}

using System.Text;
using TLD_ModBlueprintFinder.Indexing;

namespace TLD_ModBlueprintFinder.Export;

/// <summary>结果导出。只写用户明确指定的文件，绝不碰 Mods 目录。</summary>
public static class CsvExporter
{
    private static readonly string[] Header =
    {
        "类别", "蓝图名", "食谱键", "产物", "产出数量", "匹配说明",
        "制作地点", "耗时", "原料", "工具", "额外条件", "来源Mod", "来源类型", "副本数", "容器", "条目路径",
        "物品定义JSON"
    };

    /// <summary>把一次查询的三组结果汇总成 CSV 文本。</summary>
    public static string Build(SearchOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Escape)));

        AppendRows(sb, outcome.AsProduct, "作为产物");
        AppendRows(sb, outcome.AsIngredient, "作为原料");
        AppendRows(sb, outcome.AsTool, "作为工具");
        AppendDefinitions(sb, outcome.Definitions);

        return sb.ToString();
    }

    public static string BuildSingle(IEnumerable<SearchRow> rows, string category)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Escape)));
        AppendRows(sb, rows, category);
        return sb.ToString();
    }

    private static void AppendRows(StringBuilder sb, IEnumerable<SearchRow> rows, string category)
    {
        foreach (var row in rows)
        {
            var p = row.Primary;
            var values = new[]
            {
                category,
                row.BlueprintName,
                row.RecipeKey,
                p.ResultText,
                row.ResultCountText,
                row.MatchNote,
                row.LocationText,
                row.DurationText,
                row.IngredientsText,
                row.ToolsText,
                row.ExtraText,
                row.ModSources,
                row.SourceKindText,
                row.DuplicateText,
                p.ContainerPath,
                p.EntryPath,
                string.Empty        // 物品定义 JSON：配方行没有这一列的内容
            };

            sb.AppendLine(string.Join(",", values.Select(Escape)));
        }
    }

    /// <summary>
    /// 物品定义行。列位沿用配方表，只有 ID / 判定依据 / 组件 / 来源 / 路径 / JSON 有意义，
    /// 其余留空——这样一份 CSV 里两类数据能同时存在，不必多导一个文件。
    /// </summary>
    private static void AppendDefinitions(StringBuilder sb, IEnumerable<DefinitionRow> rows)
    {
        foreach (var row in rows)
        {
            var d = row.Definition;
            var values = new[]
            {
                "物品定义",
                row.ItemId,
                string.Empty,          // 食谱键
                string.Empty,          // 产物
                string.Empty,          // 产出数量
                row.ConfidenceText,    // 判定依据
                string.Empty,          // 制作地点
                string.Empty,          // 耗时
                row.ComponentsText,    // 组件
                string.Empty,          // 工具
                string.Empty,          // 额外条件
                row.ModName,
                "auto-mapped",
                string.Empty,          // 副本数
                d.ContainerPath,
                d.EntryPath,
                d.JsonText
            };

            sb.AppendLine(string.Join(",", values.Select(Escape)));
        }
    }

    /// <summary>写文件（UTF-8 带 BOM，Excel 打开不乱码）。</summary>
    public static void Save(string path, string csv)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string? value)
    {
        var s = value ?? string.Empty;
        var mustQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!mustQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

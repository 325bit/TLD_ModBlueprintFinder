using System.Text;
using TLD_ModBlueprintFinder.Indexing;

namespace TLD_ModBlueprintFinder.Export;

/// <summary>Result export. Writes only the file the user named; never touches the Mods folder.</summary>
public static class CsvExporter
{
    private static readonly string[] Header =
    {
        "类别", "蓝图名", "食谱键", "产物", "产出数量", "匹配说明",
        "制作地点", "耗时", "原料", "工具", "额外条件", "来源Mod", "来源类型", "副本数", "容器", "条目路径",
        "物品定义JSON"
    };

    /// <summary>Flattens the five result groups of one query into CSV text.</summary>
    public static string Build(SearchOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Escape)));

        AppendRows(sb, outcome.AsProduct, "作为产物（蓝图）");
        AppendRows(sb, outcome.AsIngredient, "作为原料（蓝图）");
        AppendRows(sb, outcome.AsTool, "作为工具");
        AppendRows(sb, outcome.AsRecipeProduct, "作为产物（食谱）");
        AppendRows(sb, outcome.AsRecipeIngredient, "作为原料（食谱）");
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
                string.Empty        // item definition JSON: recipe rows leave it empty
            };

            sb.AppendLine(string.Join(",", values.Select(Escape)));
        }
    }

    /// <summary>
    /// Definition rows reuse the recipe columns. Only ID / basis / components / source / paths / JSON carry
    /// data and the rest stay empty, so one CSV holds both kinds without a second export.
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
                string.Empty,          // recipe key
                string.Empty,          // product
                string.Empty,          // output count
                row.ConfidenceText,    // match basis
                string.Empty,          // crafting location
                string.Empty,          // duration
                row.ComponentsText,    // components
                string.Empty,          // tools
                string.Empty,          // extra requirements
                row.ModName,
                "auto-mapped",
                string.Empty,          // duplicate count
                d.ContainerPath,
                d.EntryPath,
                d.JsonText
            };

            sb.AppendLine(string.Join(",", values.Select(Escape)));
        }
    }

    /// <summary>Writes the file (UTF-8 with BOM so Excel opens it without mojibake).</summary>
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

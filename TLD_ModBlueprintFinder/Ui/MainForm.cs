using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TLD_ModBlueprintFinder.Editing;
using TLD_ModBlueprintFinder.Export;
using TLD_ModBlueprintFinder.Indexing;
using TLD_ModBlueprintFinder.Models;
using TLD_ModBlueprintFinder.Parsing;

namespace TLD_ModBlueprintFinder.Ui;

/// <summary>
/// Business logic of the main window. The Ui partial class owns how it looks; this file owns what it does.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly ModScanner _scanner = new();

    private ScanReport? _report;
    private BlueprintIndex? _index;
    private BlueprintSearchEngine? _engine;
    private ItemDefinitionIndex? _definitions;
    private SearchOutcome? _outcome;
    private CancellationTokenSource? _scanCts;
    private bool _busy;

    private readonly string? _initialPath;
    private readonly string? _initialItem;

    public MainForm() : this(null, null) { }

    /// <summary>
    /// Optionally presets the path and the item ID so a shortcut can run a query:
    ///   TLD_ModBlueprintFinder.exe --path "&lt;game root&gt;" --item GEAR_AcornShelled
    /// </summary>
    public MainForm(string? initialPath, string? initialItem)
    {
        _initialPath = initialPath;
        _initialItem = initialItem;

        BuildUi();
        Load += OnFormLoad;
        Shown += OnFormShown;
        FormClosing += OnFormClosing;
    }

    // Lifecycle

    private void OnFormLoad(object? sender, EventArgs e)
    {
        // Column widths are not scaled by AutoScaleMode.Dpi, so the grids are built after the handle exists
        BuildGrids();

        // Shrink first when the screen is smaller than the window, or the title bar ends up off-screen
        var work = Screen.FromControl(this).WorkingArea;
        if (Height > work.Height) Height = Math.Max(MinimumSize.Height, work.Height - S(40));
        if (Width > work.Width) Width = Math.Max(MinimumSize.Width, work.Width - S(40));

        var settings = LoadSettings();

        // Command-line presets win over the remembered path
        var startPath = !string.IsNullOrWhiteSpace(_initialPath) ? _initialPath : settings.LastPath;
        _chkAutoBackup.Checked = settings.AutoBackup;

        if (!string.IsNullOrWhiteSpace(_initialItem))
            _txtItem.Text = _initialItem;

        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
        {
            _txtPath.Text = startPath;

            if (string.IsNullOrWhiteSpace(_initialPath))
            {
                _chkSubFolders.Checked = settings.IncludeSubFolders;
                _chkMerge.Checked = settings.MergeDuplicates;
                _cmbMode.SelectedIndex = Math.Clamp(settings.MatchModeIndex, 0, _cmbMode.Items.Count - 1);
            }

            // Scan right away when the path is usable, so it does not need a click every launch
            BeginScan(silent: true);
            return;
        }
        else
        {
            var detected = DetectGameFolders();
            if (detected.Count > 0)
            {
                _txtPath.Text = detected[0];
                SetStatus(detected.Count == 1
                        ? $"已自动定位到 {detected[0]}，正在扫描…"
                        : $"检测到 {detected.Count} 个安装，已选择最近有 Mod 更新的 {detected[0]}，正在扫描…",
                    detected.Count == 1 ? Color.ForestGreen : Color.DarkOrange);

                BeginScan(silent: true);
            }
            else
            {
                SetStatus("请选择《漫漫长夜》游戏根目录（含 Mods 文件夹），然后点击「扫描 Mods」。", null);
            }
        }
    }

    /// <summary>
    /// The splitter can only be positioned once the window is really shown (first layout done).
    /// Doing it in Load reads a Width that is still the default for Dock=Fill controls, which puts the
    /// splitter far off.
    /// </summary>
    private void OnFormShown(object? sender, EventArgs e)
    {
        try
        {
            var width = _split.Width;
            if (width <= 0) return;

            _split.SplitterDistance = (int)(width * 0.58);
            _split.Panel1MinSize = S(320);
            _split.Panel2MinSize = S(240);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Ignored on a tiny window: SplitContainer keeps its own minimum widths
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        SaveSettings();
    }

    // Path selection

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择《漫漫长夜》游戏根目录（含 Mods 文件夹），或直接选择 Mods 目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (Directory.Exists(_txtPath.Text))
            dialog.SelectedPath = _txtPath.Text;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _txtPath.Text = dialog.SelectedPath;
        BeginScan();
    }

    private void OnAutoDetectClick(object? sender, EventArgs e)
    {
        var found = DetectGameFolders();
        if (found.Count == 0)
        {
            MessageBox.Show(this,
                "没能自动找到《漫漫长夜》的安装目录。\n\n" +
                "请点「浏览…」手动选择，通常是：\n" +
                @"<Steam 库目录>\steamapps\common\TheLongDark",
                "自动定位失败", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _txtPath.Text = found[0];
        SetStatus(found.Count == 1
            ? $"已定位：{found[0]}"
            : $"检测到 {found.Count} 个安装，已选择最近有 Mod 更新的一个：{found[0]}（不对请点「浏览…」更改）",
            found.Count == 1 ? Color.ForestGreen : Color.DarkOrange);

        BeginScan();
    }

    /// <summary>
    /// Finds every The Long Dark installation, newest Mods activity first.
    /// Taking the first hit is not enough: machines often hold several installs (old backups, different
    /// Steam libraries), and registry or drive order picks the wrong one - one machine had both E: and F:,
    /// and the one in use was whichever Mods folder changed more recently.
    /// </summary>
    private static List<string> DetectGameFolders()
    {
        var candidates = new List<string>();

        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steamPath &&
                !string.IsNullOrWhiteSpace(steamPath))
            {
                var normalized = steamPath.Replace('/', '\\');
                candidates.Add(Path.Combine(normalized, "steamapps", "common", "TheLongDark"));

                var vdf = Path.Combine(normalized, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var lib in ParseLibraryFolders(vdf))
                        candidates.Add(Path.Combine(lib, "steamapps", "common", "TheLongDark"));
                }
            }
        }
        catch
        {
            // Fall back to enumerating drives when the registry read fails
        }

        foreach (var drive in new[] { "C:", "D:", "E:", "F:", "G:", "H:", "I:" })
        {
            candidates.Add($@"{drive}\SteamLibrary\steamapps\common\TheLongDark");
            candidates.Add($@"{drive}\Steam\steamapps\common\TheLongDark");
            candidates.Add($@"{drive}\Program Files (x86)\Steam\steamapps\common\TheLongDark");
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(LooksLikeTldInstall)
            .Select(p => (Path: p, Ticks: LatestModWriteTime(p).Ticks))
            .OrderByDescending(x => x.Ticks)
            .Select(x => x.Path)
            .ToList();
    }

    private static bool LooksLikeTldInstall(string folder)
    {
        try
        {
            return Directory.Exists(folder) &&
                   (Directory.Exists(Path.Combine(folder, "tld_Data")) ||
                    Directory.Exists(Path.Combine(folder, "Mods")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Newest file time under Mods, used to tell which install the player is actually using.</summary>
    private static DateTime LatestModWriteTime(string gameRoot)
    {
        try
        {
            var mods = Path.Combine(gameRoot, "Mods");
            if (!Directory.Exists(mods)) return DateTime.MinValue;

            var latest = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(mods, "*", SearchOption.TopDirectoryOnly))
            {
                var time = File.GetLastWriteTime(file);
                if (time > latest) latest = time;
            }

            return latest;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Minimal VDF reader: extracts the string value after "path".</summary>
    private static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(vdfPath);
        }
        catch
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var path = parts[^1].Replace(@"\\", @"\");
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    // Scanning

    private async void OnScanClick(object? sender, EventArgs e) => await ScanAsync(silent: false).ConfigureAwait(true);

    private async void BeginScan(bool silent = false)
    {
        // Fire-and-forget handler: ScanAsync catches everything internally
        await ScanAsync(silent).ConfigureAwait(true);
    }

    private async Task ScanAsync(bool silent)
    {
        if (_busy) return;

        // Rescanning during an edit rebuilds the tables and loses the unsaved buffer
        if (_edit is not null && !silent)
        {
            MessageBox.Show(this, "正在编辑 JSON，请先「保存修改」或「取消编辑」，再重新扫描。",
                "编辑未结束", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var path = _txtPath.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            if (!silent)
                MessageBox.Show(this, "请先填写或选择游戏路径。", "缺少路径",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        _scanCts = new CancellationTokenSource();

        try
        {
            var options = new ScanOptions
            {
                IncludeSubFolders = _chkSubFolders.Checked,
                IncludeExtractedFolders = true,
                IncludeModComponentTemp = false
            };

            var progress = new Progress<ScanProgress>(p =>
            {
                _lblStatus.Text = p.Total > 0 ? $"{p.Message}  ({p.Done + 1}/{p.Total})" : p.Message;
            });

            var report = await Task.Run(() => _scanner.Scan(path, options, progress, _scanCts.Token))
                .ConfigureAwait(true);

            ApplyReport(report, silent);
        }
        catch (OperationCanceledException)
        {
            SetStatus("扫描已取消。", null);
        }
        catch (ArgumentException ex)
        {
            // Path-level problems: user-facing errors thrown by ResolveInput
            if (!silent)
                MessageBox.Show(this, ex.Message, "路径无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus(ex.Message, Color.Firebrick);
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(this, $"扫描时发生未预期的错误：\n\n{ex.Message}", "扫描失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("扫描失败：" + ex.Message, Color.Firebrick);
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            SetBusy(false);
        }
    }

    private void ApplyReport(ScanReport report, bool silent)
    {
        _report = report;
        _index = new BlueprintIndex(report.Records);
        _definitions = new ItemDefinitionIndex(report.Definitions, report.Records);
        _engine = new BlueprintSearchEngine(_index, _definitions);
        _outcome = null;

        Bind(_gridProduct, Array.Empty<SearchRow>());
        Bind(_gridIngredient, Array.Empty<SearchRow>());
        Bind(_gridTool, Array.Empty<SearchRow>());
        Bind(_gridRecipeProduct, Array.Empty<SearchRow>());
        Bind(_gridRecipeIngredient, Array.Empty<SearchRow>());
        Bind(_gridDefinition, Array.Empty<DefinitionRow>());
        _pageProduct.Text = "① 作为产物（蓝图）";
        _pageIngredient.Text = "② 作为原料（蓝图）";
        _pageTool.Text = "③ 作为工具";
        _pageRecipeProduct.Text = "④ 作为产物（食谱）";
        _pageRecipeIngredient.Text = "⑤ 作为原料（食谱）";
        _pageDefinition.Text = "⑥ 物品定义";

        // Clearing the table drops the selected row, so the buttons must be disabled or they back up a stale path
        UpdateEditButtons();

        // Only "no recipes and no definitions" means there is nothing to show: some mods ship auto-mapped
        // definitions with no recipes, and those must still be queryable.
        if (report.Records.Count == 0 && report.Definitions.Count == 0)
        {
            _txtDetail.Text = BuildEmptyReport(report);
            SetStatus("没有解析到任何蓝图或物品定义。", Color.Firebrick);

            if (!silent)
            {
                MessageBox.Show(this,
                    "这个路径下没有可用的 modcomponent。请确认：\n\n" +
                    "  · 路径指向《漫漫长夜》游戏根目录（含 Mods 文件夹）\n" +
                    "  · 或者直接指向 Mods 目录\n" +
                    "  · Mods 里确实放了 .modcomponent 文件\n\n" +
                    $"当前解析到的根目录：{report.ResolvedModRoot}",
                    "没有找到 modcomponent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return;
        }

        var failed = report.Containers.Count(c => c.LoadFailed);
        _txtDetail.Text = BuildScanSummary(report);

        SetStatus(
            $"扫描完成：{report.Containers.Count} 个容器（失败 {failed}），{report.Records.Count} 条蓝图 + {report.Definitions.Count} 份物品定义，耗时 {report.Elapsed.TotalMilliseconds:0} ms。",
            failed > 0 || report.Errors.Count > 0 ? Color.DarkOrange : Color.ForestGreen);

        // Run a search right away when an item ID is already filled in
        if (!string.IsNullOrWhiteSpace(_txtItem.Text))
            RunSearch();
    }

    // Search

    private void OnSearchClick(object? sender, EventArgs e) => RunSearch();

    private void OnItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        RunSearch();
    }

    private void RunSearch()
    {
        if (_engine is null || _index is null)
        {
            MessageBox.Show(this, "请先点击「扫描 Mods」加载蓝图数据。", "还没有数据",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var query = _txtItem.Text.Trim();
        if (query.Length == 0)
        {
            MessageBox.Show(this, "请输入物品 ID，例如 GEAR_AcornShelled。\n\n" +
                                  "提示：不知道 ID 的话可以只输入一部分（如 Acorn），\n" +
                                  "把匹配模式切成「模糊」就能查出来。",
                "缺少物品 ID", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtItem.Focus();
            return;
        }

        var mode = _cmbMode.SelectedIndex switch
        {
            1 => MatchMode.ExactOnly,
            2 => MatchMode.Fuzzy,
            _ => MatchMode.Auto
        };

        try
        {
            _outcome = _engine.Search(query, mode, _chkMerge.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "检索时出错：\n\n" + ex.Message, "检索失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Bind(_gridProduct, _outcome.AsProduct);
        Bind(_gridIngredient, _outcome.AsIngredient);
        Bind(_gridTool, _outcome.AsTool);
        Bind(_gridRecipeProduct, _outcome.AsRecipeProduct);
        Bind(_gridRecipeIngredient, _outcome.AsRecipeIngredient);
        Bind(_gridDefinition, _outcome.Definitions);

        _pageProduct.Text = $"① 作为产物（蓝图） ({_outcome.AsProduct.Count})";
        _pageIngredient.Text = $"② 作为原料（蓝图） ({_outcome.AsIngredient.Count})";
        _pageTool.Text = $"③ 作为工具 ({_outcome.AsTool.Count})";
        _pageRecipeProduct.Text = $"④ 作为产物（食谱） ({_outcome.AsRecipeProduct.Count})";
        _pageRecipeIngredient.Text = $"⑤ 作为原料（食谱） ({_outcome.AsRecipeIngredient.Count})";
        _pageDefinition.Text = $"⑥ 物品定义 ({_outcome.Definitions.Count})";

        _lblCounts.Text =
            $"蓝图产物 {_outcome.AsProduct.Count} ｜ 蓝图原料 {_outcome.AsIngredient.Count} ｜ 工具 {_outcome.AsTool.Count}" +
            $" ｜ 食谱产物 {_outcome.AsRecipeProduct.Count} ｜ 食谱原料 {_outcome.AsRecipeIngredient.Count}" +
            $" ｜ 定义 {_outcome.Definitions.Count}";

        if (_outcome.IsEmpty)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"没有找到与「{query}」相关的任何配方或物品定义。");
            sb.AppendLine();

            if (_outcome.Suggestions.Count > 0)
            {
                sb.AppendLine("你是不是想找其中之一？");
                foreach (var s in _outcome.Suggestions) sb.AppendLine("  · " + s);
                sb.AppendLine();
                sb.AppendLine("（双击建议项不会自动填入，请复制到上方输入框后重新检索。）");
            }
            else
            {
                sb.AppendLine("当前索引里没有任何相近的物品 ID。可能原因：");
                sb.AppendLine("  · 这个物品来自原版游戏，而本工具只索引 Mod 的 modcomponent（蓝图 + auto-mapped 定义）");
                sb.AppendLine("  · 该 mod 没有提供蓝图（只是添加了物品）");
                sb.AppendLine("  · modcomponent 未被扫描到（检查路径与「含子目录」选项）");
            }

            _txtDetail.Text = sb.ToString();
            SetStatus($"「{query}」无匹配。", Color.DarkOrange);
        }
        else
        {
            var inexact = _outcome.AsProduct.Concat(_outcome.AsIngredient).Concat(_outcome.AsTool)
                .Concat(_outcome.AsRecipeProduct).Concat(_outcome.AsRecipeIngredient)
                .Count(r => r.Quality != MatchQuality.Exact);

            var note = inexact > 0
                ? $"（其中 {inexact} 条为前缀/子串匹配，已用斜体标注）"
                : "";

            var defNote = _outcome.Definitions.Count > 0
                ? $"，物品定义 {_outcome.Definitions.Count} 份"
                : "";

            SetStatus($"「{query}」共命中配方 {_outcome.TotalHits} 条{defNote}。{note}",
                inexact > 0 ? Color.DarkOrange : Color.ForestGreen);

            // Auto-select the detail of the first non-empty group
            if (_outcome.AsProduct.Count > 0) SelectFirstRow(_tabs, _pageProduct, _gridProduct);
            else if (_outcome.AsIngredient.Count > 0) SelectFirstRow(_tabs, _pageIngredient, _gridIngredient);
            else if (_outcome.AsTool.Count > 0) SelectFirstRow(_tabs, _pageTool, _gridTool);
            else if (_outcome.AsRecipeProduct.Count > 0) SelectFirstRow(_tabs, _pageRecipeProduct, _gridRecipeProduct);
            else if (_outcome.AsRecipeIngredient.Count > 0) SelectFirstRow(_tabs, _pageRecipeIngredient, _gridRecipeIngredient);
            else if (_outcome.Definitions.Count > 0) SelectFirstRow(_tabs, _pageDefinition, _gridDefinition);
        }
    }

    private static void SelectFirstRow(TabControl tabs, TabPage page, DataGridView grid)
    {
        tabs.SelectedTab = page;
        if (grid.Rows.Count == 0) return;
        grid.ClearSelection();
        grid.Rows[0].Selected = true;
        grid.CurrentCell = grid.Rows[0].Cells[0];
    }

    // Detail / copy / export

    private void _gridProduct_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridProduct);
    private void _gridIngredient_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridIngredient);
    private void _gridTool_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridTool);
    private void _gridRecipeProduct_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridRecipeProduct);
    private void _gridRecipeIngredient_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridRecipeIngredient);
    private void _gridDefinition_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridDefinition);

    /// <summary>
    /// Renders non-exact hits in orange or grey italics.
    /// This matters: typing "GEAR_AcornShel" prefix-matches GEAR_AcornShelled, and without the visual
    /// difference a widened match is easy to mistake for a real one.
    /// </summary>
    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.CellStyle is null) return;
        if (grid.Rows[e.RowIndex].DataBoundItem is not IDetailRow row) return;
        if (row.Quality == MatchQuality.Exact) return;

        e.CellStyle.ForeColor = row.Quality == MatchQuality.Prefix
            ? Color.FromArgb(181, 96, 6)      // prefix: dark orange
            : Color.FromArgb(120, 120, 120);  // contains: mid grey

        // First column is "what this result is called"; italic marks a widened match
        if (e.ColumnIndex == 0 || grid.Columns[e.ColumnIndex].Name == nameof(SearchRow.BlueprintName))
            e.CellStyle.Font = new Font(grid.Font, FontStyle.Italic);
    }

    private void OnGridCellToolTip(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0) return;
        if (grid.Rows[e.RowIndex].DataBoundItem is not IDetailRow row) return;

        if (row.Quality != MatchQuality.Exact)
        {
            e.ToolTipText = $"匹配方式：{row.QualityText}\n" +
                            $"输入的「{_outcome?.Query}」不是完整物品 ID，这一行是自动补全出来的候选。\n" +
                            $"如需只看精确命中，请把匹配模式切换为「仅精确匹配」。";
            return;
        }

        // Show the full value on hover when a long text column is truncated
        var column = grid.Columns[e.ColumnIndex].Name;
        if (column is nameof(SearchRow.ModSources)
            or nameof(SearchRow.IngredientsText)
            or nameof(SearchRow.ToolsText)
            or nameof(SearchRow.RecipeKey)
            or nameof(SearchRow.BlueprintName))
        {
            e.ToolTipText = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        }
    }

    /// <summary>
    /// Renders the current row of the given grid into the detail box.
    /// The detail box is shared by every tab, so "who is selected" must come from the visible grid, not
    /// from "which cell was clicked last".
    /// </summary>
    private void ShowDetail(DataGridView grid)
    {
        // Never overwrite the text box while editing: the buffer holds unsaved user input
        if (_edit is not null) return;

        var row = CurrentRow(grid);
        if (row is null)
        {
            UpdateEditButtons();
            return;
        }

        var text = row.BuildDetailText();

        // Do not re-render identical text: selection change and mouse click both reach this method, and
        // rendering twice resets the scroll position, which makes reading long JSON painful.
        if (_txtDetail.Text != text) SetDetailText(text);

        UpdateEditButtons();
    }

    /// <summary>Detail object of the current row; on multi-select prefer the current row, then the first.</summary>
    private static IDetailRow? CurrentRow(DataGridView grid)
    {
        var row = grid.CurrentRow?.DataBoundItem as IDetailRow;
        if (row is null && grid.SelectedRows.Count > 0)
            row = grid.SelectedRows[0].DataBoundItem as IDetailRow;

        return row;
    }

    /// <summary>Enables or disables the three buttons from "is a row selected" and "is an edit open".</summary>
    private void UpdateEditButtons()
    {
        var row = CurrentRow(ActiveGrid());
        var locatable = row is not null &&
                        !string.IsNullOrEmpty(row.ContainerPath) &&
                        !string.IsNullOrEmpty(row.EntryPath);

        _btnEdit.Enabled = _edit is not null || (locatable && row!.EditableJson.Length > 0);
        _btnSaveEdit.Enabled = _edit is not null;
        _btnBackup.Enabled = _edit is not null ? true : row is not null && !string.IsNullOrEmpty(row.ContainerPath);
    }

    private void SetDetailText(string text)
    {
        _txtDetail.Text = text;
        _txtDetail.SelectionStart = 0;
        _txtDetail.ScrollToCaret();
    }

    /// <summary>Grid belonging to the current tab.</summary>
    private DataGridView ActiveGrid() =>
        _tabs.SelectedTab == _pageIngredient ? _gridIngredient
        : _tabs.SelectedTab == _pageTool ? _gridTool
        : _tabs.SelectedTab == _pageRecipeProduct ? _gridRecipeProduct
        : _tabs.SelectedTab == _pageRecipeIngredient ? _gridRecipeIngredient
        : _tabs.SelectedTab == _pageDefinition ? _gridDefinition
        : _gridProduct;

    /// <summary>
    /// Resyncs the detail panel when the tab changes.
    /// Without this the panel keeps showing the row that was clicked last, which may belong to another
    /// tab - the tab has switched but the detail is stale.
    /// </summary>
    private void OnTabChanged(object? sender, EventArgs e)
    {
        var grid = ActiveGrid();

        if (grid.Rows.Count == 0)
        {
            UpdateEditButtons();

            // Empty tab: say so explicitly instead of leaving another tab's blueprint on screen, which would
            // read as content of this tab.
            if (_outcome is { IsEmpty: false })
                SetDetailText($"「{_tabs.SelectedTab?.Text}」没有匹配结果。\n\n" +
                              "这是正常现象：该物品没有出现在这个位置。\n" +
                              "切回其它页签即可继续查看已有结果。");
            return;
        }

        // The grid may have no current row yet when the tab first shows; set one
        if (grid.CurrentRow is null)
        {
            grid.ClearSelection();
            grid.Rows[0].Selected = true;
            grid.CurrentCell = grid.Rows[0].Cells[0];
        }

        ShowDetail(grid);
    }

    private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || sender is not DataGridView grid) return;

        switch (grid.Rows[e.RowIndex].DataBoundItem)
        {
            case SearchRow row:
                TryCopy($"{row.BlueprintName} → {row.ResultText}  [{row.LocationText}]  来源 {row.ModSources}");
                SetStatus("已复制该行摘要到剪贴板。", Color.DimGray);
                break;

            case DefinitionRow def:
                TryCopy($"{def.ItemId}  ←  {def.ModName} :: {def.EntryPath}");
                SetStatus("已复制该物品定义的来源到剪贴板。", Color.DimGray);
                break;
        }
    }

    private void OnCopyClick(object? sender, EventArgs e)
    {
        var text = _txtDetail.SelectionLength > 0
            ? _txtDetail.SelectedText
            : _txtDetail.Text;

        if (string.IsNullOrWhiteSpace(text)) return;

        if (TryCopy(text))
            SetStatus($"已复制 {text.Length} 个字符。", Color.DimGray);
    }

    private bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制到剪贴板失败：" + ex.Message, "剪贴板",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    // Backup and in-place editing

    /// <summary>
    /// One edit session. Records which container and entry are being edited plus the original text, so
    /// saving never depends on the current row - a table change cannot redirect the write.
    /// </summary>
    private sealed record EditSession(string ContainerPath, string EntryPath, string OriginalText, IDetailRow Row);

    private EditSession? _edit;

    private void OnAutoBackupChanged(object? sender, EventArgs e) => SaveSettings();

    /// <summary>
    /// Opens the container editor: from "edit one entry" to "edit a whole modcomponent".
    /// It receives the current row's container and entry, so it opens on the right spot.
    /// </summary>
    private void OnOpenEditorClick(object? sender, EventArgs e)
    {
        if (_report is null || _report.Containers.Count == 0)
        {
            MessageBox.Show(this, "请先点击「扫描 Mods」加载 modcomponent，再打开编辑器。",
                "还没有可编辑的容器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_edit is not null)
        {
            MessageBox.Show(this, "正在编辑当前行，请先「保存修改」或「取消编辑」，再打开容器编辑器。",
                "编辑未结束", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var row = CurrentRow(ActiveGrid());

        try
        {
            using var editor = new ContainerEditorForm(
                _report.Containers,
                row?.ContainerPath,
                row?.EntryPath,
                _chkAutoBackup.Checked);

            editor.ShowDialog(this);

            if (!editor.AnyChange) return;

            SetStatus("容器内容已改动，正在重新扫描…", Color.DarkOrange);
            BeginScan(silent: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开容器编辑器时出错：\n\n" + ex.Message, "编辑器",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("打开容器编辑器失败：" + ex.Message, Color.Firebrick);
        }
    }

    private void OnBackupClick(object? sender, EventArgs e)
    {
        var row = CurrentRow(ActiveGrid());
        if (row is null || string.IsNullOrEmpty(row.ContainerPath))
        {
            SetStatus("请先在左侧选中一行，再备份它所属的 modcomponent。", Color.Firebrick);
            return;
        }

        try
        {
            var path = ModBackup.Create(row.ContainerPath);
            SetStatus($"已备份到 {path}", Color.ForestGreen);

            MessageBox.Show(this, $"已备份：\n\n{path}", "备份完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"备份失败：\n\n{ex.Message}", "备份失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("备份失败：" + ex.Message, Color.Firebrick);
        }
    }

    private void OnEditClick(object? sender, EventArgs e)
    {
        if (_edit is not null)
        {
            CancelEdit();
            return;
        }

        var row = CurrentRow(ActiveGrid());
        if (row is null || string.IsNullOrEmpty(row.ContainerPath) || string.IsNullOrEmpty(row.EntryPath))
        {
            SetStatus("请先在左侧选中一行，再编辑它对应的 JSON。", Color.Firebrick);
            return;
        }

        // Freezes the left table: switching rows during an edit would discard the buffer. Rather than asking
        // "save before switching?", making the switch impossible is far more predictable.
        _tabs.Enabled = false;
        _txtDetail.ReadOnly = false;
        _txtDetail.BackColor = Color.FromArgb(255, 252, 235);   // pale yellow = editing
        _txtDetail.Text = row.EditableJson;
        _txtDetail.SelectionStart = 0;
        _txtDetail.ScrollToCaret();

        // The baseline is what the editor holds, not the file text: RichTextBox returns LF for every line
        // break, so a CRLF entry would look modified before the user touched a key.
        _edit = new EditSession(row.ContainerPath, row.EntryPath, _txtDetail.Text, row);

        _btnEdit.Text = "取消编辑";
        UpdateEditButtons();

        var merged = row is SearchRow { Records.Count: > 1 } sr
            ? $"（该行合并了 {sr.Records.Count} 份同名蓝图，改动写入第一份）"
            : "";

        SetStatus($"编辑中：{Path.GetFileName(_edit.ContainerPath)} :: {_edit.EntryPath}{merged}", Color.DarkOrange);
    }

    private void CancelEdit()
    {
        if (_edit is null) return;

        _edit = null;
        ExitEditMode();
        ShowDetail(ActiveGrid());
        SetStatus("已取消编辑，没有写入任何文件。", Color.DimGray);
    }

    private void ExitEditMode()
    {
        _tabs.Enabled = true;
        _txtDetail.ReadOnly = true;
        _txtDetail.BackColor = Color.White;
        _btnEdit.Text = "编辑 JSON";
        UpdateEditButtons();
    }

    private async void OnSaveEditClick(object? sender, EventArgs e)
    {
        if (_edit is null) return;

        var session = _edit;
        var text = _txtDetail.Text;

        if (text == session.OriginalText)
        {
            CancelEdit();
            SetStatus("内容没有变化，未写入任何文件。", Color.DimGray);
            return;
        }

        if (!IsValidJson(text, out var jsonError))
        {
            var answer = MessageBox.Show(this,
                "这段 JSON 解析不了：\n\n" + jsonError +
                "\n\n仍然保存吗？游戏大概率会读不出这条配方。",
                "JSON 语法有问题", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;
        }

        string? backupPath = null;

        try
        {
            if (_chkAutoBackup.Checked)
                backupPath = ModBackup.Create(session.ContainerPath);

            ModComponentArchive.WriteText(session.ContainerPath, session.EntryPath, text);
        }
        catch (Exception ex)
        {
            var extra = backupPath is null
                ? ""
                : $"\n\n原文件已经备份到：\n{backupPath}\n（写入没成功，原 modcomponent 未被改动）";

            MessageBox.Show(this, "写入失败：\n\n" + ex.Message + extra, "保存修改失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

            SetStatus("保存失败：" + ex.Message, Color.Firebrick);
            return;
        }

        _edit = null;
        ExitEditMode();

        SetStatus(backupPath is null
                ? $"已写入 {session.EntryPath}（未备份）。正在重新扫描…"
                : $"已写入 {session.EntryPath}，备份：{Path.GetFileName(backupPath)}。正在重新扫描…",
            Color.ForestGreen);

        // The index must be rebuilt after a write, or the UI shows "saved but nothing changed"
        await ScanAsync(silent: true).ConfigureAwait(true);
    }

    private static bool IsValidJson(string text, out string error)
    {
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128
            });

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OnExportClick(object? sender, EventArgs e)
    {
        if (_outcome is null || _outcome.IsEmpty)
        {
            MessageBox.Show(this, "当前没有可导出的结果，请先检索。", "没有结果",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var safeQuery = string.Concat(_outcome.Query.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        using var dialog = new SaveFileDialog
        {
            Title = "导出检索结果",
            Filter = "CSV 文件|*.csv|所有文件|*.*",
            FileName = $"配方_{safeQuery}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            CsvExporter.Save(dialog.FileName, CsvExporter.Build(_outcome));
            SetStatus($"已导出到 {dialog.FileName}", Color.ForestGreen);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：\n\n" + ex.Message, "导出错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Status and busy state

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visible = busy;
        _btnScan.Enabled = !busy;
        _btnBrowse.Enabled = !busy;
        _btnDetect.Enabled = !busy;
        _btnSearch.Enabled = !busy;
        _btnExport.Enabled = !busy;
        _btnOpenEditor.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text, Color? color)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = color ?? SystemColors.ControlText;
    }

    // Report text

    private static string BuildEmptyReport(ScanReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("没有解析到任何蓝图或物品定义。");
        sb.AppendLine();
        sb.AppendLine($"解析到的根目录：{report.ResolvedModRoot}");
        sb.AppendLine();

        if (report.Errors.Count > 0)
        {
            sb.AppendLine($"── 错误（{report.Errors.Count}）──");
            foreach (var e in report.Errors.Take(60)) sb.AppendLine("  · " + e);
        }
        else
        {
            sb.AppendLine("排查建议：");
            sb.AppendLine("  1. 路径要指向游戏根目录（里面应有 Mods 文件夹），或直接指向 Mods 目录");
            sb.AppendLine("  2. Mods 目录里应有 *.modcomponent 文件（ZIP 容器）");
            sb.AppendLine("  3. 若 mod 以解压目录形式存在，确保目录下有 blueprints/ 或 recipes/ 子文件夹");
        }

        return sb.ToString();
    }

    private static string BuildScanSummary(ScanReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ 扫描摘要 ═══");
        sb.AppendLine($"根目录        : {report.ResolvedModRoot}");
        sb.AppendLine($"容器          : {report.Containers.Count} 个（失败 {report.Containers.Count(c => c.LoadFailed)}）");
        sb.AppendLine($"JSON 条目     : 见 {report.JsonEntriesSeen}，解析成功 {report.JsonEntriesParsed}，跳过 {report.JsonEntriesSkipped}");
        sb.AppendLine($"蓝图记录      : {report.Records.Count}（食谱 {report.Records.Count(r => r.SourceKind == BlueprintSourceKind.Recipe)}）");
        sb.AppendLine($"物品定义      : {report.Definitions.Count} 份（auto-mapped，含未参与配方的物品）");
        sb.AppendLine($"解析警告      : {report.WarningCount}");
        sb.AppendLine($"耗时          : {report.Elapsed.TotalMilliseconds:0} ms");
        sb.AppendLine();

        sb.AppendLine("── 按 mod 统计 ──");
        foreach (var group in report.Records
                     .GroupBy(r => r.ModName, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"  {group.Key,-34} {group.Count(),4} 条");
        }

        var loadFailed = report.Containers.Where(c => c.LoadFailed).ToList();
        if (loadFailed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── 打不开的容器 ──");
            foreach (var c in loadFailed)
                sb.AppendLine($"  {c.Name}: {c.FailureReason}");
        }

        if (report.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"── 解析错误（{report.Errors.Count}）──");
            foreach (var e in report.Errors.Take(60)) sb.AppendLine("  · " + e);
            if (report.Errors.Count > 60)
                sb.AppendLine($"  …（另有 {report.Errors.Count - 60} 条，完整内容见命令行模式输出）");
        }

        sb.AppendLine();
        sb.AppendLine("在上方输入物品 ID 后回车即可检索。");
        return sb.ToString();
    }

    // Settings (next to the exe, portable, never inside the game folder)

    private sealed class AppSettings
    {
        public string? LastPath { get; set; }
        public bool IncludeSubFolders { get; set; } = true;
        public bool MergeDuplicates { get; set; } = true;
        public int MatchModeIndex { get; set; }

        /// <summary>Back up the original before "Save changes". On by default - the only operation that overwrites user files.</summary>
        public bool AutoBackup { get; set; } = true;
    }

    /// <summary>Settings file name. Prefixed with the tool name so another tool in the same folder cannot collide.</summary>
    private const string SettingsFileName = "TLD_ModBlueprintFinder_settings.json";

    /// <summary>
    /// Settings file: TLD_ModBlueprintFinder_settings.json next to the exe.
    /// BaseDirectory rather than CurrentDirectory, so the launch working directory (a shortcut's "Start in",
    /// or the shell) cannot scatter the config elsewhere.
    /// </summary>
    private static string SettingsFile => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    private static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new AppSettings();
            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new AppSettings
            {
                LastPath = _txtPath.Text.Trim(),
                IncludeSubFolders = _chkSubFolders.Checked,
                MergeDuplicates = _chkMerge.Checked,
                MatchModeIndex = _cmbMode.SelectedIndex,
                AutoBackup = _chkAutoBackup.Checked
            };

            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A failed settings save must not block the main flow (read-only folder); the next launch just scans again
        }
    }
}

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
/// 主窗口的业务逻辑。
/// 分工：Ui 分部类只负责「长什么样」，这里只负责「做什么」。
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
    /// 可选地预置路径与物品 ID。用于命令行启动：
    ///   TLD_ModBlueprintFinder.exe --path "F:\...\TheLongDark" --item GEAR_AcornShelled
    /// 这样可以把常用查询做成桌面快捷方式，双击直接出结果。
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

    // ────────────────────────────────────────────────────────────────────
    // 生命周期
    // ────────────────────────────────────────────────────────────────────

    private void OnFormLoad(object? sender, EventArgs e)
    {
        // 列宽不会被 AutoScaleMode.Dpi 自动缩放，所以必须等句柄创建后再按实际 DPI 生成表格
        BuildGrids();

        // 屏幕比窗口还小时先收一收，避免标题栏被顶到屏幕外
        var work = Screen.FromControl(this).WorkingArea;
        if (Height > work.Height) Height = Math.Max(MinimumSize.Height, work.Height - S(40));
        if (Width > work.Width) Width = Math.Max(MinimumSize.Width, work.Width - S(40));

        var settings = LoadSettings();

        // 命令行预置优先于上次记住的路径
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

            // 路径可用就直接扫一遍，省得每次都点
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
    /// 分隔条必须在窗口真正显示（完成第一次布局）之后才能定位。
    /// 放在 Load 里会读到尚未布局的 Width（Dock=Fill 的控件此时宽度还是默认值），
    /// 结果分隔条位置偏差很大。
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
            // 窗口极小时忽略：SplitContainer 会保留默认的最小宽度限制
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        SaveSettings();
    }

    // ────────────────────────────────────────────────────────────────────
    // 路径选择
    // ────────────────────────────────────────────────────────────────────

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
    /// 找出所有可能的《漫漫长夜》安装目录，按「Mods 目录里最近一次文件更新时间」倒序。
    ///
    /// 为什么不能只取第一个：机器上往往同时存在多份安装（旧版本备份、不同 Steam 库），
    /// 只按注册表或盘符顺序取会选错——实测这台机器上 E: 与 F: 各有一份，
    /// 玩家实际在用的是 Mod 更新更频繁的那一份。
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
            // 注册表读不到就退回到盘符枚举
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

    /// <summary>Mods 目录里最新的文件修改时间，用来判断「哪份安装是玩家现在在用的」。</summary>
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

    /// <summary>极简 VDF 读取：只抽 "path" 后面的字符串值。</summary>
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

    // ────────────────────────────────────────────────────────────────────
    // 扫描
    // ────────────────────────────────────────────────────────────────────

    private async void OnScanClick(object? sender, EventArgs e) => await ScanAsync(silent: false).ConfigureAwait(true);

    private async void BeginScan(bool silent = false)
    {
        // 事件处理器风格的 fire-and-forget：ScanAsync 内部已完整捕获异常
        await ScanAsync(silent).ConfigureAwait(true);
    }

    private async Task ScanAsync(bool silent)
    {
        if (_busy) return;

        // 编辑期间重新扫描会把表格与详情重建，缓冲里的内容就没了
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
            // 路径类问题：ResolveInput 明确抛出的用户级错误
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
        Bind(_gridDefinition, Array.Empty<DefinitionRow>());
        _pageProduct.Text = "① 作为产物";
        _pageIngredient.Text = "② 作为原料";
        _pageTool.Text = "③ 作为工具";
        _pageDefinition.Text = "④ 物品定义";

        // 表格清空后选中行没了，按钮必须跟着禁用，否则会拿旧行的路径去备份
        UpdateEditButtons();

        // 只有「配方和物品定义都为空」才算真的没东西可看：
        // 有些 mod 只提供 auto-mapped 定义、不带任何配方，那种情况照样要能查。
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

        // 已经填了物品 ID 就顺手查一次
        if (!string.IsNullOrWhiteSpace(_txtItem.Text))
            RunSearch();
    }

    // ────────────────────────────────────────────────────────────────────
    // 检索
    // ────────────────────────────────────────────────────────────────────

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
        Bind(_gridDefinition, _outcome.Definitions);

        _pageProduct.Text = $"① 作为产物 ({_outcome.AsProduct.Count})";
        _pageIngredient.Text = $"② 作为原料 ({_outcome.AsIngredient.Count})";
        _pageTool.Text = $"③ 作为工具 ({_outcome.AsTool.Count})";
        _pageDefinition.Text = $"④ 物品定义 ({_outcome.Definitions.Count})";

        _lblCounts.Text = $"产物 {_outcome.AsProduct.Count} ｜ 原料 {_outcome.AsIngredient.Count} ｜ 工具 {_outcome.AsTool.Count} ｜ 定义 {_outcome.Definitions.Count}";

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
                .Count(r => r.Quality != MatchQuality.Exact);

            var note = inexact > 0
                ? $"（其中 {inexact} 条为前缀/子串匹配，已用斜体标注）"
                : "";

            var defNote = _outcome.Definitions.Count > 0
                ? $"，物品定义 {_outcome.Definitions.Count} 份"
                : "";

            SetStatus($"「{query}」共命中配方 {_outcome.TotalHits} 条{defNote}。{note}",
                inexact > 0 ? Color.DarkOrange : Color.ForestGreen);

            // 自动选中第一个非空结果的详情
            if (_outcome.AsProduct.Count > 0) SelectFirstRow(_tabs, _pageProduct, _gridProduct);
            else if (_outcome.AsIngredient.Count > 0) SelectFirstRow(_tabs, _pageIngredient, _gridIngredient);
            else if (_outcome.AsTool.Count > 0) SelectFirstRow(_tabs, _pageTool, _gridTool);
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

    // ────────────────────────────────────────────────────────────────────
    // 详情 / 复制 / 导出
    // ────────────────────────────────────────────────────────────────────

    private void _gridProduct_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridProduct);
    private void _gridIngredient_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridIngredient);
    private void _gridTool_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridTool);
    private void _gridDefinition_SelectionChanged(object? sender, EventArgs e) => ShowDetail(_gridDefinition);

    /// <summary>
    /// 非精确命中的行改成橙色 / 灰色 + 斜体。
    /// 这是必要的：用户输 "GEAR_AcornShel" 时前缀匹配会命中 GEAR_AcornShelled，
    /// 不做视觉区分的话很容易把「凑出来的结果」当成真的。
    /// </summary>
    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.CellStyle is null) return;
        if (grid.Rows[e.RowIndex].DataBoundItem is not IDetailRow row) return;
        if (row.Quality == MatchQuality.Exact) return;

        e.CellStyle.ForeColor = row.Quality == MatchQuality.Prefix
            ? Color.FromArgb(181, 96, 6)      // 前缀：深橙
            : Color.FromArgb(120, 120, 120);  // 包含：中灰

        // 第一列是「这条结果叫什么」，非精确命中时用斜体区分
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

        // 长文本列被截断时，悬停显示完整内容
        var column = grid.Columns[e.ColumnIndex].Name;
        if (column is nameof(SearchRow.ModSources)
            or nameof(SearchRow.IngredientsText)
            or nameof(SearchRow.ToolsText)
            or nameof(SearchRow.BlueprintName))
        {
            e.ToolTipText = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        }
    }

    /// <summary>
    /// 把指定表格的当前行渲染到右侧详情框。
    /// 详情框是三个页签共用的一格，所以「谁在选中状态」必须由当前可见的那个表格决定，
    /// 不能靠「上一次点过哪个格子」来记。
    /// </summary>
    private void ShowDetail(DataGridView grid)
    {
        // 编辑期间绝不改写文本框：Buffer 里是用户还没保存的内容
        if (_edit is not null) return;

        var row = CurrentRow(grid);
        if (row is null)
        {
            UpdateEditButtons();
            return;
        }

        var text = row.BuildDetailText();

        // 同一条内容不重复渲染：选中变化与鼠标点击会先后触发本方法，
        // 不去重的话详情框的滚动位置会被连续两次重置，读长 JSON 时很难受。
        if (_txtDetail.Text != text) SetDetailText(text);

        UpdateEditButtons();
    }

    /// <summary>当前选中行的详情对象；多选时优先当前行，退而取第一条选中行。</summary>
    private static IDetailRow? CurrentRow(DataGridView grid)
    {
        var row = grid.CurrentRow?.DataBoundItem as IDetailRow;
        if (row is null && grid.SelectedRows.Count > 0)
            row = grid.SelectedRows[0].DataBoundItem as IDetailRow;

        return row;
    }

    /// <summary>按「有没有选中行 / 是否在编辑」刷新三个按钮的可用状态。</summary>
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

    /// <summary>当前页签对应的表格。</summary>
    private DataGridView ActiveGrid() =>
        _tabs.SelectedTab == _pageIngredient ? _gridIngredient
        : _tabs.SelectedTab == _pageTool ? _gridTool
        : _tabs.SelectedTab == _pageDefinition ? _gridDefinition
        : _gridProduct;

    /// <summary>
    /// 切换页签时重新同步详情。
    /// 缺了这一步，详情框会停留在「上一次点击的那一行」上，而那一行很可能属于另一个页签，
    /// 于是出现「页签已经切了、详情还是旧的」这种不一致。
    /// </summary>
    private void OnTabChanged(object? sender, EventArgs e)
    {
        var grid = ActiveGrid();

        if (grid.Rows.Count == 0)
        {
            UpdateEditButtons();

            // 空页签：明确说明这一页没有结果，
            // 而不是继续显示别的页签里的蓝图（那会被误读成这一页的内容）。
            if (_outcome is { IsEmpty: false })
                SetDetailText($"「{_tabs.SelectedTab?.Text}」没有匹配结果。\n\n" +
                              "这是正常现象：该物品没有出现在这个位置。\n" +
                              "切回其它页签即可继续查看已有结果。");
            return;
        }

        // 页签首次显示时表格可能还没有当前行，补一个
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

    // ────────────────────────────────────────────────────────────────────
    // 备份与就地修改
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 一次编辑会话。记下「改的是哪个容器的哪个条目」以及原文，
    /// 这样保存时不必依赖当前选中行 —— 万一表格状态变了也不会写错文件。
    /// </summary>
    private sealed record EditSession(string ContainerPath, string EntryPath, string OriginalText, IDetailRow Row);

    private EditSession? _edit;

    private void OnAutoBackupChanged(object? sender, EventArgs e) => SaveSettings();

    /// <summary>
    /// 打开容器编辑器：从「改一条」升级到「改一整个 modcomponent」。
    /// 带上当前选中行的容器与条目，打开就落在正确的位置上。
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

        _edit = new EditSession(row.ContainerPath, row.EntryPath, row.EditableJson, row);

        // 冻结左侧表格：编辑期间切换条目会把缓冲里的内容冲掉。
        // 与其做「切换前弹窗问是否保存」那套，不如让这段时间根本切不动，行为更好预期。
        _tabs.Enabled = false;
        _txtDetail.ReadOnly = false;
        _txtDetail.BackColor = Color.FromArgb(255, 252, 235);   // 淡黄 = 编辑中
        _txtDetail.Text = row.EditableJson;
        _txtDetail.SelectionStart = 0;
        _txtDetail.ScrollToCaret();

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

        // 改完必须重新解析：索引里还是旧的原料/工具，界面会显示改了但没生效的假象
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

    // ────────────────────────────────────────────────────────────────────
    // 状态与忙碌态
    // ────────────────────────────────────────────────────────────────────

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

    // ────────────────────────────────────────────────────────────────────
    // 报告文本
    // ────────────────────────────────────────────────────────────────────

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

    // ────────────────────────────────────────────────────────────────────
    // 设置持久化（写在程序所在目录，绿色版式，绝不写进游戏目录）
    // ────────────────────────────────────────────────────────────────────

    private sealed class AppSettings
    {
        public string? LastPath { get; set; }
        public bool IncludeSubFolders { get; set; } = true;
        public bool MergeDuplicates { get; set; } = true;
        public int MatchModeIndex { get; set; }

        /// <summary>「保存修改」前是否自动备份原 modcomponent。默认开 —— 这是唯一会覆盖用户文件的操作。</summary>
        public bool AutoBackup { get; set; } = true;
    }

    /// <summary>设置文件名。带上程序名，避免和别的工具挤在同一目录时互相覆盖。</summary>
    private const string SettingsFileName = "TLD_ModBlueprintFinder_settings.json";

    /// <summary>
    /// 设置文件：与 exe 同目录的 TLD_ModBlueprintFinder_settings.json。
    /// 用 BaseDirectory 而不是 CurrentDirectory，启动时的工作目录才不会把配置甩到
    /// 快捷方式的「起始位置」或命令行所在目录去。
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
            // 设置保存失败不影响主流程（exe 放在只读目录时就是这种情况，下次启动照常先扫一遍）
        }
    }
}

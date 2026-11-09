using System.ComponentModel;
using TLD_ModBlueprintFinder.Indexing;

namespace TLD_ModBlueprintFinder.Ui;

/// <summary>
/// UI construction for the main window. Every control is created in code (no .resx), so this file can be
/// copied into any .NET 8 project and compiled as is.
/// </summary>
public sealed partial class MainForm
{
    // Input area
    private readonly TextBox _txtPath = new();
    private readonly TextBox _txtItem = new();
    private readonly Button _btnBrowse = new();
    private readonly Button _btnDetect = new();
    private readonly Button _btnScan = new();
    private readonly Button _btnSearch = new();
    private readonly ComboBox _cmbMode = new();
    private readonly CheckBox _chkMerge = new();
    private readonly CheckBox _chkSubFolders = new();

    // Result area
    private readonly TabControl _tabs = new();
    private readonly DataGridView _gridProduct = new() { Name = "gridProduct" };
    private readonly DataGridView _gridIngredient = new() { Name = "gridIngredient" };
    private readonly DataGridView _gridTool = new() { Name = "gridTool" };
    private readonly DataGridView _gridRecipeProduct = new() { Name = "gridRecipeProduct" };
    private readonly DataGridView _gridRecipeIngredient = new() { Name = "gridRecipeIngredient" };
    private readonly DataGridView _gridDefinition = new() { Name = "gridDefinition" };
    private readonly RichTextBox _txtDetail = new();
    private readonly SplitContainer _split = new();
    private readonly TabPage _pageProduct = new("① 作为产物（蓝图）");
    private readonly TabPage _pageIngredient = new("② 作为原料（蓝图）");
    private readonly TabPage _pageTool = new("③ 作为工具");
    private readonly TabPage _pageRecipeProduct = new("④ 作为产物（食谱）");
    private readonly TabPage _pageRecipeIngredient = new("⑤ 作为原料（食谱）");
    private readonly TabPage _pageDefinition = new("⑥ 物品定义");

    // Status area
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblStatus = new();
    private readonly ToolStripStatusLabel _lblCounts = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnCopy = new();

    // Backup / in-place editing
    private readonly Button _btnEdit = new();
    private readonly Button _btnSaveEdit = new();
    private readonly Button _btnOpenEditor = new();
    private readonly Button _btnBackup = new();
    private readonly CheckBox _chkAutoBackup = new();
    private readonly ToolTip _tips = new();

    /// <summary>
    /// Logical to device pixels. Before the handle exists, return the logical value: AutoScaleMode.Dpi has
    /// not kicked in yet, and multiplying by DPI here would scale the UI twice.
    /// After the handle exists the conversion serves sizes AutoScale does not cover (grid column widths).
    /// </summary>
    private int S(int logical)
    {
        if (!IsHandleCreated) return logical;
        return (int)Math.Round(logical * DeviceDpi / 96.0);
    }

    private void BuildUi()
    {
        SuspendLayout();

        Text = "《漫漫长夜》Mod 配方检索工具  ·  TLD Mod Blueprint Finder";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(1380), S(860));
        MinimumSize = new Size(S(980), S(620));
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // Top input area
        var panelTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(148),
            Padding = new Padding(S(12), S(10), S(12), S(4))
        };

        var gbPath = new GroupBox
        {
            Text = "① 游戏路径",
            Dock = DockStyle.Top,
            Height = S(62),
            Padding = new Padding(S(10), S(6), S(10), S(6))
        };

        var pathLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1
        };
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(64)));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _txtPath.Dock = DockStyle.Fill;
        _txtPath.Margin = new Padding(0, S(4), S(6), 0);
        _txtPath.PlaceholderText = @"例如 F:\SteamLibrary\steamapps\common\TheLongDark   （也可直接指向 Mods 目录或单个 .modcomponent）";

        _btnBrowse.Text = "浏览…";
        _btnBrowse.AutoSize = true;
        _btnBrowse.Margin = new Padding(0, S(2), S(6), 0);
        _btnBrowse.Click += OnBrowseClick;

        _btnDetect.Text = "自动定位";
        _btnDetect.AutoSize = true;
        _btnDetect.Margin = new Padding(0, S(2), S(6), 0);
        _btnDetect.Click += OnAutoDetectClick;

        _btnScan.Text = "扫描 Mods";
        _btnScan.AutoSize = true;
        _btnScan.Margin = new Padding(0, S(2), 0, 0);
        _btnScan.Click += OnScanClick;

        _chkSubFolders.Text = "含子目录";
        _chkSubFolders.Checked = true;
        _chkSubFolders.AutoSize = true;
        _chkSubFolders.Margin = new Padding(S(8), S(6), 0, 0);

        pathLayout.Controls.Add(MakeLabel("游戏路径"), 0, 0);
        pathLayout.Controls.Add(_txtPath, 1, 0);
        pathLayout.Controls.Add(_btnBrowse, 2, 0);
        pathLayout.Controls.Add(_btnDetect, 3, 0);
        pathLayout.Controls.Add(_btnScan, 4, 0);
        pathLayout.Controls.Add(_chkSubFolders, 5, 0);

        gbPath.Controls.Add(pathLayout);

        var gbQuery = new GroupBox
        {
            Text = "② 物品 ID",
            Dock = DockStyle.Top,
            Height = S(62),
            Padding = new Padding(S(10), S(6), S(10), S(6))
        };

        var queryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1
        };
        queryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(64)));
        queryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        queryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        queryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        queryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _txtItem.Dock = DockStyle.Fill;
        _txtItem.Margin = new Padding(0, S(4), S(6), 0);
        _txtItem.PlaceholderText = "例如 GEAR_AcornShelled / GEAR_Hammer / POWDER_Flour（回车直接检索）";
        _txtItem.KeyDown += OnItemKeyDown;

        _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbMode.Width = S(150);
        _cmbMode.Margin = new Padding(0, S(2), S(6), 0);
        _cmbMode.Items.AddRange(new object[]
        {
            "自动（精确 > 前缀 > 包含）",
            "仅精确匹配",
            "仅子串匹配（排除精确）"
        });
        _cmbMode.SelectedIndex = 0;

        _chkMerge.Text = "合并同名蓝图";
        _chkMerge.Checked = true;
        _chkMerge.AutoSize = true;
        _chkMerge.Margin = new Padding(0, S(6), S(8), 0);

        _btnSearch.Text = "检索";
        _btnSearch.AutoSize = true;
        _btnSearch.Margin = new Padding(0, S(2), 0, 0);
        _btnSearch.Click += OnSearchClick;

        queryLayout.Controls.Add(MakeLabel("物品 ID"), 0, 0);
        queryLayout.Controls.Add(_txtItem, 1, 0);
        queryLayout.Controls.Add(_cmbMode, 2, 0);
        queryLayout.Controls.Add(_chkMerge, 3, 0);
        queryLayout.Controls.Add(_btnSearch, 4, 0);

        gbQuery.Controls.Add(queryLayout);

        // Dock=Top controls stack in reverse z-order: the last one added sits on top. They are added in
        // reverse (gbQuery -> spacer -> gbPath) so "game path" becomes the first row.
        panelTop.Controls.Add(gbQuery);
        panelTop.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(6) });
        panelTop.Controls.Add(gbPath);

        // Middle result area.
        // Panel1MinSize / Panel2MinSize must not be set before layout finishes, or SplitContainer validates
        // SplitterDistance against the default 150 and throws ArgumentOutOfRange.
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = S(6);

        _tabs.Dock = DockStyle.Fill;
        _pageProduct.Controls.Add(_gridProduct);
        _pageIngredient.Controls.Add(_gridIngredient);
        _pageTool.Controls.Add(_gridTool);
        _pageRecipeProduct.Controls.Add(_gridRecipeProduct);
        _pageRecipeIngredient.Controls.Add(_gridRecipeIngredient);
        _pageDefinition.Controls.Add(_gridDefinition);
        _tabs.TabPages.Add(_pageProduct);
        _tabs.TabPages.Add(_pageIngredient);
        _tabs.TabPages.Add(_pageTool);
        _tabs.TabPages.Add(_pageRecipeProduct);
        _tabs.TabPages.Add(_pageRecipeIngredient);
        _tabs.TabPages.Add(_pageDefinition);
        _tabs.SelectedIndexChanged += OnTabChanged;
        _split.Panel1.Controls.Add(_tabs);

        var detailPanel = new Panel { Dock = DockStyle.Fill };

        var detailHeader = new Panel { Dock = DockStyle.Top, Height = S(34), Padding = new Padding(S(6), S(4), S(4), 0) };

        var lblDetail = new Label
        {
            Text = "详情 / 原始 JSON",
            Dock = DockStyle.Left,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        lblDetail.Padding = new Padding(0, S(6), 0, 0);

        _btnCopy.Text = "复制";
        _btnCopy.Width = S(64);
        _btnCopy.Height = S(26);
        _btnCopy.Dock = DockStyle.Right;
        _btnCopy.Click += OnCopyClick;

        _btnExport.Text = "导出 CSV";
        _btnExport.Width = S(92);
        _btnExport.Height = S(26);
        _btnExport.Dock = DockStyle.Right;
        _btnExport.Click += OnExportClick;

        detailHeader.Controls.Add(lblDetail);
        detailHeader.Controls.Add(_btnExport);
        detailHeader.Controls.Add(_btnCopy);

        // Second row: backup and in-place editing.
        // It gets its own row because this strip is wider than the one above; sharing the title row would
        // squeeze out "detail / raw JSON".
        var editBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(36),
            Padding = new Padding(S(6), S(2), S(4), S(2))
        };

        var editFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty
        };

        _btnEdit.Text = "编辑 JSON";
        _btnEdit.AutoSize = true;
        _btnEdit.Margin = new Padding(0, S(2), S(6), 0);
        _btnEdit.Enabled = false;
        _btnEdit.Click += OnEditClick;

        _btnSaveEdit.Text = "保存修改";
        _btnSaveEdit.AutoSize = true;
        _btnSaveEdit.Margin = new Padding(0, S(2), S(6), 0);
        _btnSaveEdit.Enabled = false;
        _btnSaveEdit.Click += OnSaveEditClick;

        _btnBackup.Text = "备份该 modcomponent";
        _btnBackup.AutoSize = true;
        _btnBackup.Margin = new Padding(0, S(2), S(6), 0);
        _btnBackup.Enabled = false;
        _btnBackup.Click += OnBackupClick;

        _btnOpenEditor.Text = "容器编辑器…";
        _btnOpenEditor.AutoSize = true;
        _btnOpenEditor.Margin = new Padding(0, S(2), S(6), 0);
        _btnOpenEditor.Click += OnOpenEditorClick;
        _tips.SetToolTip(_btnOpenEditor, "打开完整的 modcomponent JSON 编辑器，可以对容器里的文件增删改查。");

        _chkAutoBackup.Text = "自动备份";
        _chkAutoBackup.Checked = true;
        _chkAutoBackup.AutoSize = true;
        _chkAutoBackup.Margin = new Padding(0, S(6), 0, 0);
        _chkAutoBackup.CheckedChanged += OnAutoBackupChanged;

        editFlow.Controls.Add(_btnEdit);
        editFlow.Controls.Add(_btnSaveEdit);
        editFlow.Controls.Add(_btnOpenEditor);
        editFlow.Controls.Add(_btnBackup);
        editFlow.Controls.Add(_chkAutoBackup);
        editBar.Controls.Add(editFlow);

        _txtDetail.Dock = DockStyle.Fill;
        _txtDetail.ReadOnly = true;
        _txtDetail.BackColor = Color.White;
        _txtDetail.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _txtDetail.WordWrap = false;
        _txtDetail.ScrollBars = RichTextBoxScrollBars.Both;
        _txtDetail.DetectUrls = false;
        _txtDetail.Text = "在左侧任意结果行上单击，这里会显示该蓝图的完整原料清单与原始 JSON。";

        detailPanel.Controls.Add(_txtDetail);
        detailPanel.Controls.Add(editBar);
        detailPanel.Controls.Add(detailHeader);
        _split.Panel2.Controls.Add(detailPanel);

        // Bottom status bar
        _status.Dock = DockStyle.Bottom;
        _status.SizingGrip = true;

        _lblStatus.Text = "就绪：请先选择游戏路径并点击「扫描 Mods」。";
        _lblStatus.Spring = true;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;

        _lblCounts.Text = "";
        _lblCounts.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _lblCounts.BorderStyle = Border3DStyle.Etched;

        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Width = S(140);

        _status.Items.Add(_lblStatus);
        _status.Items.Add(_lblCounts);
        _status.Items.Add(_progress);

        // Assembly (order decides Dock priority: add Fill first, then Top/Bottom)
        Controls.Add(_split);
        Controls.Add(panelTop);
        Controls.Add(_status);

        ResumeLayout(true);
    }

    private void BuildGrids()
    {
        // Column widths assume the left panel starts around 820px: fixed columns total ~660 and the last one
        // fills the rest. Secondary info (output count, duration, duplicate count) lives in the detail panel
        // instead of taking a column. Blueprint and recipe pages get their own tables, and the kind is constant
        // within each, so that column is dropped - the tab title already says it.
        ConfigureGrid(_gridProduct, new[]
        {
            Col("蓝图名", nameof(SearchRow.BlueprintName), 168),
            Col("产物", nameof(SearchRow.ResultText), 168),
            Col("原料清单", nameof(SearchRow.IngredientsText), 190, fill: true),
            Col("工具", nameof(SearchRow.ToolsText), 112),
            Col("地点", nameof(SearchRow.LocationText), 74),
            Col("来源 Mod", nameof(SearchRow.ModSources), 108)
        }, _gridProduct_SelectionChanged);

        ConfigureGrid(_gridIngredient, new[]
        {
            Col("蓝图名", nameof(SearchRow.BlueprintName), 168),
            Col("用量", nameof(SearchRow.MatchNote), 78),
            Col("产物", nameof(SearchRow.ResultText), 168),
            Col("该配方完整原料", nameof(SearchRow.IngredientsText), 190, fill: true),
            Col("地点", nameof(SearchRow.LocationText), 74),
            Col("来源 Mod", nameof(SearchRow.ModSources), 108)
        }, _gridIngredient_SelectionChanged);

        ConfigureGrid(_gridTool, new[]
        {
            Col("蓝图名", nameof(SearchRow.BlueprintName), 168),
            Col("工具角色", nameof(SearchRow.MatchNote), 82),
            Col("产物", nameof(SearchRow.ResultText), 178),
            Col("原料清单", nameof(SearchRow.IngredientsText), 190, fill: true),
            Col("地点", nameof(SearchRow.LocationText), 74),
            Col("来源 Mod", nameof(SearchRow.ModSources), 108),
            Col("类型", nameof(SearchRow.SourceKindText), 54)
        }, _gridTool_SelectionChanged);

        // Recipe tables keep the raw recipe key (GAMEPLAY_Broth and friends): players use it to look the dish
        // up in a localization pack. Only the detail panel carries it - the key is far wider than a column and
        // in a table would always render as "GAMEPLAY_...".
        ConfigureGrid(_gridRecipeProduct, new[]
        {
            Col("食谱名", nameof(SearchRow.BlueprintName), 140),
            Col("产物", nameof(SearchRow.ResultText), 175),
            Col("原料清单", nameof(SearchRow.IngredientsText), 190, fill: true),
            Col("地点", nameof(SearchRow.LocationText), 60),
            Col("来源 Mod", nameof(SearchRow.ModSources), 96)
        }, _gridRecipeProduct_SelectionChanged);

        ConfigureGrid(_gridRecipeIngredient, new[]
        {
            Col("食谱名", nameof(SearchRow.BlueprintName), 140),
            Col("用量", nameof(SearchRow.MatchNote), 72),
            Col("产物", nameof(SearchRow.ResultText), 175),
            Col("该食谱完整原料", nameof(SearchRow.IngredientsText), 190, fill: true),
            Col("地点", nameof(SearchRow.LocationText), 60),
            Col("来源 Mod", nameof(SearchRow.ModSources), 96)
        }, _gridRecipeIngredient_SelectionChanged);

        // Item definitions: one auto-mapped JSON per row. No recipe columns here, because this data describes
        // the item itself, not how it is made.
        ConfigureGrid(_gridDefinition, new[]
        {
            Col("物品 ID", nameof(DefinitionRow.ItemId), 168),
            Col("判定依据", nameof(DefinitionRow.ConfidenceText), 124),
            Col("组件", nameof(DefinitionRow.ComponentsText), 200, fill: true),
            Col("来源 Mod", nameof(DefinitionRow.ModName), 108),
            Col("定义文件", nameof(DefinitionRow.EntryPath), 152),
            Col("大小", nameof(DefinitionRow.SizeText), 58)
        }, _gridDefinition_SelectionChanged);
    }

    private void ConfigureGrid(DataGridView grid, DataGridViewColumn[] columns, EventHandler onSelectionChanged)
    {
        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(230, 230, 230);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 246, 248);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(4), S(3), S(4), S(3));
        // Headers never wrap: with narrow columns an ellipsis beats a two-line header that inflates the row
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.DefaultCellStyle.Padding = new Padding(S(4), S(2), S(4), S(2));
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 252);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.RowTemplate.Height = S(24);

        grid.Columns.Clear();
        grid.Columns.AddRange(columns);
        // Both events are required:
        //   SelectionChanged - arrow keys and programmatic selection (the selection really changed)
        //   CellClick        - mouse click on the already-selected row (selection unchanged, no event)
        grid.SelectionChanged += onSelectionChanged;
        grid.CellClick += (_, e) => { if (e.RowIndex >= 0) ShowDetail(grid); };
        grid.CellDoubleClick += OnGridDoubleClick;
        grid.CellFormatting += OnGridCellFormatting;
        grid.CellToolTipTextNeeded += OnGridCellToolTip;
    }

    private DataGridViewTextBoxColumn Col(string header, string property, int logicalWidth, bool fill = false)
    {
        var col = new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = property,
            Name = property,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic,
            Width = S(logicalWidth),
            MinimumWidth = S(40)
        };

        if (fill) col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        return col;
    }

    private Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, S(4), S(4), 0)
    };

    /// <summary>
    /// Fills a table. BindingList is required for click-to-sort headers to work.
    /// Generic because the definition table binds a different row type (<see cref="DefinitionRow"/>).
    /// </summary>
    private static void Bind<T>(DataGridView grid, IEnumerable<T> rows) where T : class
    {
        grid.DataSource = new BindingList<T>(rows.ToList());
    }

}

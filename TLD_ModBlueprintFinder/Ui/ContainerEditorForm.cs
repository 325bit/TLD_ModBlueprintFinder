using System.Text;
using System.Text.Json;
using TLD_ModBlueprintFinder.Editing;
using TLD_ModBlueprintFinder.Models;

namespace TLD_ModBlueprintFinder.Ui;

/// <summary>
/// modcomponent JSON editor - create, read, update and delete files inside one container.
/// Complementary to the main window's "select a row, edit its JSON": that one edits a single entry found
/// by search, this one opens the container to browse its structure and add or remove files.
/// Every write goes through the whole-archive rewrite of <see cref="ModComponentArchive"/>, and the editor
/// holds no disk state - each read goes back to the container.
/// </summary>
public sealed class ContainerEditorForm : Form
{
    private readonly IReadOnlyList<ContainerSummary> _containers;
    private readonly bool _autoBackupDefault;
    private readonly string? _initialContainerPath;
    private readonly string? _initialEntryPath;

    /// <summary>Whether anything was written in this session. The main window rescans when it is true.</summary>
    public bool AnyChange { get; private set; }

    // Controls
    private readonly ComboBox _cmbContainer = new();
    private readonly TextBox _txtFilter = new();
    private readonly CheckBox _chkJsonOnly = new();
    private readonly Label _lblCount = new();
    private readonly SplitContainer _split = new();
    private readonly ListView _list = new();
    private readonly RichTextBox _editor = new();
    private readonly Label _lblCurrent = new();
    private readonly Button _btnBackup = new();
    private readonly CheckBox _chkAutoBackup = new();
    private readonly Button _btnReload = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnRename = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnRevert = new();
    private readonly Button _btnSave = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblStatus = new();
    private readonly ToolTip _tips = new();

    // State
    private List<ArchiveEntry> _entries = new();
    private ArchiveEntry? _current;
    private string _original = "";
    private bool _dirty;
    private bool _suppress;
    private int _lastContainerIndex;

    public ContainerEditorForm(
        IReadOnlyList<ContainerSummary> containers,
        string? initialContainerPath,
        string? initialEntryPath,
        bool autoBackup)
    {
        _containers = containers;
        _autoBackupDefault = autoBackup;
        _initialContainerPath = initialContainerPath;
        _initialEntryPath = initialEntryPath;

        BuildUi();
        Load += OnFormLoad;
        FormClosing += OnFormClosing;
    }

    private ContainerSummary? SelectedContainer => (_cmbContainer.SelectedItem as ContainerChoice)?.Summary;

    private int S(int logical) => IsHandleCreated
        ? (int)Math.Round(logical * DeviceDpi / 96.0)
        : logical;

    // Layout

    private void BuildUi()
    {
        SuspendLayout();

        Text = "modcomponent JSON 编辑器";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(S(1120), S(720));
        MinimumSize = new Size(S(820), S(520));
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        ShowInTaskbar = false;

        // Top: container selection + filter
        var top = new Panel { Dock = DockStyle.Top, Height = S(74), Padding = new Padding(S(12), S(8), S(12), 0) };

        var row1 = new TableLayoutPanel { Dock = DockStyle.Top, Height = S(30), ColumnCount = 4, RowCount = 1 };
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(52)));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _cmbContainer.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbContainer.Dock = DockStyle.Fill;
        _cmbContainer.Margin = new Padding(0, S(2), S(8), 0);
        _cmbContainer.SelectedIndexChanged += OnContainerChanged;

        _btnBackup.Text = "备份该 modcomponent";
        _btnBackup.AutoSize = true;
        _btnBackup.Margin = new Padding(0, S(1), S(8), 0);
        _btnBackup.Click += OnBackupClick;

        _chkAutoBackup.Text = "自动备份";
        _chkAutoBackup.AutoSize = true;
        _chkAutoBackup.Checked = _autoBackupDefault;
        _chkAutoBackup.Margin = new Padding(0, S(6), 0, 0);
        _tips.SetToolTip(_chkAutoBackup, "勾选后，每次写入/删除前都会先把整个 modcomponent 备份一份。");

        row1.Controls.Add(MakeLabel("容器"), 0, 0);
        row1.Controls.Add(_cmbContainer, 1, 0);
        row1.Controls.Add(_btnBackup, 2, 0);
        row1.Controls.Add(_chkAutoBackup, 3, 0);

        var row2 = new TableLayoutPanel { Dock = DockStyle.Top, Height = S(32), ColumnCount = 4, RowCount = 1 };
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(52)));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _txtFilter.Dock = DockStyle.Fill;
        _txtFilter.Margin = new Padding(0, S(3), S(8), 0);
        _txtFilter.PlaceholderText = "按路径过滤，例如 blueprints/ 、acorn（区分大小写不敏感）";
        _txtFilter.TextChanged += (_, _) => OnFilterChanged();

        _chkJsonOnly.Text = "只看 .json";
        _chkJsonOnly.Checked = true;
        _chkJsonOnly.AutoSize = true;
        _chkJsonOnly.Margin = new Padding(0, S(7), S(10), 0);
        _chkJsonOnly.CheckedChanged += (_, _) => OnFilterChanged();

        _btnReload.Text = "刷新列表";
        _btnReload.AutoSize = true;
        _btnReload.Margin = new Padding(0, S(3), 0, 0);
        _btnReload.Click += OnReloadClick;

        _lblCount.AutoSize = true;
        _lblCount.Margin = new Padding(0, S(8), 0, 0);
        _lblCount.ForeColor = Color.DimGray;

        row2.Controls.Add(MakeLabel("过滤"), 0, 0);
        row2.Controls.Add(_txtFilter, 1, 0);
        row2.Controls.Add(_chkJsonOnly, 2, 0);
        row2.Controls.Add(_btnReload, 3, 0);

        top.Controls.Add(row2);
        top.Controls.Add(row1);

        // Middle: entry list + editor
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = S(6);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.SelectedIndexChanged += OnListSelectionChanged;

        var listHost = new Panel { Dock = DockStyle.Fill };
        var listHead = new Panel { Dock = DockStyle.Bottom, Height = S(24), Padding = new Padding(S(2), S(2), 0, 0) };
        listHead.Controls.Add(_lblCount);
        listHost.Controls.Add(_list);
        listHost.Controls.Add(listHead);
        _split.Panel1.Controls.Add(listHost);

        var editorHost = new Panel { Dock = DockStyle.Fill };

        _lblCurrent.Dock = DockStyle.Top;
        _lblCurrent.Height = S(26);
        _lblCurrent.Padding = new Padding(S(6), S(4), S(6), 0);
        _lblCurrent.Text = "未选中条目";
        _lblCurrent.AutoEllipsis = true;
        _lblCurrent.ForeColor = Color.FromArgb(60, 60, 60);

        _editor.Dock = DockStyle.Fill;
        _editor.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _editor.WordWrap = false;
        _editor.ScrollBars = RichTextBoxScrollBars.Both;
        _editor.DetectUrls = false;
        _editor.BorderStyle = BorderStyle.None;
        _editor.AcceptsTab = true;
        _editor.ReadOnly = true;
        _editor.BackColor = Color.FromArgb(246, 246, 246);
        _editor.TextChanged += OnEditorTextChanged;
        _editor.KeyDown += OnEditorKeyDown;

        editorHost.Controls.Add(_editor);
        editorHost.Controls.Add(_lblCurrent);
        _split.Panel2.Controls.Add(editorHost);

        // Bottom: actions
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = S(46),
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(S(12), 0, S(12), 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty
        };

        _btnNew.Text = "新建 JSON";
        _btnNew.AutoSize = true;
        _btnNew.Margin = new Padding(0, S(8), S(6), 0);
        _btnNew.Click += OnNewClick;

        _btnRename.Text = "重命名";
        _btnRename.AutoSize = true;
        _btnRename.Margin = new Padding(0, S(8), S(6), 0);
        _btnRename.Click += OnRenameClick;

        _btnDelete.Text = "删除";
        _btnDelete.AutoSize = true;
        _btnDelete.Margin = new Padding(0, S(8), S(6), 0);
        _btnDelete.Click += OnDeleteClick;

        _btnRevert.Text = "还原";
        _btnRevert.AutoSize = true;
        _btnRevert.Margin = new Padding(0, S(8), S(6), 0);
        _tips.SetToolTip(_btnRevert, "放弃未保存的修改，重新从 modcomponent 读取这个条目。");
        _btnRevert.Click += OnRevertClick;

        flow.Controls.Add(_btnNew);
        flow.Controls.Add(_btnRename);
        flow.Controls.Add(_btnDelete);
        flow.Controls.Add(_btnRevert);

        _btnSave.Text = "保存修改";
        _btnSave.AutoSize = true;
        _btnSave.Margin = new Padding(S(8), S(8), 0, 0);
        _btnSave.Click += (_, _) => SaveCurrent();
        _tips.SetToolTip(_btnSave, "把编辑器里的内容写回 modcomponent（Ctrl+S 同效）。");

        bottom.Controls.Add(flow, 0, 0);
        bottom.Controls.Add(_btnSave, 1, 0);

        // Status bar
        _status.Dock = DockStyle.Bottom;
        _status.SizingGrip = false;
        _lblStatus.Text = "就绪。";
        _lblStatus.Spring = true;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _status.Items.Add(_lblStatus);

        Controls.Add(_split);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(_status);

        ResumeLayout(true);

        UpdateButtons();
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        // Column widths and splitter positions are not covered by AutoScaleMode.Dpi; set them after the handle exists
        try
        {
            if (_split.Width > 0)
            {
                _split.SplitterDistance = (int)(_split.Width * 0.42);
                _split.Panel1MinSize = S(260);
                _split.Panel2MinSize = S(320);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Ignored on a tiny window: SplitContainer keeps its own limits
        }

        // The first column absorbs the remaining width: three hard-coded widths overflow the panel and add a
        // horizontal scrollbar. The vertical scrollbar width must be reserved too - once it appears, so does the
        // horizontal one.
        var fixedWidth = S(66) + S(94) + SystemInformation.VerticalScrollBarWidth + S(8);
        var pathWidth = Math.Max(S(200), _list.ClientSize.Width - fixedWidth);

        _list.Columns.Clear();
        _list.Columns.Add("条目路径", pathWidth);
        _list.Columns.Add("大小", S(66));
        _list.Columns.Add("修改时间", S(94));

        Initialize(_initialContainerPath, _initialEntryPath);
    }

    private Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, S(4), S(4), 0)
    };

    // Initialization and loading

    private void Initialize(string? containerPath, string? entryPath)
    {
        if (_containers.Count == 0)
        {
            SetStatus("没有可编辑的 modcomponent，请先在主窗口点「扫描 Mods」。", Color.Firebrick);
            UpdateButtons();
            return;
        }

        var index = 0;

        _suppress = true;
        try
        {
            foreach (var c in _containers) _cmbContainer.Items.Add(new ContainerChoice(c));

            if (!string.IsNullOrEmpty(containerPath))
            {
                for (var i = 0; i < _containers.Count; i++)
                {
                    if (PathEquals(_containers[i].Path, containerPath)) { index = i; break; }
                }
            }

            _cmbContainer.SelectedIndex = index;
            _lastContainerIndex = index;
        }
        finally
        {
            _suppress = false;
        }

        LoadContainer(_containers[Math.Max(0, index)], entryPath);
    }

    private void OnContainerChanged(object? sender, EventArgs e)
    {
        if (_suppress) return;

        var container = SelectedContainer;
        if (container is null) return;

        if (!TryLeaveCurrent())
        {
            // User cancelled: put the combo box back on the previous container
            _suppress = true;
            try { _cmbContainer.SelectedIndex = _lastContainerIndex; }
            finally { _suppress = false; }

            return;
        }

        _lastContainerIndex = _cmbContainer.SelectedIndex;
        LoadContainer(container, null);
    }

    private void LoadContainer(ContainerSummary container, string? selectPath)
    {
        ClearEditor();

        try
        {
            _entries = ModComponentArchive.ListEntries(container.Path).ToList();
        }
        catch (Exception ex)
        {
            _entries = new List<ArchiveEntry>();
            RefreshList();
            SetStatus("读取失败：" + ex.Message, Color.Firebrick);
            MessageBox.Show(this, $"打不开这个 modcomponent：\n\n{ex.Message}", "读取失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RefreshList();

        var target = selectPath ?? _entries.FirstOrDefault(e => e.IsJson)?.Path;
        if (target is null || !SelectPath(target, reload: true))
        {
            SetStatus($"{container.Name}：里面没有 .json 文件。", Color.DarkOrange);
            return;
        }

        var total = _entries.Count(e => !e.IsDirectory);
        SetStatus($"{container.Name}：{total} 个文件，{_entries.Count(e => e.IsJson)} 个 .json。", Color.ForestGreen);
    }

    private void RefreshList()
    {
        var prev = _suppress;
        _suppress = true;

        try
        {
            _list.BeginUpdate();
            _list.Items.Clear();

            var filter = _txtFilter.Text.Trim();
            var jsonOnly = _chkJsonOnly.Checked;
            var total = 0;

            foreach (var entry in _entries)
            {
                if (entry.IsDirectory) continue;
                total++;

                if (jsonOnly && !entry.IsJson) continue;
                if (filter.Length > 0 &&
                    entry.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var item = new ListViewItem(entry.Path) { Tag = entry };
                item.SubItems.Add(entry.SizeText);
                item.SubItems.Add(entry.TimeText);
                _list.Items.Add(item);
            }

            _list.EndUpdate();
            _lblCount.Text = $"显示 {_list.Items.Count} / 共 {total} 个文件";
        }
        finally
        {
            _suppress = prev;
        }
    }

    private void OnFilterChanged()
    {
        if (_suppress) return;

        RefreshList();

        // The current entry is still listed: keep it selected and do not re-read, so the editor keeps its content
        if (_current is not null && SelectPath(_current.Path, reload: false)) return;

        // Filtered out with no unsaved changes: jump to the first entry, so filtering also locates
        if (!_dirty && _list.Items.Count > 0)
        {
            SelectPath(((ArchiveEntry)_list.Items[0].Tag!).Path, reload: true);
            return;
        }

        // With unsaved changes, leave the editor alone - the content stays and clearing the filter finds it again
        var prev = _suppress;
        _suppress = true;
        try { _list.SelectedItems.Clear(); }
        finally { _suppress = prev; }
    }

    // Selection and loading

    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppress) return;
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not ArchiveEntry entry) return;

        if (!TryLeaveCurrent())
        {
            if (_current is not null) SelectPath(_current.Path, reload: false);
            return;
        }

        LoadEntry(entry);
    }

    private bool SelectPath(string path, bool reload)
    {
        var item = FindItem(path);
        if (item is null) return false;

        var prev = _suppress;
        _suppress = true;

        try
        {
            _list.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
            _list.EnsureVisible(item.Index);
        }
        finally
        {
            _suppress = prev;
        }

        if (reload && item.Tag is ArchiveEntry entry) LoadEntry(entry);
        return true;
    }

    private ListViewItem? FindItem(string path)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is ArchiveEntry entry && PathEquals(entry.Path, path)) return item;
        }

        return null;
    }

    /// <summary>Loads entry content into the editor. Binary entries are read-only so a texture is never written back as text.</summary>
    private void LoadEntry(ArchiveEntry entry)
    {
        var container = SelectedContainer;
        if (container is null) return;

        EntryContent? content;
        try
        {
            content = ModComponentArchive.ReadContent(container.Path, entry.Path);
        }
        catch (Exception ex)
        {
            SetStatus("读取条目失败：" + ex.Message, Color.Firebrick);
            return;
        }

        _current = entry;

        var prev = _suppress;
        _suppress = true;

        try
        {
            if (content is null)
            {
                _original = "";
                _editor.ReadOnly = true;
                _editor.Text = "";
            }
            else if (content.IsBinary)
            {
                _original = "";
                _editor.ReadOnly = true;
                _editor.Text = $"（这是一个二进制文件，{ArchiveEntry.FormatSize(content.SizeBytes)}。" +
                                "编辑器只处理文本 JSON，这个条目不能在这里改。）";
            }
            else
            {
                _original = content.Text;
                _editor.ReadOnly = false;
                _editor.Text = content.Text;
                _editor.SelectionStart = 0;
                _editor.ScrollToCaret();
            }

            _editor.BackColor = _editor.ReadOnly ? Color.FromArgb(246, 246, 246) : Color.White;
        }
        finally
        {
            _suppress = prev;
        }

        UpdateDirty();
    }

    private void ClearEditor()
    {
        _current = null;
        _original = "";

        var prev = _suppress;
        _suppress = true;

        try
        {
            _editor.Text = "";
            _editor.ReadOnly = true;
            _editor.BackColor = Color.FromArgb(246, 246, 246);
        }
        finally
        {
            _suppress = prev;
        }

        UpdateDirty();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppress) return;
        UpdateDirty();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control || e.KeyCode != Keys.S) return;

        e.SuppressKeyPress = true;
        SaveCurrent();
    }

    private void UpdateDirty()
    {
        _dirty = _current is not null && !_editor.ReadOnly && _editor.Text != _original;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var has = _current is not null;
        var editable = has && !_editor.ReadOnly;
        var hasContainer = SelectedContainer is not null;

        _btnNew.Enabled = hasContainer;
        _btnBackup.Enabled = hasContainer;
        _btnReload.Enabled = hasContainer;
        _btnRename.Enabled = has;
        _btnDelete.Enabled = has;
        _btnRevert.Enabled = editable && _dirty;
        _btnSave.Enabled = editable && _dirty;

        _lblCurrent.Text = _current is null
            ? "未选中条目"
            : (_dirty ? "● " : "") + _current.Path + (editable ? "" : "    只读");
    }

    // New file

    private void OnNewClick(object? sender, EventArgs e)
    {
        var container = SelectedContainer;
        if (container is null) return;
        if (!TryLeaveCurrent()) return;

        var suggested = SuggestNewPath();

        var input = PromptText(this, "新建 JSON 条目",
            "条目路径（相对 modcomponent 根目录，必须以 .json 结尾）",
            suggested,
            value =>
            {
                var error = ModComponentArchive.ValidateEntryPath(value);
                if (error is not null) return error;

                var normalized = ModComponentArchive.Normalize(value);
                if (ModComponentArchive.Exists(container.Path, normalized))
                    return $"「{normalized}」已经存在了。要改它请直接在列表里选中，或先换个路径。";

                return null;
            });

        if (input is null) return;

        var path = ModComponentArchive.Normalize(input);
        string? backup = null;

        try
        {
            if (_chkAutoBackup.Checked) backup = ModBackup.Create(container.Path);
            ModComponentArchive.WriteText(container.Path, path, TemplateFor(path));
        }
        catch (Exception ex)
        {
            ShowWriteError("新建失败", ex, backup);
            return;
        }

        AnyChange = true;

        try { _entries = ModComponentArchive.ListEntries(container.Path).ToList(); }
        catch { /* 列表刷新失败不影响已经写好的文件 */ }

        RefreshList();
        SelectPath(path, reload: true);
        SetStatus($"已新建 {path}" + (backup is null ? "" : $"，备份：{Path.GetFileName(backup)}"), Color.ForestGreen);
    }

    /// <summary>Default path for a new file: prefer a folder the container already has, not an invented structure.</summary>
    private string SuggestNewPath()
    {
        var hasBlueprints = _entries.Any(e => StartsWithFolder(e.Path, "blueprints"));
        var hasRecipes = _entries.Any(e => StartsWithFolder(e.Path, "recipes"));
        var hasAutoMapped = _entries.Any(e => StartsWithFolder(e.Path, "auto-mapped") || StartsWithFolder(e.Path, "auto_mapped"));

        var folder = hasBlueprints ? "blueprints"
            : hasRecipes ? "recipes"
            : hasAutoMapped ? "auto-mapped"
            : "blueprints";

        var stem = folder switch
        {
            "recipes" => "new-recipe",
            "auto-mapped" => "new-item",
            _ => "new-blueprint"
        };

        var candidate = $"{folder}/{stem}.json";
        for (var i = 2; _entries.Any(e => PathEquals(e.Path, candidate)); i++)
            candidate = $"{folder}/{stem}-{i}.json";

        return candidate;
    }

    /// <summary>
    /// Initial content for a new file. Each folder gets a skeleton that already works - an empty file is
    /// silently skipped by ModComponent, and the user thinks the file was created successfully.
    /// </summary>
    private static string TemplateFor(string path)
    {
        if (StartsWithFolder(path, "recipes"))
        {
            return """
{
    "RecipeName": "GAMEPLAY_NewRecipe",
    "RecipeDescription": "GAMEPLAY_NewRecipeDescription",
    "RequiredSkillLevel": 1,
    "BlueprintData": {
        "Name": "REC_NewRecipe",
        "RequiredGear": [
            { "Item": "GEAR_ScrapMetal", "Count": 1 }
        ],
        "CraftedResult": "GEAR_ScrapMetal",
        "CraftedResultCount": 1,
        "DurationMinutes": 10,
        "RequiredCraftingLocation": "Workbench"
    }
}

""";
        }

        if (StartsWithFolder(path, "auto-mapped") || StartsWithFolder(path, "auto_mapped"))
        {
            return """
{
    "ModGenericComponent": {
        "DisplayNameLocalizationId": "GAMEPLAY_NewItem",
        "DescriptionLocalizationId": "GAMEPLAY_NewItemDescription"
    }
}

""";
        }

        return """
{
    "Name": "NewBlueprint",
    "RequiredGear": [
        { "Item": "GEAR_ScrapMetal", "Count": 1 }
    ],
    "CraftedResult": "GEAR_ScrapMetal",
    "CraftedResultCount": 1,
    "DurationMinutes": 10,
    "RequiredCraftingLocation": "Workbench"
}

""";
    }

    // Save

    private bool SaveCurrent()
    {
        var container = SelectedContainer;
        if (container is null || _current is null || _editor.ReadOnly) return false;

        var text = _editor.Text;
        var path = _current.Path;

        if (text == _original)
        {
            SetStatus("内容没有变化，未写入。", Color.DimGray);
            return true;
        }

        if (!IsValidJson(text, out var jsonError))
        {
            var answer = MessageBox.Show(this,
                "这段 JSON 解析不了：\n\n" + jsonError +
                "\n\n仍然保存吗？游戏大概率会读不出这个文件。",
                "JSON 语法有问题", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return false;
        }

        string? backup = null;

        try
        {
            if (_chkAutoBackup.Checked) backup = ModBackup.Create(container.Path);
            ModComponentArchive.WriteText(container.Path, path, text);
        }
        catch (Exception ex)
        {
            ShowWriteError("保存失败", ex, backup);
            return false;
        }

        AnyChange = true;
        _original = text;

        RefreshList();
        SelectPath(path, reload: false);
        UpdateDirty();

        SetStatus($"已写入 {path}" + (backup is null ? "" : $"，备份：{Path.GetFileName(backup)}"), Color.ForestGreen);
        return true;
    }

    private void OnRevertClick(object? sender, EventArgs e)
    {
        if (_current is null) return;

        if (_dirty && MessageBox.Show(this,
                $"放弃对「{_current.Path}」的未保存修改，重新从 modcomponent 读取？",
                "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        LoadEntry(_current);
        SetStatus($"已从 modcomponent 重新读取 {_current.Path}。", Color.DimGray);
    }

    // Rename / delete

    private void OnRenameClick(object? sender, EventArgs e)
    {
        var container = SelectedContainer;
        if (container is null || _current is null) return;

        var oldPath = _current.Path;

        if (!TryLeaveCurrent()) return;

        var input = PromptText(this, "重命名条目",
            "新的条目路径（内容原样搬过去，不做重新编码）",
            oldPath,
            value =>
            {
                var error = ModComponentArchive.ValidateEntryPath(value);
                if (error is not null) return error;

                var normalized = ModComponentArchive.Normalize(value);
                if (PathEquals(normalized, oldPath)) return "新路径和原路径一样。";
                if (ModComponentArchive.Exists(container.Path, normalized))
                    return $"「{normalized}」已经存在了。";

                return null;
            });

        if (input is null) return;

        var newPath = ModComponentArchive.Normalize(input);
        string? backup = null;

        try
        {
            if (_chkAutoBackup.Checked) backup = ModBackup.Create(container.Path);
            ModComponentArchive.Rename(container.Path, oldPath, newPath);
        }
        catch (Exception ex)
        {
            ShowWriteError("重命名失败", ex, backup);
            return;
        }

        AnyChange = true;

        try { _entries = ModComponentArchive.ListEntries(container.Path).ToList(); }
        catch { /* 同上 */ }

        RefreshList();
        SelectPath(newPath, reload: true);
        SetStatus($"已重命名：{oldPath} → {newPath}", Color.ForestGreen);
    }

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        var container = SelectedContainer;
        if (container is null || _current is null) return;

        var path = _current.Path;

        var confirm = MessageBox.Show(this,
            $"要从「{container.Name}」里删掉这个条目吗？\n\n{path}\n\n" +
            "删除会把整个 modcomponent 重写一遍，删掉的条目在游戏里就没了。" +
            (_chkAutoBackup.Checked ? "\n（已勾选自动备份，删之前会先备份一份）" : "\n（没有勾选自动备份，删了就找不回来）"),
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.Yes) return;

        var index = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
        string? backup = null;

        try
        {
            if (_chkAutoBackup.Checked) backup = ModBackup.Create(container.Path);
            ModComponentArchive.Delete(container.Path, path);
        }
        catch (Exception ex)
        {
            ShowWriteError("删除失败", ex, backup);
            return;
        }

        AnyChange = true;
        _current = null;

        try { _entries = ModComponentArchive.ListEntries(container.Path).ToList(); }
        catch { _entries.RemoveAll(x => PathEquals(x.Path, path)); }

        RefreshList();

        if (_list.Items.Count > 0)
        {
            var next = Math.Clamp(index, 0, _list.Items.Count - 1);
            SelectPath(((ArchiveEntry)_list.Items[next].Tag!).Path, reload: true);
        }
        else
        {
            ClearEditor();
        }

        SetStatus($"已删除 {path}" + (backup is null ? "" : $"，备份：{Path.GetFileName(backup)}"), Color.ForestGreen);
    }

    // Backup / refresh

    private void OnBackupClick(object? sender, EventArgs e)
    {
        var container = SelectedContainer;
        if (container is null) return;

        try
        {
            var path = ModBackup.Create(container.Path);
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

    private void OnReloadClick(object? sender, EventArgs e)
    {
        var container = SelectedContainer;
        if (container is null) return;

        if (!TryLeaveCurrent()) return;

        LoadContainer(container, _current?.Path);
    }

    // Finish

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!TryLeaveCurrent()) e.Cancel = true;
    }

    /// <summary>Handles unsaved changes before leaving the current entry. False means cancelled: the caller must abort.</summary>
    private bool TryLeaveCurrent()
    {
        if (!_dirty || _current is null) return true;

        var answer = MessageBox.Show(this,
            $"「{_current.Path}」还有未保存的修改。\n\n要先保存吗？",
            "未保存的修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        return answer switch
        {
            DialogResult.Yes => SaveCurrent(),
            DialogResult.No => true,
            _ => false
        };
    }

    private void ShowWriteError(string title, Exception ex, string? backup)
    {
        var extra = backup is null
            ? ""
            : $"\n\n原文件已经备份到：\n{backup}\n（写入没成功，原 modcomponent 未被改动）";

        MessageBox.Show(this, $"{title}：\n\n{ex.Message}{extra}", title,
            MessageBoxButtons.OK, MessageBoxIcon.Error);

        SetStatus($"{title}：{ex.Message}", Color.Firebrick);
    }

    private void SetStatus(string text, Color color)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = color;
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

    private static bool PathEquals(string a, string b) =>
        string.Equals(ModComponentArchive.Normalize(a), ModComponentArchive.Normalize(b),
            StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithFolder(string path, string folder) =>
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private sealed record ContainerChoice(ContainerSummary Summary)
    {
        public override string ToString() => $"{Summary.Name}    ·    {Summary.Path}";
    }

    // Single-line input box (WinForms has no built-in InputBox)

    /// <summary>
    /// Shows a validated single-line input box. Validation errors are reported in place instead of closing and
    /// reopening the dialog as the native InputBox does, so the typed path survives.
    /// </summary>
    private static string? PromptText(
        IWin32Window owner,
        string title,
        string caption,
        string initial,
        Func<string, string?> validate)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(600, 148),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96F, 96F)
        };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 6) };

        var lbl = new Label { Dock = DockStyle.Top, Height = 34, Text = caption };
        var txt = new TextBox { Dock = DockStyle.Top, Text = initial };

        body.Controls.Add(txt);
        body.Controls.Add(lbl);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 14, 0)
        };

        var ok = new Button { Text = "确定", Width = 88, Height = 28, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new Button
        {
            Text = "取消",
            Width = 88,
            Height = 28,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(0)
        };

        ok.Click += (_, _) =>
        {
            var error = validate(txt.Text.Trim());
            if (error is not null)
            {
                MessageBox.Show(form, error, "路径无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txt.Focus();
                txt.SelectAll();
                return;
            }

            form.DialogResult = DialogResult.OK;
        };

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Controls.Add(body);
        form.Controls.Add(buttons);

        txt.Focus();
        txt.SelectAll();

        return form.ShowDialog(owner) == DialogResult.OK
            ? txt.Text.Trim()
            : null;
    }
}

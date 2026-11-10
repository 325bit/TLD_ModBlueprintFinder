using System.Runtime.InteropServices;

namespace TLD_ModBlueprintFinder.Ui;

internal enum ThemeMode
{
    /// <summary>The original look: white surfaces, system chrome.</summary>
    Light,

    /// <summary>Dark surfaces, low glare. Everything else in the UI is identical.</summary>
    Dark
}

/// <summary>
/// What a status line is saying, instead of which colour it uses. The palette owns the colour, so the same
/// "warning" reads as orange on white and as amber on grey without a second lookup at the call site.
/// </summary>
internal enum StatusTone
{
    Default,
    Ok,
    Warn,
    Error,
    Muted
}

/// <summary>
/// One complete colour scheme. Every colour the UI uses is named here rather than written at the call site,
/// so a second theme is a second instance and nothing else - no control needs to know which mode is active.
/// </summary>
internal sealed class Palette
{
    /// <summary>Form and panel background. Everything without an explicit colour inherits it.</summary>
    public required Color Window { get; init; }

    /// <summary>Grids, text boxes and the JSON editor while it is editable.</summary>
    public required Color Surface { get; init; }

    /// <summary>Alternate grid rows, and buttons in the dark scheme.</summary>
    public required Color SurfaceAlt { get; init; }

    /// <summary>JSON shown but not editable (the container editor, or a binary entry).</summary>
    public required Color SurfaceReadOnly { get; init; }

    /// <summary>The detail box while "Edit JSON" is active - the colour itself says "you can type here".</summary>
    public required Color SurfaceEditing { get; init; }

    /// <summary>Grid column headers and the status strip.</summary>
    public required Color Header { get; init; }

    /// <summary>Grid lines, button borders, splitter.</summary>
    public required Color Border { get; init; }

    public required Color Text { get; init; }

    public required Color TextMuted { get; init; }

    public required Color Selection { get; init; }

    public required Color SelectionText { get; init; }

    /// <summary>Underline of the selected tab, and the accent used for emphasis.</summary>
    public required Color Accent { get; init; }

    /// <summary>Source cell of a row that comes from the game itself.</summary>
    public required Color VanillaTint { get; init; }

    /// <summary>A hit that only matched by prefix / by substring. Must stand out from an exact hit.</summary>
    public required Color PrefixMatch { get; init; }

    public required Color ContainsMatch { get; init; }

    public required Color Success { get; init; }

    public required Color Warning { get; init; }

    public required Color Error { get; init; }

    public static readonly Palette Light = new()
    {
        Window = Color.FromArgb(240, 240, 240),
        Surface = Color.White,
        SurfaceAlt = Color.FromArgb(250, 250, 252),
        SurfaceReadOnly = Color.FromArgb(246, 246, 246),
        SurfaceEditing = Color.FromArgb(255, 252, 235),
        Header = Color.FromArgb(245, 246, 248),
        Border = Color.FromArgb(230, 230, 230),
        Text = Color.FromArgb(0, 0, 0),
        TextMuted = Color.FromArgb(105, 105, 105),
        Selection = Color.FromArgb(0, 120, 215),
        SelectionText = Color.White,
        Accent = Color.FromArgb(0, 120, 215),
        VanillaTint = Color.FromArgb(0, 108, 122),
        PrefixMatch = Color.FromArgb(181, 96, 6),
        ContainsMatch = Color.FromArgb(120, 120, 120),
        Success = Color.FromArgb(34, 139, 34),
        Warning = Color.FromArgb(255, 140, 0),
        Error = Color.FromArgb(178, 34, 34)
    };

    public static readonly Palette Dark = new()
    {
        Window = Color.FromArgb(30, 33, 38),
        Surface = Color.FromArgb(38, 42, 48),
        SurfaceAlt = Color.FromArgb(43, 48, 55),
        SurfaceReadOnly = Color.FromArgb(32, 36, 41),
        SurfaceEditing = Color.FromArgb(51, 47, 30),
        Header = Color.FromArgb(46, 51, 58),
        Border = Color.FromArgb(59, 66, 75),
        Text = Color.FromArgb(221, 226, 232),
        TextMuted = Color.FromArgb(152, 161, 172),
        Selection = Color.FromArgb(42, 86, 128),
        SelectionText = Color.White,
        Accent = Color.FromArgb(78, 161, 224),
        VanillaTint = Color.FromArgb(106, 205, 196),
        PrefixMatch = Color.FromArgb(232, 163, 61),
        ContainsMatch = Color.FromArgb(138, 146, 155),
        Success = Color.FromArgb(86, 184, 112),
        Warning = Color.FromArgb(224, 160, 58),
        Error = Color.FromArgb(224, 108, 108)
    };
}

/// <summary>
/// Applies the active <see cref="Palette"/> to a live window.
/// WinForms has no style cascade: colours set once stay, so switching the mode means walking the tree and
/// assigning again. The walk only touches controls that WinForms would leave in a system colour - the rest
/// inherit from the form and need no code.
/// </summary>
internal static class Theme
{
    public static ThemeMode Mode { get; set; } = ThemeMode.Light;

    public static bool IsDark => Mode == ThemeMode.Dark;

    public static Palette Current => IsDark ? Palette.Dark : Palette.Light;

    public static Color Tone(StatusTone tone) => tone switch
    {
        StatusTone.Ok => Current.Success,
        StatusTone.Warn => Current.Warning,
        StatusTone.Error => Current.Error,
        StatusTone.Muted => Current.TextMuted,
        _ => Current.Text
    };

    /// <summary>Recolours a whole window, including the title bar and the native scroll bars.</summary>
    public static void Apply(Form form)
    {
        var palette = Current;

        form.BackColor = palette.Window;
        form.ForeColor = palette.Text;

        ApplyToChildren(form, palette);

        UseDarkTitleBar(form, IsDark);
    }

    private static void ApplyToChildren(Control parent, Palette palette)
    {
        foreach (Control child in parent.Controls)
        {
            Style(child, palette);

            // TabPage is not painted by its parent, so it is handled here and its own children below.
            if (child.HasChildren) ApplyToChildren(child, palette);
        }
    }

    private static void Style(Control control, Palette palette)
    {
        switch (control)
        {
            case DataGridView grid:
                StyleGrid(grid, palette);
                break;

            case TabControl tabs:
                StyleTabs(tabs, palette);
                break;

            case TabPage page:
                // UseVisualStyleBackColor wins over BackColor, so the flag alone restores the light look.
                page.UseVisualStyleBackColor = !IsDark;
                page.BackColor = palette.Surface;
                page.ForeColor = palette.Text;
                break;

            case StatusStrip strip:
                StyleStatusStrip(strip, palette);
                break;

            case ListView list:
                list.BackColor = palette.Surface;
                list.ForeColor = palette.Text;
                StyleList(list, palette);
                UseDarkScrollBars(list);
                break;

            // RichTextBox is deliberately left without a background: the two callers disagree on what an
            // editable box looks like (white here, pale yellow in the detail panel), so they set it themselves.
            case RichTextBox box:
                box.ForeColor = palette.Text;
                UseDarkScrollBars(box);
                break;

            case TextBox text:
                text.BackColor = palette.Surface;
                text.ForeColor = palette.Text;
                // Fixed3D draws its top/left edge in white, which turns a dark field into a bright rectangle.
                text.BorderStyle = IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                break;

            case ComboBox combo:
                StyleCombo(combo, palette);
                break;

            case Button button:
                StyleButton(button, palette);
                break;

            case CheckBox check:
                check.ForeColor = palette.Text;
                break;

            case SplitContainer split:
                split.BackColor = IsDark ? palette.Border : SystemColors.Control;
                break;

            case GroupBox group:
                group.BackColor = palette.Window;
                group.ForeColor = palette.Text;
                break;
        }
    }

    private static void StyleGrid(DataGridView grid, Palette palette)
    {
        grid.BackgroundColor = palette.Surface;
        grid.GridColor = palette.Border;

        grid.DefaultCellStyle.BackColor = palette.Surface;
        grid.DefaultCellStyle.ForeColor = palette.Text;
        grid.DefaultCellStyle.SelectionBackColor = palette.Selection;
        grid.DefaultCellStyle.SelectionForeColor = palette.SelectionText;

        grid.AlternatingRowsDefaultCellStyle.BackColor = palette.SurfaceAlt;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = palette.Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = palette.Selection;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = palette.SelectionText;

        grid.ColumnHeadersDefaultCellStyle.BackColor = palette.Header;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Text;

        // "Raised" separators are drawn with 3D highlight colours, so they stay white on a dark header.
        // Light keeps them: that is how the tool has always looked.
        grid.ColumnHeadersBorderStyle = IsDark
            ? DataGridViewHeaderBorderStyle.Single
            : DataGridViewHeaderBorderStyle.Raised;

        // Without these two the header of the column holding the current cell is filled with the row
        // selection colour, which turns a whole header cell into a blue block.
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.Header;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.Text;

        UseDarkScrollBars(grid);
    }

    /// <summary>
    /// Buttons ignore BackColor while they use the visual-style background, which is why the light mode has
    /// to reset the flag rather than just assign the system colour.
    /// </summary>
    private static void StyleButton(Button button, Palette palette)
    {
        if (!IsDark)
        {
            // Order matters: assigning BackColor switches UseVisualStyleBackColor off again, so the flag
            // has to come last or the button keeps the classic 3D face after a round trip through dark.
            button.FlatStyle = FlatStyle.Standard;
            button.BackColor = SystemColors.Control;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
            return;
        }

        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = palette.SurfaceAlt;
        button.ForeColor = palette.Text;
        button.FlatAppearance.BorderColor = palette.Border;
        button.FlatAppearance.MouseOverBackColor = palette.Header;
        button.FlatAppearance.MouseDownBackColor = palette.Border;
    }

    /// <summary>
    /// The column header of a detailed ListView is a native SysHeader32 child window, and it keeps its
    /// light fill whatever colour the list itself is given - there is no property for it. Owner drawing is
    /// the only way to reach it: the header is painted here, and items are handed straight back to the
    /// native renderer, so rows keep the system selection and focus drawing.
    /// </summary>
    private static void StyleList(ListView list, Palette palette)
    {
        list.DrawColumnHeader -= OnDrawColumnHeader;
        list.DrawItem -= OnDrawListRow;
        list.DrawSubItem -= OnDrawListSubItem;

        // Owner drawing also takes the rows away from the control; it is only worth paying for the header.
        if (!IsDark)
        {
            list.OwnerDraw = false;
            return;
        }

        list.OwnerDraw = true;
        list.DrawColumnHeader += OnDrawColumnHeader;
        list.DrawItem += OnDrawListRow;
        list.DrawSubItem += OnDrawListSubItem;
    }

    private static void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var palette = Current;

        using (var back = new SolidBrush(palette.Header))
            e.Graphics.FillRectangle(back, e.Bounds);

        // A single pixel of border on the right and the bottom, the same as the grid headers the main
        // window shows, so the two headers read as the same control.
        using (var line = new Pen(palette.Border))
        {
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 5,
                e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1,
                e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        var text = e.Header?.Text ?? string.Empty;
        TextRenderer.DrawText(e.Graphics, text, e.Font, Rectangle.Inflate(e.Bounds, -6, 0), palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    private static void OnDrawListSubItem(object? sender, DrawListViewSubItemEventArgs e) => e.DrawDefault = true;

    private static void OnDrawListRow(object? sender, DrawListViewItemEventArgs e) => e.DrawDefault = true;

    /// <summary>
    /// The tab strip is drawn by the system, so the dark scheme takes it over. Light keeps the native
    /// drawing - the goal is to leave the original look untouched, not to re-implement it.
    /// </summary>
    private static void StyleTabs(TabControl tabs, Palette palette)
    {
        tabs.BackColor = palette.Window;

        tabs.DrawItem -= OnDrawTab;
        tabs.DrawMode = IsDark ? TabDrawMode.OwnerDrawFixed : TabDrawMode.Normal;

        // Normal appearance draws an etched page border (a white 2 px line) that the system renderer keeps
        // light; flat buttons have no page border, so there is nothing left to fight.
        //
        // Not Multiline: an owner-drawn multiline strip loses its last tab (comctl32 reports no rect for it),
        // so a narrow window would hide a page instead of scrolling to it.
        tabs.Appearance = IsDark ? TabAppearance.FlatButtons : TabAppearance.Normal;

        if (IsDark) tabs.DrawItem += OnDrawTab;

        tabs.Refresh();
    }

    private static void OnDrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is TabControl tabs) PaintTab(tabs, e.Graphics, e.Index);
    }

    /// <summary>
    /// Owner-draw only reaches the list of a DropDownList combo, never its field - that one is painted by
    /// <see cref="ThemedComboBox"/>. Both read their colours from here so the two halves cannot disagree.
    /// </summary>
    private static void StyleCombo(ComboBox combo, Palette palette)
    {
        // BackColor reaches the drop-down list's own background; the field ignores it.
        combo.BackColor = palette.Surface;
        combo.ForeColor = palette.Text;
        combo.FlatStyle = FlatStyle.Standard;

        if (combo.DroppedDown) combo.DroppedDown = false;

        combo.DrawItem -= OnDrawComboItem;
        combo.DrawMode = IsDark ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
        if (IsDark) combo.DrawItem += OnDrawComboItem;

        // The native field and the overpaint have to swap over, and neither assignment above repaints it.
        combo.Invalidate();
    }

    private static void OnDrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0) return;

        var palette = Current;
        var selected = (e.State & DrawItemState.Selected) != 0 &&
                       (e.State & DrawItemState.ComboBoxEdit) == 0;

        using (var back = new SolidBrush(selected ? palette.Selection : palette.Surface))
            e.Graphics.FillRectangle(back, e.Bounds);

        var text = combo.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, selected ? palette.SelectionText : palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// Draws one tab. Shared with <see cref="ThemedTabControl"/>, which has to draw the tabs a second time:
    /// the native renderer draws a light frame on every tab of a flat-button control, and nothing short of
    /// painting over it removes that frame.
    /// </summary>
    internal static void PaintTab(TabControl tabs, Graphics graphics, int index)
    {
        if (index < 0 || index >= tabs.TabPages.Count) return;

        var palette = Current;
        var selected = index == tabs.SelectedIndex;
        var bounds = tabs.GetTabRect(index);

        using (var back = new SolidBrush(selected ? palette.Surface : palette.Window))
            graphics.FillRectangle(back, bounds);

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            graphics.FillRectangle(accent, new Rectangle(bounds.Left, bounds.Bottom - 2, bounds.Width, 2));
        }

        // Leave the accent line its two pixels, or the text sits on top of it
        var textBounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right,
            bounds.Bottom - (selected ? 2 : 0));

        TextRenderer.DrawText(graphics, tabs.TabPages[index].Text, tabs.Font, textBounds,
            selected ? palette.Text : palette.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static ToolStripRenderer? _stripRenderer;
    private static Palette? _stripRendererPalette;

    private static void StyleStatusStrip(StatusStrip strip, Palette palette)
    {
        // The default renderer paints a fixed gradient that ignores BackColor, hence a renderer per palette.
        if (!ReferenceEquals(_stripRendererPalette, palette))
        {
            _stripRenderer = new FlatStripRenderer(palette);
            _stripRendererPalette = palette;
        }

        strip.Renderer = _stripRenderer;
        strip.BackColor = palette.Header;
        strip.ForeColor = palette.Text;

        foreach (ToolStripItem item in strip.Items)
            item.ForeColor = palette.Text;
    }

    /// <summary>Status strip background and top border from the palette; the rest is the stock renderer.</summary>
    private sealed class FlatStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _palette;

        public FlatStripRenderer(Palette palette) : base(new StripColorTable(palette)) => _palette = palette;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_palette.Header);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_palette.Border);
            var width = e.ToolStrip.Width;
            e.Graphics.DrawLine(pen, 0, 0, width, 0);
        }
    }

    private sealed class StripColorTable : ProfessionalColorTable
    {
        private readonly Palette _palette;

        public StripColorTable(Palette palette) => _palette = palette;

        public override Color StatusStripGradientBegin => _palette.Header;
        public override Color StatusStripGradientEnd => _palette.Header;
        public override Color StatusStripBorder => _palette.Border;
        public override Color SeparatorDark => _palette.Border;
        public override Color SeparatorLight => _palette.Header;
    }

    // Native chrome

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Dark title bar on Windows 10 2004 and later (attribute 20), 1809/1903 use 19. Rejecting an unknown
    /// attribute is harmless, so the newer one is tried first and the older one is the fallback.
    /// </summary>
    private static void UseDarkTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated) return;

        try
        {
            var value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Windows without DWM (or a non-desktop session): the title bar simply keeps its system colour
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    /// <summary>
    /// Scroll bars are painted by the control's window theme, not by WinForms. On the builds this was tested
    /// on the call changes nothing for DataGridView or RichEdit - those draw their scroll bars from the
    /// process-wide colour mode, which an app can only change through an undocumented uxtheme export, so the
    /// scroll bars stay light. Kept because it does work for the controls that honour it, and it is a no-op
    /// otherwise.
    /// </summary>
    private static void UseDarkScrollBars(Control control)
    {
        // The subscription stays in place for the life of the control, not just until the first handle:
        // a window theme lives on the handle, so any later recreation (a DPI change, a control that rebuilds
        // itself) puts the light bars back. Re-arming on every call keeps exactly one handler attached.
        control.HandleCreated -= OnHandleCreatedForTheme;
        control.HandleCreated += OnHandleCreatedForTheme;

        // Docked children inside a SplitContainer often get their handle only when the form is shown,
        // and a theme set on a missing handle is silently lost.
        if (!control.IsHandleCreated) return;

        ApplyScrollBarTheme(control);
    }

    private static void OnHandleCreatedForTheme(object? sender, EventArgs e)
    {
        if (sender is not Control control) return;

        ApplyScrollBarTheme(control);

        // The window theme repaints the scroll bars but not the surface around them.
        control.Invalidate();
    }

    private static void ApplyScrollBarTheme(Control control)
    {
        try
        {
            SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : null, null);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}

namespace TLD_ModBlueprintFinder.Ui;

/// <summary>
/// A TabControl that repaints what the native renderer always draws in system colours: the light 2 px frame
/// around every tab, the empty part of the strip (right of the last tab), and the 2-4 px frame around the
/// pages. None of them can be reached through BackColor, Appearance or the window theme, so a dark scheme
/// would otherwise keep a light outline around the whole result area.
/// Light mode returns before painting: there the native drawing is exactly the look this tool always had.
/// </summary>
internal sealed class ThemedTabControl : TabControl
{
    private const int WmPaint = 0x000F;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || !IsHandleCreated || !Theme.IsDark) return;

        var palette = Theme.Current;
        var page = DisplayRectangle;

        // GetDC, not BeginPaint: the update region is already validated at this point, so painting has to
        // happen on a device context that is not clipped to it. This runs after the native paint, which is
        // what makes an overpaint possible in the first place.
        using var g = Graphics.FromHwnd(Handle);

        // The whole strip, tabs included: their native frame has to go, and Theme.PaintTab draws them back.
        using (var back = new SolidBrush(palette.Window))
            g.FillRectangle(back, 0, 0, Width, page.Top);

        for (var i = 0; i < TabCount; i++) Theme.PaintTab(this, g, i);

        // Page frame: the strip the renderer draws around the page area. Filled with the page colour so it
        // reads as part of the page instead of as a border.
        using var body = new SolidBrush(palette.Surface);
        if (page.Left > 0) g.FillRectangle(body, 0, page.Top, page.Left, Height - page.Top);
        if (Width > page.Right) g.FillRectangle(body, page.Right, page.Top, Width - page.Right, Height - page.Top);
        if (Height > page.Bottom) g.FillRectangle(body, 0, page.Bottom, Width, Height - page.Bottom);
    }
}

namespace TLD_ModBlueprintFinder.Ui;

/// <summary>
/// A CheckBox whose tick box is drawn here instead of by the window theme. The themed glyph is a white square
/// whatever the surrounding surface is - the text follows ForeColor, the box does not - which leaves a bright
/// dot next to every option in the dark scheme.
/// Light mode returns before painting, so the native glyph stays exactly as it was.
/// </summary>
internal sealed class ThemedCheckBox : CheckBox
{
    private int Glyph => (int)Math.Round(13 * DeviceDpi / 96.0);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (!Theme.IsDark) return;

        var palette = Theme.Current;
        var size = Glyph;

        // The native glyph is 13 px at 96 dpi and sits on the left edge, vertically centred. Cover one pixel
        // more than we draw, so no part of the light square survives a different DPI rounding.
        var glyph = new Rectangle(0, (Height - size) / 2, size, size);
        var cover = Rectangle.Inflate(glyph, 1, 1);

        using (var back = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(back, cover);

        using (var face = new SolidBrush(palette.SurfaceAlt))
            e.Graphics.FillRectangle(face, glyph);

        using (var edge = new Pen(palette.Border))
            e.Graphics.DrawRectangle(edge, glyph.X, glyph.Y, glyph.Width - 1, glyph.Height - 1);

        if (!Checked) return;

        using var tick = new Pen(palette.Accent, Math.Max(1.6f, size / 8f));
        var x = glyph.Left + size * 0.22f;
        var y = glyph.Top + size * 0.52f;
        e.Graphics.DrawLine(tick, x, y, glyph.Left + size * 0.42f, glyph.Top + size * 0.72f);
        e.Graphics.DrawLine(tick, glyph.Left + size * 0.42f, glyph.Top + size * 0.72f, glyph.Left + size * 0.78f,
            glyph.Top + size * 0.30f);
    }
}

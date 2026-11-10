namespace TLD_ModBlueprintFinder.Ui;

/// <summary>
/// A DropDownList ComboBox whose field is painted by us. The native control draws its field and its drop-down
/// button from the window theme, which is always light: <c>BackColor</c>, <c>FlatStyle</c> and
/// <c>DrawMode.OwnerDrawFixed</c> only reach the list items, so the field stays a white block in a dark scheme.
/// The list itself is owner-drawn through <see cref="Theme"/>; only the closed field needs this class.
/// Light mode returns before painting, so the native drawing stays exactly as it was.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    private const int WmPaint = 0x000F;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || !IsHandleCreated || !Theme.IsDark) return;

        var palette = Theme.Current;
        var bounds = ClientRectangle;

        // Same width the native drop-down button uses, so the chevron lands where the button was.
        var arrowWidth = SystemInformation.VerticalScrollBarWidth;
        var arrow = new Rectangle(bounds.Right - arrowWidth, bounds.Top, arrowWidth, bounds.Height);

        using var g = Graphics.FromHwnd(Handle);

        using (var field = new SolidBrush(palette.Surface))
            g.FillRectangle(field, bounds);

        using (var button = new SolidBrush(palette.SurfaceAlt))
            g.FillRectangle(button, arrow);

        var text = Text;
        var textBounds = Rectangle.FromLTRB(bounds.Left + 4, bounds.Top, arrow.Left - 4, bounds.Bottom);
        TextRenderer.DrawText(g, text, Font, textBounds, palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);

        var cx = arrow.Left + arrow.Width / 2;
        var cy = arrow.Top + arrow.Height / 2 + 1;
        using (var chevron = new Pen(palette.TextMuted, 1.6f))
        {
            g.DrawLine(chevron, cx - 4, cy - 2, cx, cy + 2);
            g.DrawLine(chevron, cx, cy + 2, cx + 4, cy - 2);
        }

        using var frame = new Pen(palette.Border);
        g.DrawRectangle(frame, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }
}

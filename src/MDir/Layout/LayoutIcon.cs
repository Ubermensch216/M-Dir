namespace MDir.Layout;

/// <summary>Draws a small picture of a layout for menus, using the same arrangement code as the real window.</summary>
internal static class LayoutIcon
{
    public static Bitmap Create(PaneLayout layout, int size)
    {
        var bitmap = new Bitmap(size, size);
        using Graphics graphics = Graphics.FromImage(bitmap);
        var outer = new Rectangle(1, 2, size - 2, size - 4);
        LayoutArrangement arrangement = PaneArranger.Arrange(layout, layout.DefaultRatios, outer, Math.Max(1, size / 16));

        using var fill = new SolidBrush(Color.FromArgb(0x5B, 0x9B, 0xD5));
        using var border = new Pen(Color.FromArgb(0x2E, 0x5C, 0x8A));
        foreach (Rectangle pane in arrangement.Panes)
        {
            graphics.FillRectangle(fill, pane);
            graphics.DrawRectangle(border, pane.Left, pane.Top, pane.Width - 1, pane.Height - 1);
        }

        return bitmap;
    }
}

namespace MDir.Panes;

/// <summary>Icon-font glyphs for toolbar buttons (Segoe Fluent Icons on Windows 11, MDL2 Assets on 10).</summary>
internal static class Glyphs
{
    private const string FluentFont = "Segoe Fluent Icons";
    private const string FallbackFont = "Segoe MDL2 Assets";

    // Code points are private-use characters, written as casts so they stay visible in the source.
    public static readonly string Back = ((char)0xE72B).ToString();
    public static readonly string Forward = ((char)0xE72A).ToString();
    public static readonly string Up = ((char)0xE74A).ToString();
    public static readonly string Refresh = ((char)0xE72C).ToString();
    public static readonly string ViewList = ((char)0xE8FD).ToString();
    public static readonly string ChevronDown = ((char)0xE70D).ToString();
    public static readonly string Add = ((char)0xE710).ToString();
    public static readonly string Close = ((char)0xE711).ToString();

    public static Font Font { get; } = CreateFont();

    public static ToolStripButton CreateButton(string glyph, string toolTip, EventHandler onClick) =>
        new(glyph, null, onClick) { Font = Font, ToolTipText = toolTip, DisplayStyle = ToolStripItemDisplayStyle.Text };

    private static Font CreateFont()
    {
        using var probe = new Font(FluentFont, 10f);
        return new Font(probe.Name == FluentFont ? FluentFont : FallbackFont, 10f);
    }
}

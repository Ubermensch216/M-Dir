namespace MDir.Features.Hwp;

// The document model the HWP, HWPX, HWPML and built-in Office readers produce and HwpHtml draws.
// Lengths are points (1/72 inch, HWPUNIT / 100); colours are "#rrggbb" or null for the default.

/// <summary>A stretch of text in one character shape.</summary>
internal sealed class HwpRun
{
    public string Text { get; init; } = string.Empty;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public double? Size { get; init; }
    public string? Color { get; init; }
    public string? Background { get; init; }

    /// <summary>1 superscript, -1 subscript, 0 neither.</summary>
    public int Sup { get; init; }

    public string? Font { get; init; }

    /// <summary>Extra space between letters in em: Hancom's letter spacing (자간) and width ratio (장평) together.</summary>
    public double Spacing { get; init; }
}

/// <summary>An inline equation (Hancom EqEdit script), drawn as HTML math by <see cref="HwpHtml"/>.</summary>
internal sealed record HwpEquationRun(string Script);

/// <summary>A picture embedded in the document (PNG, JPEG, GIF or BMP bytes).</summary>
internal sealed record HwpImage(byte[] Data, double? Width, double? Height);

/// <summary>One visible edge of a table cell border.</summary>
internal sealed record HwpBorderEdge(string Style, double WidthPt, string Color);

/// <summary>Per-side borders for a table cell; null sides have no visible border.</summary>
internal sealed record HwpCellBorders(HwpBorderEdge? Top, HwpBorderEdge? Right, HwpBorderEdge? Bottom, HwpBorderEdge? Left)
{
    public bool HasAny => Top is not null || Right is not null || Bottom is not null || Left is not null;
}

/// <summary>A paragraph: runs of text, equations and pictures.</summary>
internal sealed class HwpPara
{
    /// <summary><see cref="HwpRun"/>, <see cref="HwpEquationRun"/> and <see cref="HwpImage"/> items in order.</summary>
    public List<object> Runs { get; } = [];

    public string Align { get; init; } = "left";
    public double Left { get; init; }

    /// <summary>First-line indent relative to <see cref="Left"/> (negative = hanging).</summary>
    public double First { get; init; }

    public double Before { get; init; }
    public double After { get; init; }

    /// <summary>Line height as a multiple (1.6 = 160%), or null for the default.</summary>
    public double? Line { get; init; }

    /// <summary>The paragraph starts a new page.</summary>
    public bool PageBreak { get; set; }

    /// <summary>The font size of the paragraph's first character shape: the height of a line even when it is empty.</summary>
    public double? Size { get; set; }
}

internal sealed class HwpCell
{
    /// <summary><see cref="HwpPara"/> and <see cref="HwpTable"/> items.</summary>
    public List<object> Blocks { get; } = [];

    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public string? Background { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string VAlign { get; set; } = "top";
    public bool Border { get; set; } = true;

    /// <summary>Per-side border definitions when known from BorderFill; overrides <see cref="Border"/> when set.</summary>
    public HwpCellBorders? Borders { get; set; }
}

internal sealed class HwpTable
{
    public List<List<HwpCell>> Rows { get; } = [];
    public List<double?> ColumnWidths { get; } = [];
    public bool Border { get; set; } = true;
    public string? Caption { get; set; }
    public bool CaptionBelow { get; set; }
}

/// <summary>Paper size and margins (points).</summary>
internal sealed record HwpPage(double Width, double Height, double Left, double Right, double Top, double Bottom)
{
    /// <summary>A4 portrait with Hancom's default margins (30 mm sides, 20+15 mm top, 15+15 mm bottom).</summary>
    public static HwpPage Default { get; } = new(595.3, 841.9, 85, 85, 99.2, 85);
}

internal static class HwpSpacing
{
    /// <summary>
    /// Letter spacing (percent of the font size) and width ratio (percent) as CSS letter-spacing in em. Browsers cannot
    /// narrow glyphs, so a ratio below 100% takes the space out between letters instead. 0.6 em is between the width of
    /// a Hangul glyph (about 0.9 em) and of a digit or Latin letter (about 0.5 em), so neither is squeezed too much.
    /// </summary>
    public static double ToEm(double spacingPercent, double ratioPercent) =>
        Math.Round((spacingPercent / 100.0) + ((Math.Clamp(ratioPercent, 50, 200) - 100) / 100.0 * 0.6), 3);
}

internal sealed class HwpDoc
{
    /// <summary><see cref="HwpPara"/> and <see cref="HwpTable"/> items.</summary>
    public List<object> Blocks { get; } = [];

    /// <summary>Footnotes and endnotes collected across the document.</summary>
    public List<string> Footnotes { get; } = [];

    public HwpPage Page { get; set; } = HwpPage.Default;

    /// <summary>The document was longer than the reader's limit and was cut.</summary>
    public bool Truncated { get; set; }
}

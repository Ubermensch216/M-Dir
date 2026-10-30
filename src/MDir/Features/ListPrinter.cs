using System.Drawing.Printing;

namespace MDir.Features;

/// <summary>
/// Prints a file list as a table (name, size, modified date, type) with a title and page numbers. Drawing is
/// separate from the print job (<see cref="CreateDocument"/>), so a page can be drawn onto any <see cref="Graphics"/>.
/// </summary>
internal sealed class ListPrinter(string title, IReadOnlyList<ExportRow> rows)
{
    private const float NameShare = 0.46f, SizeShare = 0.14f, DateShare = 0.22f;

    public string Title { get; } = title;

    public IReadOnlyList<ExportRow> Rows { get; } = rows;

    /// <summary>How many rows fit on one page of <paramref name="pageHeight"/> (the title, header line and footer take space).</summary>
    public static int RowsPerPage(float pageHeight, float rowHeight) => Math.Max(1, (int)((pageHeight - 4 * rowHeight) / rowHeight));

    public static int PageCountFor(int rowCount, int rowsPerPage) => Math.Max(1, (rowCount + rowsPerPage - 1) / rowsPerPage);

    /// <summary>The rows shown on page <paramref name="pageIndex"/> (zero-based).</summary>
    public IReadOnlyList<ExportRow> PageRows(int pageIndex, int rowsPerPage) => [.. Rows.Skip(pageIndex * rowsPerPage).Take(rowsPerPage)];

    /// <summary>Draws one page into <paramref name="area"/>. Returns whether another page follows.</summary>
    public bool DrawPage(Graphics g, RectangleF area, int pageIndex, Font font)
    {
        float rowHeight = font.GetHeight(g) * 1.25f;
        int perPage = RowsPerPage(area.Height, rowHeight);
        int pages = PageCountFor(Rows.Count, perPage);
        using var bold = new Font(font, FontStyle.Bold);
        using var pen = new Pen(Color.Gray, 0.5f);
        var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        var right = new StringFormat(format) { Alignment = StringAlignment.Far };

        float y = area.Top;
        g.DrawString(Title, bold, Brushes.Black, new RectangleF(area.Left, y, area.Width, rowHeight), format);
        y += rowHeight * 1.5f;

        float nameWidth = area.Width * NameShare, sizeWidth = area.Width * SizeShare, dateWidth = area.Width * DateShare;
        float typeWidth = area.Width - nameWidth - sizeWidth - dateWidth;
        DrawRow(g, bold, y, "이름", "크기", "수정한 날짜", "종류");
        y += rowHeight;
        g.DrawLine(pen, area.Left, y, area.Right, y);

        foreach (ExportRow row in PageRows(pageIndex, perPage))
        {
            DrawRow(g, font, y, row.Name, row.Size is { } size ? PreviewPane.FormatSize(size) : string.Empty,
                row.Modified.ToString("yyyy-MM-dd HH:mm"), row.Type);
            y += rowHeight;
        }

        g.DrawString($"{pageIndex + 1} / {pages}", font, Brushes.Black, new RectangleF(area.Left, area.Bottom - rowHeight, area.Width, rowHeight),
            new StringFormat { Alignment = StringAlignment.Center });
        return pageIndex + 1 < pages;

        void DrawRow(Graphics graphics, Font rowFont, float top, string name, string size, string date, string type)
        {
            float x = area.Left;
            graphics.DrawString(name, rowFont, Brushes.Black, new RectangleF(x, top, nameWidth - 4, rowHeight), format);
            x += nameWidth;
            graphics.DrawString(size, rowFont, Brushes.Black, new RectangleF(x, top, sizeWidth - 8, rowHeight), right);
            x += sizeWidth;
            graphics.DrawString(date, rowFont, Brushes.Black, new RectangleF(x + 4, top, dateWidth - 4, rowHeight), format);
            x += dateWidth;
            graphics.DrawString(type, rowFont, Brushes.Black, new RectangleF(x, top, typeWidth, rowHeight), format);
        }
    }

    /// <summary>A print job for the list: print it, or show it in a preview dialog.</summary>
    public PrintDocument CreateDocument()
    {
        var document = new PrintDocument { DocumentName = Title };
        int page = 0;
        document.BeginPrint += (_, _) => page = 0;
        document.PrintPage += (_, e) =>
        {
            // Printer graphics use hundredths of an inch, like the margins; a point-sized font is converted for them.
            using var font = new Font("Segoe UI", 9f, GraphicsUnit.Point);
            e.HasMorePages = DrawPage(e.Graphics!, e.MarginBounds, page++, font);
        };
        return document;
    }
}

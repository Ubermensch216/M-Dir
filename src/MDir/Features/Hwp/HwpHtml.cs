using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MDir.Features.Hwp;

/// <summary>An HWP/HWPX document drawn as an HTML page on disk: white sheets of the document's paper size on a grey desk.</summary>
/// <param name="HtmlPath">The page (no script); its pictures are files next to it.</param>
/// <param name="SheetWidthPx">The width of a sheet plus its margin in CSS pixels, for fitting it to the window.</param>
/// <param name="Sheets">How many sheets: the document's explicit page and section breaks plus one.</param>
internal sealed record HwpPreview(string HtmlPath, double SheetWidthPx, int Sheets, bool Truncated);

/// <summary>
/// Draws <see cref="HwpDoc"/> as HTML for the WebBrowser control: fonts, sizes, colours, letter spacing, alignment,
/// indents, line spacing, tables (merged cells, backgrounds, borders, heights) and pictures. Pages are not typeset:
/// a sheet ends at an explicit page or section break and grows with its content.
/// </summary>
internal static class HwpHtml
{
    private const double PxPerPt = 96.0 / 72.0;
    private const string DefaultFonts = "'함초롬바탕','맑은 고딕','Malgun Gothic',serif";

    /// <summary>Writes index.html (and the pictures, each once) into <paramref name="folder"/>.</summary>
    public static HwpPreview Render(HwpDoc doc, string folder)
    {
        Directory.CreateDirectory(folder);
        var writer = new Writer(folder);
        HwpPage page = doc.Page;
        StringBuilder html = writer.Html;
        html.Append("<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\"><style>");
        html.Append("html,body{margin:0;padding:0;background:#e4e4e4;}");
        html.Append(CultureInfo.InvariantCulture, $".sheet{{background:#fff;color:#000;margin:8px auto;box-sizing:border-box;border:1px solid #c8c8c8;width:{Pt(page.Width)};min-height:{Pt(page.Height)};padding:{Pt(page.Top)} {Pt(page.Right)} {Pt(page.Bottom)} {Pt(page.Left)};font-family:{DefaultFonts};font-size:10pt;overflow:hidden;}}");
        html.Append("p{margin:0;white-space:pre-wrap;word-wrap:break-word;}");
        html.Append("table{border-collapse:collapse;table-layout:fixed;margin:0;}");
        html.Append("td{padding:1.4pt 5.1pt;overflow:hidden;word-wrap:break-word;}");
        html.Append("td.b{border:1px solid #000;}");
        html.Append("img{max-width:100%;vertical-align:bottom;}");
        html.Append(".note{margin:8px auto;text-align:center;color:#666;font:9pt 'Malgun Gothic',sans-serif;}");
        html.Append("</style></head><body>");

        int sheets = 1;
        html.Append("<div class=\"sheet\">");
        bool empty = true;
        foreach (object block in doc.Blocks)
        {
            if (block is HwpPara { PageBreak: true } && !empty)
            {
                html.Append("</div><div class=\"sheet\">");
                sheets++;
            }

            writer.Block(block);
            empty = false;
        }

        html.Append("</div>");
        if (doc.Truncated)
            html.Append("<div class=\"note\">문서가 길어서 앞부분만 표시합니다.</div>");

        html.Append("</body></html>");
        string path = Path.Combine(folder, "index.html");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(true));
        return new HwpPreview(path, (page.Width * PxPerPt) + 24, sheets, doc.Truncated);
    }

    private sealed class Writer(string folder)
    {
        private readonly Dictionary<string, string> _images = [];

        public StringBuilder Html { get; } = new(64 * 1024);

        public void Block(object block)
        {
            if (block is HwpTable table)
                Table(table);
            else if (block is HwpPara para)
                Para(para);
        }

        private void Para(HwpPara para)
        {
            StringBuilder html = Html;
            html.Append("<p style=\"text-align:").Append(para.Align);
            if (para.Left != 0)
                html.Append(";margin-left:").Append(Pt(para.Left));
            if (para.First != 0)
                html.Append(";text-indent:").Append(Pt(para.First));
            if (para.Before > 0)
                html.Append(";margin-top:").Append(Pt(para.Before));
            if (para.After > 0)
                html.Append(";margin-bottom:").Append(Pt(para.After));
            if (para.Line is { } line)
                html.Append(";line-height:").Append(Num(line));

            // An empty paragraph keeps the height of a line in its own font size (spacer rows of tables are often 1-3 pt).
            if (para.Size is { } size)
                html.Append(";font-size:").Append(Pt(size));

            html.Append("\">");
            bool any = false;
            foreach (object run in para.Runs)
            {
                if (run is HwpRun text && text.Text.Length > 0)
                {
                    Run(text);
                    any = true;
                }
                else if (run is HwpImage image && ImageTag(image) is { } tag)
                {
                    html.Append(tag);
                    any = true;
                }
            }

            if (!any)
                html.Append("&nbsp;");

            html.Append("</p>");
        }

        private void Run(HwpRun run)
        {
            var style = new StringBuilder();
            if (run.Font is { Length: > 0 } font)
                style.Append("font-family:'").Append(font.Replace("'", string.Empty, StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal)).Append("',").Append(DefaultFonts).Append(';');
            if (run.Size is { } size)
                style.Append("font-size:").Append(Pt(run.Sup != 0 ? size * 0.7 : size)).Append(';');
            if (run.Spacing != 0)
                style.Append("letter-spacing:").Append(Num(run.Spacing)).Append("em;");
            if (run.Bold)
                style.Append("font-weight:bold;");
            if (run.Italic)
                style.Append("font-style:italic;");
            if (run.Underline || run.Strike)
                style.Append("text-decoration:").Append(run.Underline ? "underline " : string.Empty).Append(run.Strike ? "line-through" : string.Empty).Append(';');
            if (run.Color is { } color)
                style.Append("color:").Append(color).Append(';');
            if (run.Background is { } background && background != "#ffffff")
                style.Append("background:").Append(background).Append(';');
            if (run.Sup != 0)
                style.Append("vertical-align:").Append(run.Sup > 0 ? "super" : "sub").Append(';');

            string text = WebUtility.HtmlEncode(run.Text).Replace("\n", "<br>", StringComparison.Ordinal).Replace("\t", "&emsp;&emsp;", StringComparison.Ordinal);
            if (style.Length == 0)
                Html.Append(text);
            else
                Html.Append("<span style=\"").Append(style).Append("\">").Append(text).Append("</span>");
        }

        private void Table(HwpTable table)
        {
            StringBuilder html = Html;
            bool known = table.ColumnWidths.Count > 0 && table.ColumnWidths.All(w => w is > 0);
            html.Append("<table");
            if (known)
                html.Append(" style=\"width:").Append(Pt(table.ColumnWidths.Sum(w => w!.Value))).Append(";max-width:100%\"");
            else
                html.Append(" style=\"width:100%\"");

            html.Append('>');
            if (known)
            {
                html.Append("<colgroup>");
                foreach (double? width in table.ColumnWidths)
                    html.Append("<col style=\"width:").Append(Pt(width!.Value)).Append("\">");
                html.Append("</colgroup>");
            }

            foreach (List<HwpCell> row in table.Rows)
            {
                html.Append("<tr>");
                foreach (HwpCell cell in row)
                {
                    html.Append("<td");
                    if (cell.Border)
                        html.Append(" class=\"b\"");
                    if (cell.ColSpan > 1)
                        html.Append(" colspan=\"").Append(cell.ColSpan).Append('"');
                    if (cell.RowSpan > 1)
                        html.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');

                    html.Append(" style=\"vertical-align:").Append(cell.VAlign);
                    if (cell.Height is > 0 and var height && cell.RowSpan == 1)
                        html.Append(";height:").Append(Pt(height));
                    if (cell.Background is { } background)
                        html.Append(";background:").Append(background);
                    html.Append("\">");
                    foreach (object block in cell.Blocks)
                        Block(block);
                    html.Append("</td>");
                }

                html.Append("</tr>");
            }

            html.Append("</table>");
        }

        private string? ImageTag(HwpImage image)
        {
            if (MimeOf(image.Data) is not { } extension)
                return null;

            // A picture used many times (a logo on every page) is written once.
            string hash = Convert.ToHexString(SHA256.HashData(image.Data))[..24];
            if (!_images.TryGetValue(hash, out string? name))
            {
                name = $"img{_images.Count}.{extension}";
                File.WriteAllBytes(Path.Combine(folder, name), image.Data);
                _images[hash] = name;
            }

            var tag = new StringBuilder("<img src=\"").Append(name).Append('"');
            if (image.Width is { } width)
                tag.Append(" style=\"width:").Append(Pt(width)).Append('"');
            return tag.Append(" alt=\"\">").ToString();
        }
    }

    /// <summary>The file extension of the picture formats the browser shows (WMF/EMF and others are left out).</summary>
    internal static string? MimeOf(byte[] data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "png",
        [0xFF, 0xD8, 0xFF, ..] => "jpg",
        [0x47, 0x49, 0x46, 0x38, ..] => "gif",
        [0x42, 0x4D, ..] => "bmp",
        _ => null,
    };

    private static string Pt(double value) => Num(value) + "pt";

    private static string Num(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reads .hwp and .hwpx files and keeps their drawn pages in %TEMP%\M-Dir\hwp-view, so a document is read once.</summary>
internal static class HwpDocument
{
    private const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>Bumped when the drawing changes, so pages drawn by an older M-Dir are not reused.</summary>
    private const int RenderVersion = 2;

    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(3);
    private static int _pruned;

    internal static string CacheFolder { get; set; } = Path.Combine(Path.GetTempPath(), "M-Dir", "hwp-view");

    public static bool IsHwpPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".hwp" or ".hwpx";

    /// <summary>
    /// The drawn document (from the cache when the file did not change), or null when it cannot be read:
    /// password-protected, distribution-only, DRM-protected, damaged, or not really a Hancom file.
    /// </summary>
    public static HwpPreview? Preview(string path)
    {
        try
        {
            var file = new FileInfo(path);
            string key = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{RenderVersion}";
            string folder = Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]);
            string meta = Path.Combine(folder, "meta.txt");
            if (File.Exists(meta) && File.ReadAllText(meta).Split('|') is [var width, var sheets, var truncated]
                && double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && int.TryParse(sheets, out int s))
            {
                Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
                return new HwpPreview(Path.Combine(folder, "index.html"), w, s, truncated == "1");
            }

            if (Read(path) is not { } doc)
                return null;

            Prune();

            // Draw into a folder of its own and move it into place, so a half-written page is never reused.
            string partial = folder + ".part-" + Guid.NewGuid().ToString("N")[..8];
            HwpPreview drawn = HwpHtml.Render(doc, partial);
            File.WriteAllText(Path.Combine(partial, "meta.txt"),
                string.Create(CultureInfo.InvariantCulture, $"{drawn.SheetWidthPx}|{drawn.Sheets}|{(drawn.Truncated ? 1 : 0)}"));
            try
            {
                Directory.Move(partial, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drawn at the same time by another M-Dir, or the new folder is briefly locked (a virus scanner):
                // show this copy; it is not reused and the cleanup removes it later.
                return drawn;
            }

            return drawn with { HtmlPath = Path.Combine(folder, "index.html") };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The document, or null when it cannot be read.</summary>
    public static HwpDoc? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes)
                return null;

            byte[] data = new byte[stream.Length];
            stream.ReadExactly(data);
            return Read(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static HwpDoc? Read(byte[] data)
    {
        try
        {
            // Look at the content, not the name: some .hwp files are really HWPX and the other way round.
            bool zip = data is [0x50, 0x4B, ..];
            return zip ? HwpxReader.Read(new MemoryStream(data, writable: false)) : HwpReader.Read(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or ArgumentException
            or IndexOutOfRangeException or OverflowException or FormatException or InvalidOperationException or KeyNotFoundException
            or OutOfMemoryException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Deletes drawn documents not looked at for a few days (once per run).</summary>
    private static void Prune()
    {
        if (Interlocked.Exchange(ref _pruned, 1) != 0 || !Directory.Exists(CacheFolder))
            return;

        DateTime limit = DateTime.UtcNow - CacheAge;
        foreach (DirectoryInfo dir in new DirectoryInfo(CacheFolder).EnumerateDirectories())
        {
            try
            {
                if (dir.LastWriteTimeUtc < limit)
                    dir.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MDir.Features.Hwp;

/// <summary>An HWP/HWPX/HML or Office document drawn as an HTML page on disk: white sheets on a grey desk.</summary>
/// <param name="HtmlPath">The page (no script); its pictures are files next to it.</param>
/// <param name="SheetWidthPx">The width of a sheet plus its margin in CSS pixels, for fitting it to the window.</param>
/// <param name="Sheets">How many sheets: explicit/layout page breaks plus one.</param>
internal sealed record HwpPreview(string HtmlPath, double SheetWidthPx, int Sheets, bool Truncated);

/// <summary>
/// Draws <see cref="HwpDoc"/> as script-free HTML for the WebBrowser control: Korean font alias/serif-sans stacks,
/// sizes, colours, letter spacing, alignment, indents, line spacing, equations (stacked fractions/roots), per-side
/// table borders (merged cells, backgrounds, captions, heights), pictures and footnotes.
/// </summary>
internal static class HwpHtml
{
    private const double PxPerPt = 96.0 / 72.0;
    private const string DefaultFonts = "'HCR Batang','함초롬바탕','맑은 고딕','Malgun Gothic',serif";

    private static readonly Dictionary<string, string> FontAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["함초롬바탕"] = "'HCR Batang','함초롬바탕','한컴바탕'",
        ["한컴바탕"] = "'HCR Batang','함초롬바탕','한컴바탕'",
        ["함초롬돋움"] = "'HCR Dotum','함초롬돋움','한컴돋움'",
        ["한컴돋움"] = "'HCR Dotum','함초롬돋움','한컴돋움'",
        ["맑은 고딕"] = "'Malgun Gothic','맑은 고딕'",
        ["맑은고딕"] = "'Malgun Gothic','맑은 고딕'",
        ["굴림"] = "'Gulim','굴림'",
        ["굴림체"] = "'GulimChe','굴림체','Gulim'",
        ["돋움"] = "'Dotum','돋움'",
        ["돋움체"] = "'DotumChe','돋움체','Dotum'",
        ["바탕"] = "'Batang','바탕'",
        ["바탕체"] = "'BatangChe','바탕체','Batang'",
        ["궁서"] = "'Gungsuh','궁서'",
        ["궁서체"] = "'GungsuhChe','궁서체','Gungsuh'",
        ["나눔고딕"] = "'NanumGothic','나눔고딕'",
        ["나눔명조"] = "'NanumMyeongjo','나눔명조'",
    };

    /// <summary>Builds a CSS font-family stack preserving serif vs sans-serif fallback (benchmarked from kordoc head-styles.ts).</summary>
    internal static string CssFontStack(string face)
    {
        string trimmed = face.Trim().Replace("'", string.Empty, StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal);
        if (trimmed.Length == 0)
            return DefaultFonts;

        string head = FontAliases.TryGetValue(trimmed, out string? alias) ? alias : $"'{trimmed}'";
        bool isSerif = trimmed.Contains("바탕", StringComparison.Ordinal)
            || trimmed.Contains("명조", StringComparison.Ordinal)
            || trimmed.Contains("궁서", StringComparison.Ordinal)
            || trimmed.Contains("신명", StringComparison.Ordinal)
            || trimmed.Contains("순명", StringComparison.Ordinal)
            || trimmed.Contains("Batang", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Myeong", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Mincho", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Gungsuh", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Serif", StringComparison.OrdinalIgnoreCase);

        string generic = isSerif
            ? "'HCR Batang','Batang','맑은 고딕',serif"
            : "'Malgun Gothic','HCR Dotum','Dotum',sans-serif";
        return $"{head},{generic}";
    }

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
        html.Append(".tbl-cap{font-size:9pt;color:#333;margin:3pt 0;font-weight:bold;}");
        html.Append(".eq{font-family:'Cambria Math','Times New Roman',serif;padding:0 1.5pt;}");
        html.Append(".eq-frac{display:inline-table;vertical-align:middle;text-align:center;font-size:0.88em;line-height:1.1;margin:0 1pt;}");
        html.Append(".eq-num{display:table-row;border-bottom:1px solid currentColor;padding:0 2pt;}");
        html.Append(".eq-den{display:table-row;padding:0 2pt;}");
        html.Append(".eq-rad{border-top:1px solid currentColor;padding:0 1pt;}");
        html.Append(".footnotes{margin-top:16pt;padding-top:6pt;border-top:1px solid #888;font-size:8.5pt;color:#333;line-height:1.4;}");
        html.Append(".footnotes p{margin:2pt 0;}");
        html.Append(".note{margin:8px auto;text-align:center;color:#666;font:9pt 'Malgun Gothic',sans-serif;}");
        html.Append("</style></head><body>");

        int sheets = 1;
        html.Append("<div class=\"sheet\" id=\"sheet-0\">");
        bool empty = true;
        foreach (object block in doc.Blocks)
        {
            if (block is HwpPara { PageBreak: true } && !empty)
            {
                html.Append(CultureInfo.InvariantCulture, $"</div><div class=\"sheet\" id=\"sheet-{sheets}\">");
                sheets++;
            }

            writer.Block(block);
            empty = false;
        }

        if (doc.Footnotes.Count > 0)
        {
            html.Append("<div class=\"footnotes\">");
            foreach (string note in doc.Footnotes)
                html.Append("<p>").Append(WebUtility.HtmlEncode(note)).Append("</p>");
            html.Append("</div>");
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
            double left = para.First < 0 ? para.Left - para.First : para.Left;
            if (left != 0)
                html.Append(";margin-left:").Append(Pt(left));
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
                else if (run is HwpEquationRun eq && HwpEquation.ToHtml(eq.Script) is { Length: > 0 } eqHtml)
                {
                    html.Append(eqHtml);
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
                style.Append("font-family:").Append(CssFontStack(font)).Append(';');
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
            if (!string.IsNullOrEmpty(table.Caption) && !table.CaptionBelow)
                html.Append("<div class=\"tbl-cap\">").Append(WebUtility.HtmlEncode(table.Caption)).Append("</div>");

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
                    if (cell.Borders is null && cell.Border)
                        html.Append(" class=\"b\"");
                    if (cell.ColSpan > 1)
                        html.Append(" colspan=\"").Append(cell.ColSpan).Append('"');
                    if (cell.RowSpan > 1)
                        html.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');

                    html.Append(" style=\"vertical-align:").Append(cell.VAlign);
                    if (cell.Borders is { } edges)
                    {
                        AppendEdge(html, "border-top", edges.Top);
                        AppendEdge(html, "border-right", edges.Right);
                        AppendEdge(html, "border-bottom", edges.Bottom);
                        AppendEdge(html, "border-left", edges.Left);
                    }

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
            if (!string.IsNullOrEmpty(table.Caption) && table.CaptionBelow)
                html.Append("<div class=\"tbl-cap\">").Append(WebUtility.HtmlEncode(table.Caption)).Append("</div>");
        }

        private static void AppendEdge(StringBuilder html, string prop, HwpBorderEdge? edge)
        {
            if (edge is null)
                return;

            html.Append(';').Append(prop).Append(':').Append(Pt(edge.WidthPt)).Append(' ').Append(edge.Style).Append(' ').Append(edge.Color);
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

/// <summary>Reads .hwp, .hwpx and .hml files and keeps their drawn pages in %TEMP%\M-Dir\hwp-view, so a document is read once.</summary>
internal static class HwpDocument
{
    private const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>Bumped when the drawing changes, so pages drawn by an older M-Dir are not reused.</summary>
    private const int RenderVersion = 4;

    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(3);
    private static int _pruned;

    internal static string CacheFolder { get; set; } = Path.Combine(Path.GetTempPath(), "M-Dir", "hwp-view");

    public static bool IsHwpPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".hwp" or ".hwpx" or ".hml" or ".hwpml";

    /// <summary>
    /// The drawn document (from the cache when the file did not change), or null when it cannot be read:
    /// password-protected, DRM-protected, damaged, or not really a Hancom file.
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
            // Look at magic bytes, not extension: .hwp files may actually be HWPX (ZIP) or HWPML (XML).
            if (data is [0x50, 0x4B, ..])
                return HwpxReader.ReadBytes(data);

            if (HwpmlReader.IsHwpml(data))
                return HwpmlReader.Read(data);

            return HwpReader.Read(data);
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

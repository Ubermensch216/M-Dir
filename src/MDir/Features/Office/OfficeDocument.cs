using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MDir.Features.Hwp;

namespace MDir.Features.Office;

/// <summary>
/// Zero-dependency built-in Office Open XML (<c>.docx</c>, <c>.xlsx</c>, <c>.pptx</c>) reader and HTML preview
/// fallback when no Windows Shell preview handler is registered or the handler fails.
/// Benchmarked from kordoc (<c>src/docx/parser.ts</c>, <c>src/xlsx/parser.ts</c>, <c>src/pptx/parser.ts</c>).
/// </summary>
internal static partial class OfficeDocument
{
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const long MaxPartBytes = 64L * 1024 * 1024;
    private const int RenderVersion = 1;
    private const int MaxSheets = 20;
    private const int MaxSheetCells = 8000;
    private const int MaxSlides = 200;
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(3);
    private static int _pruned;

    private static readonly HashSet<string> SkipSlidePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "sldNum", "dt", "ftr", "hdr", "sldImg",
    };

    // ECMA-376 built-in date format IDs + Korean East-Asian built-in IDs (27..36, 50..58), benchmarked from kordoc xlsx/parser.ts.
    private static readonly Dictionary<int, bool> BuiltinDateFormats = new()
    {
        [14] = false, [15] = false, [16] = false, [17] = false,
        [18] = true, [19] = true, [20] = true, [21] = true, [22] = true,
        [27] = false, [28] = false, [29] = false, [30] = false, [31] = false,
        [32] = true, [33] = true, [34] = false, [35] = false, [36] = false,
        [45] = true, [46] = true, [47] = true,
        [50] = false, [51] = false, [52] = false, [53] = false, [54] = false,
        [55] = false, [56] = false, [57] = false, [58] = false,
    };

    internal static string CacheFolder { get; set; } = Path.Combine(Path.GetTempPath(), "M-Dir", "doc-view");

    public static bool IsOfficePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm";

    public static HwpPreview? Preview(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes)
                return null;

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

    public static HwpDoc? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes)
                return null;

            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static HwpDoc? Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        return Read(ms);
    }

    internal static HwpDoc? Read(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (FindEntry(zip, "word/document.xml") is not null)
                return ReadDocx(zip);

            if (FindEntry(zip, "xl/workbook.xml") is not null)
                return ReadXlsx(zip);

            if (FindEntry(zip, "ppt/presentation.xml") is not null)
                return ReadPptx(zip);

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or ArgumentException
            or IndexOutOfRangeException or OverflowException or FormatException or InvalidOperationException
            or KeyNotFoundException or OutOfMemoryException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- DOCX ----

    private static HwpDoc? ReadDocx(ZipArchive zip)
    {
        if (FindEntry(zip, "word/document.xml") is not { } docEntry)
            return null;

        XElement root = LoadXml(docEntry);
        XElement? body = Child(root, "body");
        if (body is null)
            return null;

        Dictionary<string, int> headingStyles = ReadDocxHeadingStyles(zip);
        Dictionary<string, string> rels = ReadRels(zip, "word/document.xml");
        Dictionary<string, string> footnotes = ReadDocxFootnotes(zip);

        var doc = new HwpDoc();
        int count = 0;

        foreach (XElement el in FlattenDocx(body))
        {
            if (count >= HwpLimits.MaxBlocks)
            {
                doc.Truncated = true;
                break;
            }

            string local = el.Name.LocalName;
            if (local == "p")
            {
                doc.Blocks.Add(ReadDocxPara(el, headingStyles, rels, zip, footnotes, doc.Footnotes));
                count++;
            }
            else if (local == "tbl")
            {
                doc.Blocks.Add(ReadDocxTable(el, headingStyles, rels, zip, footnotes, doc.Footnotes));
                count++;
            }
        }

        return doc;
    }

    private static Dictionary<string, int> ReadDocxHeadingStyles(ZipArchive zip)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (FindEntry(zip, "word/styles.xml") is not { } entry)
            return result;

        XElement stylesRoot = LoadXml(entry);
        var basedOn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement st in Children(stylesRoot, "style"))
        {
            string? id = Attr(st, "styleId");
            if (string.IsNullOrEmpty(id))
                continue;

            if (Attr(Child(st, "basedOn"), "val") is { Length: > 0 } parent)
                basedOn[id] = parent;

            int? level = null;
            if (Child(st, "pPr") is { } pPr && Child(pPr, "outlineLvl") is { } ol
                && int.TryParse(Attr(ol, "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lv) && lv is >= 0 and <= 5)
            {
                level = lv + 1;
            }

            if (level is null && Attr(Child(st, "name"), "val") is { } name)
            {
                Match m = HeadingNameRegex().Match(name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int h) && h is >= 1 and <= 6)
                    level = h;
            }

            if (level is { } found)
                result[id] = found;
        }

        // Resolve basedOn inheritance with cycle guard.
        foreach (string id in basedOn.Keys)
        {
            if (result.ContainsKey(id))
                continue;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            string? cur = basedOn[id];
            while (cur is not null && seen.Add(cur))
            {
                if (result.TryGetValue(cur, out int lv))
                {
                    result[id] = lv;
                    break;
                }

                basedOn.TryGetValue(cur, out cur);
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadDocxFootnotes(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (FindEntry(zip, "word/footnotes.xml") is not { } entry)
            return map;

        XElement root = LoadXml(entry);
        foreach (XElement fn in Children(root, "footnote"))
        {
            string? id = Attr(fn, "id");
            string? type = Attr(fn, "type");
            if (string.IsNullOrEmpty(id) || type is "separator" or "continuationSeparator")
                continue;

            string text = string.Concat(Descendants(fn, "t").Select(t => t.Value)).Trim();
            if (text.Length > 0)
                map[id] = text;
        }

        return map;
    }

    private static HwpPara ReadDocxPara(
        XElement p,
        Dictionary<string, int> headingStyles,
        Dictionary<string, string> rels,
        ZipArchive zip,
        Dictionary<string, string> footnotes,
        List<string> docFootnotes)
    {
        XElement? pPr = Child(p, "pPr");
        string? styleId = Attr(Child(pPr, "pStyle"), "val");
        int headingLevel = styleId is not null && headingStyles.TryGetValue(styleId, out int hl) ? hl : 0;

        string align = (Attr(Child(pPr, "jc"), "val"))?.ToLowerInvariant() switch
        {
            "center" => "center",
            "right" or "end" => "right",
            "both" or "distribute" => "justify",
            _ => "left",
        };

        XElement? ind = Child(pPr, "ind");
        double left = TwipsToPt(Attr(ind, "left"));
        double first = TwipsToPt(Attr(ind, "firstLine")) - TwipsToPt(Attr(ind, "hanging"));
        XElement? spacing = Child(pPr, "spacing");
        double before = TwipsToPt(Attr(spacing, "before"));
        double after = TwipsToPt(Attr(spacing, "after"));
        bool pageBreak = Child(pPr, "pageBreakBefore") is not null;

        if (headingLevel > 0)
        {
            before = Math.Max(before, headingLevel == 1 ? 10 : 7);
            after = Math.Max(after, 4);
        }

        var para = new HwpPara
        {
            Align = align,
            Left = left,
            First = first,
            Before = before,
            After = after,
            Line = 1.35,
            PageBreak = pageBreak,
        };

        double? headingSize = headingLevel switch
        {
            1 => 16.0,
            2 => 13.5,
            3 => 11.5,
            >= 4 => 10.5,
            _ => null,
        };

        if (Child(pPr, "numPr") is not null)
            para.Runs.Add(new HwpRun { Text = "• ", Size = headingSize ?? 10.0, Font = "맑은 고딕" });

        foreach (XElement child in FlattenDocx(p))
        {
            string local = child.Name.LocalName;
            if (local == "hyperlink")
            {
                foreach (XElement hr in FlattenDocx(child))
                {
                    if (hr.Name.LocalName == "r")
                        AppendDocxRun(hr, para, headingLevel, headingSize, rels, zip, footnotes, docFootnotes, isLink: true);
                }
            }
            else if (local == "r")
            {
                AppendDocxRun(child, para, headingLevel, headingSize, rels, zip, footnotes, docFootnotes, isLink: false);
            }
        }

        return para;
    }

    private static void AppendDocxRun(
        XElement r,
        HwpPara para,
        int headingLevel,
        double? headingSize,
        Dictionary<string, string> rels,
        ZipArchive zip,
        Dictionary<string, string> footnotes,
        List<string> docFootnotes,
        bool isLink)
    {
        XElement? rPr = Child(r, "rPr");
        bool bold = headingLevel > 0 || IsOn(Child(rPr, "b"));
        bool italic = IsOn(Child(rPr, "i"));
        bool underline = isLink || (Child(rPr, "u") is { } u && !string.Equals(Attr(u, "val"), "none", StringComparison.OrdinalIgnoreCase));
        bool strike = IsOn(Child(rPr, "strike")) || IsOn(Child(rPr, "dstrike"));
        string? color = isLink ? "#0563c1" : HwpxReader.HexColor(Attr(Child(rPr, "color"), "val"));
        string? bg = HwpxReader.HexColor(Attr(Child(rPr, "shd"), "fill"));
        double? size = double.TryParse(Attr(Child(rPr, "sz"), "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out double halfPt) && halfPt > 0
            ? halfPt / 2.0
            : headingSize;
        int sup = (Attr(Child(rPr, "vertAlign"), "val"))?.ToLowerInvariant() switch
        {
            "superscript" => 1,
            "subscript" => -1,
            _ => 0,
        };
        string? font = Attr(Child(rPr, "rFonts"), "eastAsia") ?? Attr(Child(rPr, "rFonts"), "ascii");

        var sb = new StringBuilder();
        foreach (XElement item in r.Elements())
        {
            switch (item.Name.LocalName)
            {
                case "t":
                    sb.Append(item.Value);
                    break;
                case "tab":
                    sb.Append('\t');
                    break;
                case "br":
                    if (string.Equals(Attr(item, "type"), "page", StringComparison.OrdinalIgnoreCase))
                        para.PageBreak = true;
                    else
                        sb.Append('\n');
                    break;
                case "footnoteReference":
                    if (Attr(item, "id") is { } fnId && footnotes.TryGetValue(fnId, out string? noteText))
                    {
                        FlushRun(sb);
                        int num = docFootnotes.Count + 1;
                        docFootnotes.Add($"{num}) {noteText}");
                        para.Runs.Add(new HwpRun { Text = $"{num})", Sup = 1, Size = size });
                    }

                    break;
                case "drawing":
                    FlushRun(sb);
                    foreach (XElement blip in Descendants(item, "blip"))
                    {
                        string? rId = Attr(blip, "embed");
                        if (rId is not null && rels.TryGetValue(rId, out string? target) && LoadImageEntry(zip, target) is { } imgData)
                        {
                            double? w = null;
                            if (Descendants(item, "extent").FirstOrDefault() is { } ext
                                && double.TryParse(Attr(ext, "cx"), NumberStyles.Float, CultureInfo.InvariantCulture, out double cx) && cx > 0)
                            {
                                w = cx / 12700.0; // EMU → pt
                            }

                            para.Runs.Add(new HwpImage(imgData, w, null));
                        }
                    }

                    break;
            }
        }

        FlushRun(sb);

        void FlushRun(StringBuilder buf)
        {
            if (buf.Length == 0)
                return;

            para.Size ??= size;
            para.Runs.Add(new HwpRun
            {
                Text = buf.ToString(),
                Bold = bold,
                Italic = italic,
                Underline = underline,
                Strike = strike,
                Size = size,
                Color = color,
                Background = bg,
                Sup = sup,
                Font = font,
            });
            buf.Clear();
        }
    }

    private static HwpTable ReadDocxTable(
        XElement tbl,
        Dictionary<string, int> headingStyles,
        Dictionary<string, string> rels,
        ZipArchive zip,
        Dictionary<string, string> footnotes,
        List<string> docFootnotes)
    {
        var table = new HwpTable { Border = true };
        var gridCols = new List<double?>();
        if (Child(tbl, "tblGrid") is { } grid)
        {
            foreach (XElement col in Children(grid, "gridCol"))
            {
                double pt = TwipsToPt(Attr(col, "w"));
                gridCols.Add(pt > 0 ? pt : null);
            }
        }

        // Track active vertical merges by column index to increment RowSpan of the anchor cell.
        var activeVMerge = new Dictionary<int, HwpCell>();
        foreach (XElement tr in FlattenDocx(tbl).Where(e => e.Name.LocalName == "tr"))
        {
            var row = new List<HwpCell>();
            int colIdx = 0;
            foreach (XElement tc in FlattenDocx(tr).Where(e => e.Name.LocalName == "tc"))
            {
                XElement? tcPr = Child(tc, "tcPr");
                int colSpan = int.TryParse(Attr(Child(tcPr, "gridSpan"), "val"), out int gs) && gs > 1 ? gs : 1;
                XElement? vMerge = Child(tcPr, "vMerge");
                if (vMerge is not null && !string.Equals(Attr(vMerge, "val"), "restart", StringComparison.OrdinalIgnoreCase))
                {
                    if (activeVMerge.TryGetValue(colIdx, out HwpCell? anchor))
                        anchor.RowSpan++;
                    colIdx += colSpan;
                    continue;
                }

                string? bg = HwpxReader.HexColor(Attr(Child(tcPr, "shd"), "fill"));
                HwpCellBorders? borders = ParseDocxBorders(Child(tcPr, "tcBorders"));
                var cell = new HwpCell
                {
                    ColSpan = colSpan,
                    RowSpan = 1,
                    Background = bg,
                    Border = borders is null || borders.HasAny,
                    Borders = borders,
                    VAlign = (Attr(Child(tcPr, "vAlign"), "val"))?.ToLowerInvariant() switch
                    {
                        "center" => "middle",
                        "bottom" => "bottom",
                        _ => "top",
                    },
                };

                foreach (XElement child in FlattenDocx(tc))
                {
                    if (child.Name.LocalName == "p")
                        cell.Blocks.Add(ReadDocxPara(child, headingStyles, rels, zip, footnotes, docFootnotes));
                    else if (child.Name.LocalName == "tbl")
                        cell.Blocks.Add(ReadDocxTable(child, headingStyles, rels, zip, footnotes, docFootnotes));
                }

                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new HwpPara());

                if (vMerge is not null)
                    activeVMerge[colIdx] = cell;
                else
                    activeVMerge.Remove(colIdx);

                row.Add(cell);
                colIdx += colSpan;
            }

            if (row.Count > 0)
                table.Rows.Add(row);
        }

        table.ColumnWidths.AddRange(gridCols);
        return table;
    }

    private static HwpCellBorders? ParseDocxBorders(XElement? bordersEl)
    {
        if (bordersEl is null)
            return null;

        return new HwpCellBorders(
            Top: ParseDocxEdge(Child(bordersEl, "top")),
            Right: ParseDocxEdge(Child(bordersEl, "right") ?? Child(bordersEl, "end")),
            Bottom: ParseDocxEdge(Child(bordersEl, "bottom")),
            Left: ParseDocxEdge(Child(bordersEl, "left") ?? Child(bordersEl, "start")));

        static HwpBorderEdge? ParseDocxEdge(XElement? el)
        {
            if (el is null)
                return null;

            string? val = Attr(el, "val");
            if (string.IsNullOrEmpty(val) || string.Equals(val, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(val, "nil", StringComparison.OrdinalIgnoreCase))
                return null;

            string color = HwpxReader.HexColor(Attr(el, "color")) ?? "#000000";
            if (color == "#ffffff")
                return null;

            string style = val.ToLowerInvariant() switch
            {
                "dashed" or "dashsmallgap" or "dotdash" => "dashed",
                "dotted" => "dotted",
                "double" => "double",
                _ => "solid",
            };
            double widthPt = double.TryParse(Attr(el, "sz"), NumberStyles.Float, CultureInfo.InvariantCulture, out double eighths) && eighths > 0
                ? Math.Max(0.5, Math.Round(eighths / 8.0, 2))
                : 0.75;
            return new HwpBorderEdge(style, style == "double" ? Math.Max(widthPt, 2.0) : widthPt, color);
        }
    }

    /// <summary>Transparently flattens <c>w:sdt/w:sdtContent</c>, <c>w:ins</c>, <c>w:smartTag</c> while skipping <c>w:del</c>.</summary>
    private static IEnumerable<XElement> FlattenDocx(XElement parent)
    {
        foreach (XElement el in parent.Elements())
        {
            string local = el.Name.LocalName;
            if (local == "sdt")
            {
                if (Child(el, "sdtContent") is { } content)
                {
                    foreach (XElement inner in FlattenDocx(content))
                        yield return inner;
                }
            }
            else if (local is "ins" or "smartTag" or "dir" or "bdo")
            {
                foreach (XElement inner in FlattenDocx(el))
                    yield return inner;
            }
            else if (local != "del")
            {
                yield return el;
            }
        }
    }

    // ---- XLSX ----

    private static HwpDoc? ReadXlsx(ZipArchive zip)
    {
        if (FindEntry(zip, "xl/workbook.xml") is not { } wbEntry)
            return null;

        XElement wb = LoadXml(wbEntry);
        bool date1904 = Descendants(wb, "workbookPr").FirstOrDefault() is { } wbPr
            && (Attr(wbPr, "date1904") is "1" or "true");

        Dictionary<string, string> rels = ReadRels(zip, "xl/workbook.xml");
        List<string> sharedStrings = ReadXlsxSharedStrings(zip);
        Dictionary<int, bool> dateXfs = ReadXlsxDateStyles(zip);

        var sheets = new List<(string Name, string Path)>();
        foreach (XElement sh in Descendants(wb, "sheet"))
        {
            string name = Attr(sh, "name") ?? $"Sheet{sheets.Count + 1}";
            string? rId = sh.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Contains("relationships", StringComparison.OrdinalIgnoreCase))?.Value
                ?? Attr(sh, "id");
            if (rId is not null && rels.TryGetValue(rId, out string? target))
                sheets.Add((name, target));
        }

        if (sheets.Count == 0)
            return null;

        // Landscape A4 width for comfortable spreadsheet viewing.
        var doc = new HwpDoc { Page = new HwpPage(841.9, 595.3, 36, 36, 36, 36) };
        int renderedSheets = 0;

        foreach ((string sheetName, string sheetPath) in sheets)
        {
            if (renderedSheets >= MaxSheets)
            {
                doc.Truncated = true;
                break;
            }

            if (FindEntry(zip, sheetPath) is not { } sheetEntry)
                continue;

            var titlePara = new HwpPara { Before = 0, After = 8, PageBreak = renderedSheets > 0 };
            titlePara.Runs.Add(new HwpRun { Text = sheetName, Bold = true, Size = 12.0, Color = "#1f4e79", Font = "맑은 고딕" });
            doc.Blocks.Add(titlePara);

            HwpTable? grid = ReadXlsxSheet(LoadXml(sheetEntry), sharedStrings, dateXfs, date1904, out bool sheetTruncated);
            if (grid is not null)
                doc.Blocks.Add(grid);
            if (sheetTruncated)
                doc.Truncated = true;

            renderedSheets++;
        }

        return renderedSheets > 0 ? doc : null;
    }

    private static List<string> ReadXlsxSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        if (FindEntry(zip, "xl/sharedStrings.xml") is not { } entry)
            return list;

        XElement root = LoadXml(entry);
        foreach (XElement si in Children(root, "si"))
            list.Add(CollectXlsxRichText(si));

        return list;
    }

    /// <summary>Collects text inside <c>&lt;si&gt;</c> or <c>&lt;is&gt;</c>, skipping <c>&lt;rPh&gt;</c> furigana runs.</summary>
    private static string CollectXlsxRichText(XElement root)
    {
        var sb = new StringBuilder();
        Walk(root);
        return sb.ToString();

        void Walk(XElement el)
        {
            foreach (XElement child in el.Elements())
            {
                string local = child.Name.LocalName;
                if (local == "rPh")
                    continue;
                if (local == "t")
                    sb.Append(child.Value);
                else
                    Walk(child);
            }
        }
    }

    private static Dictionary<int, bool> ReadXlsxDateStyles(ZipArchive zip)
    {
        var dateXfs = new Dictionary<int, bool>();
        if (FindEntry(zip, "xl/styles.xml") is not { } entry)
            return dateXfs;

        XElement root = LoadXml(entry);
        var customFormats = new Dictionary<int, string>();
        foreach (XElement nf in Descendants(root, "numFmt"))
        {
            if (int.TryParse(Attr(nf, "numFmtId"), out int id))
                customFormats[id] = Attr(nf, "formatCode") ?? string.Empty;
        }

        if (Descendants(root, "cellXfs").FirstOrDefault() is not { } cellXfs)
            return dateXfs;

        int index = 0;
        foreach (XElement xf in Children(cellXfs, "xf"))
        {
            if (int.TryParse(Attr(xf, "numFmtId"), out int fmtId))
            {
                if (BuiltinDateFormats.TryGetValue(fmtId, out bool hasTime))
                {
                    dateXfs[index] = hasTime;
                }
                else if (customFormats.TryGetValue(fmtId, out string? code) && TryClassifyDateFormat(code, out bool customTime))
                {
                    dateXfs[index] = customTime;
                }
            }

            index++;
        }

        return dateXfs;
    }

    internal static bool TryClassifyDateFormat(string code, out bool hasTime)
    {
        string stripped = QuotedOrBracketedRegex().Replace(code, string.Empty);
        if (!DateTokenRegex().IsMatch(stripped))
        {
            hasTime = false;
            return false;
        }

        hasTime = TimeTokenRegex().IsMatch(stripped);
        return true;
    }

    internal static string? DateSerialToIso(double serial, bool date1904, bool hasTime)
    {
        if (!double.IsFinite(serial) || serial < 0)
            return null;

        double days = serial;
        if (date1904)
            days += 1462;
        else if (days < 60)
            days += 1;

        try
        {
            DateTime dt = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(days);
            if (dt.Year > 9999)
                return null;
            return hasTime ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static HwpTable? ReadXlsxSheet(XElement root, List<string> sharedStrings, Dictionary<int, bool> dateXfs, bool date1904, out bool truncated)
    {
        truncated = false;
        var values = new Dictionary<(int Row, int Col), string>();
        int maxRow = -1, maxCol = -1;

        if (Child(root, "sheetData") is not { } sheetData)
            return null;

        int rowFallback = 0;
        foreach (XElement rowEl in Children(sheetData, "row"))
        {
            int rowIdx = int.TryParse(Attr(rowEl, "r"), out int rNum) && rNum > 0 ? rNum - 1 : rowFallback;
            rowFallback = rowIdx + 1;
            int colFallback = 0;

            foreach (XElement c in Children(rowEl, "c"))
            {
                int colIdx = colFallback;
                if (Attr(c, "r") is { } refStr && TryParseCellRef(refStr, out int parsedRow, out int parsedCol))
                {
                    rowIdx = parsedRow;
                    colIdx = parsedCol;
                }

                colFallback = colIdx + 1;
                string cellVal = ReadXlsxCellValue(c, sharedStrings, dateXfs, date1904);
                if (cellVal.Length > 0)
                {
                    values[(rowIdx, colIdx)] = cellVal;
                    if (rowIdx > maxRow)
                        maxRow = rowIdx;
                    if (colIdx > maxCol)
                        maxCol = colIdx;
                }
            }
        }

        if (maxRow < 0 || maxCol < 0)
            return null;

        int cols = Math.Min(maxCol + 1, 50);
        int maxRowsByBudget = Math.Max(1, MaxSheetCells / Math.Max(1, cols));
        int rows = Math.Min(maxRow + 1, maxRowsByBudget);
        if (maxRow + 1 > rows || maxCol + 1 > cols)
            truncated = true;

        var merges = new Dictionary<(int Row, int Col), (int RowSpan, int ColSpan)>();
        var covered = new HashSet<(int Row, int Col)>();
        if (Child(root, "mergeCells") is { } mc)
        {
            foreach (XElement m in Children(mc, "mergeCell"))
            {
                string? range = Attr(m, "ref");
                if (range is null || range.Split(':') is not [var a, var b])
                    continue;
                if (!TryParseCellRef(a, out int r1, out int c1) || !TryParseCellRef(b, out int r2, out int c2))
                    continue;
                if (r1 >= rows || c1 >= cols)
                    continue;

                int rEnd = Math.Min(r2, rows - 1);
                int cEnd = Math.Min(c2, cols - 1);
                merges[(r1, c1)] = (rEnd - r1 + 1, cEnd - c1 + 1);
                for (int rr = r1; rr <= rEnd; rr++)
                {
                    for (int cc = c1; cc <= cEnd; cc++)
                    {
                        if (rr != r1 || cc != c1)
                            covered.Add((rr, cc));
                    }
                }
            }
        }

        var gridBorder = new HwpCellBorders(
            new HwpBorderEdge("solid", 0.5, "#cccccc"),
            new HwpBorderEdge("solid", 0.5, "#cccccc"),
            new HwpBorderEdge("solid", 0.5, "#cccccc"),
            new HwpBorderEdge("solid", 0.5, "#cccccc"));

        var table = new HwpTable { Border = true };
        for (int r = 0; r < rows; r++)
        {
            var row = new List<HwpCell>();
            for (int c = 0; c < cols; c++)
            {
                if (covered.Contains((r, c)))
                    continue;

                (int rSpan, int cSpan) = merges.TryGetValue((r, c), out var sp) ? sp : (1, 1);
                string text = values.GetValueOrDefault((r, c), string.Empty);
                var p = new HwpPara { Size = 9.0 };
                if (text.Length > 0)
                    p.Runs.Add(new HwpRun { Text = text, Size = 9.0, Bold = r == 0, Font = "맑은 고딕" });

                var cell = new HwpCell
                {
                    RowSpan = rSpan,
                    ColSpan = cSpan,
                    Border = true,
                    Borders = gridBorder,
                    Background = r == 0 ? "#f2f5f9" : null,
                    VAlign = "middle",
                };
                cell.Blocks.Add(p);
                row.Add(cell);
            }

            if (row.Count > 0)
                table.Rows.Add(row);
        }

        return table;
    }

    private static string ReadXlsxCellValue(XElement c, List<string> sharedStrings, Dictionary<int, bool> dateXfs, bool date1904)
    {
        string type = Attr(c, "t") ?? string.Empty;
        if (type == "inlineStr")
            return Child(c, "is") is { } isEl ? CollectXlsxRichText(isEl).Trim() : string.Empty;

        string raw = Child(c, "v")?.Value ?? string.Empty;
        if (raw.Length == 0)
            return string.Empty;

        if (type == "s")
            return int.TryParse(raw, out int idx) && idx >= 0 && idx < sharedStrings.Count ? sharedStrings[idx].Trim() : string.Empty;

        if (type == "b")
            return raw == "1" ? "TRUE" : "FALSE";

        if (type is "str" or "e")
            return raw.Trim();

        // Numeric or date serial
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
        {
            if (int.TryParse(Attr(c, "s"), out int styleIdx) && dateXfs.TryGetValue(styleIdx, out bool hasTime)
                && DateSerialToIso(num, date1904, hasTime) is { } iso)
            {
                return iso;
            }

            // Clean IEEE 754 representation artifacts (132.30000000000001 → 132.3).
            return num.ToString("G15", CultureInfo.InvariantCulture);
        }

        return raw.Trim();
    }

    private static bool TryParseCellRef(string reference, out int row, out int col)
    {
        Match m = CellRefRegex().Match(reference);
        if (!m.Success)
        {
            row = col = 0;
            return false;
        }

        int c = 0;
        foreach (char ch in m.Groups[1].Value)
            c = (c * 26) + (ch - 'A' + 1);
        col = c - 1;
        row = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
        return row >= 0 && col >= 0;
    }

    // ---- PPTX ----

    private static HwpDoc? ReadPptx(ZipArchive zip)
    {
        List<string> slidePaths = ResolvePptxSlidePaths(zip);
        if (slidePaths.Count == 0)
            return null;

        // 16:9 slide-like sheet dimensions in points.
        var doc = new HwpDoc { Page = new HwpPage(720, 450, 36, 36, 30, 30) };
        int slideNo = 0;

        foreach (string slidePath in slidePaths)
        {
            if (slideNo >= MaxSlides)
            {
                doc.Truncated = true;
                break;
            }

            if (FindEntry(zip, slidePath) is not { } slideEntry)
                continue;

            XElement slideRoot = LoadXml(slideEntry);
            if (Attr(slideRoot, "show") == "0")
                continue; // hidden slide

            slideNo++;
            Dictionary<string, string> rels = ReadRels(zip, slidePath);

            var header = new HwpPara { Before = 0, After = 8, PageBreak = slideNo > 1 };
            header.Runs.Add(new HwpRun { Text = $"슬라이드 {slideNo}", Bold = true, Size = 9.0, Color = "#666666", Font = "맑은 고딕" });
            doc.Blocks.Add(header);

            if (Child(slideRoot, "cSld") is { } cSld && Child(cSld, "spTree") is { } spTree)
                ReadPptxTree(spTree, rels, zip, doc.Blocks);

            // Speaker notes (if any)
            string? notesPath = rels.Values.FirstOrDefault(t => t.Contains("notesSlides/", StringComparison.OrdinalIgnoreCase));
            if (notesPath is not null && FindEntry(zip, notesPath) is { } notesEntry)
            {
                string notesText = ExtractPptxNotes(LoadXml(notesEntry));
                if (notesText.Length > 0)
                {
                    var notePara = new HwpPara { Before = 10, Left = 10 };
                    notePara.Runs.Add(new HwpRun { Text = $"[발표자 노트] {notesText}", Italic = true, Size = 9.0, Color = "#555555", Font = "맑은 고딕" });
                    doc.Blocks.Add(notePara);
                }
            }
        }

        return slideNo > 0 ? doc : null;
    }

    private static List<string> ResolvePptxSlidePaths(ZipArchive zip)
    {
        var ordered = new List<string>();
        if (FindEntry(zip, "ppt/presentation.xml") is { } presEntry)
        {
            XElement pres = LoadXml(presEntry);
            Dictionary<string, string> rels = ReadRels(zip, "ppt/presentation.xml");
            if (Descendants(pres, "sldIdLst").FirstOrDefault() is { } list)
            {
                foreach (XElement sld in Children(list, "sldId"))
                {
                    string? rId = sld.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Contains("relationships", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (rId is not null && rels.TryGetValue(rId, out string? target) && FindEntry(zip, target) is not null)
                        ordered.Add(target);
                }
            }
        }

        if (ordered.Count > 0)
            return ordered;

        return zip.Entries
            .Where(e => SlidePartRegex().IsMatch(e.FullName))
            .OrderBy(e => int.Parse(SlidePartRegex().Match(e.FullName).Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(e => e.FullName)
            .ToList();
    }

    private static void ReadPptxTree(XElement container, Dictionary<string, string> rels, ZipArchive zip, List<object> blocks)
    {
        foreach (XElement el in container.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "sp":
                    string? phType = Descendants(el, "ph").FirstOrDefault() is { } ph ? (Attr(ph, "type") ?? "obj") : null;
                    if (phType is not null && SkipSlidePlaceholders.Contains(phType))
                        continue;

                    bool isTitle = phType is "title" or "ctrTitle";
                    bool isSub = phType is "subTitle";
                    if (Child(el, "txBody") is { } txBody)
                    {
                        foreach (XElement p in Children(txBody, "p"))
                        {
                            HwpPara? para = ReadDrawingPara(p, isTitle, isSub);
                            if (para is not null)
                                blocks.Add(para);
                        }
                    }

                    break;

                case "grpSp":
                    ReadPptxTree(el, rels, zip, blocks);
                    break;

                case "pic":
                    if (Descendants(el, "blip").FirstOrDefault() is { } blip && Attr(blip, "embed") is { } rId
                        && rels.TryGetValue(rId, out string? target) && LoadImageEntry(zip, target) is { } bytes)
                    {
                        var imgPara = new HwpPara { Before = 4, After = 4 };
                        imgPara.Runs.Add(new HwpImage(bytes, 360, null));
                        blocks.Add(imgPara);
                    }

                    break;

                case "graphicFrame":
                    foreach (XElement tbl in Descendants(el, "tbl"))
                        blocks.Add(ReadPptxTable(tbl));
                    break;
            }
        }
    }

    private static HwpPara? ReadDrawingPara(XElement p, bool isTitle, bool isSub)
    {
        XElement? pPr = Child(p, "pPr");
        int lvl = int.TryParse(Attr(pPr, "lvl"), out int lv) ? Math.Max(0, lv) : 0;
        bool hasBullet = !isTitle && !isSub && Child(pPr, "buNone") is null
            && (Child(pPr, "buChar") is not null || Child(pPr, "buAutoNum") is not null || lvl > 0);
        string align = (Attr(pPr, "algn"))?.ToLowerInvariant() switch
        {
            "ctr" => "center",
            "r" => "right",
            "just" => "justify",
            _ => "left",
        };

        var para = new HwpPara
        {
            Align = align,
            Left = lvl * 16.0,
            Before = isTitle ? 4 : 2,
            After = isTitle ? 8 : 3,
            Line = 1.3,
        };

        if (hasBullet)
        {
            string bChar = Attr(Child(pPr, "buChar"), "char") ?? "•";
            para.Runs.Add(new HwpRun { Text = bChar + " ", Size = 10.5, Font = "맑은 고딕" });
        }

        bool anyText = false;
        foreach (XElement child in p.Elements())
        {
            if (child.Name.LocalName is "r" or "fld")
            {
                string text = Child(child, "t")?.Value ?? string.Empty;
                if (text.Length == 0)
                    continue;

                XElement? rPr = Child(child, "rPr");
                bool bold = isTitle || Attr(rPr, "b") == "1";
                bool italic = Attr(rPr, "i") == "1";
                double size = double.TryParse(Attr(rPr, "sz"), NumberStyles.Float, CultureInfo.InvariantCulture, out double hundredths) && hundredths > 0
                    ? Math.Clamp(hundredths / 100.0, 8.0, 26.0)
                    : (isTitle ? 18.0 : isSub ? 13.0 : 10.5);
                int baseline = int.TryParse(Attr(rPr, "baseline"), out int bl) ? bl : 0;
                string? color = Descendants(rPr, "srgbClr").FirstOrDefault() is { } clr ? HwpxReader.HexColor(Attr(clr, "val")) : null;

                para.Size ??= size;
                para.Runs.Add(new HwpRun
                {
                    Text = text,
                    Bold = bold,
                    Italic = italic,
                    Size = size,
                    Color = color,
                    Sup = baseline > 0 ? 1 : baseline < 0 ? -1 : 0,
                    Font = "맑은 고딕",
                });
                anyText = true;
            }
            else if (child.Name.LocalName == "br")
            {
                para.Runs.Add(new HwpRun { Text = "\n" });
            }
        }

        return anyText ? para : null;
    }

    private static HwpTable ReadPptxTable(XElement tbl)
    {
        var table = new HwpTable { Border = true };
        var border = new HwpCellBorders(
            new HwpBorderEdge("solid", 0.5, "#999999"),
            new HwpBorderEdge("solid", 0.5, "#999999"),
            new HwpBorderEdge("solid", 0.5, "#999999"),
            new HwpBorderEdge("solid", 0.5, "#999999"));

        int rowIndex = 0;
        foreach (XElement tr in Children(tbl, "tr"))
        {
            var row = new List<HwpCell>();
            foreach (XElement tc in Children(tr, "tc"))
            {
                if (Attr(tc, "hMerge") == "1" || Attr(tc, "vMerge") == "1")
                    continue;

                var cell = new HwpCell
                {
                    ColSpan = int.TryParse(Attr(tc, "gridSpan"), out int gs) && gs > 1 ? gs : 1,
                    RowSpan = int.TryParse(Attr(tc, "rowSpan"), out int rs) && rs > 1 ? rs : 1,
                    Border = true,
                    Borders = border,
                    Background = rowIndex == 0 ? "#eef2f8" : null,
                    VAlign = "middle",
                };
                if (Child(tc, "txBody") is { } txBody)
                {
                    foreach (XElement p in Children(txBody, "p"))
                    {
                        if (ReadDrawingPara(p, isTitle: false, isSub: false) is { } para)
                            cell.Blocks.Add(para);
                    }
                }

                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new HwpPara());

                row.Add(cell);
            }

            if (row.Count > 0)
                table.Rows.Add(row);
            rowIndex++;
        }

        return table;
    }

    private static string ExtractPptxNotes(XElement notesRoot)
    {
        var sb = new StringBuilder();
        foreach (XElement sp in Descendants(notesRoot, "sp"))
        {
            string? phType = Descendants(sp, "ph").FirstOrDefault() is { } ph ? Attr(ph, "type") : null;
            if (phType is not null && SkipSlidePlaceholders.Contains(phType))
                continue;

            foreach (XElement t in Descendants(sp, "t"))
            {
                string val = t.Value.Trim();
                if (val.Length > 0)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(val);
                }
            }
        }

        return sb.ToString();
    }

    // ---- Shared ZIP / XML helpers ----

    private static Dictionary<string, string> ReadRels(ZipArchive zip, string partPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int slash = partPath.LastIndexOf('/');
        string dir = slash >= 0 ? partPath[..slash] : string.Empty;
        string fileName = slash >= 0 ? partPath[(slash + 1)..] : partPath;
        string relsPath = string.IsNullOrEmpty(dir) ? $"_rels/{fileName}.rels" : $"{dir}/_rels/{fileName}.rels";
        if (FindEntry(zip, relsPath) is not { } entry)
            return map;

        XElement root = LoadXml(entry);
        foreach (XElement rel in Children(root, "Relationship"))
        {
            string? id = Attr(rel, "Id");
            string? target = Attr(rel, "Target");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target) || string.Equals(Attr(rel, "TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                continue;

            map[id] = ResolvePartPath(dir, target);
        }

        return map;
    }

    private static string ResolvePartPath(string baseDir, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');

        var segs = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (string seg in target.Split('/'))
        {
            if (seg == "..")
            {
                if (segs.Count > 0)
                    segs.RemoveAt(segs.Count - 1);
            }
            else if (seg.Length > 0 && seg != ".")
            {
                segs.Add(seg);
            }
        }

        return string.Join('/', segs);
    }

    private static byte[]? LoadImageEntry(ZipArchive zip, string path)
    {
        if (FindEntry(zip, path) is not { } entry || entry.Length > HwpLimits.MaxImageBytes)
            return null;

        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static XElement LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPartBytes)
            throw new InvalidDataException($"{entry.FullName} is too large.");

        using Stream stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        return XElement.Load(reader);
    }

    private static IEnumerable<XElement> Descendants(XElement? el, string localName) =>
        el is null ? [] : el.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? el, string localName) =>
        el is null ? [] : el.Elements().Where(e => e.Name.LocalName == localName);

    private static XElement? Child(XElement? el, string localName) => Children(el, localName).FirstOrDefault();

    private static string? Attr(XElement? el, string localName) =>
        el?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static bool IsOn(XElement? el) =>
        el is not null && Attr(el, "val") is not ("0" or "false" or "False");

    private static double TwipsToPt(string? val) =>
        double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double twips) ? twips / 20.0 : 0;

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

    [GeneratedRegex(@"^heading\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingNameRegex();

    [GeneratedRegex(@"^([A-Z]+)(\d+)$")]
    private static partial Regex CellRefRegex();

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex SlidePartRegex();

    [GeneratedRegex(@"""[^""]*""|\[[^\]]*\]|\\.")]
    private static partial Regex QuotedOrBracketedRegex();

    [GeneratedRegex(@"[ymdh]", RegexOptions.IgnoreCase)]
    private static partial Regex DateTokenRegex();

    [GeneratedRegex(@"[hs]", RegexOptions.IgnoreCase)]
    private static partial Regex TimeTokenRegex();
}

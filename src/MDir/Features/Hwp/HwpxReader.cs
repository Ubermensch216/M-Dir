using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MDir.Features.Hwp;

/// <summary>
/// Reads an HWPX document (a zip of OWPML XML parts) into <see cref="HwpDoc"/>: character and paragraph shapes,
/// per-side border fills, auto-numbering/bullets, equations, footnotes, captions, track-changes deletion filtering,
/// <c>content.hpf</c> manifest ordering, <c>linesegarray</c> page boundaries, and Local File Header broken-ZIP recovery.
/// </summary>
internal sealed partial class HwpxReader
{
    private const long MaxPart = 64L * 1024 * 1024;
    private const int MidPageVertPos = 2000;

    private static readonly Dictionary<string, string> Alignments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JUSTIFY"] = "justify", ["LEFT"] = "left", ["RIGHT"] = "right", ["CENTER"] = "center",
        ["DISTRIBUTE"] = "justify", ["DISTRIBUTE_SPACE"] = "justify",
    };

    private static readonly HashSet<string> Shapes = ["rect", "ellipse", "container", "polygon", "line", "arc", "curve", "textart", "ole"];

    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, byte[]>? _recoveredParts;
    private readonly Dictionary<string, string> _fonts = [];
    private readonly Dictionary<string, CharShape> _chars = [];
    private readonly Dictionary<string, ParaShape> _paras = [];
    private readonly Dictionary<string, Fill> _fills = [];
    private readonly Dictionary<string, NumberingDef> _numberings = [];
    private readonly Dictionary<string, string> _bullets = [];
    private readonly Dictionary<string, ZipArchiveEntry> _binaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HwpNumberingState _numberingState = new();
    private readonly List<string> _footnotes = [];
    private string? _outlineNumberingId;
    private int _deleteDepth;
    private int _count;
    private bool _truncated;

    private sealed record CharShape(double Size, string? Color, string? Background, bool Bold, bool Italic, bool Underline, bool Strike, int Sup, string? Font, double Spacing);

    private sealed record ParaShape(string Align, double Left, double First, double Before, double After, double? Line, string? HeadingType, string? HeadingIdRef, int HeadingLevel);

    private sealed record Fill(string? Background, bool Border, HwpCellBorders Borders);

    private sealed record NumberingDef(Dictionary<int, (string Format, string Text, int Start)> Heads);

    private HwpxReader(ZipArchive zip) => _zip = zip;

    private HwpxReader(Dictionary<string, byte[]> recoveredParts) => _recoveredParts = recoveredParts;

    /// <summary>The document, or null when the zip has no sections.</summary>
    public static HwpDoc? Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return new HwpxReader(zip).Read();
    }

    /// <summary>Reads an HWPX from raw bytes, falling back to Local File Header recovery if the Central Directory is damaged.</summary>
    public static HwpDoc? ReadBytes(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            return Read(ms);
        }
        catch (InvalidDataException)
        {
            Dictionary<string, byte[]> recovered = RecoverLocalZipEntries(data);
            return recovered.Count > 0 ? new HwpxReader(recovered).ReadRecovered() : null;
        }
    }

    private HwpDoc? Read()
    {
        ZipArchive zip = _zip!;
        List<ZipArchiveEntry> sections = ResolveSectionEntries(zip);
        if (sections.Count == 0)
            return null;

        ZipArchiveEntry? header = FindEntry(zip, "Contents/header.xml")
            ?? FindEntry(zip, "header.xml")
            ?? FindEntry(zip, "Contents/head.xml")
            ?? FindEntry(zip, "head.xml");
        if (header is not null)
            ReadHeader(Load(header));

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase))
                _binaries[Path.GetFileNameWithoutExtension(entry.Name)] = entry;
        }

        return ReadSectionElements(sections.Select(Load));
    }

    private HwpDoc? ReadRecovered()
    {
        Dictionary<string, byte[]> parts = _recoveredParts!;
        foreach (string candidate in new[] { "Contents/header.xml", "header.xml", "Contents/head.xml", "head.xml" })
        {
            if (parts.TryGetValue(candidate, out byte[]? headerBytes))
            {
                try
                {
                    ReadHeader(LoadBytes(headerBytes));
                    break;
                }
                catch (XmlException)
                {
                }
            }
        }

        var sectionEntries = parts
            .Where(kv => SectionFileRegex().IsMatch(kv.Key))
            .OrderBy(kv => SectionOrder(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
        if (sectionEntries.Count == 0)
            return null;

        var elements = new List<XElement>();
        foreach (byte[] bytes in sectionEntries)
        {
            try
            {
                elements.Add(LoadBytes(bytes));
            }
            catch (XmlException)
            {
            }
        }

        return elements.Count > 0 ? ReadSectionElements(elements) : null;
    }

    private HwpDoc ReadSectionElements(IEnumerable<XElement> sections)
    {
        var doc = new HwpDoc();
        bool first = true;
        foreach (XElement root in sections)
        {
            _deleteDepth = 0; // Deletion tracking never crosses section boundaries.

            if (first && Descendants(root, "pagePr").FirstOrDefault() is { } page)
                doc.Page = PageOf(page);

            if (Descendants(root, "secPr").FirstOrDefault() is { } secPr
                && (string?)secPr.Attribute("outlineShapeIDRef") is { Length: > 0 } outlineId)
            {
                _outlineNumberingId = outlineId;
            }

            HashSet<XElement> layoutBreaks = DetectLayoutPageBreaks(root);

            int start = doc.Blocks.Count;
            foreach (XElement p in Children(root, "p"))
            {
                if (_count >= HwpLimits.MaxBlocks)
                {
                    _truncated = true;
                    break;
                }

                List<object> blocks = [.. Para(p)];
                if (layoutBreaks.Contains(p) && blocks.Count > 0 && blocks[0] is HwpPara firstPara)
                    firstPara.PageBreak = true;

                doc.Blocks.AddRange(blocks);
                _count++;
            }

            // A new section starts on a new page.
            if (!first && doc.Blocks.Count > start && doc.Blocks[start] is HwpPara para)
                para.PageBreak = true;

            first = false;
            if (_truncated)
                break;
        }

        doc.Footnotes.AddRange(_footnotes);
        doc.Truncated = _truncated;
        return doc;
    }

    /// <summary>
    /// Resolves section order from <c>Contents/content.hpf</c> manifest/spine first (benchmarked from kordoc
    /// <c>src/hwpx/zip-sections.ts</c>), falling back to numeric <c>sectionN.xml</c> scan.
    /// </summary>
    private static List<ZipArchiveEntry> ResolveSectionEntries(ZipArchive zip)
    {
        ZipArchiveEntry? manifest = FindEntry(zip, "Contents/content.hpf") ?? FindEntry(zip, "content.hpf");
        if (manifest is not null)
        {
            try
            {
                XElement hpf = Load(manifest);
                var idToHref = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement item in Descendants(hpf, "item"))
                {
                    string? id = (string?)item.Attribute("id");
                    string? href = (string?)item.Attribute("href");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(href))
                        idToHref[id] = NormalizeHref(href);
                }

                var ordered = new List<ZipArchiveEntry>();
                foreach (XElement itemref in Descendants(hpf, "itemref"))
                {
                    string? idref = (string?)itemref.Attribute("idref");
                    if (idref is not null && idToHref.TryGetValue(idref, out string? href)
                        && SectionFileRegex().IsMatch(href)
                        && FindEntry(zip, href) is { } entry)
                    {
                        ordered.Add(entry);
                    }
                }

                if (ordered.Count > 0)
                    return ordered;
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            {
            }
        }

        return zip.Entries
            .Where(e => SectionFileRegex().IsMatch(e.FullName))
            .OrderBy(e => SectionOrder(e.FullName))
            .ToList();
    }

    private static string NormalizeHref(string href)
    {
        string cleaned = href.Replace('\\', '/').TrimStart('/');
        if (!cleaned.Contains('/', StringComparison.Ordinal) && SectionFileRegex().IsMatch("Contents/" + cleaned))
            return "Contents/" + cleaned;
        return cleaned;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static int SectionOrder(string path)
    {
        Match m = SectionFileRegex().Match(path);
        return m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    [GeneratedRegex(@"(?:^|/)section(\d+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionFileRegex();

    /// <summary>
    /// Scans Local File Headers (<c>PK\x03\x04</c>) directly when the ZIP Central Directory is corrupt
    /// (benchmarked from kordoc <c>src/hwpx/zip-sections.ts</c>).
    /// </summary>
    private static Dictionary<string, byte[]> RecoverLocalZipEntries(byte[] data)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        long totalDecompressed = 0;
        int entryCount = 0;
        const int maxEntries = 500;
        const long maxTotal = 64L * 1024 * 1024;

        while (pos < data.Length - 30 && entryCount < maxEntries)
        {
            if (data[pos] != 0x50 || data[pos + 1] != 0x4B || data[pos + 2] != 0x03 || data[pos + 3] != 0x04)
            {
                pos++;
                continue;
            }

            entryCount++;
            ushort method = BitConverter.ToUInt16(data, pos + 8);
            uint compSize = BitConverter.ToUInt32(data, pos + 18);
            ushort nameLen = BitConverter.ToUInt16(data, pos + 26);
            ushort extraLen = BitConverter.ToUInt16(data, pos + 28);

            if (nameLen is 0 or > 1024)
            {
                pos += 4;
                continue;
            }

            long fileStart = (long)pos + 30 + nameLen + extraLen;
            if (fileStart + compSize > data.Length)
                break;

            string name = Encoding.UTF8.GetString(data, pos + 30, nameLen).Replace('\\', '/');
            if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/'))
            {
                pos = (int)(fileStart + compSize);
                continue;
            }

            if (compSize == 0 && method != 0)
            {
                pos = (int)fileStart;
                continue;
            }

            byte[] raw = data.AsSpan((int)fileStart, (int)compSize).ToArray();
            pos = (int)(fileStart + compSize);

            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                byte[] bytes = method switch
                {
                    0 => raw,
                    8 => HwpReader.Inflate(raw),
                    _ => [],
                };
                if (bytes.Length == 0)
                    continue;

                totalDecompressed += bytes.Length;
                if (totalDecompressed > maxTotal)
                    break;

                result[name] = bytes;
            }
            catch (InvalidDataException)
            {
            }
        }

        return result;
    }

    /// <summary>
    /// Detects actual page transitions from <c>&lt;hp:linesegarray&gt;</c> vertical-position resets when all top-level
    /// paragraphs carry layout cache (benchmarked from kordoc <c>src/hwpx/page-boundary.ts</c>).
    /// </summary>
    private static HashSet<XElement> DetectLayoutPageBreaks(XElement root)
    {
        var breaks = new HashSet<XElement>();
        var topParas = Children(root, "p").ToList();
        if (topParas.Count == 0)
            return breaks;

        bool multiCol = Descendants(root, "colPr").FirstOrDefault() is { } colPr && Number(colPr, "colCount", 1) > 1;
        int cur = 0;
        double prevV = double.NegativeInfinity;
        double prevH = double.NegativeInfinity;
        bool first = true;
        bool suppressMidReset = false;
        int withSegs = 0;

        foreach (XElement p in topParas)
        {
            bool explicitBreak = (string?)p.Attribute("pageBreak") == "1";
            bool brokeByExplicit = false;
            int beforePage = cur;
            if (explicitBreak && !first)
            {
                cur++;
                brokeByExplicit = true;
                suppressMidReset = false;
            }

            List<XElement> segs = LineSegsOf(p);
            if (segs.Count > 0)
                withSegs++;

            bool paraFirst = true;
            int startPage = cur;
            foreach (XElement seg in segs)
            {
                double v = Number(seg, "vertpos");
                double h = Number(seg, "horzpos");
                bool brk = v < prevV
                    ? (!multiCol || h <= prevH)
                    : (paraFirst && v == prevV && h <= prevH);
                if (brk && !(paraFirst && brokeByExplicit))
                {
                    if (!(paraFirst && suppressMidReset && v >= MidPageVertPos))
                        cur++;
                }

                if (paraFirst)
                {
                    suppressMidReset = false;
                    startPage = cur;
                }

                paraFirst = false;
                prevV = v;
                prevH = h;
            }

            if (!first && startPage > beforePage)
                breaks.Add(p);

            first = false;

            int intraAdd = 0;
            foreach (XElement r in Children(p, "run"))
            {
                foreach (XElement tbl in Children(r, "tbl"))
                    intraAdd += TableIntraBreaks(tbl);
            }

            if (intraAdd > 0)
            {
                cur += intraAdd;
                suppressMidReset = true;
            }
        }

        return withSegs == topParas.Count ? breaks : [];
    }

    private static List<XElement> LineSegsOf(XElement p) =>
        Child(p, "linesegarray") is { } lsa ? Children(lsa, "lineseg").ToList() : [];

    private static int TableIntraBreaks(XElement tbl)
    {
        var rowMax = new Dictionary<int, int>();
        foreach (XElement tr in Children(tbl, "tr"))
        {
            foreach (XElement tc in Children(tr, "tc"))
            {
                int row = (int)Number(Child(tc, "cellAddr"), "rowAddr");
                if (Child(tc, "subList") is not { } sub)
                    continue;

                double prevV = double.NegativeInfinity;
                int resets = 0;
                foreach (XElement p in Children(sub, "p"))
                {
                    foreach (XElement seg in LineSegsOf(p))
                    {
                        double v = Number(seg, "vertpos");
                        if (v < prevV)
                            resets++;
                        prevV = v;
                    }
                }

                rowMax[row] = Math.Max(rowMax.GetValueOrDefault(row), resets);
            }
        }

        return rowMax.Values.Sum();
    }

    private static XElement Load(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPart)
            throw new InvalidDataException($"{entry.FullName} is too large.");

        using Stream stream = entry.Open();
        return LoadStream(stream);
    }

    private static XElement LoadBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        return LoadStream(ms);
    }

    private static XElement LoadStream(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        return XElement.Load(reader);
    }

    // ---- header.xml ----

    private void ReadHeader(XElement root)
    {
        foreach (XElement face in Descendants(root, "fontface"))
        {
            if (string.Equals((string?)face.Attribute("lang"), "HANGUL", StringComparison.OrdinalIgnoreCase))
            {
                foreach (XElement font in Children(face, "font"))
                    _fonts[(string?)font.Attribute("id") ?? string.Empty] = (string?)font.Attribute("face") ?? string.Empty;
            }
        }

        foreach (XElement cp in Descendants(root, "charPr"))
        {
            string? shade = (string?)cp.Attribute("shadeColor");
            XElement? underline = Child(cp, "underline");
            XElement? strike = Child(cp, "strikeout");
            string? fontId = (string?)Child(cp, "fontRef")?.Attribute("hangul");
            _chars[(string?)cp.Attribute("id") ?? string.Empty] = new CharShape(
                Number(cp, "height", 1000) / 100,
                HexColor((string?)cp.Attribute("textColor")),
                shade is null or "none" ? null : HexColor(shade),
                Child(cp, "bold") is not null || (string?)cp.Attribute("bold") is "1" or "true",
                Child(cp, "italic") is not null || (string?)cp.Attribute("italic") is "1" or "true",
                underline is not null && !IsNone((string?)underline.Attribute("type")) && !IsNone((string?)underline.Attribute("shape") ?? "SOLID"),
                HasStrike(strike),
                Child(cp, "supscript") is not null ? 1 : Child(cp, "subscript") is not null ? -1 : 0,
                fontId is not null && _fonts.TryGetValue(fontId, out string? font) ? font : null,
                HwpSpacing.ToEm(Number(Child(cp, "spacing"), "hangul"), Number(Child(cp, "ratio"), "hangul", 100)));
        }

        foreach (XElement num in Descendants(root, "numbering"))
        {
            string id = (string?)num.Attribute("id") ?? string.Empty;
            if (id.Length == 0)
                continue;

            var heads = new Dictionary<int, (string Format, string Text, int Start)>();
            foreach (XElement head in Children(num, "paraHead"))
            {
                int level = (int)Number(head, "level");
                if (level is < 1 or > 10)
                    continue;

                int start = Math.Max(1, (int)Number(head, "start", 1));
                string format = (string?)head.Attribute("numFormat") ?? "DIGIT";
                heads[level] = (format, head.Value, start);
            }

            if (heads.Count > 0)
                _numberings[id] = new NumberingDef(heads);
        }

        foreach (XElement bullet in Descendants(root, "bullet"))
        {
            string id = (string?)bullet.Attribute("id") ?? string.Empty;
            string ch = (string?)bullet.Attribute("char") ?? string.Empty;
            if (id.Length > 0 && ch.Length > 0)
                _bullets[id] = HwpPuaMapper.MapText(ch);
        }

        foreach (XElement pp in Descendants(root, "paraPr"))
        {
            string align = Alignments.GetValueOrDefault((string?)Child(pp, "align")?.Attribute("horizontal") ?? "LEFT", "left");
            double left = 0, first = 0, before = 0, after = 0;
            if (Descendants(pp, "margin").FirstOrDefault() is { } margin)
            {
                left = MarginValue(margin, "left");
                first = MarginValue(margin, "intent");
                before = MarginValue(margin, "prev");
                after = MarginValue(margin, "next");
            }

            double? line = null;
            if (Descendants(pp, "lineSpacing").FirstOrDefault() is { } spacing
                && string.Equals((string?)spacing.Attribute("type") ?? "PERCENT", "PERCENT", StringComparison.OrdinalIgnoreCase))
            {
                double value = Number(spacing, "value", 160);
                if (value is >= 50 and <= 400)
                    line = value / 100;
            }

            XElement? heading = Child(pp, "heading");
            string? hType = (string?)heading?.Attribute("type");
            if (hType is not ("NUMBER" or "BULLET" or "OUTLINE"))
                hType = null;
            string? hId = (string?)heading?.Attribute("idRef");
            int hLevel = (int)Number(heading, "level");

            _paras[(string?)pp.Attribute("id") ?? string.Empty] = new ParaShape(align, left, first, before, after, line, hType, hId, hLevel);
        }

        foreach (XElement bf in Descendants(root, "borderFill"))
        {
            var edges = new HwpCellBorders(
                Top: ParseBorderEdge(Child(bf, "topBorder")),
                Right: ParseBorderEdge(Child(bf, "rightBorder")),
                Bottom: ParseBorderEdge(Child(bf, "bottomBorder")),
                Left: ParseBorderEdge(Child(bf, "leftBorder")));
            string? background = null;
            foreach (XElement brush in Descendants(bf, "winBrush"))
            {
                string? face = (string?)brush.Attribute("faceColor");
                if (!string.IsNullOrEmpty(face) && !string.Equals(face, "none", StringComparison.OrdinalIgnoreCase))
                    background = HexColor(face);
            }

            _fills[(string?)bf.Attribute("id") ?? string.Empty] = new Fill(background, edges.HasAny, edges);
        }
    }

    private static HwpBorderEdge? ParseBorderEdge(XElement? el)
    {
        if (el is null)
            return null;

        string? type = (string?)el.Attribute("type");
        if (IsNone(type))
            return null;

        string color = HexColor((string?)el.Attribute("color")) ?? "#000000";
        if (string.Equals(color, "#ffffff", StringComparison.OrdinalIgnoreCase))
            return null; // Invisible white border on white page.

        string cssStyle = type!.ToUpperInvariant() switch
        {
            "DASH" or "LONG_DASH" or "DASH_DOT" or "DASH_DOT_DOT" => "dashed",
            "DOT" or "CIRCLE" => "dotted",
            "DOUBLE_SLIM" or "SLIM_THICK" or "THICK_SLIM" or "SLIM_THICK_SLIM" => "double",
            _ => "solid",
        };
        double widthPt = ParseBorderWidthPt((string?)el.Attribute("width"), cssStyle == "double");
        return new HwpBorderEdge(cssStyle, widthPt, color);
    }

    private static double ParseBorderWidthPt(string? raw, bool isDouble)
    {
        double mm = 0.12;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string trimmed = raw.Trim();
            int spaceOrAlpha = 0;
            while (spaceOrAlpha < trimmed.Length && (char.IsDigit(trimmed[spaceOrAlpha]) || trimmed[spaceOrAlpha] is '.' or '-'))
                spaceOrAlpha++;

            if (spaceOrAlpha > 0 && double.TryParse(trimmed[..spaceOrAlpha], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
                mm = parsed;
        }

        double pt = Math.Max(0.5, Math.Round(mm * 2.834645, 2));
        return isDouble ? Math.Max(pt, 2.0) : pt;
    }

    private static double MarginValue(XElement margin, string name)
    {
        if (Child(margin, name) is not { } c)
            return 0;

        double raw = Number(c, "value");
        if (string.Equals((string?)c.Attribute("unit"), "CHAR", StringComparison.OrdinalIgnoreCase))
            raw *= 5.0; // CHAR unit = 5 HWPUNIT (verified in kordoc head-styles.ts).

        return raw / 100.0;
    }

    private static HwpPage PageOf(XElement page)
    {
        double width = Number(page, "width") / 100, height = Number(page, "height") / 100;
        if (width < 50 || height < 50)
            return HwpPage.Default;

        // "NARROWLY" is landscape; the stored width and height are those of the paper as it stands.
        bool landscape = string.Equals((string?)page.Attribute("landscape"), "NARROWLY", StringComparison.OrdinalIgnoreCase);
        (double w, double h) = landscape ? (Math.Max(width, height), Math.Min(width, height)) : (Math.Min(width, height), Math.Max(width, height));
        if (Child(page, "margin") is not { } m)
            return HwpPage.Default with { Width = w, Height = h };

        return new HwpPage(w, h,
            Number(m, "left") / 100,
            Number(m, "right") / 100,
            (Number(m, "top") + Number(m, "header")) / 100,
            (Number(m, "bottom") + Number(m, "footer")) / 100);
    }

    // ---- sections ----

    private IEnumerable<object> Para(XElement p)
    {
        ParaShape? shape = _paras.GetValueOrDefault((string?)p.Attribute("paraPrIDRef") ?? string.Empty);
        var para = new HwpPara
        {
            Align = shape?.Align ?? "left",
            Left = shape?.Left ?? 0,
            First = shape?.First ?? 0,
            Before = shape?.Before ?? 0,
            After = shape?.After ?? 0,
            Line = shape?.Line,
            PageBreak = (string?)p.Attribute("pageBreak") == "1",
        };
        string? headingPrefix = ResolveHeadingPrefix(shape);
        CharShape? firstCs = null;

        var extra = new List<object>();
        foreach (XElement run in Children(p, "run"))
        {
            CharShape? cs = _chars.GetValueOrDefault((string?)run.Attribute("charPrIDRef") ?? string.Empty);
            firstCs ??= cs;
            para.Size ??= cs?.Size;
            foreach (XElement c in run.Elements())
            {
                string name = c.Name.LocalName;
                if (name == "t")
                {
                    string text = TextOf(c);
                    if (text.Length > 0)
                    {
                        para.Runs.Add(new HwpRun
                        {
                            Text = text,
                            Bold = cs?.Bold ?? false,
                            Italic = cs?.Italic ?? false,
                            Underline = cs?.Underline ?? false,
                            Strike = cs?.Strike ?? false,
                            Size = cs?.Size,
                            Color = cs?.Color,
                            Background = cs?.Background,
                            Sup = cs?.Sup ?? 0,
                            Font = cs?.Font,
                            Spacing = cs?.Spacing ?? 0,
                        });
                    }
                }
                else if (name == "tbl")
                {
                    if (_deleteDepth == 0)
                        extra.Add(Table(c));
                }
                else if (name == "pic")
                {
                    if (_deleteDepth == 0 && Image(c) is { } image)
                        para.Runs.Add(image);
                }
                else if (name == "equation")
                {
                    if (_deleteDepth == 0 && Child(c, "script") is { } scriptEl && !string.IsNullOrWhiteSpace(scriptEl.Value))
                        para.Runs.Add(new HwpEquationRun(scriptEl.Value.Trim()));
                }
                else if (name == "compose")
                {
                    if (_deleteDepth == 0)
                    {
                        string chars = (string?)c.Attribute("composeText") ?? string.Empty;
                        bool enclosed = !IsNone((string?)c.Attribute("circleType") ?? "CHAR")
                            && !string.Equals((string?)c.Attribute("circleType"), "CHAR", StringComparison.OrdinalIgnoreCase);
                        string composed = HwpPuaMapper.ComposeText(chars, enclosed);
                        if (composed.Length > 0)
                            para.Runs.Add(new HwpRun { Text = composed, Size = cs?.Size, Color = cs?.Color, Font = cs?.Font });
                    }
                }
                else if (name == "ctrl")
                {
                    HandleCtrl(c, para, cs);
                }
                else if (Shapes.Contains(name))
                {
                    if (_deleteDepth > 0)
                        continue;

                    foreach (XElement pic in Descendants(c, "pic"))
                    {
                        if (Image(pic) is { } image)
                            para.Runs.Add(image);
                    }

                    foreach (XElement sub in Descendants(c, "subList"))
                    {
                        foreach (XElement sp in Children(sub, "p"))
                            extra.AddRange(Para(sp));
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(headingPrefix) && para.Runs.OfType<HwpRun>().Any(r => !string.IsNullOrWhiteSpace(r.Text)))
        {
            para.Runs.Insert(0, new HwpRun
            {
                Text = headingPrefix + " ",
                Bold = firstCs?.Bold ?? false,
                Italic = firstCs?.Italic ?? false,
                Size = firstCs?.Size,
                Color = firstCs?.Color,
                Font = firstCs?.Font,
                Spacing = firstCs?.Spacing ?? 0,
            });
        }

        return [para, .. extra];
    }

    private void HandleCtrl(XElement ctrl, HwpPara para, CharShape? cs)
    {
        foreach (XElement child in ctrl.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "footNote":
                case "endNote":
                    if (_deleteDepth == 0 && Child(child, "subList") is { } sub)
                    {
                        string noteText = CollectPlainText(sub);
                        if (noteText.Length > 0)
                        {
                            int num = _footnotes.Count + 1;
                            _footnotes.Add($"{num}) {noteText}");
                            para.Runs.Add(new HwpRun { Text = $"{num})", Sup = 1, Size = cs?.Size });
                        }
                    }

                    break;
            }
        }
    }

    private string? ResolveHeadingPrefix(ParaShape? shape)
    {
        if (shape?.HeadingType is null)
            return null;

        if (shape.HeadingType == "BULLET")
        {
            if (shape.HeadingIdRef is not null && _bullets.TryGetValue(shape.HeadingIdRef, out string? bullet))
                return bullet;
            return "•";
        }

        string? id = shape.HeadingType == "OUTLINE"
            ? (!string.IsNullOrEmpty(shape.HeadingIdRef) && shape.HeadingIdRef != "0" ? shape.HeadingIdRef : _outlineNumberingId)
            : shape.HeadingIdRef;
        if (string.IsNullOrEmpty(id) || !_numberings.TryGetValue(id, out NumberingDef? def))
            return null;

        int level = Math.Clamp(shape.HeadingLevel, 0, 9);
        int[] counters = _numberingState.Advance($"hwpx:{id}", level);
        if (!def.Heads.TryGetValue(level + 1, out (string Format, string Text, int Start) head) || string.IsNullOrEmpty(head.Text))
            return null;

        var formats = new string[10];
        var starts = new int[10];
        for (int lv = 0; lv < 10; lv++)
        {
            if (def.Heads.TryGetValue(lv + 1, out var h))
            {
                formats[lv] = h.Format;
                starts[lv] = h.Start;
            }
            else
            {
                formats[lv] = "DIGIT";
                starts[lv] = 1;
            }
        }

        return HwpNumbering.Expand(head.Text, counters, formats, starts);
    }

    private string TextOf(XElement t)
    {
        var text = new StringBuilder();
        foreach (XNode node in t.Nodes())
        {
            if (node is XText value)
            {
                if (_deleteDepth == 0)
                    text.Append(value.Value);
                continue;
            }

            if (node is XElement e)
            {
                switch (e.Name.LocalName)
                {
                    case "deleteBegin":
                        _deleteDepth++;
                        break;
                    case "deleteEnd":
                        if (_deleteDepth > 0)
                            _deleteDepth--;
                        break;
                    case "tab" when _deleteDepth == 0:
                        text.Append('\t');
                        break;
                    case "lineBreak" when _deleteDepth == 0:
                        text.Append('\n');
                        break;
                    case "nbSpace" or "fwSpace" when _deleteDepth == 0:
                        text.Append(' ');
                        break;
                    case "hyphen" when _deleteDepth == 0:
                        // Soft hyphen is not rendered visibly in Hancom Hangul.
                        break;
                }
            }
        }

        return HwpPuaMapper.MapText(text.ToString());
    }

    private string CollectPlainText(XElement subList)
    {
        var sb = new StringBuilder();
        foreach (XElement p in Children(subList, "p"))
        {
            var line = new StringBuilder();
            foreach (XElement run in Children(p, "run"))
            {
                foreach (XElement t in Children(run, "t"))
                    line.Append(TextOf(t));
            }

            string trimmed = line.ToString().Trim();
            if (trimmed.Length > 0)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(trimmed);
            }
        }

        return sb.ToString();
    }

    private HwpImage? Image(XElement el)
    {
        string? id = Descendants(el, "img").Select(i => (string?)i.Attribute("binaryItemIDRef")).FirstOrDefault();
        if (id is null || !_binaries.TryGetValue(id, out ZipArchiveEntry? entry) || entry.Length > HwpLimits.MaxImageBytes)
            return null;

        using Stream stream = entry.Open();
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        XElement? size = Descendants(el, "curSz").FirstOrDefault() ?? Descendants(el, "sz").FirstOrDefault();
        double? width = null, height = null;
        if (size is not null)
        {
            double w = Number(size, "width") / 100, h = Number(size, "height") / 100;
            if (w > 0 && h > 0)
                (width, height) = (w, h);
        }

        return new HwpImage(buffer.ToArray(), width, height);
    }

    private HwpTable Table(XElement tbl)
    {
        Fill? tableFill = _fills.GetValueOrDefault((string?)tbl.Attribute("borderFillIDRef") ?? string.Empty);
        bool anyBorder = tableFill?.Border ?? false;
        var table = new HwpTable();
        var widths = new Dictionary<int, double>();

        if (Child(tbl, "caption") is { } capEl && Child(capEl, "subList") is { } capSub)
        {
            string capText = CollectPlainText(capSub);
            if (capText.Length > 0)
            {
                table.Caption = capText;
                table.CaptionBelow = string.Equals((string?)capEl.Attribute("side"), "BOTTOM", StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (XElement tr in Children(tbl, "tr"))
        {
            var row = new List<HwpCell>();
            foreach (XElement tc in Children(tr, "tc"))
            {
                XElement? span = Child(tc, "cellSpan");
                XElement? address = Child(tc, "cellAddr");
                XElement? size = Child(tc, "cellSz");
                Fill? fill = _fills.GetValueOrDefault((string?)tc.Attribute("borderFillIDRef") ?? string.Empty);
                HwpCellBorders? cellBorders = fill?.Borders ?? tableFill?.Borders;
                var cell = new HwpCell
                {
                    ColSpan = Math.Max(1, (int)Number(span, "colSpan", 1)),
                    RowSpan = Math.Max(1, (int)Number(span, "rowSpan", 1)),
                    Background = fill?.Background,
                    Border = (fill?.Border ?? false) || (tableFill?.Border ?? false),
                    Borders = cellBorders,
                };
                if (size is not null)
                {
                    cell.Width = Number(size, "width") / 100;
                    cell.Height = Number(size, "height") / 100;
                    if (address is not null && cell.ColSpan == 1)
                        widths[(int)Number(address, "colAddr")] = cell.Width.Value;
                }

                if (Child(tc, "subList") is { } sub)
                {
                    cell.VAlign = ((string?)sub.Attribute("vertAlign"))?.ToUpperInvariant() switch
                    {
                        "CENTER" => "middle",
                        "BOTTOM" => "bottom",
                        _ => "top",
                    };
                    foreach (XElement p in Children(sub, "p"))
                        cell.Blocks.AddRange(Para(p));
                }

                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new HwpPara());

                anyBorder |= cell.Border;
                row.Add(cell);
            }

            table.Rows.Add(row);
        }

        int columns = (int)Number(tbl, "colCnt");
        if (columns <= 0)
            columns = table.Rows.Select(r => r.Sum(c => c.ColSpan)).DefaultIfEmpty(0).Max();

        for (int i = 0; i < columns; i++)
            table.ColumnWidths.Add(widths.TryGetValue(i, out double w) ? w : null);

        table.Border = anyBorder;
        return table;
    }

    // ---- XML helpers (names without their namespace) ----

    private static IEnumerable<XElement> Descendants(XElement el, string name) => el.Descendants().Where(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement el, string name) => el.Elements().Where(e => e.Name.LocalName == name);

    private static XElement? Child(XElement el, string name) => Children(el, name).FirstOrDefault();

    private static double Number(XElement? el, string attribute, double fallback = 0) =>
        double.TryParse((string?)el?.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private static bool IsNone(string? value) => string.IsNullOrEmpty(value) || string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase);

    // Hancom Office Hangul writes <hh:strikeout shape="3D"/> on non-struck runs when converting/saving; whitelist real line types (kordoc styles.ts).
    private static bool HasStrike(XElement? strike)
    {
        if (strike is null || IsNone((string?)strike.Attribute("type") ?? "SOLID"))
            return false;

        return ((string?)strike.Attribute("shape"))?.ToUpperInvariant() is
            "SOLID" or "DASH" or "DOT" or "DASH_DOT" or "DASH_DOT_DOT" or "LONG_DASH" or "CIRCLE"
            or "DOUBLE_SLIM" or "SLIM_THICK" or "THICK_SLIM" or "SLIM_THICK_SLIM" or "WAVE" or "DOUBLE_WAVE";
    }

    /// <summary>"#RRGGBB" / "RRGGBB" / "FFRRGGBB" to "#rrggbb"; anything else to null.</summary>
    internal static string? HexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string s = value.Trim().TrimStart('#');
        if (s.Length == 8)
            s = s[2..];

        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + s.ToLowerInvariant() : null;
    }
}

internal static class HwpLimits
{
    /// <summary>Top-level paragraphs read before the document is cut (as M-Finder).</summary>
    public const int MaxBlocks = 4000;

    public const long MaxImageBytes = 8L * 1024 * 1024;
}

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MDir.Features.Hwp;

/// <summary>
/// Reads an HWPX document (a zip of OWPML XML parts) into <see cref="HwpDoc"/>: character and paragraph shapes from
/// Contents/header.xml, then the paragraphs, tables and pictures of Contents/section0.xml, section1.xml...
/// Ported from M-Finder's viewer (filefinder/viewers/hwpx.py).
/// </summary>
internal sealed partial class HwpxReader
{
    private const long MaxPart = 64L * 1024 * 1024;

    private static readonly Dictionary<string, string> Alignments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JUSTIFY"] = "justify", ["LEFT"] = "left", ["RIGHT"] = "right", ["CENTER"] = "center",
        ["DISTRIBUTE"] = "justify", ["DISTRIBUTE_SPACE"] = "justify",
    };

    private static readonly HashSet<string> Shapes = ["rect", "ellipse", "container", "polygon", "line", "arc", "curve", "textart", "ole"];

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, string> _fonts = [];
    private readonly Dictionary<string, CharShape> _chars = [];
    private readonly Dictionary<string, ParaShape> _paras = [];
    private readonly Dictionary<string, Fill> _fills = [];
    private readonly Dictionary<string, ZipArchiveEntry> _binaries = new(StringComparer.OrdinalIgnoreCase);
    private int _count;
    private bool _truncated;

    private sealed record CharShape(double Size, string? Color, string? Background, bool Bold, bool Italic, bool Underline, bool Strike, int Sup, string? Font, double Spacing);

    private sealed record ParaShape(string Align, double Left, double First, double Before, double After, double? Line);

    private sealed record Fill(string? Background, bool Border);

    private HwpxReader(ZipArchive zip) => _zip = zip;

    /// <summary>The document, or null when the zip has no sections.</summary>
    public static HwpDoc? Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return new HwpxReader(zip).Read();
    }

    private HwpDoc? Read()
    {
        var sections = _zip.Entries
            .Where(e => SectionName().IsMatch(e.FullName))
            .OrderBy(e => int.Parse(SectionName().Match(e.FullName).Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        if (sections.Count == 0)
            return null;

        if (_zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "Contents/header.xml", StringComparison.OrdinalIgnoreCase)) is { } header)
            ReadHeader(Load(header));

        foreach (ZipArchiveEntry entry in _zip.Entries)
        {
            if (entry.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase))
                _binaries[Path.GetFileNameWithoutExtension(entry.Name)] = entry;
        }

        var doc = new HwpDoc();
        bool first = true;
        foreach (ZipArchiveEntry entry in sections)
        {
            XElement root = Load(entry);
            if (first && Descendants(root, "pagePr").FirstOrDefault() is { } page)
                doc.Page = PageOf(page);

            int start = doc.Blocks.Count;
            foreach (XElement p in Children(root, "p"))
            {
                if (_count >= HwpLimits.MaxBlocks)
                {
                    _truncated = true;
                    break;
                }

                doc.Blocks.AddRange(Para(p));
                _count++;
            }

            // A new section starts on a new page.
            if (!first && doc.Blocks.Count > start && doc.Blocks[start] is HwpPara para)
                para.PageBreak = true;

            first = false;
            if (_truncated)
                break;
        }

        doc.Truncated = _truncated;
        return doc;
    }

    [GeneratedRegex(@"^Contents/section(\d+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionName();

    private static XElement Load(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPart)
            throw new InvalidDataException($"{entry.FullName} is too large.");

        using Stream stream = entry.Open();
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
                Child(cp, "bold") is not null,
                Child(cp, "italic") is not null,
                underline is not null && !IsNone((string?)underline.Attribute("type")) && !IsNone((string?)underline.Attribute("shape") ?? "SOLID"),
                HasStrike(strike),
                Child(cp, "supscript") is not null ? 1 : Child(cp, "subscript") is not null ? -1 : 0,
                fontId is not null && _fonts.TryGetValue(fontId, out string? font) ? font : null,
                HwpSpacing.ToEm(Number(Child(cp, "spacing"), "hangul"), Number(Child(cp, "ratio"), "hangul", 100)));
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

            _paras[(string?)pp.Attribute("id") ?? string.Empty] = new ParaShape(align, left, first, before, after, line);
        }

        foreach (XElement bf in Descendants(root, "borderFill"))
        {
            bool border = new[] { "leftBorder", "rightBorder", "topBorder", "bottomBorder" }
                .Any(side => Child(bf, side) is { } b && !IsNone((string?)b.Attribute("type")));
            string? background = null;
            foreach (XElement brush in Descendants(bf, "winBrush"))
            {
                string? face = (string?)brush.Attribute("faceColor");
                if (!string.IsNullOrEmpty(face) && !string.Equals(face, "none", StringComparison.OrdinalIgnoreCase))
                    background = HexColor(face);
            }

            _fills[(string?)bf.Attribute("id") ?? string.Empty] = new Fill(background, border);
        }
    }

    private static double MarginValue(XElement margin, string name) => Child(margin, name) is { } c ? Number(c, "value") / 100 : 0;

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
        var extra = new List<object>();
        foreach (XElement run in Children(p, "run"))
        {
            CharShape? cs = _chars.GetValueOrDefault((string?)run.Attribute("charPrIDRef") ?? string.Empty);
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
                            Text = text, Bold = cs?.Bold ?? false, Italic = cs?.Italic ?? false, Underline = cs?.Underline ?? false,
                            Strike = cs?.Strike ?? false, Size = cs?.Size, Color = cs?.Color, Background = cs?.Background, Sup = cs?.Sup ?? 0,
                            Font = cs?.Font, Spacing = cs?.Spacing ?? 0,
                        });
                    }
                }
                else if (name == "tbl")
                {
                    extra.Add(Table(c));
                }
                else if (name == "pic")
                {
                    if (Image(c) is { } image)
                        para.Runs.Add(image);
                }
                else if (Shapes.Contains(name))
                {
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

        return [para, .. extra];
    }

    private static string TextOf(XElement t)
    {
        var text = new System.Text.StringBuilder();
        foreach (XNode node in t.Nodes())
        {
            if (node is XText value)
            {
                text.Append(value.Value);
                continue;
            }

            if (node is XElement e)
            {
                text.Append(e.Name.LocalName switch
                {
                    "tab" => "\t",
                    "lineBreak" => "\n",
                    "nbSpace" or "fwSpace" => " ",
                    "hyphen" => "-",
                    _ => string.Empty,
                });
            }
        }

        return text.ToString();
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
        bool border = _fills.GetValueOrDefault((string?)tbl.Attribute("borderFillIDRef") ?? string.Empty)?.Border ?? false;
        var table = new HwpTable();
        var widths = new Dictionary<int, double>();
        foreach (XElement tr in Children(tbl, "tr"))
        {
            var row = new List<HwpCell>();
            foreach (XElement tc in Children(tr, "tc"))
            {
                XElement? span = Child(tc, "cellSpan");
                XElement? address = Child(tc, "cellAddr");
                XElement? size = Child(tc, "cellSz");
                Fill? fill = _fills.GetValueOrDefault((string?)tc.Attribute("borderFillIDRef") ?? string.Empty);
                var cell = new HwpCell
                {
                    ColSpan = Math.Max(1, (int)Number(span, "colSpan", 1)),
                    RowSpan = Math.Max(1, (int)Number(span, "rowSpan", 1)),
                    Background = fill?.Background,
                    Border = (fill?.Border ?? false) || border,
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

                border |= cell.Border;
                row.Add(cell);
            }

            table.Rows.Add(row);
        }

        int columns = (int)Number(tbl, "colCnt");
        if (columns <= 0)
            columns = table.Rows.Select(r => r.Sum(c => c.ColSpan)).DefaultIfEmpty(0).Max();

        for (int i = 0; i < columns; i++)
            table.ColumnWidths.Add(widths.TryGetValue(i, out double w) ? w : null);

        table.Border = border;
        foreach (HwpCell cell in table.Rows.SelectMany(r => r))
            cell.Border = border;

        return table;
    }

    // ---- XML helpers (names without their namespace) ----

    private static IEnumerable<XElement> Descendants(XElement el, string name) => el.Descendants().Where(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement el, string name) => el.Elements().Where(e => e.Name.LocalName == name);

    private static XElement? Child(XElement el, string name) => Children(el, name).FirstOrDefault();

    private static double Number(XElement? el, string attribute, double fallback = 0) =>
        double.TryParse((string?)el?.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    private static bool IsNone(string? value) => string.IsNullOrEmpty(value) || string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase);

    // Hancom Office Hangul writes <hh:strikeout shape="3D"/> on non-struck runs when converting/saving; 3D is not a text strikeout.
    private static bool HasStrike(XElement? strike) =>
        strike is not null
        && !IsNone((string?)strike.Attribute("type") ?? "SOLID")
        && !IsNone((string?)strike.Attribute("shape"))
        && !string.Equals((string?)strike.Attribute("shape"), "3D", StringComparison.OrdinalIgnoreCase);

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

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MDir.Features.Hwp;

/// <summary>
/// Reads an HWPML 2.x XML Hancom document (<c>.hml</c> / <c>.hwpml</c>, or <c>.hwp</c> with <c>&lt;HWPML</c> XML
/// content) into <see cref="HwpDoc"/>. Benchmarked from kordoc (<c>src/hwpml/parser.ts</c>).
/// </summary>
internal sealed class HwpmlReader
{
    private const long MaxBytes = 50L * 1024 * 1024;

    private readonly Dictionary<string, string> _fonts = [];
    private readonly Dictionary<string, CharShape> _chars = [];
    private readonly Dictionary<string, ParaShape> _paras = [];
    private readonly Dictionary<string, Fill> _fills = [];
    private readonly Dictionary<string, byte[]> _binaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _footnotes = [];
    private int _count;
    private bool _truncated;

    private sealed record CharShape(double Size, string? Color, string? Background, bool Bold, bool Italic, bool Underline, bool Strike, int Sup, string? Font);

    private sealed record ParaShape(string Align, double Left, double First, double Before, double After, double? Line);

    private sealed record Fill(string? Background, bool Border, HwpCellBorders Borders);

    /// <summary>True when <paramref name="data"/> starts with an XML declaration / root containing <c>&lt;HWPML</c>.</summary>
    public static bool IsHwpml(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return false;

        int headLen = Math.Min(data.Length, 512);
        string head = Encoding.UTF8.GetString(data[..headLen]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<HWPML", StringComparison.OrdinalIgnoreCase))
            && head.Contains("<HWPML", StringComparison.OrdinalIgnoreCase);
    }

    public static HwpDoc? Read(byte[] data)
    {
        if (data.Length > MaxBytes || !IsHwpml(data))
            return null;

        string xml = Encoding.UTF8.GetString(data).TrimStart('\uFEFF').Replace("&nbsp;", "&#160;", StringComparison.Ordinal);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var sr = new StringReader(xml);
        using var xr = XmlReader.Create(sr, settings);
        XElement root = XElement.Load(xr);
        if (!string.Equals(root.Name.LocalName, "HWPML", StringComparison.OrdinalIgnoreCase))
            return null;

        return new HwpmlReader().ReadDocument(root);
    }

    private HwpDoc? ReadDocument(XElement root)
    {
        if (Child(root, "HEAD") is { } head)
            ReadHead(head);

        if (Child(root, "TAIL") is { } tail)
            ReadBinData(tail);

        if (Child(root, "BODY") is not { } body)
            return null;

        var sections = Children(body, "SECTION").ToList();
        if (sections.Count == 0)
            return null;

        var doc = new HwpDoc();
        bool first = true;
        foreach (XElement section in sections)
        {
            if (first && Descendants(section, "PAGEDEF").FirstOrDefault() is { } pageDef)
                doc.Page = PageOf(pageDef);

            int start = doc.Blocks.Count;
            foreach (XElement p in Children(section, "P"))
            {
                if (_count >= HwpLimits.MaxBlocks)
                {
                    _truncated = true;
                    break;
                }

                doc.Blocks.AddRange(Para(p));
                _count++;
            }

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

    private void ReadHead(XElement head)
    {
        if (Child(head, "MAPPINGTABLE") is not { } map)
            return;

        if (Child(map, "FACENAMELIST") is { } faceList)
        {
            foreach (XElement group in Children(faceList, "FONTFACE"))
            {
                if (string.Equals((string?)group.Attribute("Lang"), "Hangul", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = 0;
                    foreach (XElement font in Children(group, "FONT"))
                    {
                        string id = (string?)font.Attribute("Id") ?? idx.ToString(CultureInfo.InvariantCulture);
                        _fonts[id] = (string?)font.Attribute("Name") ?? string.Empty;
                        idx++;
                    }
                }
            }
        }

        if (Child(map, "CHARSHAPELIST") is { } csList)
        {
            foreach (XElement cs in Children(csList, "CHARSHAPE"))
            {
                string id = (string?)cs.Attribute("Id") ?? string.Empty;
                double size = Math.Max(2.0, Number(cs, "Height", 1000) / 100.0);
                string? color = DecimalBgr((string?)cs.Attribute("TextColor"));
                string? shade = DecimalBgr((string?)cs.Attribute("ShadeColor"));
                if (shade == "#ffffff")
                    shade = null;
                XElement? fontId = Child(cs, "FONTID");
                string? face = fontId is not null && _fonts.TryGetValue((string?)fontId.Attribute("Hangul") ?? string.Empty, out string? f) ? f : null;
                XElement? ul = Child(cs, "UNDERLINE");
                XElement? st = Child(cs, "STRIKEOUT");
                _chars[id] = new CharShape(
                    size,
                    color,
                    shade,
                    Child(cs, "BOLD") is not null,
                    Child(cs, "ITALIC") is not null,
                    ul is not null && !string.Equals((string?)ul.Attribute("Type"), "None", StringComparison.OrdinalIgnoreCase),
                    st is not null && !string.Equals((string?)st.Attribute("Shape"), "None", StringComparison.OrdinalIgnoreCase) && !string.Equals((string?)st.Attribute("Shape"), "3D", StringComparison.OrdinalIgnoreCase),
                    Child(cs, "SUPERSCRIPT") is not null ? 1 : Child(cs, "SUBSCRIPT") is not null ? -1 : 0,
                    face);
            }
        }

        if (Child(map, "PARASHAPELIST") is { } psList)
        {
            foreach (XElement ps in Children(psList, "PARASHAPE"))
            {
                string id = (string?)ps.Attribute("Id") ?? string.Empty;
                string align = ((string?)ps.Attribute("Align"))?.ToUpperInvariant() switch
                {
                    "CENTER" => "center",
                    "RIGHT" => "right",
                    "JUSTIFY" or "DISTRIBUTE" => "justify",
                    _ => "left",
                };
                XElement? margin = Child(ps, "PARAMARGIN");
                double left = Number(margin, "Left") / 200.0;
                double first = Number(margin, "Indent") / 200.0;
                double before = Number(margin, "Prev") / 200.0;
                double after = Number(margin, "Next") / 200.0;
                double? line = null;
                if (string.Equals((string?)margin?.Attribute("LineSpacingType") ?? "Percent", "Percent", StringComparison.OrdinalIgnoreCase))
                {
                    double ls = Number(margin, "LineSpacing", 160);
                    if (ls is >= 50 and <= 400)
                        line = ls / 100.0;
                }

                _paras[id] = new ParaShape(align, left, first, before, after, line);
            }
        }

        if (Child(map, "BORDERFILLLIST") is { } bfList)
        {
            foreach (XElement bf in Children(bfList, "BORDERFILL"))
            {
                string id = (string?)bf.Attribute("Id") ?? string.Empty;
                var edges = new HwpCellBorders(
                    Top: ParseEdge(Child(bf, "TOPBORDER")),
                    Right: ParseEdge(Child(bf, "RIGHTBORDER")),
                    Bottom: ParseEdge(Child(bf, "BOTTOMBORDER")),
                    Left: ParseEdge(Child(bf, "LEFTBORDER")));
                string? bg = null;
                if (Descendants(bf, "WINDOWBRUSH").FirstOrDefault() is { } wb)
                    bg = DecimalBgr((string?)wb.Attribute("FaceColor"));
                _fills[id] = new Fill(bg, edges.HasAny, edges);
            }
        }
    }

    private void ReadBinData(XElement tail)
    {
        if (Child(tail, "BINDATASTORAGE") is not { } storage)
            return;

        foreach (XElement bin in Children(storage, "BINDATA"))
        {
            string id = (string?)bin.Attribute("Id") ?? string.Empty;
            if (id.Length == 0 || string.IsNullOrWhiteSpace(bin.Value))
                continue;

            try
            {
                byte[] raw = Convert.FromBase64String(bin.Value.Trim());
                if (string.Equals((string?)bin.Attribute("Compress"), "true", StringComparison.OrdinalIgnoreCase))
                    raw = HwpReader.Inflate(raw);

                if (raw.Length <= HwpLimits.MaxImageBytes)
                    _binaries[id] = raw;
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
            }
        }
    }

    private IEnumerable<object> Para(XElement p)
    {
        ParaShape? shape = _paras.GetValueOrDefault((string?)p.Attribute("ParaShape") ?? string.Empty);
        var para = new HwpPara
        {
            Align = shape?.Align ?? "left",
            Left = shape?.Left ?? 0,
            First = shape?.First ?? 0,
            Before = shape?.Before ?? 0,
            After = shape?.After ?? 0,
            Line = shape?.Line,
            PageBreak = string.Equals((string?)p.Attribute("PageBreak"), "true", StringComparison.OrdinalIgnoreCase),
        };
        var extra = new List<object>();

        foreach (XElement textEl in Children(p, "TEXT"))
        {
            CharShape? cs = _chars.GetValueOrDefault((string?)textEl.Attribute("CharShape") ?? string.Empty);
            para.Size ??= cs?.Size;

            foreach (XElement child in textEl.Elements())
            {
                string tag = child.Name.LocalName.ToUpperInvariant();
                if (tag == "CHAR")
                {
                    string text = CharText(child);
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
                        });
                    }
                }
                else if (tag == "TABLE")
                {
                    extra.Add(Table(child));
                }
                else if (tag == "EQUATION")
                {
                    if (Child(child, "SCRIPT") is { } scriptEl && !string.IsNullOrWhiteSpace(scriptEl.Value))
                        para.Runs.Add(new HwpEquationRun(scriptEl.Value.Trim()));
                }
                else if (tag == "PICTURE")
                {
                    if (Picture(child) is { } img)
                        para.Runs.Add(img);
                }
                else if (tag is "FOOTNOTE" or "ENDNOTE")
                {
                    string noteText = CollectNoteText(child);
                    if (noteText.Length > 0)
                    {
                        int num = _footnotes.Count + 1;
                        _footnotes.Add($"{num}) {noteText}");
                        para.Runs.Add(new HwpRun { Text = $"{num})", Sup = 1, Size = cs?.Size });
                    }
                }
                else if (tag is "RECTANGLE" or "ELLIPSE" or "POLYGON" or "CONTAINER")
                {
                    foreach (XElement pl in Descendants(child, "PARALIST"))
                    {
                        foreach (XElement sp in Children(pl, "P"))
                            extra.AddRange(Para(sp));
                    }
                }
            }
        }

        if (para.Runs.Count == 0 && extra.Count > 0)
        {
            if (para.PageBreak && extra[0] is HwpPara first)
                first.PageBreak = true;
            return extra;
        }

        return [para, .. extra];
    }

    private static string CharText(XElement charEl)
    {
        var sb = new StringBuilder();
        foreach (XNode node in charEl.Nodes())
        {
            if (node is XText txt)
            {
                sb.Append(txt.Value);
            }
            else if (node is XElement e)
            {
                sb.Append(e.Name.LocalName.ToUpperInvariant() switch
                {
                    "TAB" => "\t",
                    "LINEBREAK" => "\n",
                    "NBSPACE" or "FWSPACE" => " ",
                    _ => string.Empty,
                });
            }
        }

        return HwpPuaMapper.MapText(sb.ToString());
    }

    private static string CollectNoteText(XElement noteEl)
    {
        var sb = new StringBuilder();
        foreach (XElement ch in Descendants(noteEl, "CHAR"))
        {
            string s = CharText(ch).Trim();
            if (s.Length > 0)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(s);
            }
        }

        return sb.ToString();
    }

    private HwpImage? Picture(XElement pic)
    {
        string? binId = Descendants(pic, "IMAGE").Select(i => (string?)i.Attribute("BinItem")).FirstOrDefault();
        if (binId is null || !_binaries.TryGetValue(binId, out byte[]? data))
            return null;

        XElement? size = Descendants(pic, "SIZE").FirstOrDefault();
        double? width = null, height = null;
        if (size is not null)
        {
            double w = Number(size, "Width") / 100.0;
            double h = Number(size, "Height") / 100.0;
            if (w > 0 && h > 0)
                (width, height) = (w, h);
        }

        return new HwpImage(data, width, height);
    }

    private HwpTable Table(XElement tbl)
    {
        Fill? tableFill = _fills.GetValueOrDefault((string?)tbl.Attribute("BorderFill") ?? string.Empty);
        bool anyBorder = tableFill?.Border ?? false;
        var table = new HwpTable();
        var widths = new Dictionary<int, double>();

        foreach (XElement tr in Children(tbl, "ROW"))
        {
            var row = new List<HwpCell>();
            foreach (XElement tc in Children(tr, "CELL"))
            {
                Fill? fill = _fills.GetValueOrDefault((string?)tc.Attribute("BorderFill") ?? string.Empty);
                var cell = new HwpCell
                {
                    ColSpan = Math.Max(1, (int)Number(tc, "ColSpan", 1)),
                    RowSpan = Math.Max(1, (int)Number(tc, "RowSpan", 1)),
                    Width = Number(tc, "Width") is > 0 and var cw ? cw / 100.0 : null,
                    Height = Number(tc, "Height") is > 0 and var ch ? ch / 100.0 : null,
                    Background = fill?.Background,
                    Border = (fill?.Border ?? false) || (tableFill?.Border ?? false),
                    Borders = fill?.Borders ?? tableFill?.Borders,
                };
                if (cell.Width is > 0 && cell.ColSpan == 1)
                    widths[(int)Number(tc, "ColAddr")] = cell.Width.Value;

                if (Child(tc, "PARALIST") is { } pl)
                {
                    cell.VAlign = ((string?)pl.Attribute("VertAlign"))?.ToUpperInvariant() switch
                    {
                        "CENTER" => "middle",
                        "BOTTOM" => "bottom",
                        _ => "top",
                    };
                    foreach (XElement p in Children(pl, "P"))
                        cell.Blocks.AddRange(Para(p));
                }

                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new HwpPara());

                anyBorder |= cell.Border;
                row.Add(cell);
            }

            table.Rows.Add(row);
        }

        int cols = (int)Number(tbl, "ColCount");
        if (cols <= 0)
            cols = table.Rows.Select(r => r.Sum(c => c.ColSpan)).DefaultIfEmpty(0).Max();

        for (int i = 0; i < cols; i++)
            table.ColumnWidths.Add(widths.TryGetValue(i, out double w) ? w : null);

        table.Border = anyBorder;
        return table;
    }

    private static HwpPage PageOf(XElement page)
    {
        double width = Number(page, "Width") / 100.0;
        double height = Number(page, "Height") / 100.0;
        if (width < 50 || height < 50)
            return HwpPage.Default;

        bool landscape = (string?)page.Attribute("Landscape") == "1";
        (double w, double h) = landscape ? (Math.Max(width, height), Math.Min(width, height)) : (Math.Min(width, height), Math.Max(width, height));
        if (Child(page, "PAGEMARGIN") is not { } m)
            return HwpPage.Default with { Width = w, Height = h };

        return new HwpPage(w, h,
            Number(m, "Left") / 100.0,
            Number(m, "Right") / 100.0,
            (Number(m, "Top") + Number(m, "Header")) / 100.0,
            (Number(m, "Bottom") + Number(m, "Footer")) / 100.0);
    }

    private static HwpBorderEdge? ParseEdge(XElement? el)
    {
        if (el is null)
            return null;

        string? type = (string?)el.Attribute("Type");
        if (string.IsNullOrEmpty(type) || string.Equals(type, "None", StringComparison.OrdinalIgnoreCase))
            return null;

        string color = DecimalBgr((string?)el.Attribute("Color")) ?? "#000000";
        if (color == "#ffffff")
            return null;

        string style = type.ToUpperInvariant() switch
        {
            "DASH" or "LONGDASH" or "DASHDOT" or "DASHDOTDOT" => "dashed",
            "DOT" or "CIRCLE" => "dotted",
            "DOUBLESLIM" or "SLIMTHICK" or "THICKSLIM" => "double",
            _ => "solid",
        };
        return new HwpBorderEdge(style, style == "double" ? 2.0 : 0.75, color);
    }

    private static string? DecimalBgr(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (raw.StartsWith('#'))
            return HwpxReader.HexColor(raw);

        if (!uint.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint val) || val == 0xFFFFFFFF)
            return null;

        return $"#{val & 0xFF:x2}{(val >> 8) & 0xFF:x2}{(val >> 16) & 0xFF:x2}";
    }

    private static IEnumerable<XElement> Descendants(XElement el, string name) =>
        el.Descendants().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement el, string name) =>
        el.Elements().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static XElement? Child(XElement el, string name) => Children(el, name).FirstOrDefault();

    private static double Number(XElement? el, string attribute, double fallback = 0) =>
        double.TryParse((string?)el?.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
}

using System.IO.Compression;
using System.Text;

namespace MDir.Features.Hwp;

/// <summary>
/// Reads an HWP 5.x document (an OLE compound file) into <see cref="HwpDoc"/>: character and paragraph shapes, border
/// fills and the picture table from DocInfo, then the record trees of BodyText/Section0, Section1... (paragraphs,
/// tables, pictures, text boxes). Ported from M-Finder's viewer (filefinder/viewers/hwp.py).
/// </summary>
internal sealed class HwpReader
{
    private const int TagIdMappings = 17, TagBinData = 18, TagFaceName = 19, TagBorderFill = 20, TagCharShape = 21, TagParaShape = 25;
    private const int TagParaHeader = 66, TagParaText = 67, TagParaCharShape = 68;
    private const int TagCtrlHeader = 71, TagListHeader = 72, TagPageDef = 73, TagTable = 77, TagPicture = 85;

    private readonly CompoundFile _file;
    private readonly bool _compressed;
    private readonly List<CharShape> _chars = [];
    private readonly List<ParaShape> _paras = [];
    private readonly List<Fill> _fills = [];
    private readonly List<(int Id, string Extension, int Attributes)> _bins = [];
    private int _count;
    private bool _truncated;

    private sealed record CharShape(double Size, bool Italic, bool Bold, bool Underline, bool Strike, int Sup, string? Color, string? Font, double Spacing);

    private sealed record ParaShape(string Align, double Left, double First, double Before, double After, double? Line);

    private sealed record Fill(bool Border, string? Background);

    private sealed class Node(int tag, int level, byte[] data)
    {
        public int Tag { get; } = tag;
        public int Level { get; } = level;
        public byte[] Data { get; } = data;
        public List<Node> Kids { get; } = [];
    }

    private HwpReader(CompoundFile file, bool compressed)
    {
        _file = file;
        _compressed = compressed;
    }

    /// <summary>
    /// The document, or null when it is not an HWP 5 file or cannot be read without a password (encrypted,
    /// distribution-only or DRM-protected).
    /// </summary>
    public static HwpDoc? Read(byte[] data)
    {
        if (CompoundFile.TryOpen(data) is not { } file)
            return null;

        byte[]? header = file.ReadStream("FileHeader");
        if (header is null || header.Length < 40 || !Encoding.ASCII.GetString(header, 0, 17).StartsWith("HWP Document File", StringComparison.Ordinal))
            return null;

        uint flags = BitConverter.ToUInt32(header, 36);
        if ((flags & (0x02 | 0x04 | 0x10)) != 0)
            return null;

        var reader = new HwpReader(file, (flags & 0x01) != 0);
        return reader.ReadDocument();
    }

    private HwpDoc? ReadDocument()
    {
        if (ReadPart("DocInfo") is not { } docInfo)
            return null;

        ReadDocInfo(docInfo);
        var sections = _file.ListStreams("BodyText")
            .Where(n => n.StartsWith("Section", StringComparison.OrdinalIgnoreCase) && int.TryParse(n.AsSpan(7), out _))
            .OrderBy(n => int.Parse(n.AsSpan(7)))
            .ToList();
        if (sections.Count == 0)
            return null;

        var doc = new HwpDoc();
        bool first = true;
        foreach (string name in sections)
        {
            if (ReadPart("BodyText/" + name) is not { } data)
                continue;

            List<Node> roots = Tree(data);
            if (first && Find(roots, TagPageDef) is { } pageDef)
                doc.Page = PageOf(pageDef.Data);

            int start = doc.Blocks.Count;
            foreach (Node node in roots)
            {
                if (node.Tag != TagParaHeader)
                    continue;

                if (_count >= HwpLimits.MaxBlocks)
                {
                    _truncated = true;
                    break;
                }

                try
                {
                    doc.Blocks.AddRange(Para(node));
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    // A damaged paragraph: skip it, keep the rest.
                }

                _count++;
            }

            if (!first && doc.Blocks.Count > start && doc.Blocks[start] is HwpPara para)
                para.PageBreak = true;

            first = false;
            if (_truncated)
                break;
        }

        doc.Truncated = _truncated;
        return doc;
    }

    private byte[]? ReadPart(string path)
    {
        byte[]? raw = _file.ReadStream(path);
        return raw is null ? null : _compressed ? Inflate(raw) : raw;
    }

    internal static byte[] Inflate(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    // ---- records ----

    private static IEnumerable<(int Tag, int Level, byte[] Data)> Records(byte[] data)
    {
        int position = 0;
        while (position + 4 <= data.Length)
        {
            uint head = BitConverter.ToUInt32(data, position);
            position += 4;
            int tag = (int)(head & 0x3FF), level = (int)((head >> 10) & 0x3FF);
            long size = head >> 20;
            if (size == 0xFFF)
            {
                if (position + 4 > data.Length)
                    yield break;

                size = BitConverter.ToUInt32(data, position);
                position += 4;
            }

            int length = (int)Math.Min(size, data.Length - position);
            yield return (tag, level, data.AsSpan(position, length).ToArray());
            position += length;
        }
    }

    private static List<Node> Tree(byte[] data)
    {
        var roots = new List<Node>();
        var stack = new Stack<Node>();
        foreach ((int tag, int level, byte[] record) in Records(data))
        {
            var node = new Node(tag, level, record);
            while (stack.Count > 0 && stack.Peek().Level >= level)
                stack.Pop();

            (stack.Count > 0 ? stack.Peek().Kids : roots).Add(node);
            stack.Push(node);
        }

        return roots;
    }

    private static Node? Find(IEnumerable<Node> nodes, int tag)
    {
        foreach (Node node in nodes)
        {
            if (node.Tag == tag)
                return node;

            if (Find(node.Kids, tag) is { } found)
                return found;
        }

        return null;
    }

    private static ushort U16(byte[] d, int offset, ushort fallback = 0) => d.Length >= offset + 2 ? BitConverter.ToUInt16(d, offset) : fallback;

    private static uint U32(byte[] d, int offset) => d.Length >= offset + 4 ? BitConverter.ToUInt32(d, offset) : 0;

    private static int I32(byte[] d, int offset) => d.Length >= offset + 4 ? BitConverter.ToInt32(d, offset) : 0;

    /// <summary>A COLORREF (0x00BBGGRR) as "#rrggbb"; 0xFFFFFFFF (none) as null.</summary>
    private static string? Bgr(uint value) =>
        value == 0xFFFFFFFF ? null : $"#{value & 0xFF:x2}{(value >> 8) & 0xFF:x2}{(value >> 16) & 0xFF:x2}";

    // ---- DocInfo ----

    private void ReadDocInfo(byte[] data)
    {
        var faces = new List<string>();
        var counts = new List<uint>();
        foreach ((int tag, _, byte[] d) in Records(data))
        {
            try
            {
                switch (tag)
                {
                    case TagIdMappings:
                        for (int i = 0; i + 4 <= d.Length && i < 18 * 4; i += 4)
                            counts.Add(BitConverter.ToUInt32(d, i));
                        break;

                    case TagFaceName:
                        int length = U16(d, 1);
                        faces.Add(Encoding.Unicode.GetString(d, 3, Math.Min(length * 2, d.Length - 3)));
                        break;

                    case TagBinData:
                        int attributes = U16(d, 0);
                        if ((attributes & 0xF) is 1 or 2 && d.Length >= 6)
                            _bins.Add((U16(d, 2), Encoding.Unicode.GetString(d, 6, Math.Min(U16(d, 4) * 2, d.Length - 6)), attributes));
                        else
                            _bins.Add((0, string.Empty, attributes));
                        break;

                    case TagBorderFill:
                        bool border = d.Length >= 26 && Enumerable.Range(0, 4).Any(i => d[2 + (i * 6)] != 0);
                        string? background = d.Length >= 40 && (U32(d, 32) & 1) != 0 ? Bgr(U32(d, 36)) : null;
                        _fills.Add(new Fill(border, background));
                        break;

                    case TagCharShape when d.Length >= 56:
                        uint attr = U32(d, 46);
                        int hangulFaces = counts.Count > 1 ? (int)Math.Min(counts[1], (uint)faces.Count) : faces.Count;
                        int face = U16(d, 0);
                        uint color = U32(d, 52);
                        _chars.Add(new CharShape(
                            Math.Max(2.0, I32(d, 42) / 100.0),
                            (attr & 1) != 0,
                            (attr & 2) != 0,
                            ((attr >> 2) & 3) is 1 or 3,
                            ((attr >> 18) & 7) != 0,
                            ((attr >> 15) & 1) != 0 ? 1 : ((attr >> 16) & 1) != 0 ? -1 : 0,
                            color != 0 ? Bgr(color) : null,
                            face < hangulFaces ? faces[face] : null,

                            // Width ratios (장평) are 7 bytes from offset 14, letter spacings (자간, signed) from 21; Hangul first.
                            HwpSpacing.ToEm((sbyte)d[21], d[14] == 0 ? 100 : d[14])));
                        break;

                    case TagParaShape when d.Length >= 24:
                        uint pattr = U32(d, 0);
                        string align = ((pattr >> 2) & 7) switch { 1 => "left", 2 => "right", 3 => "center", _ => "justify" };
                        double? line = null;
                        if ((pattr & 3) == 0)
                        {
                            uint value = U32(d, 24);
                            if (value is >= 50 and <= 400)
                                line = value / 100.0;
                        }

                        _paras.Add(new ParaShape(align, I32(d, 4) / 200.0, I32(d, 12) / 200.0, I32(d, 16) / 200.0, I32(d, 20) / 200.0, line));
                        break;
                }
            }
            catch (ArgumentException)
            {
                // A short or damaged record: skip it.
            }
        }
    }

    private static HwpPage PageOf(byte[] d)
    {
        if (d.Length < 40)
            return HwpPage.Default;

        double width = U32(d, 0) / 100.0, height = U32(d, 4) / 100.0;
        if (width < 50 || height < 50)
            return HwpPage.Default;

        bool landscape = (U32(d, 36) & 1) != 0;
        (double w, double h) = landscape ? (Math.Max(width, height), Math.Min(width, height)) : (Math.Min(width, height), Math.Max(width, height));
        return new HwpPage(w, h, U32(d, 8) / 100.0, U32(d, 12) / 100.0, (U32(d, 16) + U32(d, 24)) / 100.0, (U32(d, 20) + U32(d, 28)) / 100.0);
    }

    // ---- body ----

    /// <summary>PARA_TEXT as (character position, character) pairs, control codes resolved.</summary>
    private static List<(int Position, char Char)> DecodeText(byte[] rec)
    {
        var chars = new List<(int, char)>();
        int i = 0, n = rec.Length / 2 * 2, position = 0;
        while (i < n)
        {
            int ch = rec[i] | (rec[i + 1] << 8);
            if (ch >= 32)
            {
                chars.Add((position, (char)ch));
                i += 2;
                position++;
            }
            else if (ch == 13)
            {
                break;
            }
            else if (ch is 10 or 30 or 31)
            {
                chars.Add((position, ch == 10 ? '\n' : ' '));
                i += 2;
                position++;
            }
            else if (ch == 0 || ch is >= 24 and <= 29)
            {
                i += 2;
                position++;
            }
            else
            {
                // Inline and extended controls (tab, fields, tables, pictures...) take 8 characters.
                if (ch == 9)
                    chars.Add((position, '\t'));

                i += 16;
                position += 8;
            }
        }

        return chars;
    }

    private List<object> Para(Node node)
    {
        byte[] d = node.Data;
        int shapeId = U16(d, 8);
        ParaShape? shape = shapeId < _paras.Count ? _paras[shapeId] : null;
        byte[]? text = null;
        var shapes = new List<(uint Position, uint Id)>();
        var controls = new List<Node>();
        foreach (Node k in node.Kids)
        {
            switch (k.Tag)
            {
                case TagParaText:
                    text = k.Data;
                    break;
                case TagParaCharShape:
                    for (int i = 0; i + 8 <= k.Data.Length; i += 8)
                        shapes.Add((BitConverter.ToUInt32(k.Data, i), BitConverter.ToUInt32(k.Data, i + 4)));
                    break;
                case TagCtrlHeader:
                    controls.Add(k);
                    break;
            }
        }

        var para = new HwpPara
        {
            Align = shape?.Align ?? "left",
            Left = shape?.Left ?? 0,
            First = shape?.First ?? 0,
            Before = shape?.Before ?? 0,
            After = shape?.After ?? 0,
            Line = shape?.Line,

            // Byte 11 is the break type: 0x04 page break (0x01 section break also starts a page).
            PageBreak = d.Length > 11 && (d[11] & 0x05) != 0,
        };

        shapes.Sort();
        if (shapes.Count > 0 && shapes[0].Id < _chars.Count)
            para.Size = _chars[(int)shapes[0].Id].Size;

        if (text is not null)
        {
            int j = 0;
            long current = -1;
            var buffer = new StringBuilder();
            foreach ((int position, char ch) in DecodeText(text))
            {
                while (j + 1 < shapes.Count && shapes[j + 1].Position <= position)
                    j++;

                long id = shapes.Count > 0 ? shapes[j].Id : -1;
                if (id != current)
                {
                    Flush(para, buffer, current);
                    current = id;
                }

                buffer.Append(ch);
            }

            Flush(para, buffer, current);
        }

        var extra = new List<object>();
        foreach (Node control in controls)
        {
            if (control.Data.Length < 4)
                continue;

            // The control id is stored reversed: "tbl " as " lbt", "gso " as " osg".
            string id = new([(char)control.Data[3], (char)control.Data[2], (char)control.Data[1], (char)control.Data[0]]);
            try
            {
                if (id == "tbl ")
                {
                    extra.Add(Table(control));
                }
                else if (id == "gso ")
                {
                    double w = U32(control.Data, 16) / 100.0, h = U32(control.Data, 20) / 100.0;
                    para.Runs.AddRange(Pictures(control, w > 0 ? w : null, h > 0 ? h : null));
                    extra.AddRange(TextBoxParas(control));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
            {
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

    private void Flush(HwpPara para, StringBuilder buffer, long shapeId)
    {
        if (buffer.Length == 0)
            return;

        CharShape? cs = shapeId >= 0 && shapeId < _chars.Count ? _chars[(int)shapeId] : null;
        para.Runs.Add(new HwpRun
        {
            Text = buffer.ToString(), Bold = cs?.Bold ?? false, Italic = cs?.Italic ?? false, Underline = cs?.Underline ?? false,
            Strike = cs?.Strike ?? false, Size = cs?.Size, Color = cs?.Color, Sup = cs?.Sup ?? 0, Font = cs?.Font, Spacing = cs?.Spacing ?? 0,
        });
        buffer.Clear();
    }

    private HwpImage? Image(int binItem, double? width, double? height)
    {
        if (binItem < 1 || binItem > _bins.Count)
            return null;

        (int id, string extension, int attributes) = _bins[binItem - 1];
        if (extension.Length == 0 || _file.ReadStream($"BinData/BIN{id:X4}.{extension}") is not { } data)
            return null;

        int mode = (attributes >> 4) & 3;
        if (mode == 1 || (mode == 0 && _compressed))
        {
            try
            {
                data = Inflate(data);
            }
            catch (InvalidDataException)
            {
                // Stored uncompressed after all.
            }
        }

        return data.Length > HwpLimits.MaxImageBytes ? null : new HwpImage(data, width, height);
    }

    private List<HwpImage> Pictures(Node node, double? width, double? height)
    {
        var images = new List<HwpImage>();
        foreach (Node k in node.Kids)
        {
            if (k.Tag == TagPicture && k.Data.Length >= 73 && Image(U16(k.Data, 71), width, height) is { } image)
                images.Add(image);

            images.AddRange(Pictures(k, width, height));
        }

        return images;
    }

    private List<object> TextBoxParas(Node node)
    {
        var blocks = new List<object>();
        List<Node> kids = node.Kids;
        for (int i = 0; i < kids.Count; i++)
        {
            Node k = kids[i];
            if (k.Tag == TagListHeader)
            {
                foreach (Node pk in k.Kids.Where(pk => pk.Tag == TagParaHeader))
                    blocks.AddRange(Para(pk));

                for (int j = i + 1; j < kids.Count && kids[j].Tag == TagParaHeader; j++)
                    blocks.AddRange(Para(kids[j]));
            }
            else if (k.Tag != TagParaHeader)
            {
                blocks.AddRange(TextBoxParas(k));
            }
        }

        return blocks;
    }

    private HwpTable Table(Node node)
    {
        int columns = 0;
        var cells = new List<(int Row, int Column, HwpCell Cell)>();
        var widths = new Dictionary<int, double>();
        HwpCell? current = null;
        bool anyBorder = false;
        foreach (Node k in node.Kids)
        {
            if (k.Tag == TagTable)
            {
                columns = U16(k.Data, 6);
            }
            else if (k.Tag == TagListHeader)
            {
                byte[] d = k.Data;
                current = new HwpCell();
                if (d.Length >= 34)
                {
                    int column = U16(d, 8), row = U16(d, 10);
                    current.ColSpan = Math.Max(1, (int)U16(d, 12, 1));
                    current.RowSpan = Math.Max(1, (int)U16(d, 14, 1));
                    current.Width = U32(d, 16) / 100.0;
                    current.Height = U32(d, 20) / 100.0;
                    int fillId = U16(d, 32);
                    Fill? fill = fillId >= 1 && fillId <= _fills.Count ? _fills[fillId - 1] : null;
                    current.Background = fill?.Background;
                    current.Border = fill?.Border ?? false;
                    anyBorder |= current.Border;
                    if (current.ColSpan == 1)
                        widths[column] = current.Width.Value;

                    cells.Add((row, column, current));
                }
                else
                {
                    cells.Add((cells.Count, 0, current));
                }

                foreach (Node pk in k.Kids.Where(pk => pk.Tag == TagParaHeader))
                    current.Blocks.AddRange(Para(pk));
            }
            else if (k.Tag == TagParaHeader && current is not null)
            {
                current.Blocks.AddRange(Para(k));
            }
        }

        var table = new HwpTable { Border = anyBorder };
        int lastRow = int.MinValue;
        foreach ((int row, _, HwpCell cell) in cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
        {
            if (row != lastRow)
            {
                table.Rows.Add([]);
                lastRow = row;
            }

            if (cell.Blocks.Count == 0)
                cell.Blocks.Add(new HwpPara());

            cell.Border = anyBorder;
            table.Rows[^1].Add(cell);
        }

        for (int i = 0; i < columns; i++)
            table.ColumnWidths.Add(widths.TryGetValue(i, out double w) ? w : null);

        return table;
    }
}

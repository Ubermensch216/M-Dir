using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MDir.Features.Hwp;

/// <summary>
/// Reads an HWP 5.x document (OLE compound file, including distribution-only documents via <see cref="HwpCrypto"/>)
/// into <see cref="HwpDoc"/>: character/paragraph shapes, per-side border fills, auto-numbering/bullets, pictures,
/// tables, text boxes, equations (<c>EQEDIT</c>), footnotes, PUA mapping, and <c>PARA_LINE_SEG</c> page boundaries.
/// </summary>
internal sealed class HwpReader
{
    private const int TagIdMappings = 17, TagBinData = 18, TagFaceName = 19, TagBorderFill = 20, TagCharShape = 21;
    private const int TagNumbering = 23, TagBullet = 24, TagParaShape = 25;
    private const int TagParaHeader = 66, TagParaText = 67, TagParaCharShape = 68, TagParaLineSeg = 69;
    private const int TagCtrlHeader = 71, TagListHeader = 72, TagPageDef = 73, TagTable = 77, TagPicture = 85, TagEqEdit = 88;
    private const int MaxDecompressBytes = 100 * 1024 * 1024;
    private const int MidPageVertPos = 2000;

    private readonly CompoundFile _file;
    private readonly bool _compressed;
    private readonly bool _distribution;
    private readonly List<CharShape> _chars = [];
    private readonly List<ParaShape> _paras = [];
    private readonly List<Fill> _fills = [];
    private readonly List<NumberingDef> _numberings = [];
    private readonly List<string> _bullets = [];
    private readonly List<(int Id, string Extension, int Attributes)> _bins = [];
    private readonly HwpNumberingState _numberingState = new();
    private readonly List<string> _footnotes = [];
    private int _outlineNumberingId;
    private int _count;
    private bool _truncated;

    private sealed record CharShape(double Size, bool Italic, bool Bold, bool Underline, bool Strike, int Sup, string? Color, string? Font, double Spacing);

    private sealed record ParaShape(string Align, double Left, double First, double Before, double After, double? Line, int HeadType, int ParaLevel, int NumberingId);

    private sealed record Fill(bool Border, HwpCellBorders Borders, string? Background);

    private sealed record NumberingDef(string[] LevelFormats, string[] NumberFormats, int[] StartNumbers);

    private sealed class Node(int tag, int level, byte[] data)
    {
        public int Tag { get; } = tag;
        public int Level { get; } = level;
        public byte[] Data { get; } = data;
        public List<Node> Kids { get; } = [];
    }

    private HwpReader(CompoundFile file, bool compressed, bool distribution)
    {
        _file = file;
        _compressed = compressed;
        _distribution = distribution;
    }

    /// <summary>
    /// The document, or null when it is not an HWP 5 file or cannot be read without a password (encrypted or
    /// DRM-protected). Distribution-only documents (flag 0x04) are decrypted from <c>ViewText/Section*</c>.
    /// </summary>
    public static HwpDoc? Read(byte[] data)
    {
        if (CompoundFile.TryOpen(data) is not { } file)
            return null;

        byte[]? header = file.ReadStream("FileHeader");
        if (header is null || header.Length < 40 || !Encoding.ASCII.GetString(header, 0, 17).StartsWith("HWP Document File", StringComparison.Ordinal))
            return null;

        uint flags = BitConverter.ToUInt32(header, 36);
        if ((flags & (0x02 | 0x10)) != 0)
            return null; // password-encrypted or DRM

        bool compressed = (flags & 0x01) != 0;
        bool distribution = (flags & 0x04) != 0;
        var reader = new HwpReader(file, compressed, distribution);
        return reader.ReadDocument();
    }

    private HwpDoc? ReadDocument()
    {
        if (ReadPart("DocInfo") is not { } docInfo)
            return null;

        ReadDocInfo(docInfo);
        string storage = _distribution ? "ViewText" : "BodyText";
        var sections = _file.ListStreams(storage)
            .Where(n => n.StartsWith("Section", StringComparison.OrdinalIgnoreCase) && int.TryParse(n.AsSpan(7), out _))
            .OrderBy(n => int.Parse(n.AsSpan(7), CultureInfo.InvariantCulture))
            .ToList();
        if (sections.Count == 0)
            return null;

        var doc = new HwpDoc();
        bool first = true;
        int loadedSections = 0;
        foreach (string name in sections)
        {
            byte[]? data = ReadSectionData(storage + "/" + name);
            if (data is null)
                continue;

            loadedSections++;
            List<Node> roots = Tree(data);
            if (first && Find(roots, TagPageDef) is { } pageDef)
                doc.Page = PageOf(pageDef.Data);

            // Detect real page boundaries from PARA_LINE_SEG when all top-level paragraphs carry layout cache.
            HashSet<Node> layoutPageBreaks = DetectLayoutPageBreaks(roots);

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
                    List<object> blocks = Para(node);
                    if (layoutPageBreaks.Contains(node) && blocks.Count > 0 && blocks[0] is HwpPara firstPara)
                        firstPara.PageBreak = true;

                    doc.Blocks.AddRange(blocks);
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

        if (loadedSections == 0)
            return null;

        doc.Footnotes.AddRange(_footnotes);
        doc.Truncated = _truncated;
        return doc;
    }

    private byte[]? ReadSectionData(string path)
    {
        if (_distribution)
        {
            byte[]? raw = _file.ReadStream(path);
            return raw is null ? null : HwpCrypto.DecryptViewText(raw, _compressed);
        }

        return ReadPart(path);
    }

    private byte[]? ReadPart(string path)
    {
        byte[]? raw = _file.ReadStream(path);
        return raw is null ? null : _compressed ? Inflate(raw) : raw;
    }

    /// <summary>Inflates an HWP compressed stream (supports both zlib-wrapped 0x78 and raw RFC-1951 deflate, capped at 100 MB).</summary>
    internal static byte[] Inflate(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0x78)
        {
            try
            {
                using var zInput = new MemoryStream(raw);
                using var zlib = new ZLibStream(zInput, CompressionMode.Decompress);
                return CopyBounded(zlib);
            }
            catch (InvalidDataException)
            {
                // Fall through to raw deflate.
            }
        }

        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        return CopyBounded(deflate);
    }

    private static byte[] CopyBounded(Stream source)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxDecompressBytes)
                throw new InvalidDataException("Decompressed stream exceeds safety limit.");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    // ---- records & layout page boundaries ----

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

    /// <summary>
    /// Detects actual page transitions from <c>PARA_LINE_SEG</c> vertical-position resets when every top-level
    /// paragraph has a layout cache (benchmarked from kordoc <c>src/hwp5/page-boundary.ts</c>).
    /// </summary>
    private static HashSet<Node> DetectLayoutPageBreaks(List<Node> roots)
    {
        var breaks = new HashSet<Node>();
        var topParas = roots.Where(n => n.Tag == TagParaHeader).ToList();
        if (topParas.Count == 0)
            return breaks;

        int withSegs = 0;
        int cur = 0;
        int prevV = int.MinValue;
        int prevH = int.MinValue;
        bool first = true;
        bool suppressMidReset = false;

        foreach (Node p in topParas)
        {
            bool explicitBreak = p.Data.Length > 11 && (p.Data[11] & 0x05) != 0;
            bool brokeByExplicit = false;
            int beforePage = cur;
            if (explicitBreak && !first)
            {
                cur++;
                brokeByExplicit = true;
                suppressMidReset = false;
            }

            bool paraFirst = true;
            int startPage = cur;
            bool hadSeg = false;
            int intraAdd = 0;

            foreach (Node k in p.Kids)
            {
                if (k.Tag == TagParaLineSeg && k.Data.Length >= 36)
                {
                    hadSeg = true;
                    for (int off = 0; off + 36 <= k.Data.Length; off += 36)
                    {
                        int v = BitConverter.ToInt32(k.Data, off + 4);
                        int h = BitConverter.ToInt32(k.Data, off + 24);
                        bool brk = v < prevV || (paraFirst && v == prevV && h <= prevH);
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
                }
                else if (k.Tag == TagCtrlHeader && k.Data.Length >= 4 && ReadCtrlId(k.Data) == "tbl ")
                {
                    intraAdd += TableIntraBreaks(k);
                }
            }

            if (hadSeg)
                withSegs++;

            if (!first && startPage > beforePage)
                breaks.Add(p);

            first = false;
            if (intraAdd > 0)
            {
                cur += intraAdd;
                suppressMidReset = true;
            }
        }

        return withSegs == topParas.Count ? breaks : [];
    }

    private static int TableIntraBreaks(Node tblCtrl)
    {
        var rowMax = new Dictionary<int, int>();
        int curRow = -1;
        int prevV = int.MinValue;
        int resets = 0;

        void Flush()
        {
            if (curRow >= 0)
                rowMax[curRow] = Math.Max(rowMax.GetValueOrDefault(curRow), resets);
            resets = 0;
            prevV = int.MinValue;
        }

        foreach (Node k in tblCtrl.Kids)
        {
            if (k.Tag == TagListHeader && k.Data.Length >= 12)
            {
                Flush();
                curRow = U16(k.Data, 10);
                foreach (Node pk in k.Kids.Where(pk => pk.Tag == TagParaHeader))
                    ScanCellSegs(pk, ref prevV, ref resets);
            }
            else if (k.Tag == TagParaHeader && curRow >= 0)
            {
                ScanCellSegs(k, ref prevV, ref resets);
            }
        }

        Flush();
        return rowMax.Values.Sum();

        static void ScanCellSegs(Node paraNode, ref int pv, ref int r)
        {
            foreach (Node seg in paraNode.Kids.Where(x => x.Tag == TagParaLineSeg))
            {
                for (int off = 0; off + 36 <= seg.Data.Length; off += 36)
                {
                    int v = BitConverter.ToInt32(seg.Data, off + 4);
                    if (v < pv)
                        r++;
                    pv = v;
                }
            }
        }
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

    private static string ReadCtrlId(byte[] d) =>
        new([(char)d[3], (char)d[2], (char)d[1], (char)d[0]]);

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
                        HwpCellBorders edges = ParseBorderFillEdges(d);
                        string? background = d.Length >= 40 && (U32(d, 32) & 1) != 0 ? Bgr(U32(d, 36)) : null;
                        _fills.Add(new Fill(edges.HasAny, edges, background));
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
                            HwpSpacing.ToEm((sbyte)d[21], d[14] == 0 ? 100 : d[14])));
                        break;

                    case TagNumbering when d.Length >= 14:
                        _numberings.Add(ParseNumbering(d));
                        break;

                    case TagBullet when d.Length >= 14:
                        ushort bulletCode = U16(d, 12);
                        string bulletChar = bulletCode > 0 && bulletCode != 0xFFFF
                            ? HwpPuaMapper.MapText(((char)bulletCode).ToString(), unfoldHwp5: true)
                            : "•";
                        _bullets.Add(bulletChar);
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

                        int headType = (int)((pattr >> 23) & 3);
                        int paraLevel = (int)((pattr >> 25) & 7);
                        int numberingId = d.Length >= 32 ? U16(d, 30) : 0;
                        _paras.Add(new ParaShape(align, I32(d, 4) / 200.0, I32(d, 12) / 200.0, I32(d, 16) / 200.0, I32(d, 20) / 200.0, line, headType, paraLevel, numberingId));
                        break;
                }
            }
            catch (ArgumentException)
            {
                // A short or damaged record: skip it.
            }
        }
    }

    private static HwpCellBorders ParseBorderFillEdges(byte[] d)
    {
        // Order in HWP5 BORDER_FILL: left(0), right(1), top(2), bottom(3), each 6 bytes: type(u8), width(u8), color(COLORREF u32).
        return new HwpCellBorders(
            Top: ParseEdge(d, 2),
            Right: ParseEdge(d, 1),
            Bottom: ParseEdge(d, 3),
            Left: ParseEdge(d, 0));

        static HwpBorderEdge? ParseEdge(byte[] data, int index)
        {
            int o = 2 + (index * 6);
            if (data.Length < o + 6)
                return null;

            byte lineType = data[o];
            if (lineType == 0)
                return null;

            uint colorRaw = BitConverter.ToUInt32(data, o + 2);
            if ((colorRaw & 0xFFFFFF) == 0xFFFFFF)
                return null; // invisible white border on white page

            string color = Bgr(colorRaw) ?? "#000000";
            string cssStyle = lineType switch
            {
                2 or 4 or 5 or 6 => "dashed",
                3 or 7 => "dotted",
                8 or 9 or 10 or 11 => "double",
                _ => "solid",
            };
            double widthPt = Hwp5ThicknessPt(data[o + 1], cssStyle == "double");
            return new HwpBorderEdge(cssStyle, widthPt, color);
        }
    }

    private static double Hwp5ThicknessPt(byte code, bool isDouble)
    {
        double mm = code switch
        {
            0 => 0.1,
            1 => 0.12,
            2 => 0.15,
            3 => 0.2,
            4 => 0.25,
            5 => 0.3,
            6 => 0.4,
            7 => 0.5,
            8 => 0.6,
            9 => 0.7,
            10 => 1.0,
            11 => 1.5,
            12 => 2.0,
            13 => 3.0,
            14 => 4.0,
            15 => 5.0,
            _ => 0.12,
        };
        double pt = Math.Max(0.5, Math.Round(mm * 2.834645, 2));
        return isDouble ? Math.Max(pt, 2.0) : pt;
    }

    private static NumberingDef ParseNumbering(byte[] d)
    {
        var formats = new string[7];
        var numFormats = new string[7];
        var starts = new int[7];
        Array.Fill(starts, 1);

        int offset = 0;
        for (int level = 0; level < 7; level++)
        {
            if (offset + 12 > d.Length)
            {
                formats[level] = string.Empty;
                numFormats[level] = "DIGIT";
                continue;
            }

            uint attr = U32(d, offset);
            numFormats[level] = HwpNumbering.FromHwp5Code((int)((attr >> 5) & 0x0F));
            offset += 12;

            if (offset + 2 <= d.Length)
            {
                int len = U16(d, offset);
                offset += 2;
                int byteLen = len * 2;
                if (len > 0 && offset + byteLen <= d.Length)
                {
                    formats[level] = Encoding.Unicode.GetString(d, offset, byteLen);
                    offset += byteLen;
                }
                else
                {
                    formats[level] = string.Empty;
                }
            }
            else
            {
                formats[level] = string.Empty;
            }
        }

        int baseStart = 1;
        if (offset + 2 <= d.Length)
        {
            int s = U16(d, offset);
            if (s > 0)
                baseStart = s;
            offset += 2;
        }

        for (int level = 0; level < 7; level++)
        {
            if (offset + 4 <= d.Length)
            {
                int s = (int)U32(d, offset);
                starts[level] = s > 0 ? s : baseStart;
                offset += 4;
            }
            else
            {
                starts[level] = baseStart;
            }
        }

        return new NumberingDef(formats, numFormats, starts);
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
            else if (ch == 10)
            {
                // Some HWP5 documents wrap inline equations as 0x000A + 0x000B + 14-byte extended payload.
                if (i + 18 <= n && (rec[i + 2] | (rec[i + 3] << 8)) == 0x000B)
                {
                    i += 18;
                    position += 9;
                }
                else
                {
                    chars.Add((position, '\n'));
                    i += 2;
                    position++;
                }
            }
            else if (ch is 30 or 31)
            {
                chars.Add((position, ' '));
                i += 2;
                position++;
            }
            else if (ch == 0 || ch is >= 24 and <= 29)
            {
                // 0x18 (24) soft hyphen is not drawn by Hancom; 25..29 reserved.
                i += 2;
                position++;
            }
            else
            {
                // Inline and extended controls (tab, fields, tables, pictures, equations...) take 8 WCHARs.
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

        // Capture section outline numbering ID if this paragraph carries secd.
        foreach (Node control in controls)
        {
            if (control.Data.Length >= 26 && ReadCtrlId(control.Data) == "secd")
            {
                // Outline numbering ID is at offset 24 (u16) in SECD header when present.
                int oid = U16(control.Data, 24);
                if (oid > 0)
                    _outlineNumberingId = oid;
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
        CharShape? firstCs = shapes.Count > 0 && shapes[0].Id < _chars.Count ? _chars[(int)shapes[0].Id] : null;
        if (firstCs is not null)
            para.Size = firstCs.Size;

        // Resolve automatic numbering / bullet prefix (advance counters even on empty paragraphs, matching Hancom).
        string? headingPrefix = ResolveHeadingPrefix(shape);

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

        var extra = new List<object>();
        foreach (Node control in controls)
        {
            if (control.Data.Length < 4)
                continue;

            string id = ReadCtrlId(control.Data);
            try
            {
                switch (id)
                {
                    case "tbl ":
                        extra.Add(Table(control));
                        break;

                    case "gso ":
                        double w = U32(control.Data, 16) / 100.0, h = U32(control.Data, 20) / 100.0;
                        para.Runs.AddRange(Pictures(control, w > 0 ? w : null, h > 0 ? h : null));
                        extra.AddRange(TextBoxParas(control));
                        break;

                    case "eqed":
                        if (ExtractEquation(control) is { Length: > 0 } script)
                            para.Runs.Add(new HwpEquationRun(script));
                        break;

                    case "fn  ":
                    case "en  ":
                        string noteText = ExtractNoteText(control);
                        if (noteText.Length > 0)
                        {
                            int num = _footnotes.Count + 1;
                            _footnotes.Add($"{num}) {noteText}");
                            para.Runs.Add(new HwpRun { Text = $"{num})", Sup = 1, Size = firstCs?.Size });
                        }

                        break;

                    case "tcps":
                        if (ExtractComposeText(control.Data) is { Length: > 0 } composed)
                            para.Runs.Add(new HwpRun { Text = composed, Size = firstCs?.Size, Font = firstCs?.Font });
                        break;
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

    private string? ResolveHeadingPrefix(ParaShape? shape)
    {
        if (shape is null || shape.HeadType == 0)
            return null;

        if (shape.HeadType == 3)
        {
            int idx = shape.NumberingId - 1;
            return idx >= 0 && idx < _bullets.Count ? _bullets[idx] : "•";
        }

        int numId = shape.HeadType == 1
            ? (shape.NumberingId > 0 ? shape.NumberingId : _outlineNumberingId)
            : shape.NumberingId;
        if (numId <= 0 || numId > _numberings.Count)
            return null;

        NumberingDef def = _numberings[numId - 1];
        int level = Math.Clamp(shape.ParaLevel, 0, 6);
        int[] counters = _numberingState.Advance($"hwp5:{numId}", level);
        string fmt = def.LevelFormats[level];
        if (string.IsNullOrEmpty(fmt))
            return null;

        return HwpNumbering.Expand(fmt, counters, def.NumberFormats, def.StartNumbers);
    }

    private void Flush(HwpPara para, StringBuilder buffer, long shapeId)
    {
        if (buffer.Length == 0)
            return;

        string mapped = HwpPuaMapper.MapText(buffer.ToString(), unfoldHwp5: true);
        buffer.Clear();
        if (mapped.Length == 0)
            return;

        CharShape? cs = shapeId >= 0 && shapeId < _chars.Count ? _chars[(int)shapeId] : null;
        para.Runs.Add(new HwpRun
        {
            Text = mapped,
            Bold = cs?.Bold ?? false,
            Italic = cs?.Italic ?? false,
            Underline = cs?.Underline ?? false,
            Strike = cs?.Strike ?? false,
            Size = cs?.Size,
            Color = cs?.Color,
            Sup = cs?.Sup ?? 0,
            Font = cs?.Font,
            Spacing = cs?.Spacing ?? 0,
        });
    }

    private static string? ExtractEquation(Node ctrl)
    {
        Node? eq = Find(ctrl.Kids, TagEqEdit);
        if (eq is null || eq.Data.Length < 6)
            return null;

        int len = U16(eq.Data, 4);
        if (len <= 0 || 6 + (len * 2) > eq.Data.Length)
            return null;

        return Encoding.Unicode.GetString(eq.Data, 6, len * 2).Replace("\0", string.Empty).Trim();
    }

    private string ExtractNoteText(Node ctrl)
    {
        var sb = new StringBuilder();
        foreach (Node k in ctrl.Kids)
        {
            if (k.Tag == TagParaHeader)
                AppendParaPlainText(k, sb);
            else if (k.Tag == TagListHeader)
            {
                foreach (Node pk in k.Kids.Where(x => x.Tag == TagParaHeader))
                    AppendParaPlainText(pk, sb);
            }
        }

        return sb.ToString().Trim();
    }

    private void AppendParaPlainText(Node paraNode, StringBuilder sb)
    {
        foreach (Node k in paraNode.Kids)
        {
            if (k.Tag == TagParaText)
            {
                string raw = new([.. DecodeText(k.Data).Select(c => c.Char)]);
                string mapped = HwpPuaMapper.MapText(raw, unfoldHwp5: true).Trim();
                if (mapped.Length > 0)
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(mapped);
                }
            }
        }
    }

    private static string? ExtractComposeText(byte[] data)
    {
        // TCPS: ctrlId(4) + charCount(u16@4) + chars(UTF-16LE) + circleType(u32 after chars)
        if (data.Length < 6)
            return null;

        int len = U16(data, 4);
        if (len <= 0 || len > 16 || 6 + (len * 2) > data.Length)
            return null;

        string chars = Encoding.Unicode.GetString(data, 6, len * 2).Trim('\0');
        bool enclosed = data.Length >= 6 + (len * 2) + 4 && U32(data, 6 + (len * 2)) != 0;
        return HwpPuaMapper.ComposeText(chars, enclosed);
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
        string? caption = null;
        bool seenTableTag = false;

        foreach (Node k in node.Kids)
        {
            if (k.Tag == TagTable)
            {
                seenTableTag = true;
                columns = U16(k.Data, 6);
            }
            else if (k.Tag == TagListHeader)
            {
                byte[] d = k.Data;
                if (!seenTableTag && d.Length < 34)
                {
                    // Caption list header appears before or outside cell headers with shorter data.
                    var capSb = new StringBuilder();
                    foreach (Node pk in k.Kids.Where(pk => pk.Tag == TagParaHeader))
                        AppendParaPlainText(pk, capSb);
                    if (capSb.Length > 0)
                        caption = capSb.ToString().Trim();
                    continue;
                }

                current = new HwpCell();
                if (d.Length >= 34)
                {
                    int column = U16(d, 8), row = U16(d, 10);
                    current.ColSpan = Math.Max(1, (int)U16(d, 12, 1));
                    current.RowSpan = Math.Max(1, (int)U16(d, 14, 1));
                    current.Width = U32(d, 16) / 100.0;
                    current.Height = U32(d, 20) / 100.0;
                    uint listAttr = U32(d, 4);
                    current.VAlign = ((listAttr >> 5) & 3) switch
                    {
                        1 => "middle",
                        2 => "bottom",
                        _ => "top",
                    };
                    int fillId = U16(d, 32);
                    Fill? fill = fillId >= 1 && fillId <= _fills.Count ? _fills[fillId - 1] : null;
                    current.Background = fill?.Background;
                    current.Border = fill?.Border ?? false;
                    current.Borders = fill?.Borders;
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

        var table = new HwpTable { Border = anyBorder, Caption = caption };
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

            table.Rows[^1].Add(cell);
        }

        for (int i = 0; i < columns; i++)
            table.ColumnWidths.Add(widths.TryGetValue(i, out double w) ? w : null);

        return table;
    }
}

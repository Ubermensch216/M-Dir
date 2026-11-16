using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MDir.Features;

/// <summary>
/// Reads the whole body text of a Hancom document (<c>.hwp</c>, <c>.hwpx</c>) without Hancom Office. The shell
/// preview handler Hancom installs only paints the first page, so the preview pane shows this instead: every
/// paragraph of every page, in order (without fonts, pictures or layout). Explicit page breaks become a divider line.
/// </summary>
internal static class HwpText
{
    /// <summary>The divider written where the document forces a new page.</summary>
    public const string PageBreakLine = "──────────────── 쪽 나누기 ────────────────";

    /// <summary>Stops reading when this many characters were produced (a guard against absurd files).</summary>
    private const int CharLimit = 16 * 1024 * 1024;

    private const int HwpTagParaHeader = 0x42;
    private const int HwpTagParaText = 0x43;
    private const int HwpTagCtrlHeader = 0x47;

    public static bool IsHwpPath(string path) => IsHwp(path) || IsHwpx(path);

    private static bool IsHwp(string path) => string.Equals(Path.GetExtension(path), ".hwp", StringComparison.OrdinalIgnoreCase);

    private static bool IsHwpx(string path) => string.Equals(Path.GetExtension(path), ".hwpx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The text of the document, or null when it cannot be read (password-protected, distribution-only, damaged, or
    /// not really a Hancom file). The caller then falls back to the shell preview handler.
    /// </summary>
    public static TextPreviewData? Read(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            string? text = IsHwpx(path) ? ReadHwpx(data) : ReadHwp(data);
            if (text is null)
                return null;

            return new TextPreviewData(text.ReplaceLineEndings("\r\n"), "한글 문서 본문 전체", Truncated: false, IsBinary: false, data.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException
            or InvalidOperationException or ArgumentException or OutOfMemoryException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    // ---- HWPX: a zip of XML parts; the body is Contents/section0.xml, section1.xml, ... ----

    internal static string? ReadHwpx(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var sections = zip.Entries
            .Where(e => e.FullName.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SectionNumber(e.FullName))
            .ToList();
        if (sections.Count == 0)
            return null;

        var text = new StringBuilder();
        foreach (ZipArchiveEntry entry in sections)
        {
            using Stream stream = entry.Open();
            AppendHwpxSection(stream, text);
            if (text.Length > CharLimit)
                break;
        }

        return text.ToString().TrimEnd();
    }

    private static int SectionNumber(string name)
    {
        string digits = new(Path.GetFileNameWithoutExtension(name).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : int.MaxValue;
    }

    private static void AppendHwpxSection(Stream stream, StringBuilder text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
        using var reader = XmlReader.Create(stream, settings);
        bool inText = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "p":
                            if (reader.GetAttribute("pageBreak") == "1" && text.Length > 0)
                                StartLine(text).AppendLine(PageBreakLine);
                            break;
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tab" when inText:
                            text.Append('\t');
                            break;
                        case "lineBreak" when inText:
                            text.Append('\n');
                            break;
                        case "nbSpace" or "fwSpace" when inText:
                            text.Append(' ');
                            break;
                    }

                    break;

                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText:
                    text.Append(reader.Value);
                    break;

                case XmlNodeType.EndElement:
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = false;
                            break;
                        case "p":
                            // Each paragraph (also those inside table cells) is a line of its own.
                            if (text.Length > 0 && text[^1] != '\n')
                                text.Append('\n');
                            break;
                    }

                    break;
            }

            if (text.Length > CharLimit)
                return;
        }
    }

    private static StringBuilder StartLine(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != '\n')
            text.Append('\n');

        return text;
    }

    // ---- HWP 5.x: an OLE compound file; the body is BodyText/Section0, Section1, ... (deflated records) ----

    internal static string? ReadHwp(byte[] data)
    {
        var file = CompoundFile.TryOpen(data);
        if (file is null)
            return null;

        byte[]? header = file.ReadStream("FileHeader");
        if (header is null || header.Length < 40 || !Encoding.ASCII.GetString(header, 0, 17).StartsWith("HWP Document File", StringComparison.Ordinal))
            return null;

        uint flags = BitConverter.ToUInt32(header, 36);
        bool compressed = (flags & 0x01) != 0;
        bool encrypted = (flags & 0x02) != 0;
        bool distribution = (flags & 0x04) != 0;
        bool drm = (flags & 0x10) != 0;
        if (encrypted || distribution || drm)
            return null;

        var sectionNames = file.ListStreams("BodyText")
            .Where(n => n.StartsWith("Section", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => int.TryParse(n.AsSpan("Section".Length), out int i) ? i : int.MaxValue)
            .ToList();
        if (sectionNames.Count == 0)
            return null;

        var text = new StringBuilder();
        foreach (string name in sectionNames)
        {
            byte[]? raw = file.ReadStream("BodyText/" + name);
            if (raw is null)
                continue;

            AppendHwpSection(compressed ? Inflate(raw) : raw, text);
            if (text.Length > CharLimit)
                break;
        }

        return text.ToString().TrimEnd();
    }

    private static byte[] Inflate(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Walks the record stream of one section and appends the text of every paragraph.</summary>
    internal static void AppendHwpSection(byte[] data, StringBuilder text)
    {
        int position = 0;

        // Paragraphs inside a header or footer control are page furniture, not body text: skipped by level.
        int skipAboveLevel = -1;
        while (position + 4 <= data.Length)
        {
            uint head = BitConverter.ToUInt32(data, position);
            position += 4;
            int tag = (int)(head & 0x3FF);
            int level = (int)((head >> 10) & 0x3FF);
            long size = head >> 20;
            if (size == 0xFFF)
            {
                if (position + 4 > data.Length)
                    break;

                size = BitConverter.ToUInt32(data, position);
                position += 4;
            }

            if (size > data.Length - position)
                break;

            int start = position;
            int length = (int)size;
            position += length;

            if (skipAboveLevel >= 0)
            {
                if (level > skipAboveLevel)
                    continue;

                skipAboveLevel = -1;
            }

            switch (tag)
            {
                case HwpTagCtrlHeader when length >= 4:
                    // The control id is stored reversed: "head"/"foot" read as "daeh"/"toof".
                    string id = Encoding.ASCII.GetString(data, start, 4);
                    if (id is "daeh" or "toof")
                        skipAboveLevel = level;
                    break;

                case HwpTagParaHeader when length >= 12:
                    // Byte 11 holds the break type: 0x01 section, 0x02 multi-column, 0x04 page, 0x08 column.
                    if ((data[start + 11] & 0x05) != 0 && text.Length > 0)
                        StartLine(text).AppendLine(PageBreakLine);
                    break;

                case HwpTagParaText:
                    AppendParaText(data, start, length, text);
                    if (text.Length > CharLimit)
                        return;
                    break;
            }
        }
    }

    private static void AppendParaText(byte[] data, int start, int length, StringBuilder text)
    {
        int end = start + length - (length % 2);
        int position = start;
        while (position < end)
        {
            int code = data[position] | (data[position + 1] << 8);
            position += 2;
            switch (code)
            {
                case 10:
                    text.Append('\n');
                    break;
                case 13:
                    // Paragraph end.
                    break;
                case 9:
                    text.Append('\t');
                    position += 14;
                    break;
                case 24:
                    text.Append('-');
                    break;
                case 30 or 31:
                    text.Append(' ');
                    break;
                case 0 or (>= 25 and <= 29):
                    break;
                case < 32:
                    // Inline and extended controls (fields, tables, pictures, section definitions...) take 16 bytes.
                    position += 14;
                    break;
                default:
                    text.Append((char)code);
                    break;
            }
        }

        text.Append('\n');
    }

    /// <summary>A minimal reader of OLE compound (CFB) files: enough to find a stream by path and read it.</summary>
    private sealed class CompoundFile
    {
        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint FreeSector = 0xFFFFFFFF;
        private const int EntrySize = 128;

        private readonly byte[] _data;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly uint _miniCutoff;
        private readonly uint[] _fat;
        private uint[] _miniFat = [];
        private readonly List<Entry> _entries = [];
        private byte[] _miniStream = [];

        private CompoundFile(byte[] data)
        {
            _data = data;
            _sectorSize = 1 << BitConverter.ToUInt16(data, 30);
            _miniSectorSize = 1 << BitConverter.ToUInt16(data, 32);
            _miniCutoff = BitConverter.ToUInt32(data, 56);

            // The FAT sectors are listed in the header (109 of them) and then in the DIFAT chain.
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++)
            {
                uint sector = BitConverter.ToUInt32(data, 76 + (i * 4));
                if (sector is FreeSector or EndOfChain)
                    break;

                fatSectors.Add(sector);
            }

            uint difat = BitConverter.ToUInt32(data, 68);
            int perSector = (_sectorSize / 4) - 1;
            for (int guard = 0; difat is not (FreeSector or EndOfChain) && guard < 100_000; guard++)
            {
                int offset = SectorOffset(difat);
                for (int i = 0; i < perSector; i++)
                {
                    uint sector = BitConverter.ToUInt32(data, offset + (i * 4));
                    if (sector is FreeSector or EndOfChain)
                        break;

                    fatSectors.Add(sector);
                }

                difat = BitConverter.ToUInt32(data, offset + (perSector * 4));
            }

            _fat = new uint[fatSectors.Count * (_sectorSize / 4)];
            for (int i = 0; i < fatSectors.Count; i++)
                Buffer.BlockCopy(data, SectorOffset(fatSectors[i]), _fat, i * _sectorSize, _sectorSize);
        }

        private sealed record Entry(string Name, int Type, int Left, int Right, int Child, uint Start, long Size);

        public static CompoundFile? TryOpen(byte[] data)
        {
            ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
            if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(signature))
                return null;

            var file = new CompoundFile(data);
            file.Load();
            return file;
        }

        private void Load()
        {
            byte[] directory = ReadChain(BitConverter.ToUInt32(_data, 48), long.MaxValue);
            for (int offset = 0; offset + EntrySize <= directory.Length; offset += EntrySize)
            {
                int nameLength = Math.Max(0, BitConverter.ToUInt16(directory, offset + 64) - 2);
                string name = Encoding.Unicode.GetString(directory, offset, Math.Min(nameLength, 64));
                _entries.Add(new Entry(
                    name,
                    directory[offset + 66],
                    BitConverter.ToInt32(directory, offset + 68),
                    BitConverter.ToInt32(directory, offset + 72),
                    BitConverter.ToInt32(directory, offset + 76),
                    BitConverter.ToUInt32(directory, offset + 116),
                    (long)BitConverter.ToUInt64(directory, offset + 120)));
            }

            if (_entries.Count == 0)
                return;

            uint miniFatStart = BitConverter.ToUInt32(_data, 60);
            if (miniFatStart is not (FreeSector or EndOfChain))
            {
                byte[] raw = ReadChain(miniFatStart, long.MaxValue);
                _miniFat = new uint[raw.Length / 4];
                Buffer.BlockCopy(raw, 0, _miniFat, 0, _miniFat.Length * 4);
            }

            Entry root = _entries[0];
            _miniStream = ReadChain(root.Start, root.Size);
        }

        private int SectorOffset(uint sector) => checked((int)((sector + 1L) * _sectorSize));

        /// <summary>Reads a chain of regular (FAT) sectors, at most <paramref name="size"/> bytes.</summary>
        private byte[] ReadChain(uint start, long size)
        {
            var output = new MemoryStream();
            uint sector = start;
            for (int guard = 0; sector is not (EndOfChain or FreeSector) && sector < _fat.Length && guard <= _fat.Length && output.Length < size; guard++)
            {
                int offset = SectorOffset(sector);
                int count = Math.Min(_sectorSize, _data.Length - offset);
                if (count <= 0)
                    break;

                output.Write(_data, offset, count);
                sector = _fat[sector];
            }

            byte[] result = output.ToArray();
            return size < result.Length ? result[..(int)size] : result;
        }

        /// <summary>The names of the streams directly inside the storage at <paramref name="storage"/> (a path).</summary>
        public IEnumerable<string> ListStreams(string storage)
        {
            Entry? parent = Find(storage);
            if (parent is null)
                return [];

            var names = new List<string>();
            Walk(parent.Child, entry =>
            {
                if (entry.Type == 2)
                    names.Add(entry.Name);
            });
            return names;
        }

        /// <summary>The contents of the stream at <paramref name="path"/> ("BodyText/Section0"), or null.</summary>
        public byte[]? ReadStream(string path)
        {
            Entry? entry = Find(path);
            if (entry is not { Type: 2 })
                return null;

            if (entry.Size < _miniCutoff)
                return ReadMini(entry.Start, entry.Size);

            return ReadChain(entry.Start, entry.Size);
        }

        private byte[] ReadMini(uint start, long size)
        {
            var output = new MemoryStream();
            uint sector = start;
            for (int guard = 0; sector is not (EndOfChain or FreeSector) && sector < _miniFat.Length && guard <= _miniFat.Length && output.Length < size; guard++)
            {
                long offset = (long)sector * _miniSectorSize;
                int count = (int)Math.Min(_miniSectorSize, _miniStream.Length - offset);
                if (count <= 0)
                    break;

                output.Write(_miniStream, (int)offset, count);
                sector = _miniFat[sector];
            }

            byte[] result = output.ToArray();
            return size < result.Length ? result[..(int)size] : result;
        }

        private Entry? Find(string path)
        {
            if (_entries.Count == 0)
                return null;

            Entry current = _entries[0];
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                Entry? next = null;
                Walk(current.Child, entry =>
                {
                    if (next is null && string.Equals(entry.Name, part, StringComparison.OrdinalIgnoreCase))
                        next = entry;
                });
                if (next is null)
                    return null;

                current = next;
            }

            return current;
        }

        /// <summary>Visits a storage's children (a binary tree of siblings) without recursion, guarding against cycles.</summary>
        private void Walk(int root, Action<Entry> visit)
        {
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                if (index < 0 || index >= _entries.Count || !seen.Add(index))
                    continue;

                Entry entry = _entries[index];
                visit(entry);
                stack.Push(entry.Left);
                stack.Push(entry.Right);
            }
        }
    }
}

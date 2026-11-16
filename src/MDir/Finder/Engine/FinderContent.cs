using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MDir.Features.Hwp;

namespace MDir.Finder.Engine;

/// <summary>
/// Text of documents whose files keep it compressed or in markup: HWP/HWPX through M-Dir's own readers, Word/Excel/
/// PowerPoint and OpenDocument from their XML parts, PDF through <see cref="FinderPdfText"/> (port of <c>preview.py</c>).
/// </summary>
internal static partial class FinderDocumentText
{
    /// <summary>Extensions whose content search looks at the extracted text.</summary>
    public static readonly HashSet<string> StructuredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".hwp", ".hwpx", ".pdf",
    };

    [GeneratedRegex(@"</(?:w:p|text:p|text:h|a:p|row|si|hp:p)\s*>|<(?:w:br|w:tab|text:tab|text:line-break|hp:lineBreak|hp:tab)\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    /// <summary>The document's text, or null when it cannot be read (not that format, encrypted, damaged).</summary>
    public static string? TryExtract(string path, string extension)
    {
        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".hwp":
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        return HwpFastText(bytes) ?? HwpText(HwpReader.Read(bytes));
                    }

                case ".hwpx":
                    return Zip(path, e => HwpxSection().IsMatch(e.FullName), HwpxSectionOrder);
                case ".pdf":
                    return FinderPdfText.TryExtract(File.ReadAllBytes(path));
                case ".docx" or ".docm":
                    return Zip(path, e => e.FullName is "word/document.xml" || e.FullName.StartsWith("word/header", StringComparison.Ordinal) || e.FullName.StartsWith("word/footer", StringComparison.Ordinal) || e.FullName is "word/footnotes.xml");
                case ".xlsx" or ".xlsm":
                    return Zip(path, e => e.FullName == "xl/sharedStrings.xml" || (e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)));
                case ".pptx" or ".pptm":
                    return Zip(path, e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal));
                case ".odt" or ".ods" or ".odp":
                    return Zip(path, e => e.FullName == "content.xml");
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or OutOfMemoryException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^Contents/section\d+\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex HwpxSection();

    /// <summary>Section0, Section1, ... Section10 in numeric order.</summary>
    private static int HwpxSectionOrder(ZipArchiveEntry entry) =>
        int.TryParse(new string([.. entry.FullName.Where(char.IsDigit)]), CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static string? Zip(string path, Func<ZipArchiveEntry, bool> wanted, Func<ZipArchiveEntry, int>? order = null)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        IEnumerable<ZipArchiveEntry> entries = order is null
            ? zip.Entries.Where(wanted).OrderBy(e => e.FullName, StringComparer.Ordinal)
            : zip.Entries.Where(wanted).OrderBy(order);
        foreach (ZipArchiveEntry entry in entries)
        {
            if (entry.Length > 256L << 20)
                continue;

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            sb.Append(XmlText(reader.ReadToEnd())).Append('\n');
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>The text of an XML part: tags dropped, paragraph/row ends kept as line breaks.</summary>
    internal static string XmlText(string xml)
    {
        string withBreaks = BreakRegex().Replace(xml, "\n");
        return WebUtility.HtmlDecode(TagRegex().Replace(withBreaks, string.Empty));
    }

    /// <summary>
    /// The paragraph texts of an HWP 5 file straight from its body records, without building the document model
    /// (no fonts, tables or pictures), which is several times faster. Null when it is not a readable HWP file.
    /// </summary>
    private static string? HwpFastText(byte[] data)
    {
        if (CompoundFile.TryOpen(data) is not { } file)
            return null;

        byte[]? header = file.ReadStream("FileHeader");
        if (header is null || header.Length < 40)
            return null;

        uint flags = BitConverter.ToUInt32(header, 36);
        if ((flags & (0x02 | 0x04 | 0x10)) != 0)
            return null; // encrypted, distribution-only or DRM

        bool compressed = (flags & 1) != 0;
        var sections = file.ListStreams("BodyText")
            .Select(n => (Name: n, Number: SectionNumber().Match(n)))
            .Where(x => x.Number.Success)
            .OrderBy(x => int.Parse(x.Number.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        var sb = new StringBuilder();
        foreach ((string name, _) in sections)
        {
            byte[]? raw = file.ReadStream("BodyText/" + name);
            if (raw is null)
                continue;

            byte[] body = compressed ? HwpReader.Inflate(raw) : raw;
            int pos = 0;
            while (pos + 4 <= body.Length)
            {
                uint head = BitConverter.ToUInt32(body, pos);
                pos += 4;
                int tag = (int)(head & 0x3FF);
                long size = head >> 20;
                if (size == 0xFFF)
                {
                    if (pos + 4 > body.Length)
                        break;
                    size = BitConverter.ToUInt32(body, pos);
                    pos += 4;
                }

                if (pos + size > body.Length)
                    break;
                if (tag == 67) // HWPTAG_PARA_TEXT
                {
                    AppendParaText(body.AsSpan(pos, (int)size), sb);
                    sb.Append('\n');
                }

                pos += (int)size;
            }
        }

        if (sb.ToString().AsSpan().Trim().IsEmpty && file.ReadStream("PrvText") is { } preview)
            return Encoding.Unicode.GetString(preview).Replace("\r\n", "\n").Replace('\r', '\n');
        return sb.ToString();
    }

    [GeneratedRegex(@"^Section(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionNumber();

    /// <summary>One PARA_TEXT record: UTF-16LE with embedded control codes (inline and extended controls take 16 bytes).</summary>
    private static void AppendParaText(ReadOnlySpan<byte> rec, StringBuilder sb)
    {
        int i = 0, n = rec.Length / 2 * 2;
        while (i < n)
        {
            int ch = rec[i] | (rec[i + 1] << 8);
            if (ch >= 32)
            {
                sb.Append((char)ch);
                i += 2;
            }
            else if (ch is 0 or 10 or 13 || ch is >= 24 and <= 31)
            {
                if (ch is 10 or 13)
                    sb.Append('\n');
                i += 2;
            }
            else
            {
                if (ch == 9)
                    sb.Append('\t');
                i += 16;
            }
        }
    }

    private static string? HwpText(HwpDoc? doc)
    {
        if (doc is null)
            return null;

        var sb = new StringBuilder();
        Blocks(doc.Blocks, sb);
        return sb.ToString();
    }

    private static void Blocks(IEnumerable<object> blocks, StringBuilder sb)
    {
        foreach (object block in blocks)
        {
            switch (block)
            {
                case HwpPara para:
                    foreach (object run in para.Runs)
                    {
                        if (run is HwpRun r)
                            sb.Append(r.Text);
                    }

                    sb.Append('\n');
                    break;
                case HwpTable table:
                    foreach (List<HwpCell> row in table.Rows)
                    {
                        foreach (HwpCell cell in row)
                        {
                            Blocks(cell.Blocks, sb);
                            sb.Append('\t');
                        }
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// Decides whether a file's contents match a <c>content:</c> term: structured documents by their extracted text, any
/// other file as bytes (UTF-8, UTF-16LE and the ANSI code page; ASCII case folding). Port of <c>_compile_content</c>.
/// </summary>
internal sealed class FinderContentMatcher
{
    private readonly Regex? _regex;
    private readonly string _needle;
    private readonly byte[][] _patterns;
    private readonly bool _case;
    private readonly long _max;

    private FinderContentMatcher(Regex? regex, string needle, byte[][] patterns, bool matchCase, long max)
    {
        _regex = regex;
        _needle = needle;
        _patterns = patterns;
        _case = matchCase;
        _max = max;
    }

    public static FinderContentMatcher Create(string term, FinderFlags flags, long contentMax)
    {
        Regex? rx = null;
        if (flags.Regex)
        {
            try
            {
                rx = new Regex(term, (flags.Case ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new FinderQueryException($"Invalid regular expression: {ex.Message}");
            }
        }

        string needle = flags.Case ? term : term.ToLowerInvariant();
        var pats = new List<byte[]> { Encoding.UTF8.GetBytes(needle), Encoding.Unicode.GetBytes(needle) };
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            pats.Add(ansi.GetBytes(needle));
        }
        catch (Exception ex) when (ex is ArgumentException or EncoderFallbackException or NotSupportedException)
        {
            // No such code page, or the term has characters outside it.
        }

        byte[][] patterns = [.. pats.Where(p => p.Length > 0).Select(p => flags.Case ? p : AsciiLower(p)).DistinctBy(Convert.ToHexString)];
        return new FinderContentMatcher(rx, needle, patterns, flags.Case, contentMax);
    }

    private static byte[] AsciiLower(byte[] data)
    {
        byte[] copy = (byte[])data.Clone();
        AsciiLowerInPlace(copy);
        return copy;
    }

    private static void AsciiLowerInPlace(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] is >= (byte)'A' and <= (byte)'Z')
                data[i] += 32;
        }
    }

    private bool MatchText(string text)
    {
        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return text.Contains(_needle, _case ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(string path, string name, CancellationToken cancel)
    {
        if (_patterns.Length == 0 && _regex is null)
            return false;

        try
        {
            string ext = Path.GetExtension(name);
            if (FinderDocumentText.StructuredExtensions.Contains(ext) && new FileInfo(path).Length <= _max)
            {
                // The text of these documents is stored compressed: search the extracted text, and the bytes only
                // when it cannot be read.
                if (FinderDocumentText.TryExtract(path, ext) is { } text)
                    return MatchText(text);
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (fs.Length > _max)
                return false;

            if (_regex is not null)
            {
                byte[] raw = new byte[fs.Length];
                fs.ReadExactly(raw);
                return MatchText(Encoding.UTF8.GetString(raw)) || MatchText(Encoding.Unicode.GetString(raw));
            }

            int keep = _patterns.Max(p => p.Length) - 1;
            byte[] buffer = new byte[(1 << 20) + keep];
            int tail = 0;
            while (true)
            {
                if (cancel.IsCancellationRequested)
                    return false;

                int read = fs.Read(buffer, tail, 1 << 20);
                if (read <= 0)
                    return false;

                Span<byte> data = buffer.AsSpan(tail, read);
                if (!_case)
                    AsciiLowerInPlace(data);
                Span<byte> window = buffer.AsSpan(0, tail + read);
                foreach (byte[] p in _patterns)
                {
                    if (window.IndexOf(p) >= 0)
                        return true;
                }

                tail = Math.Min(keep, window.Length);
                if (tail > 0)
                    window[^tail..].CopyTo(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}

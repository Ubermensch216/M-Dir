using System.Text;

namespace MDir.Features;

/// <summary>The beginning of a text file, decoded.</summary>
/// <param name="Text">The text with Windows line endings (what a TextBox shows).</param>
/// <param name="Truncated">The file is longer than what was read.</param>
internal sealed record TextPreviewData(string Text, string EncodingName, bool Truncated, bool IsBinary, long FileLength);

/// <summary>Reads and decodes text files: BOM, then UTF-8, then the Korean ANSI code page.</summary>
internal static class TextFile
{
    public const int DefaultLimit = 256 * 1024;
    private const int SniffLength = 8192;
    private const int KoreanCodePage = 949;

    static TextFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>File name extensions shown as text without asking a preview handler.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".json", ".jsonc", ".xml", ".xaml", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".config",
        ".properties", ".toml", ".yaml", ".yml", ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".vb", ".fs", ".c", ".h",
        ".cpp", ".hpp", ".cc", ".cxx", ".rs", ".go", ".java", ".kt", ".py", ".rb", ".php", ".pl", ".js", ".mjs", ".ts", ".tsx",
        ".jsx", ".css", ".scss", ".less", ".sql", ".sh", ".bash", ".bat", ".cmd", ".ps1", ".psm1", ".lua", ".r", ".swift",
        ".gradle", ".cmake", ".gitignore", ".gitattributes", ".editorconfig", ".reg", ".inf", ".srt", ".tex", ".rst", ".qdr", ".mdr",
    };

    public static bool IsKnownText(string path) => TextExtensions.Contains(Path.GetExtension(path));

    public static TextPreviewData Read(string path, int limit = DefaultLimit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        byte[] buffer = new byte[(int)Math.Min(length, limit)];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                break;

            read += n;
        }

        return Decode(buffer.AsSpan(0, read), truncated: length > read, length);
    }

    public static TextPreviewData Decode(ReadOnlySpan<byte> data, bool truncated, long fileLength)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Result(Encoding.UTF8.GetString(data[3..]), "UTF-8 (BOM)");

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Result(Encoding.Unicode.GetString(EvenLength(data[2..])), "UTF-16 LE");

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Result(Encoding.BigEndianUnicode.GetString(EvenLength(data[2..])), "UTF-16 BE");

        if (LooksBinary(data))
            return new TextPreviewData(string.Empty, "binary", truncated, IsBinary: true, fileLength);

        ReadOnlySpan<byte> utf8 = truncated ? WithoutPartialUtf8Tail(data) : data;
        try
        {
            return Result(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: Windows-949 is what Korean text files without a BOM usually are.
            return Result(Encoding.GetEncoding(KoreanCodePage).GetString(data), "CP949 (한국어)");
        }

        TextPreviewData Result(string text, string encoding) =>
            new(text.ReplaceLineEndings("\r\n"), encoding, truncated, IsBinary: false, fileLength);
    }

    /// <summary>True when the first bytes contain NULs or many control characters, which text does not.</summary>
    internal static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, SniffLength)];
        if (head.IsEmpty)
            return false;

        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0)
                return true;
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D or 0x0C or 0x1B))
                control++;
        }

        return control * 100 / head.Length > 10;
    }

    private static ReadOnlySpan<byte> EvenLength(ReadOnlySpan<byte> data) => data[..(data.Length & ~1)];

    /// <summary>Cuts a UTF-8 sequence that the read limit chopped in the middle, so it is not mistaken for invalid text.</summary>
    internal static ReadOnlySpan<byte> WithoutPartialUtf8Tail(ReadOnlySpan<byte> data)
    {
        for (int back = 1; back <= Math.Min(3, data.Length); back++)
        {
            byte b = data[^back];
            if ((b & 0xC0) == 0x80)
                continue;

            int expected = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return expected > back ? data[..^back] : data;
        }

        return data;
    }
}

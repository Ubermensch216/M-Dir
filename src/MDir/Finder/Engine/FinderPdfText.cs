using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MDir.Finder.Engine;

/// <summary>
/// A small PDF text extractor for <c>content:</c> searches: reads the objects (also those packed in object streams),
/// finds the pages with their fonts, and decodes the text operators of the content streams through the fonts'
/// ToUnicode maps. Encrypted files and fonts without a usable map give no text (the search then looks at the bytes).
/// </summary>
internal static partial class FinderPdfText
{
    private sealed record Name(string Value);

    private sealed record Ref(int Number);

    private sealed record Keyword(string Value);

    private sealed class Dict : Dictionary<string, object?>;

    private sealed class PdfObject
    {
        public object? Value { get; set; }
        public byte[]? Stream { get; set; }
    }

    private sealed class Font
    {
        public bool Composite { get; init; }
        public Dictionary<int, string>? Map { get; init; }
    }

    [GeneratedRegex(@"(?<![0-9])(\d{1,10})\s+(\d{1,5})\s+obj\b")]
    private static partial Regex ObjectRegex();

    public static string? TryExtract(byte[] data)
    {
        try
        {
            string latin = Encoding.Latin1.GetString(data);
            if (!latin.StartsWith("%PDF", StringComparison.Ordinal) && !latin.AsSpan(0, Math.Min(1024, latin.Length)).Contains("%PDF", StringComparison.Ordinal))
                return null;
            if (latin.Contains("/Encrypt", StringComparison.Ordinal))
                return null;

            var objects = new Dictionary<int, PdfObject>();
            foreach (Match m in ObjectRegex().Matches(latin))
            {
                int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                ReadObject(data, latin, m.Index + m.Length, number, objects);
            }

            // Objects packed in object streams.
            foreach ((int _, PdfObject obj) in objects.ToList())
            {
                if (obj.Value is Dict d && d.GetValueOrDefault("Type") is Name { Value: "ObjStm" } && obj.Stream is not null && Decode(d, obj.Stream, objects) is { } decoded)
                    ReadObjectStream(d, decoded, objects);
            }

            var fonts = new Dictionary<int, Font>();
            var sb = new StringBuilder();
            foreach ((int number, PdfObject obj) in objects.OrderBy(o => o.Key))
            {
                if (obj.Value is not Dict page || page.GetValueOrDefault("Type") is not Name { Value: "Page" })
                    continue;

                Dict? resources = Resolve(Inherited(page, "Resources", objects), objects) as Dict;
                Dict? fontDict = resources is null ? null : Resolve(resources.GetValueOrDefault("Font"), objects) as Dict;
                foreach (object? content in ContentStreams(page, objects))
                {
                    if (content is byte[] bytes)
                        Interpret(bytes, fontDict, objects, fonts, sb);
                }

                sb.Append('\n');
                if (sb.Length > 64 << 20)
                    break;
            }

            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    // ---- objects ----

    private static void ReadObject(byte[] data, string latin, int pos, int number, Dictionary<int, PdfObject> objects)
    {
        var lexer = new Lexer(latin, pos);
        object? value = ParseValue(lexer);
        var obj = new PdfObject { Value = value };
        lexer.SkipWhite();
        if (value is Dict dict && lexer.StartsWith("stream"))
        {
            int start = lexer.Position + 6;
            if (start < latin.Length && latin[start] == '\r')
                start++;
            if (start < latin.Length && latin[start] == '\n')
                start++;
            int end = -1;
            if (dict.GetValueOrDefault("Length") is double length && start + (long)length <= data.Length)
            {
                int candidate = start + (int)length;
                int check = latin.IndexOf("endstream", candidate, Math.Min(32, latin.Length - candidate), StringComparison.Ordinal);
                if (check >= 0)
                    end = candidate;
            }

            if (end < 0)
            {
                end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
                if (end < 0)
                    end = data.Length;
            }

            obj.Stream = data.AsSpan(start, Math.Max(0, end - start)).ToArray();
        }

        objects[number] = obj;
    }

    private static void ReadObjectStream(Dict dict, byte[] decoded, Dictionary<int, PdfObject> objects)
    {
        if (dict.GetValueOrDefault("N") is not double n || dict.GetValueOrDefault("First") is not double first)
            return;

        string text = Encoding.Latin1.GetString(decoded);
        var header = new Lexer(text, 0);
        var entries = new List<(int Number, int Offset)>();
        for (int i = 0; i < (int)n; i++)
        {
            if (header.Next() is not double num || header.Next() is not double off)
                break;
            entries.Add(((int)num, (int)off));
        }

        foreach ((int number, int offset) in entries)
        {
            int at = (int)first + offset;
            if (at < 0 || at >= text.Length || objects.ContainsKey(number))
                continue;

            objects[number] = new PdfObject { Value = ParseValue(new Lexer(text, at)) };
        }
    }

    private static object? Resolve(object? value, Dictionary<int, PdfObject> objects, int depth = 0)
    {
        while (value is Ref r && depth++ < 16)
            value = objects.TryGetValue(r.Number, out PdfObject? o) ? o.Value : null;
        return value;
    }

    private static object? Inherited(Dict page, string key, Dictionary<int, PdfObject> objects)
    {
        Dict? current = page;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.TryGetValue(key, out object? v))
                return v;
            current = Resolve(current.GetValueOrDefault("Parent"), objects) as Dict;
        }

        return null;
    }

    private static IEnumerable<object?> ContentStreams(Dict page, Dictionary<int, PdfObject> objects)
    {
        object? contents = page.GetValueOrDefault("Contents");
        IEnumerable<object?> items = Resolve(contents, objects) is List<object?> list ? list : [contents];
        foreach (object? item in items)
        {
            if (item is Ref r && objects.TryGetValue(r.Number, out PdfObject? obj) && obj.Stream is not null && obj.Value is Dict d)
                yield return Decode(d, obj.Stream, objects);
        }
    }

    private static byte[]? Decode(Dict dict, byte[] raw, Dictionary<int, PdfObject> objects)
    {
        object? filter = Resolve(dict.GetValueOrDefault("Filter"), objects);
        List<string> filters = filter switch
        {
            Name n => [n.Value],
            List<object?> l => [.. l.OfType<Name>().Select(x => x.Value)],
            _ => [],
        };
        byte[] data = raw;
        foreach (string f in filters)
        {
            if (f is "FlateDecode" or "Fl")
                data = Inflate(data);
            else
                return null; // LZW, ASCII85, images...: not text we can read
        }

        return data;
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        try
        {
            z.CopyTo(output);
        }
        catch (InvalidDataException)
        {
            // A damaged tail: keep what was decoded.
        }

        return output.ToArray();
    }

    // ---- fonts ----

    private static Font? LoadFont(object? reference, Dictionary<int, PdfObject> objects, Dictionary<int, Font> cache)
    {
        if (reference is Ref r && cache.TryGetValue(r.Number, out Font? cached))
            return cached;

        if (Resolve(reference, objects) is not Dict dict)
            return null;

        bool composite = dict.GetValueOrDefault("Subtype") is Name { Value: "Type0" };
        Dictionary<int, string>? map = null;
        if (dict.GetValueOrDefault("ToUnicode") is Ref tu && objects.TryGetValue(tu.Number, out PdfObject? cmap) && cmap.Stream is not null && cmap.Value is Dict cd && Decode(cd, cmap.Stream, objects) is { } cmapData)
            map = ParseCMap(Encoding.Latin1.GetString(cmapData));

        var font = new Font { Composite = composite, Map = map };
        if (reference is Ref key)
            cache[key.Number] = font;
        return font;
    }

    [GeneratedRegex(@"beginbfchar(.*?)endbfchar", RegexOptions.Singleline)]
    private static partial Regex BfCharRegex();

    [GeneratedRegex(@"beginbfrange(.*?)endbfrange", RegexOptions.Singleline)]
    private static partial Regex BfRangeRegex();

    [GeneratedRegex(@"<([0-9A-Fa-f\s]*)>|\[|\]")]
    private static partial Regex CMapTokenRegex();

    private static Dictionary<int, string> ParseCMap(string cmap)
    {
        var map = new Dictionary<int, string>();
        foreach (Match block in BfCharRegex().Matches(cmap))
        {
            List<string> hex = Hexes(block.Groups[1].Value);
            for (int i = 0; i + 1 < hex.Count; i += 2)
                map[Code(hex[i])] = Utf16(hex[i + 1]);
        }

        foreach (Match block in BfRangeRegex().Matches(cmap))
        {
            MatchCollection tokens = CMapTokenRegex().Matches(block.Groups[1].Value);
            for (int i = 0; i + 2 < tokens.Count;)
            {
                int lo = Code(tokens[i].Groups[1].Value), hi = Code(tokens[i + 1].Groups[1].Value);
                if (tokens[i + 2].Value == "[")
                {
                    int j = i + 3, code = lo;
                    while (j < tokens.Count && tokens[j].Value != "]")
                    {
                        if (code <= hi)
                            map[code] = Utf16(tokens[j].Groups[1].Value);
                        code++;
                        j++;
                    }

                    i = j + 1;
                }
                else
                {
                    string dst = Utf16(tokens[i + 2].Groups[1].Value);
                    for (int code = lo; code <= hi && code - lo < 65536; code++)
                    {
                        if (dst.Length > 0)
                            map[code] = dst[..^1] + (char)(dst[^1] + (code - lo));
                    }

                    i += 3;
                }
            }
        }

        return map;
    }

    private static List<string> Hexes(string text) => [.. CMapTokenRegex().Matches(text).Where(m => m.Groups[1].Success).Select(m => m.Groups[1].Value)];

    private static int Code(string hex) => int.Parse(Regex.Replace(hex, @"\s+", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Utf16(string hex)
    {
        string h = Regex.Replace(hex, @"\s+", string.Empty);
        if (h.Length % 4 != 0)
            h = h.PadLeft((h.Length + 3) / 4 * 4, '0');
        var sb = new StringBuilder();
        for (int i = 0; i < h.Length; i += 4)
            sb.Append((char)int.Parse(h.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ---- content streams ----

    private static void Interpret(byte[] content, Dict? fonts, Dictionary<int, PdfObject> objects, Dictionary<int, Font> cache, StringBuilder sb)
    {
        var lexer = new Lexer(Encoding.Latin1.GetString(content), 0);
        var operands = new List<object?>();
        Font? font = null;
        while (true)
        {
            object? token = lexer.Next(content: true);
            if (token is End)
                break;

            if (token is not Keyword keyword)
            {
                operands.Add(token);
                continue;
            }

            switch (keyword.Value)
            {
                case "Tf":
                    if (operands.Count >= 2 && operands[^2] is Name fontName && fonts is not null)
                        font = LoadFont(fonts.GetValueOrDefault(fontName.Value), objects, cache);
                    break;
                case "Tj" or "'" or "\"":
                    if (operands.Count > 0 && operands[^1] is byte[] s)
                        AppendText(sb, s, font);
                    if (keyword.Value != "Tj")
                        sb.Append('\n');
                    break;
                case "TJ":
                    if (operands.Count > 0 && operands[^1] is List<object?> parts)
                    {
                        foreach (object? part in parts)
                        {
                            if (part is byte[] str)
                                AppendText(sb, str, font);
                            else if (part is double kerning && kerning < -250)
                                sb.Append(' ');
                        }
                    }

                    break;
                case "Td" or "TD" or "T*" or "ET":
                    if (sb.Length > 0 && sb[^1] != '\n')
                        sb.Append('\n');
                    break;
                case "BI":
                    lexer.SkipInlineImage();
                    break;
            }

            operands.Clear();
        }
    }

    private static void AppendText(StringBuilder sb, byte[] bytes, Font? font)
    {
        if (font is null)
        {
            foreach (byte b in bytes)
                sb.Append(b >= 32 ? (char)b : ' ');
            return;
        }

        if (font.Composite)
        {
            if (font.Map is null)
                return;

            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                if (font.Map.TryGetValue((bytes[i] << 8) | bytes[i + 1], out string? text))
                    sb.Append(text);
            }

            return;
        }

        foreach (byte b in bytes)
        {
            if (font.Map is not null && font.Map.TryGetValue(b, out string? text))
                sb.Append(text);
            else if (b >= 32)
                sb.Append((char)b);
        }
    }

    // ---- lexer ----

    private sealed record End;

    private sealed class Lexer(string text, int position)
    {
        public int Position { get; private set; } = position;

        public void Reset(int to) => Position = to;

        public bool StartsWith(string word) => string.CompareOrdinal(text, Position, word, 0, word.Length) == 0;

        public void SkipWhite()
        {
            while (Position < text.Length)
            {
                char c = text[Position];
                if (c == '%')
                {
                    while (Position < text.Length && text[Position] is not ('\r' or '\n'))
                        Position++;
                }
                else if (c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0')
                {
                    Position++;
                }
                else
                {
                    break;
                }
            }
        }

        private static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

        private static bool IsWhite(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';

        /// <summary>The next token: a number (double), <see cref="Name"/>, string (byte[]), "[" / "]" / "&lt;&lt;" / "&gt;&gt;" keywords, other keywords, or <see cref="End"/>.</summary>
        public object? Next(bool content = false)
        {
            SkipWhite();
            if (Position >= text.Length)
                return new End();

            char c = text[Position];
            switch (c)
            {
                case '/':
                    {
                        Position++;
                        var sb = new StringBuilder();
                        while (Position < text.Length && !IsWhite(text[Position]) && !IsDelimiter(text[Position]))
                        {
                            char ch = text[Position++];
                            if (ch == '#' && Position + 1 < text.Length && int.TryParse(text.AsSpan(Position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                Position += 2;
                            }
                            else
                            {
                                sb.Append(ch);
                            }
                        }

                        return new Name(sb.ToString());
                    }

                case '(':
                    return LiteralString();
                case '<' when Position + 1 < text.Length && text[Position + 1] == '<':
                    Position += 2;
                    return new Keyword("<<");
                case '<':
                    return HexString();
                case '>' when Position + 1 < text.Length && text[Position + 1] == '>':
                    Position += 2;
                    return new Keyword(">>");
                case '[':
                    Position++;
                    return content ? ContentArray() : new Keyword("[");
                case ']':
                    Position++;
                    return new Keyword("]");
                case '{' or '}' or ')' or '>':
                    Position++;
                    return new Keyword(c.ToString());
            }

            int start = Position;
            while (Position < text.Length && !IsWhite(text[Position]) && !IsDelimiter(text[Position]))
                Position++;
            string word = text[start..Position];
            if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return number;
            return new Keyword(word);
        }

        private List<object?> ContentArray()
        {
            var list = new List<object?>();
            while (true)
            {
                object? t = Next(content: true);
                if (t is End || t is Keyword { Value: "]" })
                    return list;
                list.Add(t);
            }
        }

        private byte[] LiteralString()
        {
            Position++;
            var bytes = new List<byte>();
            int depth = 1;
            while (Position < text.Length)
            {
                char c = text[Position++];
                if (c == '\\' && Position < text.Length)
                {
                    char e = text[Position++];
                    switch (e)
                    {
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'b': bytes.Add(8); break;
                        case 'f': bytes.Add(12); break;
                        case '\r':
                            if (Position < text.Length && text[Position] == '\n')
                                Position++;
                            break;
                        case '\n': break;
                        case >= '0' and <= '7':
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2 && Position < text.Length && text[Position] is >= '0' and <= '7'; k++)
                                    v = (v * 8) + (text[Position++] - '0');
                                bytes.Add((byte)v);
                                break;
                            }

                        default: bytes.Add((byte)e); break;
                    }
                }
                else if (c == '(')
                {
                    depth++;
                    bytes.Add((byte)c);
                }
                else if (c == ')')
                {
                    if (--depth == 0)
                        break;
                    bytes.Add((byte)c);
                }
                else
                {
                    bytes.Add((byte)c);
                }
            }

            return [.. bytes];
        }

        private byte[] HexString()
        {
            Position++;
            var digits = new StringBuilder();
            while (Position < text.Length && text[Position] != '>')
            {
                if (Uri.IsHexDigit(text[Position]))
                    digits.Append(text[Position]);
                Position++;
            }

            Position++;
            if (digits.Length % 2 == 1)
                digits.Append('0');
            return Convert.FromHexString(digits.ToString());
        }

        /// <summary>After <c>BI</c>: skips the inline image's dictionary and data up to <c>EI</c>.</summary>
        public void SkipInlineImage()
        {
            int id = text.IndexOf("ID", Position, StringComparison.Ordinal);
            if (id < 0)
            {
                Position = text.Length;
                return;
            }

            int ei = text.IndexOf("EI", id + 2, StringComparison.Ordinal);
            Position = ei < 0 ? text.Length : ei + 2;
        }
    }

    private static object? ParseValue(Lexer lexer)
    {
        object? token = lexer.Next();
        switch (token)
        {
            case End:
                return null;
            case double number:
                {
                    // "12 0 R" is a reference.
                    if (number is >= 0 and < int.MaxValue && number == Math.Floor(number))
                    {
                        int save = lexer.Position;
                        if (lexer.Next() is double gen && gen == Math.Floor(gen) && lexer.Next() is Keyword { Value: "R" })
                            return new Ref((int)number);
                        lexer.Reset(save);
                    }

                    return number;
                }

            case Keyword { Value: "<<" }:
                {
                    var dict = new Dict();
                    while (true)
                    {
                        object? key = lexer.Next();
                        if (key is End or Keyword { Value: ">>" })
                            return dict;
                        if (key is Name name)
                            dict[name.Value] = ParseValue(lexer);
                    }
                }

            case Keyword { Value: "[" }:
                {
                    var list = new List<object?>();
                    while (true)
                    {
                        int save = lexer.Position;
                        object? t = lexer.Next();
                        if (t is End or Keyword { Value: "]" })
                            return list;
                        lexer.Reset(save);
                        list.Add(ParseValue(lexer));
                    }
                }

            case Keyword { Value: "true" }:
                return true;
            case Keyword { Value: "false" }:
                return false;
            default:
                return token;
        }
    }
}

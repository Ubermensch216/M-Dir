using System.Globalization;
using System.Text.RegularExpressions;

namespace MDir.Finder.Engine;

/// <summary>A bad search: unknown function, bad number or date, broken regular expression.</summary>
internal sealed class FinderQueryException(string message) : Exception(message);

/// <summary>Match options in effect for a term (set by the panel's toggles or by modifiers such as <c>case:</c> in the text).</summary>
internal readonly record struct FinderFlags(bool Case = false, bool WholeWord = false, bool Path = false, bool Regex = false, bool WholeFileName = false, bool Start = false, bool End = false);

internal abstract record QNode;

internal sealed record QTrue : QNode
{
    public static QTrue Instance { get; } = new();
}

internal sealed record QAnd(List<QNode> Items) : QNode;

internal sealed record QOr(List<QNode> Items) : QNode;

internal sealed record QNot(QNode Item) : QNode;

internal sealed record QText(string Term, FinderFlags Flags) : QNode;

internal sealed record QIsDir : QNode;

internal sealed record QIsFile : QNode;

internal sealed record QExt(HashSet<string> Extensions) : QNode;

internal sealed record QEmpty : QNode;

internal sealed record QSize(double Lo, double Hi) : QNode;

/// <param name="Which">"mtime", "ctime" or "atime".</param>
internal sealed record QDate(string Which, double Lo, double Hi) : QNode;

internal sealed record QContent(string Term, FinderFlags Flags) : QNode;

internal sealed record QLen(double Lo, double Hi) : QNode;

internal sealed record QDepth(double Lo, double Hi) : QNode;

internal sealed record QParent(string Path) : QNode;

internal sealed record QAttrib(uint Mask) : QNode;

/// <summary>
/// The search syntax (port of <c>query.py</c>): space = AND, <c>|</c> = OR, <c>!</c> = NOT, <c>&lt; &gt;</c> grouping,
/// <c>"..."</c> quoting, <c>*</c>/<c>?</c> wildcards, modifiers (<c>case:</c> <c>regex:</c> <c>ww:</c> <c>path:</c>...) and functions
/// (<c>ext:</c> <c>size:</c> <c>dm:</c> <c>content:</c> <c>folder:</c> <c>parent:</c> <c>depth:</c> <c>attrib:</c>...) and macros (<c>pic:</c> <c>doc:</c>...).
/// </summary>
internal static partial class FinderQueryParser
{
    public static readonly IReadOnlyDictionary<string, string> Macros = new Dictionary<string, string>
    {
        ["audio"] = "mp3;wav;flac;aac;ogg;wma;m4a;opus;aiff;mid;midi;ape",
        ["video"] = "mp4;mkv;avi;mov;wmv;flv;webm;m4v;mpg;mpeg;3gp;ts",
        ["pic"] = "jpg;jpeg;png;gif;bmp;tif;tiff;webp;ico;svg;heic;raw;psd",
        ["picture"] = "jpg;jpeg;png;gif;bmp;tif;tiff;webp;ico;svg;heic;raw;psd",
        ["doc"] = "doc;docx;xls;xlsx;ppt;pptx;pdf;txt;rtf;odt;ods;odp;md;csv;hwp;hwpx",
        ["document"] = "doc;docx;xls;xlsx;ppt;pptx;pdf;txt;rtf;odt;ods;odp;md;csv;hwp;hwpx",
        ["zip"] = "zip;rar;7z;tar;gz;bz2;xz;cab;iso;tgz;zst",
        ["compressed"] = "zip;rar;7z;tar;gz;bz2;xz;cab;iso;tgz;zst",
        ["exe"] = "exe;bat;cmd;com;msi;scr;ps1",
        ["executable"] = "exe;bat;cmd;com;msi;scr;ps1",
    };

    private static readonly Dictionary<string, Func<FinderFlags, FinderFlags>> Modifiers = new()
    {
        ["case"] = f => f with { Case = true },
        ["nocase"] = f => f with { Case = false },
        ["regex"] = f => f with { Regex = true },
        ["noregex"] = f => f with { Regex = false },
        ["wholeword"] = f => f with { WholeWord = true },
        ["ww"] = f => f with { WholeWord = true },
        ["nowholeword"] = f => f with { WholeWord = false },
        ["noww"] = f => f with { WholeWord = false },
        ["path"] = f => f with { Path = true },
        ["matchpath"] = f => f with { Path = true },
        ["nopath"] = f => f with { Path = false },
        ["nomatchpath"] = f => f with { Path = false },
        ["wfn"] = f => f with { WholeFileName = true },
        ["wholefilename"] = f => f with { WholeFileName = true },
        ["nowfn"] = f => f with { WholeFileName = false },
        ["nowholefilename"] = f => f with { WholeFileName = false },
        ["startwith"] = f => f with { Start = true },
        ["startswith"] = f => f with { Start = true },
        ["endwith"] = f => f with { End = true },
        ["endswith"] = f => f with { End = true },
    };

    private static readonly HashSet<string> Functions =
    [
        "file", "files", "folder", "folders", "ext", "size", "dm", "datemodified", "dc", "datecreated",
        "da", "dateaccessed", "content", "len", "parent", "infolder", "nosubfolders", "depth", "empty", "attrib", "attributes",
    ];

    private static readonly Dictionary<string, string> DateFunctions = new()
    {
        ["dm"] = "mtime", ["datemodified"] = "mtime", ["dc"] = "ctime", ["datecreated"] = "ctime",
        ["da"] = "atime", ["dateaccessed"] = "atime",
    };

    public static QNode Parse(string text, FinderFlags flags = default) => new Parser(Tokenize(text), flags).Parse();

    // ---- lexer ----

    private enum Tok
    {
        Or,
        Not,
        Open,
        Close,
        Word,
    }

    private readonly record struct Token(Tok Kind, string Text = "");

    [GeneratedRegex("^[A-Za-z]+:$")]
    private static partial Regex PrefixRegex();

    private static List<Token> Tokenize(string text)
    {
        var toks = new List<Token>();
        int i = 0, n = text.Length, depth = 0;
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '|')
            {
                toks.Add(new Token(Tok.Or));
                i++;
            }
            else if (c == '!')
            {
                toks.Add(new Token(Tok.Not));
                i++;
            }
            else if (c == '<')
            {
                toks.Add(new Token(Tok.Open));
                depth++;
                i++;
            }
            else if (c == '>' && depth > 0)
            {
                toks.Add(new Token(Tok.Close));
                depth--;
                i++;
            }
            else
            {
                var buf = new System.Text.StringBuilder();
                bool inq = false;
                while (i < n)
                {
                    c = text[i];
                    if (c == '"')
                    {
                        inq = !inq;
                        i++;
                        continue;
                    }

                    if (!inq)
                    {
                        if (char.IsWhiteSpace(c) || c == '|')
                            break;
                        if (c == '>' && depth > 0 && !PrefixRegex().IsMatch(buf.ToString()))
                            break;
                    }

                    buf.Append(c);
                    i++;
                }

                if (buf.Length > 0)
                    toks.Add(new Token(Tok.Word, buf.ToString()));
            }
        }

        return toks;
    }

    // ---- parser ----

    private sealed class Parser(List<Token> toks, FinderFlags flags)
    {
        private int _pos;
        private FinderFlags _flags = flags;

        private Tok? Peek() => _pos < toks.Count ? toks[_pos].Kind : null;

        public QNode Parse()
        {
            QNode node = OrExpr();
            if (_pos < toks.Count)
                throw new FinderQueryException("Unexpected '>'");
            return node;
        }

        private QNode OrExpr()
        {
            var parts = new List<QNode> { AndExpr() };
            while (Peek() == Tok.Or)
            {
                _pos++;
                parts.Add(AndExpr());
            }

            return parts.Count == 1 ? parts[0] : new QOr(parts);
        }

        private QNode AndExpr()
        {
            var items = new List<QNode>();
            while (Peek() is not (null or Tok.Or or Tok.Close))
                items.Add(Unary());
            items.RemoveAll(x => x is QTrue);
            if (items.Count == 0)
                return QTrue.Instance;
            return items.Count == 1 ? items[0] : new QAnd(items);
        }

        private QNode Unary()
        {
            Tok? kind = Peek();
            if (kind == Tok.Not)
            {
                _pos++;
                if (Peek() is null or Tok.Or or Tok.Close)
                    return QTrue.Instance;
                return new QNot(Unary());
            }

            if (kind == Tok.Open)
            {
                _pos++;
                QNode node = OrExpr();
                if (Peek() == Tok.Close)
                    _pos++;
                return node;
            }

            string word = toks[_pos].Text;
            _pos++;
            return Word(word);
        }

        private QNode Word(string word)
        {
            int colon = word.IndexOf(':');
            if (colon > 0)
            {
                string name = word[..colon];
                string value = word[(colon + 1)..];
                string lname = name.ToLowerInvariant();
                if (name.All(char.IsLetter))
                {
                    if (Modifiers.TryGetValue(lname, out Func<FinderFlags, FinderFlags>? apply))
                    {
                        if (value.Length == 0)
                        {
                            _flags = apply(_flags);
                            return QTrue.Instance;
                        }

                        return new QText(value, apply(_flags));
                    }

                    if (Functions.Contains(lname) || Macros.ContainsKey(lname))
                        return Function(lname, value);
                }
            }

            return new QText(word, _flags);
        }

        private QNode Function(string name, string value)
        {
            if (name is "file" or "files" or "folder" or "folders")
            {
                QNode node = name.StartsWith("folder", StringComparison.Ordinal) ? new QIsDir() : new QIsFile();
                return value.Length > 0 ? new QAnd([node, new QText(value, _flags)]) : node;
            }

            if (Macros.TryGetValue(name, out string? macro))
            {
                QNode node = new QExt([.. macro.Split(';')]);
                return value.Length > 0 ? new QAnd([node, new QText(value, _flags)]) : node;
            }

            if (name == "ext")
                return new QExt([.. value.Split(';').Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0)]);
            if (name == "empty")
                return new QEmpty();
            if (value.Length == 0)
                throw new FinderQueryException($"'{name}:' needs a value");

            switch (name)
            {
                case "size":
                    {
                        (double lo, double hi) = Numeric(value, SizeAtom);
                        return new QSize(lo, hi);
                    }

                case "content":
                    return new QContent(value, _flags);
                case "len":
                    {
                        (double lo, double hi) = Numeric(value, IntAtom);
                        return new QLen(lo, hi);
                    }

                case "depth":
                    {
                        (double lo, double hi) = Numeric(value, IntAtom);
                        return new QDepth(lo, hi);
                    }

                case "parent" or "infolder" or "nosubfolders":
                    return new QParent(value.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant());
                case "attrib" or "attributes":
                    return new QAttrib(AttribMask(value));
            }

            if (DateFunctions.TryGetValue(name, out string? which))
            {
                (double lo, double hi) = Numeric(value, s => DateAtom(s));
                return new QDate(which, lo, hi);
            }

            throw new FinderQueryException($"Unknown function '{name}:'");
        }
    }

    // ---- numbers, sizes, dates ----

    private static readonly Dictionary<string, double> SizeUnits = new()
    {
        [""] = 1, ["b"] = 1, ["k"] = 1 << 10, ["kb"] = 1 << 10, ["kib"] = 1 << 10, ["m"] = 1 << 20, ["mb"] = 1 << 20,
        ["mib"] = 1 << 20, ["g"] = 1 << 30, ["gb"] = 1 << 30, ["gib"] = 1 << 30, ["t"] = 1L << 40, ["tb"] = 1L << 40,
    };

    private static readonly Dictionary<string, (double, double)> SizeConstants = new()
    {
        ["empty"] = (0, 0),
        ["tiny"] = (1, 10 << 10),
        ["small"] = ((10 << 10) + 1, 100 << 10),
        ["medium"] = ((100 << 10) + 1, 1 << 20),
        ["large"] = ((1 << 20) + 1, 16 << 20),
        ["huge"] = ((16 << 20) + 1, 128 << 20),
        ["gigantic"] = ((128 << 20) + 1, double.PositiveInfinity),
        ["unknown"] = (-1, -1),
    };

    private static (double, double) IntAtom(string s)
    {
        if (!long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
            throw new FinderQueryException($"Invalid number '{s}'");
        return (v, v);
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([a-z]*)$")]
    private static partial Regex SizeRegex();

    private static (double, double) SizeAtom(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (SizeConstants.TryGetValue(s, out (double, double) constant))
            return constant;

        Match m = SizeRegex().Match(s);
        if (!m.Success || !SizeUnits.TryGetValue(m.Groups[2].Value, out double unit))
            throw new FinderQueryException($"Invalid size '{s}'");

        double v = Math.Floor(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * unit);
        return (v, v);
    }

    [GeneratedRegex(@"^(\d{4})(?:[-/](\d{1,2})(?:[-/](\d{1,2})(?:[ T](\d{1,2})(?::(\d{2})(?::(\d{2}))?)?)?)?)?$")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^last(\d+)(second|minute|hour|day|week|month|year)s?$")]
    private static partial Regex LastRegex();

    private static readonly Dictionary<string, double> UnitSeconds = new()
    {
        ["second"] = 1, ["minute"] = 60, ["hour"] = 3600, ["day"] = 86400, ["week"] = 604800, ["month"] = 2592000, ["year"] = 31536000,
    };

    private static double Epoch(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>The [start, end) interval (epoch seconds) a date expression denotes.</summary>
    internal static (double, double) DateAtom(string s, DateTime? nowOverride = null)
    {
        s = s.Trim().ToLowerInvariant();
        DateTime now = nowOverride ?? DateTime.Now;
        DateTime today = now.Date;

        static (double, double) Span(DateTime a, DateTime b) => (Epoch(a), Math.BitDecrement(Epoch(b)));

        switch (s)
        {
            case "today": return Span(today, today.AddDays(1));
            case "yesterday": return Span(today.AddDays(-1), today);
        }

        DateTime week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        if (s == "thisweek") return Span(week, week.AddDays(7));
        if (s == "lastweek") return Span(week.AddDays(-7), week);
        DateTime month = new(today.Year, today.Month, 1);
        if (s == "thismonth") return Span(month, month.AddMonths(1));
        if (s == "lastmonth") return Span(month.AddMonths(-1), month);
        DateTime year = new(today.Year, 1, 1);
        if (s == "thisyear") return Span(year, year.AddYears(1));
        if (s == "lastyear") return Span(year.AddYears(-1), year);

        Match last = LastRegex().Match(s);
        if (last.Success)
        {
            double nowEpoch = Epoch(now);
            return (nowEpoch - long.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture) * UnitSeconds[last.Groups[2].Value], nowEpoch);
        }

        Match m = DateRegex().Match(s);
        if (m.Success)
        {
            int?[] p = [.. m.Groups.Cast<Group>().Skip(1).Select(g => g.Success ? (int?)int.Parse(g.Value, CultureInfo.InvariantCulture) : null)];
            try
            {
                int y = p[0]!.Value;
                DateTime a, b;
                if (p[1] is null) { a = new DateTime(y, 1, 1); b = a.AddYears(1); }
                else if (p[2] is null) { a = new DateTime(y, p[1]!.Value, 1); b = a.AddMonths(1); }
                else if (p[3] is null) { a = new DateTime(y, p[1]!.Value, p[2]!.Value); b = a.AddDays(1); }
                else if (p[4] is null) { a = new DateTime(y, p[1]!.Value, p[2]!.Value, p[3]!.Value, 0, 0); b = a.AddHours(1); }
                else if (p[5] is null) { a = new DateTime(y, p[1]!.Value, p[2]!.Value, p[3]!.Value, p[4]!.Value, 0); b = a.AddMinutes(1); }
                else { a = new DateTime(y, p[1]!.Value, p[2]!.Value, p[3]!.Value, p[4]!.Value, p[5]!.Value); b = a.AddSeconds(1); }
                return Span(a, b);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new FinderQueryException($"Invalid date '{s}'");
            }
        }

        throw new FinderQueryException($"Invalid date '{s}'");
    }

    /// <summary>A comparison or range expression (<c>&gt;1gb</c>, <c>1k..1m</c>, <c>=5</c>) as an inclusive (lo, hi) interval.</summary>
    internal static (double, double) Numeric(string value, Func<string, (double, double)> atom)
    {
        string v = value.Trim();
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            string a = v[..dots], b = v[(dots + 2)..];
            double lo = a.Trim().Length > 0 ? atom(a).Item1 : double.NegativeInfinity;
            double hi = b.Trim().Length > 0 ? atom(b).Item2 : double.PositiveInfinity;
            return (lo, hi);
        }

        foreach (string op in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (!v.StartsWith(op, StringComparison.Ordinal))
                continue;

            (double lo, double hi) = atom(v[op.Length..]);
            return op switch
            {
                ">=" => (lo, double.PositiveInfinity),
                "<=" => (double.NegativeInfinity, hi),
                ">" => (Math.BitIncrement(hi), double.PositiveInfinity),
                "<" => (double.NegativeInfinity, Math.BitDecrement(lo)),
                _ => (lo, hi),
            };
        }

        return atom(v);
    }

    private static readonly Dictionary<char, uint> AttributeBits = new()
    {
        ['R'] = 0x1, ['H'] = 0x2, ['S'] = 0x4, ['D'] = 0x10, ['A'] = 0x20, ['T'] = 0x100, ['L'] = 0x400, ['C'] = 0x800, ['O'] = 0x1000, ['I'] = 0x2000, ['E'] = 0x4000,
    };

    private static uint AttribMask(string letters)
    {
        uint mask = 0;
        foreach (char ch in letters.ToUpperInvariant())
        {
            if (!AttributeBits.TryGetValue(ch, out uint bit))
                throw new FinderQueryException($"Unknown attribute '{ch}'");
            mask |= bit;
        }

        return mask;
    }
}

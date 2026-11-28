using System.Globalization;
using System.Text;

namespace MDir.Features.Hwp;

/// <summary>
/// Hancom 7-level paragraph numbering counter state machine and formatters (digit, circled, Roman, Latin,
/// Korean 가나다, 자모, 일/이/삼, 한자 一/二/三). Benchmarked from kordoc (<c>src/shared/numbering.ts</c>,
/// <c>src/hwp5/numbering.ts</c>, <c>src/hwpx/para-heading.ts</c>).
/// </summary>
internal static class HwpNumbering
{
    private static readonly int[] HangulInitials = [0, 2, 3, 5, 6, 7, 9, 11, 12, 14, 15, 16, 17, 18];
    private static readonly int[] HangulMedials = [0, 4, 8, 13, 18, 20];
    private const string Jamo = "ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎ";
    private const string CircledJamo = "㉠㉡㉢㉣㉤㉥㉦㉧㉨㉩㉪㉫㉬㉭";
    private static readonly string[] HangulDigits = ["", "일", "이", "삼", "사", "오", "육", "칠", "팔", "구"];
    private static readonly string[] HangulUnits = ["", "십", "백", "천", "만"];
    private static readonly string[] HanjaDigits = ["", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
    private static readonly string[] HanjaUnits = ["", "十", "百", "千", "萬"];

    /// <summary>0-based n → 가, 나, 다, ..., 하, 거, 너, ...</summary>
    public static string HangulOrdinal(int n)
    {
        if (n < 0)
            return (n + 1).ToString(CultureInfo.InvariantCulture);

        int cols = HangulInitials.Length;
        int vowel = HangulMedials[Math.Min(n / cols, HangulMedials.Length - 1)];
        int init = HangulInitials[n % cols];
        return char.ConvertFromUtf32(0xAC00 + (init * 588) + (vowel * 28));
    }

    /// <summary>0-based n → ①..㊿ (up to 50), then (51).</summary>
    public static string CircledNumber(int n) => n switch
    {
        < 0 => (n + 1).ToString(CultureInfo.InvariantCulture),
        < 20 => char.ConvertFromUtf32(0x2460 + n),
        < 35 => char.ConvertFromUtf32(0x3251 + (n - 20)),
        < 50 => char.ConvertFromUtf32(0x32B1 + (n - 35)),
        _ => $"({n + 1})",
    };

    /// <summary>0-based n → ㉮..㉻ (14 chars), then 가나다 fallback.</summary>
    public static string CircledHangul(int n) =>
        n is >= 0 and < 14 ? char.ConvertFromUtf32(0x326E + n) : HangulOrdinal(n);

    /// <summary>1-based n → Roman numeral (falls back to Arabic outside 1..3999).</summary>
    public static string RomanNumeral(int n, bool upper)
    {
        if (n is <= 0 or > 3999)
            return n.ToString(CultureInfo.InvariantCulture);

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = upper
            ? ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"]
            : ["m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i"];

        var sb = new StringBuilder();
        int num = n;
        for (int i = 0; i < values.Length; i++)
        {
            while (num >= values[i])
            {
                sb.Append(symbols[i]);
                num -= values[i];
            }
        }

        return sb.ToString();
    }

    /// <summary>HWP5 Table 43 format code → normalized format name.</summary>
    public static string FromHwp5Code(int code) => code switch
    {
        1 => "CIRCLED_DIGIT",
        2 => "ROMAN_CAPITAL",
        3 => "ROMAN_SMALL",
        4 => "LATIN_CAPITAL",
        5 => "LATIN_SMALL",
        6 => "CIRCLED_LATIN_CAPITAL",
        7 => "CIRCLED_LATIN_SMALL",
        8 => "HANGUL_SYLLABLE",
        9 => "CIRCLED_HANGUL_SYLLABLE",
        10 => "HANGUL_JAMO",
        11 => "CIRCLED_HANGUL_JAMO",
        12 => "HANGUL_PHONETIC",
        13 => "IDEOGRAPH",
        _ => "DIGIT",
    };

    /// <summary>Formats a 1-based number according to a Hancom OWPML numFormat name.</summary>
    public static string Format(int n, string? formatName) => (formatName?.ToUpperInvariant()) switch
    {
        "CIRCLED_DIGIT" => n >= 1 ? CircledNumber(n - 1) : n.ToString(CultureInfo.InvariantCulture),
        "ROMAN_CAPITAL" => RomanNumeral(n, upper: true),
        "ROMAN_SMALL" => RomanNumeral(n, upper: false),
        "LATIN_CAPITAL" => FormatLatin(n, upper: true),
        "LATIN_SMALL" => FormatLatin(n, upper: false),
        "CIRCLED_LATIN_CAPITAL" => n is >= 1 and <= 26 ? char.ConvertFromUtf32(0x24B6 + n - 1) : FormatLatin(n, upper: true),
        "CIRCLED_LATIN_SMALL" => n is >= 1 and <= 26 ? char.ConvertFromUtf32(0x24D0 + n - 1) : FormatLatin(n, upper: false),
        "HANGUL_SYLLABLE" => n >= 1 ? HangulOrdinal(n - 1) : n.ToString(CultureInfo.InvariantCulture),
        "CIRCLED_HANGUL_SYLLABLE" => n >= 1 ? CircledHangul(n - 1) : n.ToString(CultureInfo.InvariantCulture),
        "HANGUL_JAMO" => n >= 1 && n <= Jamo.Length ? Jamo[n - 1].ToString() : n.ToString(CultureInfo.InvariantCulture),
        "CIRCLED_HANGUL_JAMO" => n >= 1 && n <= CircledJamo.Length ? CircledJamo[n - 1].ToString() : n.ToString(CultureInfo.InvariantCulture),
        "HANGUL_PHONETIC" => FormatEastAsian(n, HangulDigits, HangulUnits, "영"),
        "IDEOGRAPH" or "CIRCLED_IDEOGRAPH" or "DECAGON_CIRCLE" => FormatEastAsian(n, HanjaDigits, HanjaUnits, "零"),
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Expands a Hancom format string such as <c>"^1."</c> or <c>"(^2)"</c> using the current level counters,
    /// start numbers, and format names. Drops stray <c>^</c> before non-alphanumeric chars (e.g. <c>"^  □"</c> → <c>"□"</c>).
    /// </summary>
    public static string Expand(string formatStr, int[] counters, IReadOnlyList<string> formats, IReadOnlyList<int> starts)
    {
        if (string.IsNullOrEmpty(formatStr))
            return string.Empty;

        var sb = new StringBuilder(formatStr.Length + 4);
        int i = 0;
        while (i < formatStr.Length)
        {
            char ch = formatStr[i];
            if (ch == '^' && i + 1 < formatStr.Length)
            {
                char next = formatStr[i + 1];
                if (next is >= '1' and <= '9')
                {
                    int idx = next - '1';
                    int count = idx < counters.Length ? counters[idx] : 0;
                    int start = idx < starts.Count ? starts[idx] : 1;
                    int num = count > 0 ? start - 1 + count : start;
                    string fmt = idx < formats.Count ? formats[idx] : "DIGIT";
                    sb.Append(Format(num, fmt));
                    i += 2;
                    continue;
                }

                if (!char.IsLetterOrDigit(next))
                {
                    i++;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }

        return HwpPuaMapper.MapText(sb.ToString()).Trim();
    }

    private static string FormatLatin(int n, bool upper)
    {
        if (n <= 0)
            return n.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        int num = n;
        char baseChar = upper ? 'A' : 'a';
        while (num > 0)
        {
            num--;
            sb.Insert(0, (char)(baseChar + (num % 26)));
            num /= 26;
        }

        return sb.ToString();
    }

    private static string FormatEastAsian(int n, string[] digits, string[] units, string zero)
    {
        if (n == 0)
            return zero;

        if (n is < 0 or > 99999)
            return n.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        int num = n;
        int unit = 0;
        while (num > 0)
        {
            int d = num % 10;
            if (d > 0)
            {
                string digitStr = d == 1 && unit > 0 ? string.Empty : digits[d];
                sb.Insert(0, digitStr + units[unit]);
            }

            num /= 10;
            unit++;
        }

        return sb.ToString();
    }
}

/// <summary>Stateful 10-level paragraph numbering tracker matching Hancom Hangul's counter inheritance rules.</summary>
internal sealed class HwpNumberingState
{
    private const int Levels = 10;
    private string _currentId = string.Empty;
    private int[] _counters = new int[Levels];
    private readonly Dictionary<string, int[]> _history = [];

    public int[] Advance(string numberingId, int level)
    {
        int lv = Math.Clamp(level, 0, Levels - 1);
        if (!string.Equals(_currentId, numberingId, StringComparison.Ordinal))
        {
            if (_currentId.Length > 0)
                _history[_currentId] = (int[])_counters.Clone();

            if (_history.TryGetValue(numberingId, out int[]? saved))
            {
                _counters = (int[])saved.Clone();
            }
            else
            {
                int[] prev = _counters;
                _counters = new int[Levels];
                for (int i = 0; i < lv; i++)
                    _counters[i] = prev[i];
            }

            _currentId = numberingId;
        }

        _counters[lv]++;
        for (int i = lv + 1; i < Levels; i++)
            _counters[i] = 0;

        return (int[])_counters.Clone();
    }
}

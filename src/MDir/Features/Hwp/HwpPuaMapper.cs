using System.Text;

namespace MDir.Features.Hwp;

/// <summary>
/// Maps Hancom PUA (Private Use Area) characters and Wingdings symbol codes to standard Unicode characters.
/// Benchmarked from kordoc (<c>src/shared/pua.ts</c>, <c>src/shared/symbol-fonts.ts</c>, <c>src/hwp5/record.ts</c>):
/// <list type="bullet">
///   <item>HWP5 folded Supplementary PUA-A (<c>U+A000..U+A48C</c> → <c>U+F0000..U+F048C</c>, e.g. <c>(인)</c>).</item>
///   <item>BMP Symbol / Wingdings PUA (<c>U+F020..U+F0FF</c> → <c>●■□➔✔☑❶..</c>).</item>
///   <item>Hancom Supplementary PUA-A (<c>U+F0000..U+F09FF</c> → bullets, box-drawing, paired enclosed numbers <c>①..㊿</c>).</item>
/// </list>
/// </summary>
internal static class HwpPuaMapper
{
    internal const int FoldedPuaStart = 0xA000;
    internal const int FoldedPuaEnd = 0xA48C;
    internal const int FoldedPuaShift = 0xF0000 - 0xA000;

    /// <summary>BMP Symbol PUA (U+F020..U+F0FF) verified against Hancom PDF output; key is (code - 0xF000).</summary>
    private static readonly Dictionary<int, string> BmpSymbolMap = new()
    {
        // Shapes / symbols
        [0x6C] = "●", [0x6D] = "●", [0x6E] = "■", [0x6F] = "□",
        [0x70] = "□", [0x71] = "□", [0x72] = "□", [0x73] = "⬧",
        [0x74] = "⧫", [0x75] = "◆", [0x76] = "❖", [0x77] = "⬥",
        // Dots / bullets / stars
        [0x9E] = "·", [0x9F] = "•", [0xA0] = "·", [0xA1] = "⚪",
        [0xA2] = "○", [0xA3] = "○", [0xA4] = "◉", [0xA5] = "◎",
        [0xA7] = "▪", [0xA8] = "◻", [0xAA] = "✦", [0xAB] = "★",
        [0xAC] = "✶", [0xAD] = "✴", [0xAE] = "✹",
        // Hands
        [0x45] = "☜", [0x46] = "☞", [0x47] = "☝", [0x48] = "☟",
        // Checkmarks
        [0xFB] = "✗", [0xFC] = "✔", [0xFD] = "☒", [0xFE] = "☑",
        // Arrows
        [0xE8] = "➔", [0xEF] = "⇦", [0xF0] = "⇨", [0xF1] = "⇧", [0xF2] = "⇩",
        // Misc
        [0x22] = "✂", [0x36] = "⌛", [0x4A] = "☺", [0x4E] = "☠",
        [0x52] = "☼", [0x54] = "❄", [0x58] = "✠", [0x59] = "✡",
    };

    /// <summary>Wingdings 0x21..0xFF → Unicode (index = code - 0x21, empty = undefined).</summary>
    private static readonly string[] Wingdings =
    [
        "🖉", "✂", "✁", "👓", "🕭", "🕮", "🕯", "🕿", "✆", "🖂", "🖃", "📪", "📫", "📬", "📭", "📁",
        "📂", "📄", "🗏", "🗐", "🗄", "⌛", "🖮", "🖰", "🖲", "🖳", "🖴", "🖫", "🖬", "✇", "✍", "🖎",
        "✌", "👌", "👍", "👎", "☜", "☞", "☝", "☟", "🖐", "☺", "😐", "☹", "💣", "☠", "🏳", "🏱",
        "✈", "☼", "💧", "❄", "🕆", "✞", "🕈", "✠", "✡", "☪", "☯", "ॐ", "☸", "♈", "♉", "♊",
        "♋", "♌", "♍", "♎", "♏", "♐", "♑", "♒", "♓", "🙰", "🙵", "●", "○", "■", "□", "□",
        "❑", "❒", "⬧", "⧫", "◆", "❖", "⬥", "⌧", "⮹", "⌘", "🏵", "🏶", "🙶", "🙷", "", "⓪",
        "①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩", "⓿", "❶", "❷", "❸", "❹", "❺",
        "❻", "❼", "❽", "❾", "❿", "🙢", "🙠", "🙡", "🙣", "🙞", "🙜", "🙝", "🙟", "·", "•", "▪",
        "⚪", "○", "◯", "◉", "◎", "🔿", "▪", "◻", "🟂", "✦", "★", "✶", "✴", "✹", "✵", "⯐",
        "⌖", "⟡", "⌑", "⯑", "✪", "✰", "🕐", "🕑", "🕒", "🕓", "🕔", "🕕", "🕖", "🕗", "🕘", "🕙",
        "🕚", "🕛", "⮰", "⮱", "⮲", "⮳", "⮴", "⮵", "⮶", "⮷", "🙪", "🙫", "🙕", "🙔", "🙗", "🙖",
        "🙐", "🙑", "🙒", "🙓", "⌫", "⌦", "⮘", "⮚", "⮙", "⮛", "⮈", "⮊", "⮉", "⮋", "←", "→",
        "↑", "↓", "↖", "↗", "↙", "↘", "⬅", "➔", "⬆", "⬇", "⬉", "⬈", "⬋", "⬊", "⇦", "⇨",
        "⇧", "⇩", "⬄", "⇳", "⬀", "⬁", "⬃", "⬂", "🢬", "🢭", "✗", "✔", "☒", "☑", "",
    ];

    /// <summary>Hancom Supplementary PUA-A (U+F0000..U+F09FF) verified mappings.</summary>
    private static readonly Dictionary<int, string> SupplementaryMap = new()
    {
        [0xF003B] = "↓",
        [0xF0090] = "✺",
        [0xF00DA] = "[예]",
        [0xF00E1] = "(인)",
        [0xF012B] = "(인)",
        [0xF02EC] = "◇",
        [0xF02EF] = "·",
        [0xF02FB] = "▸",
        [0xF02FC] = "►",
        [0xF031C] = "■",
        [0xF03A0] = "↵",
        [0xF03A7] = "⊟",
        [0xF03A8] = "⊞",
        [0xF03C5] = "□",
        [0xF03DA] = "□",
        [0xF03EF] = "한",
        [0xF03F0] = "글",
        [0xF03F1] = "과",
        [0xF03F2] = "컴",
        [0xF03F3] = "퓨",
        [0xF03F4] = "터",
        [0xF03FF] = "□",
        [0xF0806] = "┌",
        [0xF0807] = "┬",
        [0xF0808] = "┐",
        [0xF080C] = "└",
        [0xF080E] = "┘",
        [0xF080F] = "━",
        [0xF0810] = "│",
        [0xF081C] = "┈",
        [0xF0827] = "■",
        [0xF0832] = "═",
        [0xF0848] = "━",
        [0xF0854] = "《",
        [0xF0855] = "》",
    };

    /// <summary>Unfolds an HWP5 WCHAR in U+A000..U+A48C to its Supplementary PUA-A code point (U+F0000..U+F048C).</summary>
    public static int UnfoldHwp5CodePoint(int codeUnit) =>
        codeUnit is >= FoldedPuaStart and <= FoldedPuaEnd ? codeUnit + FoldedPuaShift : codeUnit;

    /// <summary>Maps a Wingdings code (0x21..0xFF) to Unicode, or null if undefined.</summary>
    public static string? WingdingsChar(int code)
    {
        if (code is < 0x21 or > 0xFF)
            return null;

        string mapped = Wingdings[code - 0x21];
        return mapped.Length > 0 ? mapped : null;
    }

    /// <summary>Formats an enclosed number 0..50 as ⓪①..㊿, or (n) above 50.</summary>
    public static string EnclosedNumber(int n) => n == 0 ? "⓪" : HwpNumbering.CircledNumber(n - 1);

    /// <summary>Formats composed text (hp:compose / HWP5 tcps): digits inside a circle/box become circled numbers.</summary>
    public static string ComposeText(string chars, bool enclosed)
    {
        if (enclosed && chars.Length is 1 or 2 && int.TryParse(chars, out int num))
            return EnclosedNumber(num);

        return MapText(chars);
    }

    /// <summary>
    /// Maps Hancom PUA characters (BMP symbol PUA + Supplementary PUA-A + paired enclosed number glyphs)
    /// to standard Unicode. Unmapped BMP PUA characters are preserved (may be Hanyang PUA archaic Hangul).
    /// Unmapped Supplementary PUA-A characters are omitted so they don't render as broken boxes.
    /// </summary>
    public static string MapText(string text, bool unfoldHwp5 = false)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Fast path: check if any code unit needs mapping, unfolding, or surrogate repair.
        bool needsWork = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c is >= '\uF020' and <= '\uF0FF')
                || char.IsSurrogate(c)
                || (unfoldHwp5 && c is >= '\uA000' and <= '\uA48C'))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
            return text;

        var codePoints = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codePoints.Add(char.ConvertToUtf32(c, text[i + 1]));
                    i++;
                }
                else
                {
                    codePoints.Add(0x25A1); // □ for lone surrogate (matches Hancom Hangul)
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                codePoints.Add(0x25A1);
            }
            else
            {
                codePoints.Add(unfoldHwp5 ? UnfoldHwp5CodePoint(c) : c);
            }
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < codePoints.Count; i++)
        {
            int cp = codePoints[i];
            if (i + 1 < codePoints.Count && TryPairEnclosedNumber(cp, codePoints[i + 1], out string? paired))
            {
                sb.Append(paired);
                i++;
                continue;
            }

            if (TryMapCodePoint(cp, out string? mapped))
            {
                sb.Append(mapped);
            }
            else if (cp is >= 0xF0000 and <= 0xFFFFF)
            {
                // Unmapped Supplementary PUA-A: drop to avoid broken glyph boxes.
            }
            else if (cp <= 0xFFFF)
            {
                sb.Append((char)cp);
            }
            else
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
        }

        return sb.ToString();
    }

    private static int? TensDigit(int cp) => cp switch
    {
        >= 0xF0288 and <= 0xF0291 => cp - 0xF0288,
        >= 0xF02BA and <= 0xF02C2 => cp - 0xF02B9,
        _ => null,
    };

    private static int? UnitsDigit(int cp) => cp switch
    {
        >= 0xF0292 and <= 0xF029B => cp - 0xF0292,
        >= 0xF02C3 and <= 0xF02CC => cp - 0xF02C3,
        _ => null,
    };

    private static bool TryPairEnclosedNumber(int first, int second, out string? result)
    {
        if (TensDigit(first) is { } tens && UnitsDigit(second) is { } units)
        {
            result = EnclosedNumber((tens * 10) + units);
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryMapCodePoint(int cp, out string? mapped)
    {
        if (cp is >= 0xF020 and <= 0xF0FF)
        {
            int low = cp - 0xF000;
            if (BmpSymbolMap.TryGetValue(low, out mapped))
                return true;

            mapped = WingdingsChar(low);
            return mapped is not null;
        }

        if (cp is >= 0xF0000 and <= 0xF09FF)
        {
            if (TensDigit(cp) is { } tens)
            {
                mapped = EnclosedNumber(tens);
                return true;
            }

            if (cp is >= 0xF02B0 and <= 0xF02B9)
            {
                mapped = EnclosedNumber(cp - 0xF02B0);
                return true;
            }

            if (UnitsDigit(cp) is { } units)
            {
                mapped = units.ToString();
                return true;
            }

            return SupplementaryMap.TryGetValue(cp, out mapped);
        }

        mapped = null;
        return false;
    }
}

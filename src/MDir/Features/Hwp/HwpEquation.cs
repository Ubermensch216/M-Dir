using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MDir.Features.Hwp;

/// <summary>
/// Converts Hancom EqEdit (HULK) equation scripts (from HWP5 <c>EQEDIT</c> and HWPX <c>&lt;hp:equation&gt;&lt;hp:script&gt;</c>)
/// into readable Unicode plain text and script-free HTML math (stacked fractions, roots, superscripts/subscripts, Greek
/// letters and operators). Benchmarked from kordoc (<c>src/hwpx/equation.ts</c>, <c>src/hwp5/equation.ts</c>,
/// <c>src/render/equation-text.ts</c>, <c>src/render/equation-layout.ts</c>).
/// </summary>
internal static partial class HwpEquation
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        // Greek lowercase & uppercase
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["omicron"] = "ο", ["pi"] = "π",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["varphi"] = "φ",
        ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["ALPHA"] = "A", ["BETA"] = "B", ["GAMMA"] = "Γ", ["DELTA"] = "Δ", ["EPSILON"] = "E", ["ZETA"] = "Z",
        ["ETA"] = "H", ["THETA"] = "Θ", ["IOTA"] = "I", ["KAPPA"] = "K", ["LAMBDA"] = "Λ", ["MU"] = "M",
        ["NU"] = "N", ["XI"] = "Ξ", ["OMICRON"] = "O", ["PI"] = "Π", ["RHO"] = "P", ["SIGMA"] = "Σ",
        ["TAU"] = "T", ["UPSILON"] = "Υ", ["PHI"] = "Φ", ["CHI"] = "X", ["PSI"] = "Ψ", ["OMEGA"] = "Ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π",
        ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",

        // Operators & relations
        ["TIMES"] = "×", ["times"] = "×", ["cdot"] = "·", ["DIV"] = "÷", ["div"] = "÷", ["divide"] = "÷",
        ["+-"] = "±", ["-+"] = "∓", ["pm"] = "±", ["mp"] = "∓",
        ["LEQ"] = "≤", ["leq"] = "≤", ["le"] = "≤", ["<="] = "≤",
        ["GEQ"] = "≥", ["geq"] = "≥", ["ge"] = "≥", [">="] = "≥",
        ["!="] = "≠", ["neq"] = "≠", ["ne"] = "≠",
        ["=="] = "≡", ["equiv"] = "≡", ["APPROX"] = "≈", ["approx"] = "≈", ["SIM"] = "∼", ["sim"] = "∼",
        ["SIMEQ"] = "≃", ["CONG"] = "≅", ["PROPTO"] = "∝", ["<<"] = "≪", [">>"] = "≫",
        ["SMALLSUM"] = "∑", ["smallsum"] = "∑", ["sum"] = "∑", ["SUM"] = "∑",
        ["SMALLPROD"] = "∏", ["prod"] = "∏", ["PROD"] = "∏",
        ["int"] = "∫", ["INT"] = "∫", ["dint"] = "∬", ["tint"] = "∭", ["oint"] = "∮",
        ["INF"] = "∞", ["inf"] = "∞", ["infty"] = "∞",
        ["Partial"] = "∂", ["partial"] = "∂", ["NABLA"] = "∇", ["nabla"] = "∇",
        ["IN"] = "∈", ["in"] = "∈", ["NOTIN"] = "∉", ["notin"] = "∉", ["OWNS"] = "∋",
        ["SUBSET"] = "⊂", ["subset"] = "⊂", ["SUPERSET"] = "⊃", ["SUBSETEQ"] = "⊆", ["SUPSETEQ"] = "⊇",
        ["CUP"] = "∪", ["cup"] = "∪", ["union"] = "⋃", ["SMALLINTER"] = "∩", ["smallinter"] = "∩", ["cap"] = "∩", ["inter"] = "⋂",
        ["EMPTYSET"] = "∅", ["emptyset"] = "∅",
        ["FORALL"] = "∀", ["EXIST"] = "∃", ["THEREFORE"] = "∴", ["therefore"] = "∴", ["BECAUSE"] = "∵", ["because"] = "∵",
        ["LOR"] = "∨", ["LAND"] = "∧", ["OPLUS"] = "⊕", ["OMINUS"] = "⊖", ["OTIMES"] = "⊗", ["ODOT"] = "⊙",
        ["->"] = "→", ["rarrow"] = "→", ["rightarrow"] = "→", ["larrow"] = "←", ["leftarrow"] = "←",
        ["<->"] = "↔", ["lrarrow"] = "↔", ["LARROW"] = "⇐", ["RARROW"] = "⇒", ["LRARROW"] = "⇔",
        ["uparrow"] = "↑", ["downarrow"] = "↓", ["UPARROW"] = "⇑", ["DOWNARROW"] = "⇓", ["MAPSTO"] = "↦",
        ["CIRC"] = "∘", ["circ"] = "∘", ["DEG"] = "°", ["deg"] = "°", ["BULLET"] = "•", ["AST"] = "∗",
        ["ANGLE"] = "∠", ["angle"] = "∠", ["TRIANGLE"] = "△", ["triangle"] = "△",
        ["BOT"] = "⊥", ["bot"] = "⊥", ["TOP"] = "⊤", ["PVER"] = "∥", ["//"] = "∥",
        ["CDOTS"] = "⋯", ["LDOTS"] = "…", ["VDOTS"] = "⋮", ["DDOTS"] = "⋱",
        ["prime"] = "′", ["CENTIGRADE"] = "℃", ["FAHRENHEIT"] = "℉",
        ["HUND"] = "‰", ["THOU"] = "‱", ["ATT"] = "※",
        ["lim"] = "lim", ["Lim"] = "lim", ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan",
        ["sec"] = "sec", ["csc"] = "csc", ["cot"] = "cot", ["log"] = "log", ["ln"] = "ln", ["exp"] = "exp",
        ["max"] = "max", ["min"] = "min",
    };

    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ', ['i'] = 'ⁱ',
    };

    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
    };

    private static readonly HashSet<string> DropKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "it", "bold", "scale", "color", "font", "size", "left", "right", "big", "Big", "bigg", "Bigg", "underover", "pile", "lpile", "rpile",
    };

    private static readonly HashSet<string> AccentKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bar", "hat", "vec", "dot", "ddot", "tilde", "acute", "grave", "check", "under", "box",
    };

    /// <summary>Converts a Hancom EqEdit script to readable plain Unicode text (for Finder search and fallback).</summary>
    public static string ToPlainText(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        List<Token> tokens = Tokenize(script);
        var parser = new Parser(tokens, html: false);
        return CollapseSpaces(parser.ParseSequence(stopAtCloseBrace: false));
    }

    /// <summary>
    /// Converts a Hancom EqEdit script to safe, script-free HTML math (stacked fractions, roots, sup/sub, Greek/operators).
    /// </summary>
    public static string ToHtml(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return string.Empty;

        List<Token> tokens = Tokenize(script);
        var parser = new Parser(tokens, html: true);
        string body = parser.ParseSequence(stopAtCloseBrace: false).Trim();
        return body.Length == 0 ? string.Empty : $"<span class=\"eq\">{body}</span>";
    }

    private enum TokenKind { Word, GroupOpen, GroupClose, Op, Caret, Underscore, Space, String }

    private sealed record Token(TokenKind Kind, string Value);

    private static List<Token> Tokenize(string input)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c) || c is '`' or '~')
            {
                if (list.Count > 0 && list[^1].Kind != TokenKind.Space)
                    list.Add(new Token(TokenKind.Space, " "));
                i++;
                continue;
            }

            if (c == '"')
            {
                int end = input.IndexOf('"', i + 1);
                if (end < 0)
                    end = input.Length;
                list.Add(new Token(TokenKind.String, input[(i + 1)..end]));
                i = end < input.Length ? end + 1 : end;
                continue;
            }

            if (c == '{')
            {
                list.Add(new Token(TokenKind.GroupOpen, "{"));
                i++;
                continue;
            }

            if (c == '}')
            {
                list.Add(new Token(TokenKind.GroupClose, "}"));
                i++;
                continue;
            }

            if (c == '^')
            {
                list.Add(new Token(TokenKind.Caret, "^"));
                i++;
                continue;
            }

            if (c == '_')
            {
                list.Add(new Token(TokenKind.Underscore, "_"));
                i++;
                continue;
            }

            // Multi-char operators
            if (i + 2 < input.Length && input.AsSpan(i, 3) is "<->")
            {
                list.Add(new Token(TokenKind.Op, "<->"));
                i += 3;
                continue;
            }

            if (i + 1 < input.Length && input.AsSpan(i, 2) is "+-" or "-+" or "<=" or ">=" or "!=" or "==" or "->" or "<<" or ">>" or "//")
            {
                list.Add(new Token(TokenKind.Op, input.Substring(i, 2)));
                i += 2;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < input.Length && char.IsAsciiLetter(input[i]))
                    i++;
                list.Add(new Token(TokenKind.Word, input[start..i]));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                int start = i;
                while (i < input.Length && (char.IsAsciiDigit(input[i]) || (input[i] == '.' && i + 1 < input.Length && char.IsAsciiDigit(input[i + 1]))))
                    i++;
                list.Add(new Token(TokenKind.Word, input[start..i]));
                continue;
            }

            // '#' in EqEdit is a row separator in matrices/cases; '&' is a column separator.
            list.Add(new Token(TokenKind.Op, c.ToString()));
            i++;
        }

        return list;
    }

    private sealed class Parser(List<Token> tokens, bool html)
    {
        private int _pos;

        public string ParseSequence(bool stopAtCloseBrace)
        {
            var items = new List<string>();
            while (_pos < tokens.Count)
            {
                Token t = tokens[_pos];
                if (t.Kind == TokenKind.GroupClose)
                {
                    if (stopAtCloseBrace)
                        _pos++;
                    else
                        _pos++;
                    break;
                }

                if (t.Kind == TokenKind.Space)
                {
                    _pos++;
                    if (items.Count > 0 && items[^1] != " ")
                        items.Add(" ");
                    continue;
                }

                if (t.Kind == TokenKind.Word && string.Equals(t.Value, "over", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    string numerator = PopOperand(items);
                    SkipSpaces();
                    string denominator = ParseAtom();
                    items.Add(FormatFraction(numerator, denominator));
                    continue;
                }

                if (t.Kind == TokenKind.Caret || t.Kind == TokenKind.Underscore)
                {
                    bool sup = t.Kind == TokenKind.Caret;
                    _pos++;
                    SkipSpaces();
                    string scriptVal = ParseAtom();
                    items.Add(FormatScript(scriptVal, sup));
                    continue;
                }

                if (t.Kind == TokenKind.Word && string.Equals(t.Value, "from", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    SkipSpaces();
                    items.Add(FormatScript(ParseAtom(), sup: false));
                    continue;
                }

                if (t.Kind == TokenKind.Word && string.Equals(t.Value, "to", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    SkipSpaces();
                    items.Add(FormatScript(ParseAtom(), sup: true));
                    continue;
                }

                string atom = ParseAtom();
                if (atom.Length > 0)
                    items.Add(atom);
            }

            return string.Concat(items).Trim();
        }

        private string ParseAtom()
        {
            SkipSpaces();
            if (_pos >= tokens.Count)
                return string.Empty;

            Token t = tokens[_pos++];
            switch (t.Kind)
            {
                case TokenKind.GroupOpen:
                    return ParseSequence(stopAtCloseBrace: true);

                case TokenKind.String:
                    return Encode(t.Value);

                case TokenKind.Op:
                    if (t.Value == "#")
                        return html ? "; " : "; ";
                    if (t.Value == "&")
                        return " ";
                    if (Symbols.TryGetValue(t.Value, out string? symOp))
                        return Encode(symOp);
                    return Encode(t.Value);

                case TokenKind.Word:
                    if (string.Equals(t.Value, "sqrt", StringComparison.OrdinalIgnoreCase))
                    {
                        SkipSpaces();
                        string rad = ParseAtom();
                        return FormatRoot(null, rad);
                    }

                    if (string.Equals(t.Value, "root", StringComparison.OrdinalIgnoreCase))
                    {
                        SkipSpaces();
                        string index = ParseAtom();
                        SkipSpaces();
                        if (_pos < tokens.Count && tokens[_pos].Kind == TokenKind.Word && string.Equals(tokens[_pos].Value, "of", StringComparison.OrdinalIgnoreCase))
                            _pos++;
                        SkipSpaces();
                        string rad = ParseAtom();
                        return FormatRoot(index, rad);
                    }

                    if (string.Equals(t.Value, "matrix", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Value, "pmatrix", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Value, "bmatrix", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Value, "dmatrix", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Value, "cases", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Value, "eqalign", StringComparison.OrdinalIgnoreCase))
                    {
                        SkipSpaces();
                        string inner = ParseAtom();
                        return t.Value.ToLowerInvariant() switch
                        {
                            "pmatrix" => $"({inner})",
                            "bmatrix" => $"[{inner}]",
                            "dmatrix" => $"|{inner}|",
                            "cases" => $"{{{inner}}}",
                            _ => inner,
                        };
                    }

                    if (AccentKeywords.Contains(t.Value))
                    {
                        SkipSpaces();
                        string target = ParseAtom();
                        if (html && string.Equals(t.Value, "bar", StringComparison.OrdinalIgnoreCase))
                            return $"<span style=\"text-decoration:overline\">{target}</span>";
                        if (html && string.Equals(t.Value, "under", StringComparison.OrdinalIgnoreCase))
                            return $"<span style=\"text-decoration:underline\">{target}</span>";
                        if (html && string.Equals(t.Value, "box", StringComparison.OrdinalIgnoreCase))
                            return $"<span style=\"border:1px solid currentColor;padding:0 2pt\">{target}</span>";
                        return target;
                    }

                    if (DropKeywords.Contains(t.Value))
                    {
                        // "left (" / "right )" → emit the delimiter if it's not "."
                        if (string.Equals(t.Value, "left", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Value, "right", StringComparison.OrdinalIgnoreCase))
                        {
                            SkipSpaces();
                            if (_pos < tokens.Count && tokens[_pos].Kind == TokenKind.Op)
                            {
                                string delim = tokens[_pos++].Value;
                                return delim == "." ? string.Empty : Encode(delim);
                            }
                        }
                        else if (string.Equals(t.Value, "color", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Value, "scale", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Value, "size", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Value, "font", StringComparison.OrdinalIgnoreCase))
                        {
                            SkipSpaces();
                            if (_pos < tokens.Count && tokens[_pos].Kind is TokenKind.Word or TokenKind.String or TokenKind.GroupOpen)
                                _ = ParseAtom();
                        }

                        return string.Empty;
                    }

                    if (Symbols.TryGetValue(t.Value, out string? sym))
                        return Encode(sym);

                    if (Symbols.TryGetValue(t.Value.ToLowerInvariant(), out string? symLower))
                        return Encode(symLower);

                    return Encode(t.Value);

                default:
                    return string.Empty;
            }
        }

        private string PopOperand(List<string> items)
        {
            while (items.Count > 0 && items[^1] == " ")
                items.RemoveAt(items.Count - 1);

            if (items.Count == 0)
                return string.Empty;

            // If previous token was a grouped/formatted expression, take just that; otherwise collect contiguous term.
            string last = items[^1];
            items.RemoveAt(items.Count - 1);
            return last.Trim();
        }

        private void SkipSpaces()
        {
            while (_pos < tokens.Count && tokens[_pos].Kind == TokenKind.Space)
                _pos++;
        }

        private string FormatFraction(string num, string den)
        {
            if (html)
                return $"<span class=\"eq-frac\"><span class=\"eq-num\">{num}</span><span class=\"eq-den\">{den}</span></span>";

            string n = num.Contains(' ') ? $"({num})" : num;
            string d = den.Contains(' ') ? $"({den})" : den;
            return $"{n}/{d}";
        }

        private string FormatRoot(string? index, string radicand)
        {
            if (html)
            {
                string sup = string.IsNullOrEmpty(index) ? string.Empty : $"<sup class=\"eq-root-idx\">{index}</sup>";
                return $"{sup}√<span class=\"eq-rad\">{radicand}</span>";
            }

            string prefix = string.IsNullOrEmpty(index) ? string.Empty : ToUnicodeScript(index, Superscripts, "^");
            string body = radicand.Length > 1 ? $"({radicand})" : radicand;
            return $"{prefix}√{body}";
        }

        private string FormatScript(string value, bool sup)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (html)
                return sup ? $"<sup>{value}</sup>" : $"<sub>{value}</sub>";

            return ToUnicodeScript(value, sup ? Superscripts : Subscripts, sup ? "^" : "_");
        }

        private static string ToUnicodeScript(string value, Dictionary<char, char> map, string fallbackMark)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0)
                return string.Empty;

            var sb = new StringBuilder(trimmed.Length);
            foreach (char c in trimmed)
            {
                if (!map.TryGetValue(c, out char mapped))
                    return trimmed.Length > 1 ? $"{fallbackMark}({trimmed})" : $"{fallbackMark}{trimmed}";

                sb.Append(mapped);
            }

            return sb.ToString();
        }

        private string Encode(string value) => html ? WebUtility.HtmlEncode(value) : value;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    private static string CollapseSpaces(string text) => MultiSpaceRegex().Replace(text, " ").Trim();
}

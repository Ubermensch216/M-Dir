using System.Globalization;
using MDir.Settings;

namespace MDir.Features;

/// <summary>One colour rule. Colours of null are "not set": a later rule may supply them.</summary>
internal sealed record ColorRule(string Pattern, bool Enabled = true, Color? Text = null, Color? Background = null, bool Bold = false)
{
    /// <summary>List background of inactive panes; the rule's text colour field holds the colour (Q-Dir's convention).</summary>
    public const string InactiveBackgroundKey = "#BG";

    /// <summary>List background of the active pane.</summary>
    public const string ActiveBackgroundKey = "#BG-A";

    public const string FolderKey = "#DIR";
    public const string HiddenKey = "#HIDDEN";

    public bool IsBackgroundRule => IsKey(InactiveBackgroundKey) || IsKey(ActiveBackgroundKey);

    private bool IsKey(string key) => string.Equals(Pattern.Trim(), key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a rule set decided for one item.</summary>
internal readonly record struct ColorStyle(Color? Text, Color? Background, bool Bold)
{
    public bool IsEmpty => Text is null && Background is null && !Bold;
}

/// <summary>
/// Ordered colour rules (Q-Dir's "Filter2" table). For each item the rules are tried in order and every
/// attribute (text colour, background, bold) comes from the first matching rule that sets it, so a specific rule
/// above a catch-all <c>*</c> wins. Patterns are <c>;</c>-separated wildcards plus <c>#DIR</c> (folders) and
/// <c>#Hidden</c> (hidden items).
/// </summary>
internal sealed class ColorRuleSet
{
    private const string Section = "ColorFilter";
    private const string QDirSection = "Filter2";

    private readonly CompiledRule[] _compiled;

    public ColorRuleSet(IEnumerable<ColorRule> rules)
    {
        Rules = [.. rules];
        _compiled = [.. Rules.Where(rule => rule.Enabled && !rule.IsBackgroundRule && !(rule.Text is null && rule.Background is null && !rule.Bold))
            .Select(CompiledRule.Create)];
        InactiveBackground = Rules.FirstOrDefault(rule => rule.Enabled && string.Equals(rule.Pattern.Trim(), ColorRule.InactiveBackgroundKey, StringComparison.OrdinalIgnoreCase))?.Text;
        ActiveBackground = Rules.FirstOrDefault(rule => rule.Enabled && string.Equals(rule.Pattern.Trim(), ColorRule.ActiveBackgroundKey, StringComparison.OrdinalIgnoreCase))?.Text;
    }

    public IReadOnlyList<ColorRule> Rules { get; }

    public Color? InactiveBackground { get; }

    public Color? ActiveBackground { get; }

    /// <summary>Q-Dir's default colour scheme (without its catch-all <c>*</c> rule, so ordinary files keep the system colour).</summary>
    public static ColorRuleSet CreateDefault() => new(
    [
        new ColorRule("#Hidden", Text: Rgb(0x808080)),
        new ColorRule("*.zip;*.rar;*.gz;*.7z", Text: Rgb(0x00AA00)),
        new ColorRule("*.mp*;*.avi;*.wma", Text: Rgb(0xDD0058)),
        new ColorRule("#DIR", Text: Rgb(0x008800)),
        new ColorRule("*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.ico", Text: Rgb(0xBB00BB)),
        new ColorRule("*.html;*.htm;*.url", Text: Rgb(0x456789)),
        new ColorRule("*.pl;*.cgi;*.php;*.pdf;*.doc;*.rtf;*.xls", Text: Rgb(0xBB8844)),
        new ColorRule("*.cpp;*.hpp;*.h", Text: Rgb(0xDD0058)),
        new ColorRule("*.exe;*.dll;*.bat", Text: Rgb(0xFF0000)),
        new ColorRule(ColorRule.InactiveBackgroundKey, Enabled: false),
        new ColorRule(ColorRule.ActiveBackgroundKey, Enabled: false),
    ]);

    /// <summary>The style for an item, or null when no rule applies.</summary>
    public ColorStyle? Resolve(string name, bool isFolder, bool isHidden)
    {
        Color? text = null, background = null;
        bool bold = false;
        foreach (CompiledRule rule in _compiled)
        {
            if (!rule.Matches(name, isFolder, isHidden))
                continue;

            text ??= rule.Source.Text;
            background ??= rule.Source.Background;
            bold |= rule.Source.Bold;
            if (text is not null && background is not null && bold)
                break;
        }

        var style = new ColorStyle(text, background, bold);
        return style.IsEmpty ? null : style;
    }

    /// <summary>Reads the rules from <c>[ColorFilter]</c>; null when the file has none (the caller uses the defaults).</summary>
    public static ColorRuleSet? Read(IniFile ini)
    {
        int count = ini.GetInt(Section, "Count", -1);
        if (count < 0)
            return null;

        var rules = new List<ColorRule>();
        for (int i = 1; i <= Math.Min(count, 500); i++)
        {
            if (ParseOwnRule(ini.Get(Section, $"Rule{i}")) is { } rule)
                rules.Add(rule);
        }

        return new ColorRuleSet(rules);
    }

    public void Write(IniFile ini)
    {
        ini.Set(Section, "Count", Rules.Count);
        for (int i = 0; i < Rules.Count; i++)
        {
            ColorRule rule = Rules[i];
            ini.Set(Section, $"Rule{i + 1}",
                $"{rule.Pattern}={(rule.Enabled ? 1 : 0)}={FormatColor(rule.Text)}={FormatColor(rule.Background)}={(rule.Bold ? 1 : 0)}");
        }
    }

    /// <summary>
    /// Reads Q-Dir's <c>[Filter2]</c> table (<c>N=pattern=enabled=RRGGBB=...</c>). Only the pattern, the enabled flag
    /// and the text colour are taken; Q-Dir's remaining fields are not understood well enough to map.
    /// </summary>
    public static ColorRuleSet? ImportQDir(IniFile ini)
    {
        if (!ini.HasSection(QDirSection))
            return null;

        var rules = new List<ColorRule>();
        for (int i = 0; i < 500 && ini.Get(QDirSection, i.ToString(CultureInfo.InvariantCulture)) is { } value; i++)
        {
            string[] fields = value.Split('=');
            if (fields.Length < 3)
                continue;

            rules.Add(new ColorRule(fields[0], Enabled: fields[1].Trim() != "0", Text: ParseColor(fields[2])));
        }

        return rules.Count == 0 ? null : new ColorRuleSet(rules);
    }

    /// <summary>The rules as a Q-Dir <c>[Filter2]</c> table (background colours and bold are not representable and are dropped).</summary>
    public IniFile ExportQDir()
    {
        var ini = new IniFile();
        for (int i = 0; i < Rules.Count; i++)
        {
            ColorRule rule = Rules[i];
            ini.Set(QDirSection, i.ToString(CultureInfo.InvariantCulture), $"{rule.Pattern}={(rule.Enabled ? 1 : 0)}={FormatColor(rule.Text)}=-1=1=-1=0");
        }

        return ini;
    }

    public static string FormatColor(Color? color) => color is { } c ? $"{c.R:X2}{c.G:X2}{c.B:X2}" : "-1";

    /// <summary>Parses <c>RRGGBB</c>; "-1", empty or malformed means "not set".</summary>
    public static Color? ParseColor(string? text)
    {
        text = text?.Trim();
        if (text is not { Length: 6 } || !int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            return null;

        return Rgb(rgb);
    }

    private static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static ColorRule? ParseOwnRule(string? value)
    {
        // pattern=enabled=text=background=bold; split from the right so a '=' inside the pattern survives.
        string[] fields = value?.Split('=') ?? [];
        if (fields.Length < 5)
            return null;

        string pattern = string.Join('=', fields[..^4]);
        return new ColorRule(pattern, fields[^4].Trim() != "0", ParseColor(fields[^3]), ParseColor(fields[^2]), fields[^1].Trim() == "1");
    }

    private sealed class CompiledRule
    {
        private readonly string[] _wildcards;
        private readonly bool _folders;
        private readonly bool _hidden;

        private CompiledRule(ColorRule source, string[] wildcards, bool folders, bool hidden)
        {
            Source = source;
            _wildcards = wildcards;
            _folders = folders;
            _hidden = hidden;
        }

        public ColorRule Source { get; }

        public static CompiledRule Create(ColorRule rule)
        {
            string[] tokens = WildcardMatcher.SplitPatterns(rule.Pattern);
            return new CompiledRule(
                rule,
                [.. tokens.Where(token => !IsKeyword(token))],
                tokens.Any(token => string.Equals(token, ColorRule.FolderKey, StringComparison.OrdinalIgnoreCase)),
                tokens.Any(token => string.Equals(token, ColorRule.HiddenKey, StringComparison.OrdinalIgnoreCase)));
        }

        public bool Matches(string name, bool isFolder, bool isHidden)
        {
            if ((_folders && isFolder) || (_hidden && isHidden))
                return true;

            foreach (string wildcard in _wildcards)
            {
                if (WildcardMatcher.IsMatch(wildcard, name))
                    return true;
            }

            return false;
        }

        private static bool IsKeyword(string token) =>
            token.StartsWith('#') && (string.Equals(token, ColorRule.FolderKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, ColorRule.HiddenKey, StringComparison.OrdinalIgnoreCase));
    }
}

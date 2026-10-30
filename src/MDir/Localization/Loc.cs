using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MDir.Localization;

/// <summary>
/// Texts of the user interface in more than one language. The program's own texts are Korean and are used as the
/// keys: <see cref="T"/> returns the translation for the current language, or the Korean text itself when there is
/// none. English is built in (<see cref="EnglishStrings"/>); more languages can be added as files
/// (<c>Lang\fr.lng</c> next to the program or in the data folder): one <c>Korean text TAB translation</c> per line,
/// <c>\n</c> for a line break, <c>#</c> starts a comment.
/// </summary>
internal static class Loc
{
    public const string Korean = "ko";
    public const string English = "en";
    public const string PackExtension = ".lng";

    private static readonly ConditionalWeakTable<object, Remembered> TextKeys = new();
    private static readonly ConditionalWeakTable<object, Remembered> ToolTipKeys = new();
    private static IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    /// <summary>The language code in use ("ko", "en", or the name of a language file).</summary>
    public static string Language { get; private set; } = Korean;

    /// <summary>Raised after <see cref="Set"/> changed the language.</summary>
    public static event EventHandler? Changed;

    /// <summary>The translation of <paramref name="korean"/>, or the text itself.</summary>
    public static string T(string korean) => _table.TryGetValue(korean, out string? text) ? text : korean;

    /// <summary><see cref="T"/> followed by <see cref="string.Format(string, object[])"/>; the key holds <c>{0}</c> style placeholders.</summary>
    public static string F(string koreanFormat, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(koreanFormat), args);

    /// <summary>
    /// The languages that can be chosen: Korean, English and every pack file found in <paramref name="directories"/>
    /// (the file name without its extension is the code).
    /// </summary>
    public static IReadOnlyList<string> Available(params string?[] directories)
    {
        var codes = new List<string> { Korean, English };
        foreach (string? directory in directories)
        {
            if (directory is null || !Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*" + PackExtension))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                if (code.Length > 0 && !codes.Contains(code, StringComparer.OrdinalIgnoreCase))
                    codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>
    /// Switches to <paramref name="code"/>. An unknown code, or a language file that cannot be read, keeps Korean.
    /// Returns the language now in use.
    /// </summary>
    public static string Set(string code, params string?[] packDirectories)
    {
        IReadOnlyDictionary<string, string>? table = null;
        string language = Korean;
        if (string.Equals(code, English, StringComparison.OrdinalIgnoreCase))
        {
            table = EnglishStrings.Table;
            language = English;
        }
        else if (!string.Equals(code, Korean, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string? directory in packDirectories)
            {
                string? file = directory is null ? null : Path.Combine(directory, code + PackExtension);
                if (file is not null && File.Exists(file) && ReadPack(file) is { } pack)
                {
                    // A pack only has to hold what it translates; the rest falls back to English, then Korean.
                    var merged = new Dictionary<string, string>(EnglishStrings.Table, StringComparer.Ordinal);
                    foreach ((string key, string value) in pack)
                        merged[key] = value;

                    table = merged;
                    language = code;
                    break;
                }
            }
        }

        _table = table ?? new Dictionary<string, string>();
        bool changed = !string.Equals(Language, language, StringComparison.Ordinal);
        Language = language;
        if (changed)
            Changed?.Invoke(null, EventArgs.Empty);

        return language;
    }

    /// <summary>Reads a language file; null when it cannot be read.</summary>
    public static Dictionary<string, string>? ReadPack(string path)
    {
        try
        {
            var pack = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadLines(path, Encoding.UTF8))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#')
                    continue;

                int tab = line.IndexOf('\t');
                if (tab <= 0)
                    continue;

                pack[Unescape(line[..tab])] = Unescape(line[(tab + 1)..]);
            }

            return pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The texts of a language file for the given table, in the file's own format (for writing a new translation).</summary>
    public static string WritePack(IEnumerable<KeyValuePair<string, string>> entries) =>
        "# M-Dir language pack: Korean text <TAB> translation. \\n is a line break.\r\n"
        + string.Concat(entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{Escape(entry.Key)}\t{Escape(entry.Value)}\r\n"));

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r", string.Empty).Replace("\n", "\\n").Replace("\t", "\\t");

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                char next = text[++i];
                result.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    // ---- controls -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Translates the texts in a control tree: forms, labels, buttons, check boxes, group boxes, list column headers
    /// and the items of menus, tool bars and status bars (text and tool tip). The first time an item is seen its Korean
    /// text is remembered, so applying again after a language change translates from the original, in both directions.
    /// Text boxes, combo boxes and lists keep what the user sees in them.
    /// </summary>
    public static void Apply(Control control)
    {
        if (control is Form or Label or ButtonBase or GroupBox)
            control.Text = Translate(control, TextKeys, control.Text);

        if (control is ListView list)
        {
            foreach (ColumnHeader column in list.Columns)
                column.Text = Translate(column, TextKeys, column.Text);
        }

        if (control is ToolStrip strip)
            Apply(strip.Items);
        if (control.ContextMenuStrip is { } menu)
            Apply(menu.Items);

        foreach (Control child in control.Controls)
            Apply(child);
    }

    /// <summary>Translates menu or tool bar items and everything under them.</summary>
    public static void Apply(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            // A menu item built from a command keeps its text in sync itself; only plain items are translated here.
            if (item is not ToolStripSeparator && item is not ToolStripTextBox && item.Tag is not MDir.Commands.Command)
            {
                if (!string.IsNullOrEmpty(item.Text))
                    item.Text = Translate(item, TextKeys, item.Text);
            }

            if (!string.IsNullOrEmpty(item.ToolTipText) && item is not ToolStripSeparator)
                item.ToolTipText = Translate(item, ToolTipKeys, item.ToolTipText);

            if (item is ToolStripDropDownItem { HasDropDownItems: true } dropDown)
                Apply(dropDown.DropDownItems);
        }
    }

    private static string Translate(object owner, ConditionalWeakTable<object, Remembered> keys, string current)
    {
        // The remembered original is only valid while the text is still the one written from it; any other text was
        // set by the program since and is the new original.
        if (!keys.TryGetValue(owner, out Remembered? entry) || entry.Written != current)
        {
            entry = new Remembered(current);
            keys.AddOrUpdate(owner, entry);
        }

        entry.Written = T(entry.Original);
        return entry.Written;
    }

    private sealed class Remembered(string original)
    {
        public string Original { get; } = original;

        public string Written { get; set; } = original;
    }
}

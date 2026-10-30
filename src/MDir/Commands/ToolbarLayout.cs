namespace MDir.Commands;

/// <summary>
/// The toolbar as a list of tokens: a command id, <see cref="Separator"/> or <see cref="Layouts"/> (the pane layout
/// drop-down). The list is what the toolbar editor changes and what the settings file keeps.
/// </summary>
internal static class ToolbarLayout
{
    public const string Separator = "-";

    public const string Layouts = "@layouts";

    public static IReadOnlyList<string> Default { get; } =
    [
        "go.back", "go.forward", "go.up", "view.refresh", Separator,
        "tab.new", "file.newFolder", Separator,
        "edit.cut", "edit.copy", "edit.paste", "edit.delete", Separator,
        "view.tree", "view.filter", "view.preview", Layouts,
    ];

    /// <summary>
    /// The tokens to build from: <paramref name="saved"/> without unknown commands, repeated commands and
    /// separators that stand first, last or twice in a row. Nothing left gives the default toolbar.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> saved, Func<string, bool> isKnownCommand)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in saved)
        {
            if (token == Separator)
            {
                if (result.Count > 0 && result[^1] != Separator)
                    result.Add(token);
            }
            else if ((token == Layouts || isKnownCommand(token)) && seen.Add(token))
            {
                result.Add(token);
            }
        }

        while (result.Count > 0 && result[^1] == Separator)
            result.RemoveAt(result.Count - 1);

        return result.Count > 0 ? result : Default;
    }

    /// <summary>What to store in the settings: nothing when the toolbar is the default one.</summary>
    public static IReadOnlyList<string> ToSettings(IReadOnlyList<string> tokens) =>
        tokens.SequenceEqual(Default) ? [] : tokens;
}

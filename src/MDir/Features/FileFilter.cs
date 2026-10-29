namespace MDir.Features;

/// <summary>Case-insensitive <c>*</c> / <c>?</c> matching, as in file name filters.</summary>
internal static class WildcardMatcher
{
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharsEqual(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                // Let the last '*' swallow one more character and retry.
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    /// <summary>Splits a pattern list on <c>;</c> and <c>,</c>, trimming blanks and dropping empty entries.</summary>
    public static string[] SplitPatterns(string patterns) =>
        patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool CharsEqual(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

/// <summary>
/// A pane's file name filter, with Q-Dir's options: a plain word matches anywhere (<c>*word*</c>) when it is the
/// only pattern, the result can be inverted ("exclude" filter such as <c>Thumbs.db;*.bak</c>), and folders are
/// kept visible unless <see cref="FilterFolders"/> is on.
/// </summary>
internal sealed record FileFilter(string Pattern, bool AutoWildcard = true, bool Negative = false, bool FilterFolders = false)
{
    public static FileFilter None { get; } = new(string.Empty);

    public bool IsActive => WildcardMatcher.SplitPatterns(Pattern).Length > 0;

    /// <summary>A predicate (file name, is a folder) → keep the item; null when the filter is empty.</summary>
    public Func<string, bool, bool>? Compile()
    {
        string[] patterns = WildcardMatcher.SplitPatterns(Pattern);
        if (patterns.Length == 0)
            return null;

        if (AutoWildcard && patterns.Length == 1 && patterns[0].AsSpan().IndexOfAny('*', '?') < 0)
            patterns[0] = $"*{patterns[0]}*";

        bool negative = Negative;
        bool filterFolders = FilterFolders;
        return (name, isFolder) =>
        {
            if (isFolder && !filterFolders)
                return true;

            foreach (string pattern in patterns)
            {
                if (WildcardMatcher.IsMatch(pattern, name))
                    return !negative;
            }

            return negative;
        };
    }
}

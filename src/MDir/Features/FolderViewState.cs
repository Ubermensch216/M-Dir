using System.Globalization;

namespace MDir.Features;

/// <summary>A shell property (PROPERTYKEY): the format id and the property number, e.g. System.Size.</summary>
internal readonly record struct ViewKey(Guid Format, uint Id)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Format:D}:{Id}");

    public static bool TryParse(string text, out ViewKey key)
    {
        key = default;
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || !Guid.TryParse(text.AsSpan(0, colon), out Guid format)
            || !uint.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            return false;

        key = new ViewKey(format, id);
        return true;
    }
}

/// <summary>A shown column: which property, and how wide in pixels.</summary>
internal sealed record ColumnState(ViewKey Key, int Width);

/// <summary>One sort criterion; <see cref="Direction"/> is 1 (ascending) or -1 (descending).</summary>
internal sealed record SortState(ViewKey Key, int Direction);

/// <summary>
/// The columns (in order, with widths) and the sort order of a file list: what the user changed in a details view.
/// Written as <c>c=key@width;key@width|s=key@dir</c> so it fits a line of an INI file.
/// </summary>
internal sealed class FolderViewState : IEquatable<FolderViewState>
{
    public const int MaxColumns = 64;
    public const int MaxSortColumns = 8;
    public const int MaxWidth = 5000;

    public FolderViewState(IReadOnlyList<ColumnState> columns, IReadOnlyList<SortState> sort)
    {
        Columns = columns;
        Sort = sort;
    }

    public IReadOnlyList<ColumnState> Columns { get; }

    public IReadOnlyList<SortState> Sort { get; }

    public string Serialize() =>
        "c=" + string.Join(';', Columns.Select(column => string.Create(CultureInfo.InvariantCulture, $"{column.Key}@{column.Width}")))
        + "|s=" + string.Join(';', Sort.Select(sort => string.Create(CultureInfo.InvariantCulture, $"{sort.Key}@{sort.Direction}")));

    /// <summary>Reads <see cref="Serialize"/> output; null when the text is damaged. Bad parts are not guessed at.</summary>
    public static FolderViewState? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] halves = text.Split('|');
        if (halves.Length != 2 || !halves[0].StartsWith("c=", StringComparison.Ordinal) || !halves[1].StartsWith("s=", StringComparison.Ordinal))
            return null;

        var columns = new List<ColumnState>();
        foreach (string part in halves[0][2..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryPair(part, out ViewKey key, out int width) || width < 0 || width > MaxWidth || columns.Count >= MaxColumns)
                return null;

            columns.Add(new ColumnState(key, width));
        }

        var sort = new List<SortState>();
        foreach (string part in halves[1][2..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryPair(part, out ViewKey key, out int direction) || direction is not (1 or -1) || sort.Count >= MaxSortColumns)
                return null;

            sort.Add(new SortState(key, direction));
        }

        return columns.Count == 0 && sort.Count == 0 ? null : new FolderViewState(columns, sort);
    }

    private static bool TryPair(string text, out ViewKey key, out int value)
    {
        key = default;
        value = 0;
        int at = text.LastIndexOf('@');
        return at > 0 && ViewKey.TryParse(text[..at], out key) && int.TryParse(text.AsSpan(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool Equals(FolderViewState? other) =>
        other is not null && Columns.SequenceEqual(other.Columns) && Sort.SequenceEqual(other.Sort);

    public override bool Equals(object? obj) => Equals(obj as FolderViewState);

    public override int GetHashCode() => HashCode.Combine(Columns.Count, Sort.Count, Columns.FirstOrDefault(), Sort.FirstOrDefault());
}

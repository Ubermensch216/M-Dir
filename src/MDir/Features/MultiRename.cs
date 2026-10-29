using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MDir.Features;

public enum CaseMode
{
    None,
    Lower,
    Upper,

    /// <summary>First letter of every word upper case.</summary>
    Title,

    /// <summary>Only the first letter upper case.</summary>
    Sentence,
}

/// <summary>How selected files are renamed. The masks build the new name from the old one.</summary>
/// <param name="NameMask">
/// New name without extension. Tokens: <c>[N]</c> old name, <c>[E]</c> old extension, <c>[C]</c> counter
/// (<c>[C:3]</c> = at least 3 digits), <c>[D]</c> modified date (see <paramref name="DateFormat"/>), <c>[P]</c> parent
/// folder name, <c>[[</c> a literal '['.
/// </param>
/// <param name="ExtensionMask">New extension (without the dot); same tokens. Ignored for folders.</param>
internal sealed record RenameOptions(
    string NameMask = "[N]",
    string ExtensionMask = "[E]",
    int CounterStart = 1,
    int CounterStep = 1,
    int CounterDigits = 1,
    string DateFormat = "yyyyMMdd",
    string Find = "",
    string Replace = "",
    bool IgnoreCase = true,
    bool UseRegex = false,
    CaseMode NameCase = CaseMode.None,
    CaseMode ExtensionCase = CaseMode.None);

public enum RenameStatus
{
    /// <summary>Will be renamed.</summary>
    Ready,

    /// <summary>The new name equals the old one.</summary>
    Unchanged,

    /// <summary>The new name is empty or not allowed by Windows.</summary>
    InvalidName,

    /// <summary>Two items would get the same new name.</summary>
    DuplicateName,

    /// <summary>A different, existing file or folder already has the new name.</summary>
    TargetExists,
}

internal sealed record RenamePlanItem(string Source, string NewName, RenameStatus Status)
{
    public string Target => Path.Combine(Path.GetDirectoryName(Source) ?? string.Empty, NewName);

    public bool WillRename => Status == RenameStatus.Ready;
}

internal sealed record RenameFailure(string Source, string Error);

/// <summary>What happened when a plan was applied. <see cref="Applied"/> pairs (from, to) can be reversed with <see cref="RenameExecutor.Undo"/>.</summary>
internal sealed record RenameResult(IReadOnlyList<(string From, string To)> Applied, IReadOnlyList<RenameFailure> Failed);

internal static partial class RenamePlanner
{
    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Works out the new name of every item (in the given order, which is also the counter order) and marks the
    /// ones that cannot be renamed safely. Nothing on disk changes.
    /// </summary>
    public static IReadOnlyList<RenamePlanItem> Plan(IReadOnlyList<string> paths, RenameOptions options)
    {
        var names = new List<(string Source, string NewName)>(paths.Count);
        Regex? regex = options.UseRegex && options.Find.Length > 0 ? TryRegex(options) : null;
        for (int i = 0; i < paths.Count; i++)
            names.Add((paths[i], NewNameFor(paths[i], i, options, regex)));

        // Names that more than one item would get (per folder, ignoring case).
        var counts = names.GroupBy(item => (Path.GetDirectoryName(item.Source) ?? string.Empty).ToUpperInvariant() + "\\" + item.NewName.ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.Count());

        // A name is free for another item only when its owner really moves away (not when it stays or cannot be renamed).
        var moving = new HashSet<string>(
            names.Where(item => IsValidName(item.NewName)
                    && !string.Equals(Path.GetFileName(item.Source), item.NewName, StringComparison.Ordinal)
                    && counts[(Path.GetDirectoryName(item.Source) ?? string.Empty).ToUpperInvariant() + "\\" + item.NewName.ToUpperInvariant()] == 1)
                .Select(item => NormalizePath(item.Source)),
            StringComparer.OrdinalIgnoreCase);

        var plan = new List<RenamePlanItem>(paths.Count);
        foreach ((string source, string newName) in names)
        {
            RenameStatus status;
            string oldName = Path.GetFileName(source);
            string target = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, newName);
            if (!IsValidName(newName))
                status = RenameStatus.InvalidName;
            else if (string.Equals(oldName, newName, StringComparison.Ordinal))
                status = RenameStatus.Unchanged;
            else if (counts[(Path.GetDirectoryName(source) ?? string.Empty).ToUpperInvariant() + "\\" + newName.ToUpperInvariant()] > 1)
                status = RenameStatus.DuplicateName;
            else if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
                && (File.Exists(target) || Directory.Exists(target))
                && !moving.Contains(NormalizePath(target)))
                status = RenameStatus.TargetExists; // an item that is itself being renamed away does not block the name
            else
                status = RenameStatus.Ready;

            plan.Add(new RenamePlanItem(source, newName, status));
        }

        return plan;
    }

    /// <summary>The new name for one item, before any validity checks.</summary>
    internal static string NewNameFor(string path, int index, RenameOptions options, Regex? regex = null)
    {
        regex ??= options.UseRegex && options.Find.Length > 0 ? TryRegex(options) : null;
        bool isFolder = Directory.Exists(path);
        string fileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        string extension = isFolder ? string.Empty : Path.GetExtension(fileName).TrimStart('.');
        string stem = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);
        DateTime modified = TryGetModified(path, isFolder);
        string parent = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))) ?? string.Empty;

        string Expand(string mask) => ExpandMask(mask, stem, extension, index, options, modified, parent);

        string name = Expand(options.NameMask);
        if (options.Find.Length > 0)
            name = regex is not null ? regex.Replace(name, options.Replace) : options.UseRegex ? name : ReplaceText(name, options);
        name = ApplyCase(name, options.NameCase);

        if (isFolder)
            return name;

        string newExtension = ApplyCase(Expand(options.ExtensionMask), options.ExtensionCase);
        return newExtension.Length > 0 ? $"{name}.{newExtension}" : name;
    }

    private static string ExpandMask(string mask, string stem, string extension, int index, RenameOptions options, DateTime modified, string parent)
    {
        var result = new StringBuilder();
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] != '[')
            {
                result.Append(mask[i]);
                continue;
            }

            if (i + 1 < mask.Length && mask[i + 1] == '[')
            {
                result.Append('[');
                i++;
                continue;
            }

            int end = mask.IndexOf(']', i);
            if (end < 0)
            {
                result.Append(mask, i, mask.Length - i);
                break;
            }

            string token = mask[(i + 1)..end];
            string? value = ExpandToken(token, stem, extension, index, options, modified, parent);
            result.Append(value ?? mask.Substring(i, end - i + 1));
            i = end;
        }

        return result.ToString();
    }

    private static string? ExpandToken(string token, string stem, string extension, int index, RenameOptions options, DateTime modified, string parent)
    {
        string kind = token.Split(':')[0].ToUpperInvariant();
        string? argument = token.Contains(':') ? token[(token.IndexOf(':') + 1)..] : null;
        switch (kind)
        {
            case "N":
                return stem;
            case "E":
                return extension;
            case "P":
                return parent;
            case "C":
                int digits = argument is not null && int.TryParse(argument, out int custom) ? Math.Clamp(custom, 1, 18) : Math.Clamp(options.CounterDigits, 1, 18);
                long value = options.CounterStart + (long)index * options.CounterStep;
                return value.ToString($"D{digits}", CultureInfo.InvariantCulture);
            case "D":
                try
                {
                    return modified.ToString(string.IsNullOrEmpty(argument) ? options.DateFormat : argument, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    return null;
                }

            default:
                return null;
        }
    }

    private static string ReplaceText(string text, RenameOptions options) =>
        text.Replace(options.Find, options.Replace, options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static Regex? TryRegex(RenameOptions options)
    {
        try
        {
            return new Regex(options.Find, options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static string ApplyCase(string text, CaseMode mode) => mode switch
    {
        CaseMode.Lower => text.ToLowerInvariant(),
        CaseMode.Upper => text.ToUpperInvariant(),
        CaseMode.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
        CaseMode.Sentence => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant(),
        _ => text,
    };

    /// <summary>A name Windows accepts for a file or folder.</summary>
    internal static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith(' ') || name.EndsWith('.')
            || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        string stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        return !ReservedNames.Contains(stem.TrimEnd(), StringComparer.OrdinalIgnoreCase);
    }

    private static DateTime TryGetModified(string path, bool isFolder)
    {
        try
        {
            return isFolder ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>Applies a rename plan to the disk.</summary>
internal static class RenameExecutor
{
    /// <summary>
    /// Renames every <see cref="RenameStatus.Ready"/> item. Names that are free only once another item has been
    /// renamed (a swap, or a shifted series) are handled by moving those items aside first, so no item is ever
    /// overwritten. A failure affects only that item; the rest still run.
    /// </summary>
    public static RenameResult Apply(IReadOnlyList<RenamePlanItem> plan)
    {
        RenamePlanItem[] work = [.. plan.Where(item => item.WillRename)];
        var applied = new List<(string From, string To)>();
        var failed = new List<RenameFailure>();

        // Items whose target is another item's current name must wait until that item has moved: park them first.
        var sources = new HashSet<string>(work.Select(item => Normalize(item.Source)), StringComparer.OrdinalIgnoreCase);
        var parked = new Dictionary<RenamePlanItem, string>();
        foreach (RenamePlanItem item in work)
        {
            if (sources.Contains(Normalize(item.Target)))
            {
                string temp = Path.Combine(Path.GetDirectoryName(item.Source)!, $".mdir-rename-{Guid.NewGuid():N}");
                if (TryMove(item.Source, temp, failed, item.Source))
                    parked[item] = temp;
            }
        }

        // Items that did not need parking go first: their old names are what the parked ones are waiting for.
        foreach (RenamePlanItem item in work.OrderBy(item => parked.ContainsKey(item) ? 1 : 0))
        {
            string from = parked.TryGetValue(item, out string? temp) ? temp : item.Source;
            if (!parked.ContainsKey(item) && sources.Contains(Normalize(item.Target)))
                continue; // it could not be parked (already reported)

            if (TryMove(from, item.Target, failed, item.Source))
            {
                applied.Add((item.Source, item.Target));
            }
            else if (from != item.Source)
            {
                // Put it back under its old name rather than leave a temporary name behind.
                TryMove(from, item.Source, [], item.Source);
            }
        }

        return new RenameResult(applied, failed);
    }

    /// <summary>Reverses the renames of a previous result (in reverse order).</summary>
    public static RenameResult Undo(RenameResult result)
    {
        IReadOnlyList<RenamePlanItem> plan =
        [
            .. result.Applied.Reverse().Select(pair => new RenamePlanItem(pair.To, Path.GetFileName(pair.From), RenameStatus.Ready)),
        ];
        return Apply(plan);
    }

    private static bool TryMove(string from, string to, List<RenameFailure> failed, string reportAs)
    {
        try
        {
            if (Directory.Exists(from))
                Directory.Move(from, to);
            else
                File.Move(from, to);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(new RenameFailure(reportAs, ex.Message));
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

namespace MDir.Features;

public enum CompareStatus
{
    Same,
    OnlyLeft,
    OnlyRight,
    LeftNewer,
    RightNewer,

    /// <summary>Same modification time but different size (or, with content checking, different content).</summary>
    Different,

    /// <summary>A file on one side, a folder on the other.</summary>
    TypeMismatch,
}

public enum CompareMode
{
    /// <summary>Equal when size and modification time match (within <see cref="FolderComparer.TimeTolerance"/>).</summary>
    Quick,

    /// <summary>Files of the same size are compared byte for byte (SHA-256), whatever their times say. Slower: every such file is read.</summary>
    Content,
}

/// <summary>One difference (or, with <c>IncludeSame</c>, one equal item) between two folders.</summary>
/// <param name="RelativePath">Path below the compared folders, with '\' separators.</param>
internal sealed record CompareEntry(string RelativePath, CompareStatus Status, FileSystemInfo? Left, FileSystemInfo? Right)
{
    public bool IsFolder => (Left ?? Right) is DirectoryInfo;

    public long? LeftSize => (Left as FileInfo)?.Length;

    public long? RightSize => (Right as FileInfo)?.Length;
}

internal sealed record CompareOptions(bool Recursive = true, CompareMode Mode = CompareMode.Quick, bool IncludeSame = false);

internal sealed record CompareResult(IReadOnlyList<CompareEntry> Entries, IReadOnlyList<string> Errors)
{
    public int Count(CompareStatus status) => Entries.Count(entry => entry.Status == status);
}

/// <summary>Compares two folders by name, size, modification time and (optionally) content.</summary>
internal static class FolderComparer
{
    /// <summary>FAT stores times with 2 second resolution, so times closer than this count as equal.</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    private static readonly EnumerationOptions Enumeration = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>
    /// Compares <paramref name="left"/> with <paramref name="right"/>. A folder that exists on one side only is one
    /// entry (its contents are not listed), because copying it copies everything below. Reparse points (junctions,
    /// symbolic links) are not followed, so a link cannot make the walk loop.
    /// </summary>
    public static async Task<CompareResult> CompareAsync(string left, string right, CompareOptions options, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var entries = new List<CompareEntry>();
        var errors = new List<string>();
        await Task.Run(async () => await CompareFolderAsync(new DirectoryInfo(left), new DirectoryInfo(right), string.Empty, options, entries, errors, progress, cancellation), cancellation).ConfigureAwait(false);
        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return new CompareResult(entries, errors);
    }

    private static async Task CompareFolderAsync(DirectoryInfo left, DirectoryInfo right, string prefix, CompareOptions options,
        List<CompareEntry> entries, List<string> errors, IProgress<string>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(string.IsNullOrEmpty(prefix) ? left.FullName : Path.Combine(left.FullName, prefix));
        Dictionary<string, FileSystemInfo> leftItems = List(left, errors);
        Dictionary<string, FileSystemInfo> rightItems = List(right, errors);

        foreach (string name in leftItems.Keys.Union(rightItems.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            leftItems.TryGetValue(name, out FileSystemInfo? l);
            rightItems.TryGetValue(name, out FileSystemInfo? r);
            string relative = prefix.Length == 0 ? name : prefix + "\\" + name;

            if (l is null || r is null)
            {
                entries.Add(new CompareEntry(relative, l is null ? CompareStatus.OnlyRight : CompareStatus.OnlyLeft, l, r));
            }
            else if (l is DirectoryInfo ld && r is DirectoryInfo rd)
            {
                if (options.Recursive)
                    await CompareFolderAsync(ld, rd, relative, options, entries, errors, progress, cancellation).ConfigureAwait(false);
            }
            else if (l is FileInfo lf && r is FileInfo rf)
            {
                CompareStatus status = await CompareFilesAsync(lf, rf, options.Mode, errors, cancellation).ConfigureAwait(false);
                if (status != CompareStatus.Same || options.IncludeSame)
                    entries.Add(new CompareEntry(relative, status, l, r));
            }
            else
            {
                entries.Add(new CompareEntry(relative, CompareStatus.TypeMismatch, l, r));
            }
        }
    }

    private static async Task<CompareStatus> CompareFilesAsync(FileInfo left, FileInfo right, CompareMode mode, List<string> errors, CancellationToken cancellation)
    {
        TimeSpan difference = left.LastWriteTimeUtc - right.LastWriteTimeUtc;
        bool sameTime = difference.Duration() <= TimeTolerance;
        // Quick mode trusts size and time; content mode reads the bytes of every pair of the same size.
        if (left.Length == right.Length && sameTime && mode == CompareMode.Quick)
            return CompareStatus.Same;

        if (left.Length == right.Length && mode == CompareMode.Content)
        {
            try
            {
                string a = await FileHasher.ComputeAsync(left.FullName, HashAlgorithmKind.Sha256, null, cancellation).ConfigureAwait(false);
                string b = await FileHasher.ComputeAsync(right.FullName, HashAlgorithmKind.Sha256, null, cancellation).ConfigureAwait(false);
                return a == b ? CompareStatus.Same : NewerOrDifferent(difference, sameTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{left.FullName}: {ex.Message}");
            }
        }

        return NewerOrDifferent(difference, sameTime);
    }

    private static CompareStatus NewerOrDifferent(TimeSpan difference, bool sameTime) =>
        sameTime ? CompareStatus.Different : difference > TimeSpan.Zero ? CompareStatus.LeftNewer : CompareStatus.RightNewer;

    private static Dictionary<string, FileSystemInfo> List(DirectoryInfo directory, List<string> errors)
    {
        var items = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos("*", Enumeration))
            {
                // A junction or link is compared as a plain item and never entered (it could point back up the tree).
                if (item is DirectoryInfo && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                items[item.Name] = item;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory.FullName}: {ex.Message}");
        }

        return items;
    }
}

public enum SyncDirection
{
    LeftToRight,
    RightToLeft,

    /// <summary>New items go both ways and the newer of two versions replaces the older. Nothing is ever deleted.</summary>
    TwoWay,
}

/// <summary>One copy a sync would do.</summary>
/// <param name="Source">The file or folder to copy.</param>
/// <param name="DestinationFolder">The folder it is copied into (it keeps its name there).</param>
internal sealed record SyncCopy(string Source, string DestinationFolder, CompareEntry Entry);

internal static class SyncPlanner
{
    /// <summary>
    /// The copies that make the sides match as far as <paramref name="direction"/> allows. Items that differ without
    /// either side being newer (<see cref="CompareStatus.Different"/>, <see cref="CompareStatus.TypeMismatch"/>) are left
    /// alone: choosing a winner there is the user's call.
    /// </summary>
    public static IReadOnlyList<SyncCopy> Plan(IEnumerable<CompareEntry> entries, string leftRoot, string rightRoot, SyncDirection direction)
    {
        var copies = new List<SyncCopy>();
        foreach (CompareEntry entry in entries)
        {
            bool? leftToRight = (direction, entry.Status) switch
            {
                (_, CompareStatus.OnlyLeft) when direction != SyncDirection.RightToLeft => true,
                (_, CompareStatus.OnlyRight) when direction != SyncDirection.LeftToRight => false,
                (_, CompareStatus.LeftNewer) when direction != SyncDirection.RightToLeft => true,
                (_, CompareStatus.RightNewer) when direction != SyncDirection.LeftToRight => false,
                _ => null,
            };
            if (leftToRight is not { } forward)
                continue;

            FileSystemInfo? source = forward ? entry.Left : entry.Right;
            string destinationRoot = forward ? rightRoot : leftRoot;
            if (source is null)
                continue;

            string relativeFolder = Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
            copies.Add(new SyncCopy(source.FullName, Path.Combine(destinationRoot, relativeFolder), entry));
        }

        return copies;
    }

    /// <summary>
    /// The copies for entries the user picked: whatever exists on the source side is copied over the other side, even
    /// when the files merely "differ". Folders against files (<see cref="CompareStatus.TypeMismatch"/>) are never
    /// overwritten this way, and equal items need no copy.
    /// </summary>
    public static IReadOnlyList<SyncCopy> PlanSelected(IEnumerable<CompareEntry> entries, string leftRoot, string rightRoot, SyncDirection direction)
    {
        bool forward = direction == SyncDirection.LeftToRight;
        var copies = new List<SyncCopy>();
        foreach (CompareEntry entry in entries)
        {
            FileSystemInfo? source = forward ? entry.Left : entry.Right;
            if (direction == SyncDirection.TwoWay || source is null || entry.Status is CompareStatus.Same or CompareStatus.TypeMismatch)
                continue;

            string relativeFolder = Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
            copies.Add(new SyncCopy(source.FullName, Path.Combine(forward ? rightRoot : leftRoot, relativeFolder), entry));
        }

        return copies;
    }
}

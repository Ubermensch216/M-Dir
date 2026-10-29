using System.Collections.Concurrent;

namespace MDir.Features;

/// <summary>The size of a folder tree. <see cref="Complete"/> is false when parts could not be read (no permission).</summary>
internal sealed record FolderSizeResult(long Bytes, long Files, long Folders, bool Complete);

/// <summary>Adds up the size of a folder tree on a worker thread, without following junctions or symbolic links.</summary>
internal static class FolderSizeCalculator
{
    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };

    /// <summary>
    /// The total size of the files below <paramref name="path"/>. <paramref name="progress"/> receives the running total
    /// now and then. Links are skipped (a link back up the tree would never end, and linked data is not "in" the folder).
    /// </summary>
    public static Task<FolderSizeResult> CalculateAsync(string path, IProgress<long>? progress = null, CancellationToken cancellation = default) =>
        Task.Run(() => Calculate(path, progress, cancellation), cancellation);

    internal static FolderSizeResult Calculate(string path, IProgress<long>? progress, CancellationToken cancellation)
    {
        long bytes = 0, files = 0, folders = 0;
        bool complete = true;
        var pending = new Stack<string>();
        pending.Push(path);
        int sinceReport = 0;
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            string current = pending.Pop();
            try
            {
                foreach (FileSystemInfo item in new DirectoryInfo(current).EnumerateFileSystemInfos("*", Options))
                {
                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    if (item is DirectoryInfo)
                    {
                        folders++;
                        pending.Push(item.FullName);
                    }
                    else if (item is FileInfo file)
                    {
                        files++;
                        bytes += file.Length;
                        if (++sinceReport >= 2000)
                        {
                            sinceReport = 0;
                            progress?.Report(bytes);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        progress?.Report(bytes);
        return new FolderSizeResult(bytes, files, folders, complete);
    }
}

/// <summary>Remembers computed folder sizes for a while so selecting the same folder again is instant.</summary>
internal sealed class FolderSizeCache(TimeSpan? lifetime = null, Func<DateTime>? clock = null)
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromMinutes(5);
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, (FolderSizeResult Result, DateTime At)> _items = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _items.Count;

    public bool TryGet(string path, out FolderSizeResult? result)
    {
        if (_items.TryGetValue(Normalize(path), out var entry) && _clock() - entry.At < _lifetime)
        {
            result = entry.Result;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>Stores a result. Incomplete results are not remembered: a retry may succeed.</summary>
    public void Set(string path, FolderSizeResult result)
    {
        if (result.Complete)
            _items[Normalize(path)] = (result, _clock());
    }

    public void Clear() => _items.Clear();

    /// <summary>Forgets <paramref name="path"/> and every folder around or below it (after files changed there).</summary>
    public void Invalidate(string path)
    {
        string key = Normalize(path);
        foreach (string cached in _items.Keys)
        {
            if (cached.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) || key.StartsWith(cached + "\\", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cached, key, StringComparison.OrdinalIgnoreCase))
                _items.TryRemove(cached, out _);
        }
    }

    /// <summary>The size of the folder, from the cache when fresh, otherwise computed (and then cached).</summary>
    public async Task<FolderSizeResult> GetAsync(string path, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        if (TryGet(path, out FolderSizeResult? cached))
            return cached!;

        FolderSizeResult result = await FolderSizeCalculator.CalculateAsync(path, progress, cancellation).ConfigureAwait(false);
        Set(path, result);
        return result;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>The total size of a selection: files at once, folders by calculation.</summary>
internal sealed record SelectionSize(long Bytes, int Files, int Folders, bool Complete);

internal static class SelectionSizer
{
    /// <summary>
    /// Adds up files and folders. <paramref name="progress"/> gets the running total while folders are still being
    /// counted, so the status can show a rising number.
    /// </summary>
    public static async Task<SelectionSize> MeasureAsync(IReadOnlyList<string> paths, FolderSizeCache cache, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        long bytes = 0;
        int files = 0, folders = 0;
        bool complete = true;
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                long before = bytes;
                var inner = progress is null ? null : new Progress<long>(running => progress.Report(before + running));
                FolderSizeResult result = await cache.GetAsync(path, inner, cancellation).ConfigureAwait(false);
                bytes += result.Bytes;
                files += (int)Math.Min(result.Files, int.MaxValue);
                folders += 1 + (int)Math.Min(result.Folders, int.MaxValue);
                complete &= result.Complete;
            }
            else if (File.Exists(path))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                }
            }

            progress?.Report(bytes);
        }

        return new SelectionSize(bytes, files, folders, complete);
    }
}

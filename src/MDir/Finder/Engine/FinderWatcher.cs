namespace MDir.Finder.Engine;

/// <summary>Turns path-level change events into index edits (no file system watching in here, so it can be tested).</summary>
internal sealed class FinderChangeApplier(FinderIndex index, int vol, FinderExcludes? excludes = null)
{
    public enum Change
    {
        Added,
        Removed,
        Modified,
        RenamedOld,
        RenamedNew,
    }

    private readonly FinderExcludes _excludes = excludes ?? FinderExcludes.None;
    private readonly string _root = index.Roots[vol].Path.TrimEnd('\\', '/');

    /// <summary>Applies one change to the entry at <paramref name="relative"/> (below the root); true when the index changed.</summary>
    public bool Apply(Change change, string relative)
    {
        string full = _root + "\\" + relative.TrimStart('\\');
        lock (index.Lock)
        {
            int? existing = index.Resolve(full);
            switch (change)
            {
                case Change.Removed:
                case Change.RenamedOld:
                    return existing is not null && index.RemoveTree(existing.Value) > 0;
                case Change.Modified:
                    if (existing is null)
                        return false;
                    index.InvalidateMeta(existing.Value);
                    index.Touch();
                    return true;
                default:
                    if (existing is not null)
                    {
                        index.InvalidateMeta(existing.Value);
                        index.Touch();
                        return true;
                    }

                    return Add(full);
            }
        }
    }

    private bool Add(string full)
    {
        string? dir = Path.GetDirectoryName(full);
        if (dir is null || index.Resolve(dir) is not { } parent)
            return false;

        FileSystemInfo info;
        try
        {
            info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            if (!info.Exists)
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        bool isDir = info is DirectoryInfo;
        uint attrs = (uint)info.Attributes | (isDir ? FinderIndex.DirectoryAttribute : 0);
        string name = info.Name;
        if (_excludes.AttrExcluded(attrs) || _excludes.NameExcluded(name) || (isDir && _excludes.FolderExcluded(full)))
            return false;

        double m = FinderIndex.ToEpoch(info.LastWriteTimeUtc);
        int i = index.Add(name, parent, attrs, isDir ? -1 : ((FileInfo)info).Length, m == 0 ? 0.001 : m, 0, vol,
            FinderIndex.ToEpoch(info.CreationTimeUtc), FinderIndex.ToEpoch(info.LastAccessTimeUtc));
        if (isDir && !FinderExcludes.IsReparsePoint(attrs))
            FinderScanner.ScanInto(index, full, i, vol, _excludes); // contents may already exist (a moved-in folder)

        index.Touch();
        return true;
    }
}

/// <summary>Follows a walked root with <see cref="FileSystemWatcher"/> and keeps the index in step.</summary>
internal sealed class FinderFolderWatcher : IDisposable
{
    private readonly FinderChangeApplier _applier;
    private readonly FileSystemWatcher _watcher;
    private readonly string _root;
    private readonly Action<int> _onStale;
    private readonly int _vol;

    public FinderFolderWatcher(FinderIndex index, int vol, FinderExcludes? excludes, Action<int> onStale)
    {
        _vol = vol;
        _onStale = onStale;
        _applier = new FinderChangeApplier(index, vol, excludes);
        string root = index.Roots[vol].Path;
        _root = root.TrimEnd('\\', '/');
        _watcher = new FileSystemWatcher(root.EndsWith('\\') ? root : root + "\\")
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        };
        _watcher.Created += (_, e) => Apply(FinderChangeApplier.Change.Added, e.FullPath);
        _watcher.Deleted += (_, e) => Apply(FinderChangeApplier.Change.Removed, e.FullPath);
        _watcher.Changed += (_, e) => Apply(FinderChangeApplier.Change.Modified, e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Apply(FinderChangeApplier.Change.RenamedOld, e.OldFullPath);
            Apply(FinderChangeApplier.Change.RenamedNew, e.FullPath);
        };
        // The buffer overflowed (or the folder went away): events were lost, so only a rescan is safe.
        _watcher.Error += (_, _) => _onStale(_vol);
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    private void Apply(FinderChangeApplier.Change change, string fullPath)
    {
        if (fullPath.Length <= _root.Length || !fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _applier.Apply(change, fullPath[_root.Length..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // One odd event must not stop the watch.
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}

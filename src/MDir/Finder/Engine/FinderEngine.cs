using MDir.Settings;

namespace MDir.Finder.Engine;

/// <summary>
/// The built-in M-Finder: owns the file index, builds or loads it, keeps it current (USN journal / file system
/// notifications), and answers the panel's searches. Replaces the external M-Finder process behind
/// <see cref="IFinderBackend"/>.
/// </summary>
internal sealed class FinderEngine : IFinderBackend, IDisposable
{
    public const string DatabaseFileName = "index.mfi";

    private readonly FinderOptions _options;
    private readonly string _databaseFile;
    private readonly Func<bool>? _requestElevation;
    private readonly object _stateLock = new();
    private readonly object _buildGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IDisposable> _monitors = [];
    private readonly Dictionary<string, string> _filters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Audio"] = "audio:",
        ["Compressed"] = "zip:",
        ["Document"] = "doc:",
        ["Executable"] = "exe:",
        ["Folder"] = "folder:",
        ["Picture"] = "pic:",
        ["Video"] = "video:",
    };

    private FinderIndex _index = new();
    private CancellationTokenSource _buildCts = new();
    private CancellationTokenSource _fillCts = new();
    private Task? _startTask;
    private bool _ready;
    private bool _building;
    private string _status = string.Empty;
    private long _lastSavedRev = -1;
    private bool? _elevationAllowed;
    private bool _disposed;

    private sealed record CacheKey(FinderQuery Query, long Rev);

    private (CacheKey Key, Task<List<int>?> Task, CancellationTokenSource Cts)? _cache;

    public FinderEngine(FinderOptions options, string databaseFile, Func<bool>? requestElevation = null)
    {
        _options = options.Clone();
        _databaseFile = databaseFile;
        _requestElevation = requestElevation;
    }

    /// <summary>Tests: opens volumes through this instead of the real device / helper.</summary>
    internal Func<string, IVolumeAccess>? VolumeOverride { get; init; }

    /// <summary>Tests: the index.</summary>
    internal FinderIndex Index => _index;

    /// <summary>Tests: runs after the index is built or loaded and the background work is queued.</summary>
    internal Task Started => _startTask ?? Task.CompletedTask;

    public static string DefaultDatabaseFile(string dataDirectory) => Path.Combine(dataDirectory, "Finder", DatabaseFileName);

    // ---- start, load, build ----

    /// <summary>Loads the saved index (or builds one) in the background; safe to call again.</summary>
    public void Start()
    {
        lock (_stateLock)
            _startTask ??= Task.Run(StartCore);
    }

    private void StartCore()
    {
        try
        {
            if (Load())
            {
                StartMonitors();
                StartBackground();
                return;
            }

            Rebuild();
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested && ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"색인을 만들지 못했습니다: {ex.Message}");
        }
    }

    private List<string> ConfiguredRoots() => _options.Roots.Count > 0 ? [.. _options.Roots] : FinderNtfs.FixedDrives();

    private FinderExcludes Excludes() => new(_options.ExcludeHidden, _options.ExcludeSystem, _options.ExcludeFolders, _options.ExcludePatterns);

    private void SetStatus(string text)
    {
        lock (_stateLock)
            _status = text;
    }

    /// <summary>Access to a volume: direct when elevated, through the helper when the user allowed it, else an <see cref="IOException"/>.</summary>
    private IVolumeAccess OpenAccess(string root)
    {
        if (VolumeOverride is not null)
            return VolumeOverride(root);

        if (FinderNtfs.IsElevated())
            return new LocalVolume(root);

        if (!_options.UseMft)
            throw new IOException("NTFS 빠른 색인을 쓰지 않도록 설정되어 있습니다.");

        lock (_stateLock)
        {
            if (_elevationAllowed is null)
            {
                _elevationAllowed = _options.MftConsent switch
                {
                    FinderMftConsent.Allow => true,
                    FinderMftConsent.Deny => false,
                    _ => _requestElevation?.Invoke() ?? false,
                };
            }
        }

        if (_elevationAllowed != true)
            throw new IOException("관리자 권한을 쓰지 않으므로 NTFS 빠른 색인을 쓸 수 없습니다.");

        FinderVolumeHelper.EnsureStarted();
        return FinderVolumeHelper.Open(root);
    }

    /// <summary>Loads the saved index; false when it is missing, was built with other options, or its NTFS volumes can no longer be followed.</summary>
    private bool Load()
    {
        FinderIndex idx;
        try
        {
            idx = FinderIndex.Load(_databaseFile);
        }
        catch (FinderDatabaseException ex)
        {
            SetStatus(ex.Message);
            return false;
        }

        if (idx.Signature != _options.Signature)
        {
            SetStatus("색인 위치가 바뀌었습니다.");
            return false;
        }

        if (_options.Monitor)
        {
            foreach (FinderRoot root in idx.Roots.Where(r => r.Kind == "ntfs"))
            {
                try
                {
                    using IVolumeAccess access = OpenAccess(root.Path);
                }
                catch (IOException)
                {
                    SetStatus("NTFS 볼륨을 읽을 수 없어 다시 색인합니다.");
                    return false;
                }
            }
        }

        lock (_stateLock)
        {
            _index = idx;
            _lastSavedRev = idx.Rev;
            _ready = true;
            _status = $"{idx.Count:N0}개 항목 (저장된 색인)";
        }

        return true;
    }

    /// <summary>Indexes every configured root again and swaps the result in. Blocking; false when another build is running or it was cancelled.</summary>
    public bool Rebuild()
    {
        if (!Monitor.TryEnter(_buildGate))
            return false;

        try
        {
            lock (_stateLock)
            {
                _building = true;
                _buildCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            }

            CancellationToken cancel = _buildCts.Token;
            var fresh = new FinderIndex { Signature = _options.Signature };
            FinderExcludes excludes = Excludes();
            int total = 0;
            foreach (string root in ConfiguredRoots())
            {
                if (cancel.IsCancellationRequested)
                    return false;

                string label = root;
                int before = fresh.Count;
                void Progress(int n) => SetStatus($"색인 중... {label} {total + n:N0}개");

                bool done = false;
                if (_options.UseMft && (VolumeOverride is not null || FinderNtfs.IsNtfsRoot(root)))
                {
                    try
                    {
                        using IVolumeAccess access = OpenAccess(root);
                        FinderNtfsIndexer.IndexVolume(fresh, root, access, excludes, Progress, cancel);
                        done = true;
                    }
                    catch (IOException)
                    {
                        // The MFT could not be read; nothing was added for this root, so walk it instead.
                        done = false;
                    }
                }

                if (!done)
                    FinderScanner.ScanFolder(fresh, root, excludes, Progress, cancel);
                total += fresh.Count - before;
            }

            if (cancel.IsCancellationRequested)
                return false;

            StopMonitors();
            lock (_stateLock)
            {
                _fillCts.Cancel();
                _index = fresh;
                _ready = true;
                _status = $"{fresh.Count:N0}개 항목";
            }

            Save();
            StartMonitors();
            StartBackground();
            return true;
        }
        finally
        {
            lock (_stateLock)
                _building = false;
            Monitor.Exit(_buildGate);
        }
    }

    private void Save()
    {
        try
        {
            FinderIndex idx = _index;
            if (idx.Count == 0)
                return;
            idx.Save(_databaseFile);
            _lastSavedRev = idx.Rev;
        }
        catch (FinderDatabaseException ex)
        {
            SetStatus(ex.Message);
        }
    }

    // ---- background: metadata, folder sizes, saving ----

    private void StartBackground()
    {
        FinderIndex idx = _index;
        CancellationTokenSource cts;
        lock (_stateLock)
        {
            _fillCts.Cancel();
            _fillCts = cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }

        _ = Task.Run(() => FillWorker(idx, cts.Token));
        _ = Task.Run(() => Maintenance(_lifetime.Token));
    }

    private void FillWorker(FinderIndex idx, CancellationToken cancel)
    {
        try
        {
            for (int vol = 0; vol < idx.Roots.Count && !cancel.IsCancellationRequested; vol++)
            {
                if (idx.Roots[vol].Kind != "ntfs")
                    continue;

                bool needed;
                lock (idx.Lock)
                    needed = idx.FrnMaps[vol].Values.Any(i => idx.MTime[i] == 0.0);
                if (!needed)
                    continue;

                try
                {
                    using IVolumeAccess access = OpenAccess(idx.Roots[vol].Path);
                    FinderNtfsIndexer.FillMetadata(idx, vol, access, cancel);
                }
                catch (IOException)
                {
                    // The lazy per-file lookup below still works.
                }
            }

            if (cancel.IsCancellationRequested)
                return;

            // Records the $MFT parser skipped (extension records) and walked entries without dates.
            List<int> left;
            lock (idx.Lock)
                left = [.. Enumerable.Range(0, idx.Count).Where(i => idx.Alive[i] && idx.MTime[i] == 0.0)];
            if (left.Count is > 0 and <= 200000)
                idx.EnsureMeta(left, cancel);
            if (_options.FolderSizes && !cancel.IsCancellationRequested)
                idx.ComputeFolderSizes(cancel);
            idx.Touch();
            if (ReferenceEquals(idx, _index) && !cancel.IsCancellationRequested)
                Save();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // A failed fill leaves sizes and dates to the lazy lookup.
        }
    }

    private int _maintenanceRunning;

    /// <summary>Every 30 s: refresh folder sizes after changes; every 10 min: save the index.</summary>
    private async Task Maintenance(CancellationToken cancel)
    {
        if (Interlocked.Exchange(ref _maintenanceRunning, 1) == 1)
            return;

        try
        {
            DateTime lastSave = DateTime.UtcNow;
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
                FinderIndex idx = _index;
                if (_building)
                    continue;
                if (_options.FolderSizes && idx.FolderSizesRev != idx.Rev)
                    idx.ComputeFolderSizes(cancel);
                if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(10) && idx.Rev != _lastSavedRev && idx.Count > 0)
                {
                    Save();
                    lastSave = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _maintenanceRunning, 0);
        }
    }

    // ---- live monitoring ----

    private void StartMonitors()
    {
        StopMonitors();
        if (!_options.Monitor)
            return;

        FinderIndex idx = _index;
        FinderExcludes excludes = Excludes();
        for (int vol = 0; vol < idx.Roots.Count; vol++)
        {
            if (idx.Roots[vol].Kind == "ntfs")
            {
                var monitor = new FinderUsnMonitor(idx, vol, excludes, OnStale, root =>
                {
                    try
                    {
                        return OpenAccess(root);
                    }
                    catch (IOException)
                    {
                        return null;
                    }
                });
                monitor.Start();
                _monitors.Add(monitor);
            }
            else if (idx.Roots[vol].Kind == "folder" && Directory.Exists(idx.Roots[vol].Path))
            {
                try
                {
                    var watcher = new FinderFolderWatcher(idx, vol, excludes, OnStale);
                    watcher.Start();
                    _monitors.Add(watcher);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    // That root is simply not followed.
                }
            }
        }
    }

    private void StopMonitors()
    {
        foreach (IDisposable monitor in _monitors)
            monitor.Dispose();
        _monitors.Clear();
    }

    /// <summary>The journal wrapped or events were lost: only a new index is safe.</summary>
    private void OnStale(int vol)
    {
        if (_disposed)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                Rebuild();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                SetStatus($"색인을 다시 만들지 못했습니다: {ex.Message}");
            }
        });
    }

    // ---- IFinderBackend ----

    public Task<FinderStatus> StatusAsync()
    {
        lock (_stateLock)
        {
            FinderIndex idx = _index;
            return Task.FromResult(new FinderStatus(_ready, _building, idx.Count, _status, idx.Rev));
        }
    }

    public Task<IReadOnlyList<string>> FiltersAsync() => Task.FromResult<IReadOnlyList<string>>([.. _filters.Keys.Order(StringComparer.OrdinalIgnoreCase)]);

    public Task QuitAsync() => Task.CompletedTask;

    public async Task<FinderPage> SearchAsync(FinderQuery query, int offset, int count)
    {
        FinderIndex idx = _index;
        List<int>? ids;
        try
        {
            ids = await GetResults(idx, query).ConfigureAwait(false);
        }
        catch (FinderQueryException ex)
        {
            throw new FinderException(ex.Message);
        }

        if (ids is null)
            return new FinderPage(0, idx.Rev, []);

        var rows = new List<FinderRow>(Math.Min(count, Math.Max(0, ids.Count - offset)));
        for (int k = offset; k < ids.Count && rows.Count < count; k++)
            rows.Add(Row(idx, ids[k]));
        return new FinderPage(ids.Count, idx.Rev, rows);
    }

    private static FinderRow Row(FinderIndex idx, int i)
    {
        lock (idx.Lock)
        {
            if (i >= idx.Count)
                return new FinderRow(string.Empty, string.Empty, false, null, null);

            if (idx.MTime[i] == 0.0)
                idx.Meta(i);
            long size = idx.Size[i];
            double mtime = idx.MTime[i];
            return new FinderRow(
                idx.Names[i],
                idx.ParentPath(i),
                idx.IsDir(i),
                size >= 0 ? size : null,
                mtime > 0 && mtime < 253402300799 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(mtime * 1000)).LocalDateTime : null);
        }
    }

    private Task<List<int>?> GetResults(FinderIndex idx, FinderQuery query)
    {
        lock (_stateLock)
        {
            var key = new CacheKey(query, idx.Rev);
            if (_cache is { } cached && cached.Key == key)
                return cached.Task;

            _cache?.Cts.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Task<List<int>?> task = Task.Run(() => Compute(idx, query, cts.Token));
            _cache = (key, task, cts);
            return task;
        }
    }

    private List<int>? Compute(FinderIndex idx, FinderQuery q, CancellationToken cancel)
    {
        string filterText = q.Filter is { Length: > 0 } name && _filters.TryGetValue(name, out string? text) ? text : string.Empty;
        var flags = new FinderFlags(Case: q.MatchCase, WholeWord: q.WholeWord, Path: q.MatchPath, Regex: q.Regex);
        List<int>? ids = FinderSearch.Search(idx, q.Text, flags, cancel, filterText, null, (long)_options.ContentMaxMb << 20);
        if (ids is null)
            return null;

        return FinderSearch.Sort(idx, ids, q.Sort, q.Ascending, cancel);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        _cache?.Cts.Cancel();
        StopMonitors();
        try
        {
            if (_ready && !_building)
                Save();
        }
        catch (FinderDatabaseException)
        {
        }
    }
}

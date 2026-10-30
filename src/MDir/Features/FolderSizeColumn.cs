using System.Collections.Concurrent;

namespace MDir.Features;

/// <summary>
/// Sizes for the "Size" column of a file list, which the shell leaves empty for folders. A list asks for the folders
/// it is drawing; <see cref="TryGet"/> answers at once from what is known, <see cref="Request"/> queues the rest for a
/// few worker tasks, and <see cref="Changed"/> tells the list to draw again when something arrived. Complete results
/// are kept in the shared <see cref="FolderSizeCache"/> (so the status bar and the column agree and a refresh forgets
/// both); folders that could only be counted in part are remembered here until <see cref="CancelPending"/>.
/// </summary>
internal sealed class FolderSizeColumnService : IDisposable
{
    private readonly FolderSizeCache _cache;
    private readonly SemaphoreSlim _workers;
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FolderSizeResult> _partial = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cancel = new();

    public FolderSizeColumnService(FolderSizeCache cache, int parallelFolders = 2)
    {
        _cache = cache;
        _workers = new SemaphoreSlim(Math.Max(1, parallelFolders));
    }

    /// <summary>Raised (on a worker thread) after a size was worked out, successfully or not.</summary>
    public event Action? Changed;

    /// <summary>How many folders wait for or are in a calculation.</summary>
    public int PendingCount => _queued.Count;

    /// <summary>The size if it is known; false (and nothing started) otherwise.</summary>
    public bool TryGet(string path, out FolderSizeResult? result)
    {
        if (_cache.TryGet(path, out result))
            return true;

        return _partial.TryGetValue(Key(path), out result);
    }

    /// <summary>Starts working out the size of <paramref name="path"/> unless it is known or already queued.</summary>
    public void Request(string path)
    {
        string key = Key(path);
        if (_cache.TryGet(path, out _) || _partial.ContainsKey(key) || !_queued.TryAdd(key, 0))
            return;

        CancellationToken token = _cancel.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _workers.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    FolderSizeResult result = FolderSizeCalculator.Calculate(path, null, token);
                    if (result.Complete)
                        _cache.Set(path, result);
                    else
                        _partial[key] = result;
                }
                finally
                {
                    _workers.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _queued.TryRemove(key, out _);
            }

            Changed?.Invoke();
        }, CancellationToken.None);
    }

    /// <summary>Drops the waiting and running calculations (the list moved to another folder) and the partial results.</summary>
    public void CancelPending()
    {
        // Not disposed: a worker may still be looking at its token. It holds no unmanaged resources.
        Interlocked.Exchange(ref _cancel, new CancellationTokenSource()).Cancel();
        _partial.Clear();
    }

    /// <summary>Stops the running calculations. The semaphore and token source are left to the garbage collector because workers may still be unwinding.</summary>
    public void Dispose() => _cancel.Cancel();

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>The text drawn in the column: the size, with a trailing "+" when parts could not be read.</summary>
    public static string Format(FolderSizeResult result) =>
        PreviewPane.FormatSize(result.Bytes).Replace(" ", string.Empty) + (result.Complete ? string.Empty : "+");
}

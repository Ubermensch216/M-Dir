using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MDir.Features.Hwp;

/// <summary>Cancels an in-flight <see cref="RhwpDocument"/> open or page render and terminates its child process.</summary>
internal sealed class RhwpJob : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Process? _tracked;

    public bool IsCancelled => _cts.IsCancellationRequested;

    public CancellationToken Token => _cts.Token;

    public void Track(Process process)
    {
        lock (_lock)
        {
            if (IsCancelled)
            {
                KillQuietly(process);
                return;
            }

            _tracked = process;
        }
    }

    public void Untrack(Process process)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_tracked, process))
                _tracked = null;
        }
    }

    public void Cancel()
    {
        Process? proc;
        lock (_lock)
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();

            proc = _tracked;
            _tracked = null;
        }

        if (proc is not null)
            KillQuietly(proc);
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }

    internal static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}

/// <summary>Locates the optional <c>rhwp-preview.exe</c> helper bundled with M-Dir.</summary>
internal static class RhwpLocator
{
    private static readonly string[] ExeNames = ["rhwp-preview.exe", "rhwp.exe"];

    /// <summary>Master switch (for tests or disabling external renderer).</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Optional explicit path override (for tests).</summary>
    internal static string? ConfiguredPath { get; set; }

    public static string? FindExe()
    {
        if (!Enabled)
            return null;

        string?[] explicitCandidates =
        [
            Environment.GetEnvironmentVariable("RHWP_EXE"),
            ConfiguredPath,
        ];
        foreach (string? candidate in explicitCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        string baseDir = AppContext.BaseDirectory;
        foreach (string name in ExeNames)
        {
            string[] local =
            [
                Path.Combine(baseDir, "rhwp", name),
                Path.Combine(baseDir, name),
                Path.Combine(baseDir, "vendor", "rhwp", name),
            ];
            foreach (string candidate in local)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Development builds (bin\Debug\net10.0-windows): walk up to find repo-level vendor\rhwp or cargo target.
        DirectoryInfo? dir = new(baseDir);
        for (int depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            foreach (string name in ExeNames)
            {
                string vendored = Path.Combine(dir.FullName, "vendor", "rhwp", name);
                if (File.Exists(vendored))
                    return vendored;
            }

            string[] targets =
            [
                Path.Combine(dir.FullName, "packaging", "rhwp_preview", "target", "x86_64-pc-windows-gnu", "release", "rhwp-preview.exe"),
                Path.Combine(dir.FullName, "packaging", "rhwp_preview", "target", "x86_64-pc-windows-gnullvm", "release", "rhwp-preview.exe"),
                Path.Combine(dir.FullName, "packaging", "rhwp_preview", "target", "release", "rhwp-preview.exe"),
            ];
            foreach (string candidate in targets)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M-Dir", "rhwp");
        foreach (string name in ExeNames)
        {
            string candidate = Path.Combine(appData, name);
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (string name in ExeNames)
        {
            if (FindOnPath(name) is { } found)
                return found;
        }

        return null;
    }

    private static string? FindOnPath(string exe)
    {
        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath))
            return null;

        foreach (string entry in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(entry.Trim('"'), exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

/// <summary>Persistent <c>rhwp-preview.exe --worker</c> session keeping one parsed document in memory.</summary>
internal sealed class RhwpWorker : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly object _lock = new();
    private bool _dead;

    private RhwpWorker(Process process) => _process = process;

    public bool IsDead
    {
        get
        {
            lock (_lock)
                return _dead || HasExitedQuietly();
        }
    }

    public static RhwpWorker? Start(string exe, string workingDirectory)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
        };
        info.ArgumentList.Add("--worker");
        info.Environment["RHWP_WEBFONT_OFFLINE"] = "1";

        var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, _) => { };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            process.BeginErrorReadLine();
            return new RhwpWorker(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            return null;
        }
    }

    public JsonDocument? Request(object payload, RhwpJob job, int timeoutMs)
    {
        lock (_lock)
        {
            if (_dead || job.IsCancelled || HasExitedQuietly())
                return null;

            job.Track(_process);
            try
            {
                string json = JsonSerializer.Serialize(payload);
                _process.StandardInput.WriteLine(json);
                _process.StandardInput.Flush();

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Token, timeoutCts.Token);

                Task<string?> readTask = _process.StandardOutput.ReadLineAsync(linked.Token).AsTask();
                string? line = readTask.GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(line))
                {
                    KillLocked();
                    return null;
                }

                return JsonDocument.Parse(line);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or JsonException)
            {
                KillLocked();
                return null;
            }
            finally
            {
                job.Untrack(_process);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_dead)
                return;

            _dead = true;
            try
            {
                if (!HasExitedQuietly())
                {
                    _process.StandardInput.WriteLine("{\"op\":\"quit\"}");
                    _process.StandardInput.Flush();
                    _process.WaitForExit(1000);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }

            KillLocked();
            _process.Dispose();
        }
    }

    private bool HasExitedQuietly()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void KillLocked()
    {
        _dead = true;
        RhwpJob.KillQuietly(_process);
    }
}

/// <summary>
/// An HWP/HWPX document rendered page-by-page via <c>rhwp-preview.exe</c> into PNG files cached in
/// <c>%TEMP%\M-Dir\rhwp-view</c>. Page 0 is rendered on open; remaining pages are rendered lazily on demand.
/// </summary>
internal sealed class RhwpDocument : IDisposable
{
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const int FirstPageTimeoutMs = 240_000;
    private const int PageTimeoutMs = 300_000;
    private const float RenderScale = 2.0f;
    private const int RenderVersion = 1;
    private const long CacheLimitBytes = 600L * 1024 * 1024;
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(3);
    private static int _pruned;

    private readonly string _exe;
    private readonly object _renderLock = new();
    private RhwpWorker? _worker;
    private bool _disposed;

    internal static string CacheFolder { get; set; } = Path.Combine(Path.GetTempPath(), "M-Dir", "rhwp-view");

    private RhwpDocument(string sourcePath, string exe, string cacheDir, int pageCount, RhwpWorker? worker)
    {
        SourcePath = sourcePath;
        _exe = exe;
        CacheDir = cacheDir;
        PageCount = pageCount;
        _worker = worker;
    }

    public string SourcePath { get; }

    public string CacheDir { get; }

    public int PageCount { get; }

    public bool HasWorker => _worker is { IsDead: false };

    public string PageFile(int pageIndex) => Path.Combine(CacheDir, $"p{pageIndex}.png");

    public bool HasPage(int pageIndex) => File.Exists(PageFile(pageIndex));

    /// <summary>
    /// Opens <paramref name="path"/> and ensures page 0 is rendered on disk. Returns null when
    /// <c>rhwp-preview.exe</c> is missing, disabled, cancelled, or cannot open the document.
    /// </summary>
    public static RhwpDocument? Open(string path, RhwpJob job, string? cacheFolder = null)
    {
        if (job.IsCancelled)
            return null;

        string? exe = RhwpLocator.FindExe();
        if (exe is null)
            return null;

        try
        {
            var file = new FileInfo(path);
            var exeInfo = new FileInfo(exe);
            if (!file.Exists || file.Length > MaxFileBytes || !exeInfo.Exists)
                return null;

            string root = cacheFolder ?? CacheFolder;
            string keyRaw = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{exeInfo.Length}|{exeInfo.LastWriteTimeUtc.Ticks}|{RenderScale}|{RenderVersion}";
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyRaw)))[..32];
            string dir = Path.Combine(root, key);
            Directory.CreateDirectory(dir);
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);

            string metaFile = Path.Combine(dir, "meta.json");
            int cachedPages = ReadMetaPages(metaFile);
            string p0 = Path.Combine(dir, "p0.png");
            if (cachedPages > 0 && File.Exists(p0))
                return new RhwpDocument(path, exe, dir, cachedPages, worker: null);

            if (job.IsCancelled)
                return null;

            RhwpWorker? worker = RhwpWorker.Start(exe, dir);
            if (worker is null)
                return null;

            try
            {
                using JsonDocument? openResp = worker.Request(new { op = "open", path }, job, FirstPageTimeoutMs);
                if (openResp is null || !TryGetOk(openResp, out int pages) || pages < 1)
                {
                    worker.Dispose();
                    return null;
                }

                string part = Path.Combine(dir, $"p0.part-{Guid.NewGuid():N}.png");
                using JsonDocument? pageResp = worker.Request(new { op = "page", page = 0, @out = part, scale = RenderScale }, job, FirstPageTimeoutMs);
                if (pageResp is null || !IsOk(pageResp) || !File.Exists(part) || new FileInfo(part).Length < 100)
                {
                    DeleteQuietly(part);
                    worker.Dispose();
                    return null;
                }

                File.Move(part, p0, overwrite: true);
                WriteMetaAtomic(metaFile, pages, Path.GetFileName(path));
                PruneCache(root, key);
                return new RhwpDocument(path, exe, dir, pages, worker);
            }
            catch
            {
                worker.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return null;
        }
    }

    public bool RenderPage(int pageIndex, RhwpJob job)
    {
        if (pageIndex < 0 || pageIndex >= PageCount || job.IsCancelled)
            return false;

        if (HasPage(pageIndex))
            return true;

        lock (_renderLock)
        {
            if (_disposed || job.IsCancelled)
                return false;

            if (HasPage(pageIndex))
                return true;

            if (_worker is null || _worker.IsDead)
            {
                _worker?.Dispose();
                _worker = RhwpWorker.Start(_exe, CacheDir);
                if (_worker is null)
                    return false;

                using JsonDocument? openResp = _worker.Request(new { op = "open", path = SourcePath }, job, FirstPageTimeoutMs);
                if (openResp is null || !TryGetOk(openResp, out _))
                {
                    _worker.Dispose();
                    _worker = null;
                    return false;
                }
            }

            string part = Path.Combine(CacheDir, $"p{pageIndex}.part-{Guid.NewGuid():N}.png");
            try
            {
                using JsonDocument? resp = _worker.Request(new { op = "page", page = pageIndex, @out = part, scale = RenderScale }, job, PageTimeoutMs);
                if (resp is not null && IsOk(resp) && File.Exists(part) && new FileInfo(part).Length >= 100)
                {
                    File.Move(part, PageFile(pageIndex), overwrite: true);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (HasPage(pageIndex))
                    return true;
            }
            finally
            {
                DeleteQuietly(part);
            }

            return HasPage(pageIndex);
        }
    }

    public void Dispose()
    {
        RhwpWorker? worker;
        lock (_renderLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            worker = _worker;
            _worker = null;
        }

        worker?.Dispose();
    }

    private static bool IsOk(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;

    private static bool TryGetOk(JsonDocument doc, out int pages)
    {
        pages = 0;
        if (!IsOk(doc))
            return false;

        if (doc.RootElement.TryGetProperty("pages", out JsonElement p) && p.TryGetInt32(out int count))
        {
            pages = count;
            return true;
        }

        return false;
    }

    private static int ReadMetaPages(string metaFile)
    {
        try
        {
            if (!File.Exists(metaFile))
                return 0;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaFile, Encoding.UTF8));
            return doc.RootElement.TryGetProperty("pages", out JsonElement p) && p.TryGetInt32(out int count) ? count : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    private static void WriteMetaAtomic(string metaFile, int pages, string sourceName)
    {
        string part = metaFile + ".part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            string json = JsonSerializer.Serialize(new { pages, source = sourceName });
            File.WriteAllText(part, json, Encoding.UTF8);
            File.Move(part, metaFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(part);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void PruneCache(string cacheRoot, string keepKey)
    {
        if (!Directory.Exists(cacheRoot))
            return;

        bool firstRun = Interlocked.Exchange(ref _pruned, 1) == 0;
        DateTime ageLimit = DateTime.UtcNow - CacheAge;
        var entries = new List<(DateTime LastWrite, DirectoryInfo Dir, long Size)>();
        long total = 0;

        foreach (DirectoryInfo dir in new DirectoryInfo(cacheRoot).EnumerateDirectories())
        {
            if (string.Equals(dir.Name, keepKey, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (firstRun && dir.LastWriteTimeUtc < ageLimit)
                {
                    dir.Delete(recursive: true);
                    continue;
                }

                long size = 0;
                foreach (FileInfo f in dir.EnumerateFiles())
                    size += f.Length;
                entries.Add((dir.LastWriteTimeUtc, dir, size));
                total += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (total <= CacheLimitBytes)
            return;

        long target = (long)(CacheLimitBytes * 0.8);
        foreach ((_, DirectoryInfo dir, long size) in entries.OrderBy(e => e.LastWrite))
        {
            try
            {
                dir.Delete(recursive: true);
                total -= size;
                if (total <= target)
                    break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

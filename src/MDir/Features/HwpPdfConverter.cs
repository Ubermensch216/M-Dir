using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MDir.Features;

/// <summary>
/// Converts Hancom documents (<c>.hwp</c>, <c>.hwpx</c>) to PDF with an installed Hancom Office (the
/// <c>HWPFrame.HwpObject</c> automation server), so the preview pane can show every page with its formatting through
/// the PDF preview handler. Hancom's own preview handler only paints the first page.
/// </summary>
/// <remarks>
/// One worker thread (STA, as the automation server wants) keeps a hidden Hancom instance and closes it after a
/// minute without work. Results are cached in %TEMP%\M-Dir\hwp-preview by path, size and modified time, so a document
/// is converted once. Only the latest request matters: older requests still waiting are dropped.
/// </remarks>
internal static class HwpPdfConverter
{
    private const string ProgId = "HWPFrame.HwpObject";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);

    private static readonly object Gate = new();
    private static readonly BlockingCollection<Request> Queue = new();
    private static Thread? _worker;
    private static bool _pruned;

    private sealed record Request(string Source, string Target, TaskCompletionSource<string?> Result);

    /// <summary>True when Hancom Office's automation server is registered on this computer.</summary>
    public static bool IsAvailable { get; } = Type.GetTypeFromProgID(ProgId, throwOnError: false) is not null;

    /// <summary>The folder converted documents are kept in.</summary>
    internal static string CacheFolder { get; } = Path.Combine(Path.GetTempPath(), "M-Dir", "hwp-preview");

    /// <summary>The cached PDF of <paramref name="path"/> when it was converted before (and the file did not change), else null.</summary>
    public static string? FindCached(string path)
    {
        string? target = CachePath(path);
        return target is not null && File.Exists(target) && new FileInfo(target).Length > 0 ? target : null;
    }

    /// <summary>
    /// The PDF of <paramref name="path"/>: from the cache, or converted now. Null when Hancom Office is missing, the
    /// document cannot be opened (password, DRM...), or a newer request replaced this one before it started.
    /// </summary>
    public static Task<string?> ConvertAsync(string path)
    {
        if (!IsAvailable || CachePath(path) is not { } target)
            return Task.FromResult<string?>(null);

        if (FindCached(path) is { } cached)
            return Task.FromResult<string?>(cached);

        var request = new Request(path, target, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (Gate)
        {
            if (Queue.IsAddingCompleted)
                return Task.FromResult<string?>(null);

            // Only the newest selection is worth converting.
            while (Queue.TryTake(out Request? stale))
                stale.Result.TrySetResult(null);

            Queue.Add(request);
            if (_worker is null)
            {
                _worker = new Thread(Work) { IsBackground = true, Name = "M-Dir HWP to PDF" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();

                // A background thread just stops at exit, which would leave the hidden Hancom process running.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
        }

        return request.Result.Task;
    }

    /// <summary>Closes the hidden Hancom instance (waits a few seconds for a conversion in progress).</summary>
    public static void Shutdown()
    {
        Thread? worker;
        lock (Gate)
        {
            worker = _worker;
            if (worker is null || Queue.IsAddingCompleted)
                return;

            while (Queue.TryTake(out Request? stale))
                stale.Result.TrySetResult(null);

            Queue.CompleteAdding();
        }

        worker.Join(TimeSpan.FromSeconds(5));
    }

    private static string? CachePath(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;

            string key = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
            return Path.Combine(CacheFolder, hash + ".pdf");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Work()
    {
        object? hwp = null;
        try
        {
            while (!Queue.IsCompleted)
            {
                if (!Queue.TryTake(out Request? request, hwp is null ? Timeout.InfiniteTimeSpan : IdleTimeout))
                {
                    if (Queue.IsCompleted)
                        break;

                    // Idle: let the hidden Hancom instance go.
                    Quit(ref hwp);
                    continue;
                }

                string? result = null;
                try
                {
                    hwp ??= Start();
                    result = hwp is not null && SaveAsPdf(hwp,request.Source, request.Target) ? request.Target : null;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException or InvalidCastException
                    or IOException or UnauthorizedAccessException or MissingMethodException)
                {
                    // The instance may be broken (closed by the user, crashed): start a new one next time.
                    Quit(ref hwp);
                }

                request.Result.TrySetResult(result);
            }
        }
        finally
        {
            Quit(ref hwp);
        }
    }

    private static object? Start()
    {
        if (Type.GetTypeFromProgID(ProgId, throwOnError: false) is not { } type || Activator.CreateInstance(type) is not { } hwp)
            return null;

        // The security module (if the user registered one) lets Hancom open files without asking.
        try
        {
            Call(hwp, "RegisterModule", "FilePathCheckDLL", "FilePathCheckerModule");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException)
        {
        }

        PruneCache();
        return hwp;
    }

    private static bool SaveAsPdf(object hwp, string source, string target)
    {
        Directory.CreateDirectory(CacheFolder);
        string format = string.Equals(Path.GetExtension(source), ".hwpx", StringComparison.OrdinalIgnoreCase) ? "HWPX" : "HWP";
        string partial = target + ".part";
        try
        {
            if (Call(hwp, "Open", source, format, "forceopen:true;suspendpassword:true;versionwarning:false;lock:false") is not true)
                return false;

            bool saved = Call(hwp, "SaveAs", partial, "PDF", string.Empty) is true;
            if (!saved || !File.Exists(partial) || new FileInfo(partial).Length == 0)
                return false;

            File.Move(partial, target, overwrite: true);
            return true;
        }
        finally
        {
            try
            {
                // Close the document without saving (1 = discard changes).
                Call(hwp, "Clear", 1);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException)
            {
            }

            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static object? Call(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    private static void Quit(ref object? hwp)
    {
        if (hwp is null)
            return;

        try
        {
            Call(hwp, "Quit");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TargetInvocationException)
        {
        }

        try
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(hwp);
        }
        catch (ArgumentException)
        {
        }

        hwp = null;
    }

    /// <summary>Deletes cached PDFs not used for a week (once per run).</summary>
    private static void PruneCache()
    {
        if (_pruned)
            return;

        _pruned = true;
        try
        {
            if (!Directory.Exists(CacheFolder))
                return;

            DateTime limit = DateTime.UtcNow - CacheAge;
            foreach (FileInfo file in new DirectoryInfo(CacheFolder).EnumerateFiles())
            {
                if (file.LastWriteTimeUtc < limit)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

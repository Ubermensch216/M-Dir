using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MDir.Finder;

/// <summary>M-Finder answered a request with an error (a bad regex, an unknown request...). The connection stays usable.</summary>
internal sealed class FinderException(string message) : Exception(message);

/// <summary>No M-Finder is running for this user, or its pipe broke.</summary>
internal sealed class FinderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One search result.</summary>
/// <param name="Folder">The folder that holds the item ("" for a drive root).</param>
/// <param name="Size">Bytes; null when M-Finder does not know it (yet). Folders have one once folder sizes are computed.</param>
/// <param name="Modified">Local time; null when unknown.</param>
internal sealed record FinderRow(string Name, string Folder, bool IsDirectory, long? Size, DateTime? Modified)
{
    public string FullPath => Folder.Length == 0 ? Name : Path.Join(Folder, Name);
}

/// <summary>One page of a search: <see cref="Total"/> matches in all, <see cref="Rows"/> from the requested offset.</summary>
/// <param name="Revision">The index revision the search ran on; it changes whenever the indexed files change.</param>
internal sealed record FinderPage(int Total, long Revision, IReadOnlyList<FinderRow> Rows);

/// <summary>What M-Finder is doing.</summary>
/// <param name="Ready">An index is loaded or built, so searches return results.</param>
internal sealed record FinderStatus(bool Ready, bool Building, long Objects, string Text, long Revision);

/// <summary>A search as M-Finder's IPC understands it.</summary>
/// <param name="Sort">"name", "path", "size", "mtime" or "ext".</param>
internal sealed record FinderQuery(string Text, string Sort = "name", bool Ascending = true, string? Filter = null,
    bool MatchCase = false, bool WholeWord = false, bool MatchPath = false, bool Regex = false);

/// <summary>The requests M-Dir makes; an interface so the panel can be tested without a real M-Finder.</summary>
internal interface IFinderBackend
{
    Task<FinderStatus> StatusAsync();

    Task<FinderPage> SearchAsync(FinderQuery query, int offset, int count);

    Task<IReadOnlyList<string>> FiltersAsync();

    Task QuitAsync();
}

/// <summary>
/// M-Finder's IPC frame format (see <c>mfinder/ipc.py</c>): u32 little-endian payload length, u8 type, payload.
/// A request is one JSON frame; the reply is any number of binary frames, then an end frame (JSON result) or an
/// error frame (JSON <c>{"error": ...}</c>).
/// </summary>
internal static class FinderProtocol
{
    public const byte Json = 1;
    public const byte Binary = 2;
    public const byte End = 3;
    public const byte Error = 4;
    public const int MaxFrame = 64 << 20;

    public static async Task WriteFrameAsync(Stream stream, byte type, ReadOnlyMemory<byte> payload)
    {
        // One write per frame, so a frame is never split between two writes on the pipe.
        byte[] buffer = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
        buffer[4] = type;
        payload.CopyTo(buffer.AsMemory(5));
        await stream.WriteAsync(buffer).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(Stream stream)
    {
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrame)
            throw new IOException("M-Finder sent a frame that is too large.");

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload).ConfigureAwait(false);
        return (header[4], payload);
    }

    /// <summary>Sends <paramref name="request"/> and returns the JSON result. Throws <see cref="FinderException"/> for an error reply.</summary>
    public static async Task<JsonElement> CallAsync(Stream stream, object request)
    {
        await WriteFrameAsync(stream, Json, JsonSerializer.SerializeToUtf8Bytes(request)).ConfigureAwait(false);
        while (true)
        {
            (byte type, byte[] payload) = await ReadFrameAsync(stream).ConfigureAwait(false);
            if (type == End)
                return payload.Length == 0 ? default : JsonDocument.Parse(payload).RootElement.Clone();

            if (type == Error)
            {
                string message = "M-Finder error";
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("error", out JsonElement error) && error.GetString() is { Length: > 0 } text)
                        message = text;
                }
                catch (JsonException)
                {
                    message = Encoding.UTF8.GetString(payload);
                }

                throw new FinderException(message);
            }

            // Binary chunks are not used by the requests M-Dir makes.
        }
    }

    public static FinderPage ParsePage(JsonElement result)
    {
        var rows = new List<FinderRow>();
        if (result.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in results.EnumerateArray())
            {
                rows.Add(new FinderRow(
                    Str(row, "name"),
                    Str(row, "path"),
                    row.TryGetProperty("is_dir", out JsonElement dir) && dir.ValueKind == JsonValueKind.True,
                    row.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
                    row.TryGetProperty("date_modified", out JsonElement modified) && modified.ValueKind == JsonValueKind.Number ? FromEpoch(modified.GetDouble()) : null));
            }
        }

        return new FinderPage(Int(result, "total"), Long(result, "rev"), rows);
    }

    public static FinderStatus ParseStatus(JsonElement result) => new(
        // Before "ready" existed an instance answering at all had an index.
        !result.TryGetProperty("ready", out JsonElement ready) || ready.ValueKind == JsonValueKind.True,
        result.TryGetProperty("building", out JsonElement building) && building.ValueKind == JsonValueKind.True,
        Long(result, "objects"),
        Str(result, "status"),
        Long(result, "rev"));

    private static DateTime? FromEpoch(double seconds) =>
        seconds is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).LocalDateTime : null;

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement element, string name) => (int)Math.Clamp(Long(element, name), 0, int.MaxValue);

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
}

/// <summary>
/// Talks to a running M-Finder over its per-user named pipe (<c>\\.\pipe\M-Finder-&lt;user SID&gt;</c>, whose ACL
/// admits only this user). One connection is kept and requests take turns on it.
/// </summary>
internal sealed class FinderClient(string? pipeName = null) : IFinderBackend, IDisposable
{
    public const int PageSize = 200;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public static string DefaultPipeName => "M-Finder-" + (WindowsIdentity.GetCurrent().User?.Value ?? string.Empty);

    public string PipeName { get; } = pipeName ?? DefaultPipeName;

    /// <summary>How long to wait for the pipe when connecting.</summary>
    public int ConnectTimeoutMs { get; init; } = 1000;

    public async Task<JsonElement> CallAsync(object request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool reused = _pipe is not null;
            try
            {
                _pipe ??= await ConnectAsync().ConfigureAwait(false);
                return await FinderProtocol.CallAsync(_pipe, request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException && reused)
            {
                // The instance we were connected to went away (or was restarted): try a fresh connection once.
                Drop();
                _pipe = await ConnectAsync().ConfigureAwait(false);
                return await FinderProtocol.CallAsync(_pipe, request).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Drop();
            throw new FinderUnavailableException("M-Finder is not running.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when an M-Finder answers right now.</summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            JsonElement result = await CallAsync(new { op = "ping" }).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is FinderUnavailableException or FinderException)
        {
            return false;
        }
    }

    public async Task<FinderStatus> StatusAsync() => FinderProtocol.ParseStatus(await CallAsync(new { op = "status" }).ConfigureAwait(false));

    public async Task<FinderPage> SearchAsync(FinderQuery query, int offset, int count)
    {
        JsonElement result = await CallAsync(new
        {
            op = "search",
            query = query.Text,
            sort = query.Sort,
            asc = query.Ascending,
            filter = query.Filter ?? string.Empty,
            @case = query.MatchCase,
            ww = query.WholeWord,
            path = query.MatchPath,
            regex = query.Regex,
            offset,
            count,
        }).ConfigureAwait(false);
        return FinderProtocol.ParsePage(result);
    }

    public async Task<IReadOnlyList<string>> FiltersAsync()
    {
        try
        {
            JsonElement result = await CallAsync(new { op = "filters" }).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("filters", out JsonElement filters) && filters.ValueKind == JsonValueKind.Array)
                return [.. filters.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).Where(f => f.Length > 0)];
        }
        catch (FinderException)
        {
            // An older M-Finder without the request: no filter list.
        }

        return [];
    }

    public async Task QuitAsync()
    {
        try
        {
            await CallAsync(new { op = "quit" }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FinderUnavailableException or FinderException)
        {
            // Already gone, or an instance that cannot be ended this way.
        }
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Drop()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Drop();
        _gate.Dispose();
    }
}

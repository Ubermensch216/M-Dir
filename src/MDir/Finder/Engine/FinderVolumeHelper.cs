using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MDir.Finder.Engine;

/// <summary>
/// M-Dir itself runs as a normal user, but reading the MFT and the USN journal needs administrator rights. For that
/// M-Dir starts a second copy of itself elevated (one UAC prompt, only when the user agreed) with
/// <c>--finder-helper</c>. That copy only serves the five volume requests below over a pipe that only this user can
/// open and that asks for a one-time token first; it never returns raw disk contents, only names, attributes, sizes
/// and times, and it ends when M-Dir does.
/// </summary>
internal static class FinderVolumeHelper
{
    public const string Argument = "--finder-helper";

    private const byte OpOpen = 1, OpQueryJournal = 2, OpCreateJournal = 3, OpEnumMft = 4, OpReadJournal = 5, OpScanMetadata = 6;
    private const int BatchSize = 20000;

    // ---- server (the elevated process) ----

    /// <summary>The command line of the helper: <c>--finder-helper &lt;pipe&gt; &lt;token&gt; &lt;user sid&gt; &lt;parent pid&gt;</c>.</summary>
    public static bool IsHelperCommandLine(string[] args) => args.Length >= 5 && args[0] == Argument;

    public static int RunServer(string[] args)
    {
        string pipeName = args[1], token = args[2], sid = args[3];
        if (!int.TryParse(args[4], out int parentPid))
            return 2;

        // Ends together with M-Dir.
        var watchdog = new Thread(() =>
        {
            try
            {
                using Process parent = Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
            }

            Environment.Exit(0);
        })
        { IsBackground = true };
        watchdog.Start();

        Listen(pipeName, token, new SecurityIdentifier(sid), CancellationToken.None);
        return 0;
    }

    /// <summary>Tests: how a volume is opened (default: directly, which needs administrator rights).</summary>
    internal static Func<string, IVolumeAccess> VolumeFactory { get; set; } = root => new LocalVolume(root);

    /// <summary>Serves connections on <paramref name="pipeName"/> until cancelled; only <paramref name="user"/> may open the pipe.</summary>
    internal static void Listen(string pipeName, string token, SecurityIdentifier user, CancellationToken cancel)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16, security);
            try
            {
                pipe.WaitForConnectionAsync(cancel).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }

            new Thread(() => Serve(pipe, token)) { IsBackground = true }.Start();
        }
    }

    private static void Serve(NamedPipeServerStream pipe, string expectedToken)
    {
        using (pipe)
        {
            try
            {
                using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(reader.ReadString()), Encoding.UTF8.GetBytes(expectedToken)))
                    return;

                IVolumeAccess? volume = null;
                try
                {
                    while (true)
                    {
                        byte op = reader.ReadByte();
                        try
                        {
                            Handle(op, reader, writer, ref volume);
                        }
                        catch (VolumeException ex)
                        {
                            Status(writer, ex.Code, ex.Message);
                        }
                        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ArgumentException)
                        {
                            Status(writer, 1, ex.Message);
                        }

                        writer.Flush();
                    }
                }
                finally
                {
                    volume?.Dispose();
                }
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Status(BinaryWriter w, int code, string message = "")
    {
        w.Write(code);
        if (code != 0)
            w.Write(message);
    }

    private static void Handle(byte op, BinaryReader r, BinaryWriter w, ref IVolumeAccess? volume)
    {
        if (op == OpOpen)
        {
            volume?.Dispose();
            volume = null;
            volume = VolumeFactory(r.ReadString());
            Status(w, 0);
            return;
        }

        IVolumeAccess v = volume ?? throw new InvalidOperationException("No volume is open.");
        switch (op)
        {
            case OpQueryJournal:
                {
                    UsnJournalInfo info = v.QueryJournal();
                    Status(w, 0);
                    w.Write(info.JournalId);
                    w.Write(info.FirstUsn);
                    w.Write(info.NextUsn);
                    w.Write(info.LowestValidUsn);
                    break;
                }

            case OpCreateJournal:
                v.CreateJournal();
                Status(w, 0);
                break;
            case OpEnumMft:
                {
                    long high = r.ReadInt64();
                    Status(w, 0);
                    var batch = new List<MftEntry>(BatchSize);
                    try
                    {
                        foreach (MftEntry e in v.EnumMft(high))
                        {
                            batch.Add(e);
                            if (batch.Count >= BatchSize)
                            {
                                WriteEntries(w, batch);
                                batch.Clear();
                            }
                        }

                        if (batch.Count > 0)
                            WriteEntries(w, batch);
                        w.Write(0);
                    }
                    catch (VolumeException ex)
                    {
                        w.Write(-1);
                        w.Write(ex.Code);
                        w.Write(ex.Message);
                    }

                    break;
                }

            case OpReadJournal:
                {
                    long start = r.ReadInt64();
                    ulong id = r.ReadUInt64();
                    (long next, List<UsnRecord> records) = v.ReadJournal(start, id);
                    Status(w, 0);
                    w.Write(next);
                    w.Write(records.Count);
                    foreach (UsnRecord rec in records)
                    {
                        w.Write(rec.Frn);
                        w.Write(rec.ParentFrn);
                        w.Write(rec.Usn);
                        w.Write(rec.Reason);
                        w.Write(rec.Attrs);
                        w.Write(rec.Name);
                    }

                    break;
                }

            case OpScanMetadata:
                {
                    int length = r.ReadInt32();
                    int[]? wanted = null;
                    if (length >= 0)
                    {
                        wanted = new int[length];
                        for (int i = 0; i < length; i++)
                            wanted[i] = r.ReadInt32();
                    }

                    Status(w, 0);
                    try
                    {
                        foreach (List<MftMeta> batch in v.ScanMetadata(wanted, CancellationToken.None))
                        {
                            w.Write(batch.Count);
                            foreach (MftMeta m in batch)
                            {
                                w.Write(m.Record);
                                w.Write(m.Size);
                                w.Write(m.CTime);
                                w.Write(m.MTime);
                                w.Write(m.ATime);
                            }

                            w.Flush();
                        }

                        w.Write(0);
                    }
                    catch (VolumeException ex)
                    {
                        w.Write(-1);
                        w.Write(ex.Code);
                        w.Write(ex.Message);
                    }
                    catch (IOException ex) when (ex is not VolumeException)
                    {
                        w.Write(-1);
                        w.Write(1);
                        w.Write(ex.Message);
                    }

                    break;
                }

            default:
                throw new InvalidOperationException("Unknown request.");
        }
    }

    private static void WriteEntries(BinaryWriter w, List<MftEntry> batch)
    {
        w.Write(batch.Count);
        foreach (MftEntry e in batch)
        {
            w.Write(e.Frn);
            w.Write(e.ParentFrn);
            w.Write(e.Attrs);
            w.Write(e.Name);
        }

        w.Flush();
    }

    // ---- client (M-Dir) ----

    private static readonly object ClientLock = new();
    private static Process? _process;
    private static string _pipeName = string.Empty;
    private static string _token = string.Empty;

    /// <summary>True while an elevated helper is running.</summary>
    public static bool IsRunning
    {
        get
        {
            lock (ClientLock)
                return _process is { HasExited: false };
        }
    }

    /// <summary>Starts the elevated helper (shows the UAC prompt) when none is running. Throws <see cref="IOException"/> when it was refused or did not come up.</summary>
    public static void EnsureStarted()
    {
        lock (ClientLock)
        {
            if (_process is { HasExited: false })
                return;

            string exe = Environment.ProcessPath ?? throw new IOException("M-Dir의 실행 파일 위치를 알 수 없습니다.");
            _pipeName = "M-Dir-Finder-" + Guid.NewGuid().ToString("N");
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (string arg in new[] { Argument, _pipeName, _token, sid, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(arg);

            try
            {
                _process = Process.Start(info);
            }
            catch (Win32Exception ex)
            {
                throw new IOException(ex.NativeErrorCode == 1223 ? "관리자 권한 요청이 취소되었습니다." : ex.Message, ex);
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (_process is null or { HasExited: true })
                    throw new IOException("관리자 권한 도우미가 바로 끝났습니다.");

                try
                {
                    using var probe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                    probe.Connect(200);
                    return;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    Thread.Sleep(100);
                }
            }

            throw new IOException("관리자 권한 도우미가 응답하지 않습니다.");
        }
    }

    /// <summary>Opens <paramref name="root"/> through the helper (which must have been started).</summary>
    public static IVolumeAccess Open(string root)
    {
        string pipe, token;
        lock (ClientLock)
        {
            pipe = _pipeName;
            token = _token;
        }

        return OpenAt(pipe, token, root);
    }

    internal static IVolumeAccess OpenAt(string pipe, string token, string root)
    {
        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
        try
        {
            stream.Connect(5000);
            var volume = new HelperVolume(stream);
            volume.Handshake(token, root);
            return volume;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed class HelperVolume : IVolumeAccess
    {
        private readonly NamedPipeClientStream _stream;
        private readonly BinaryReader _reader;
        private readonly BinaryWriter _writer;

        public HelperVolume(NamedPipeClientStream stream)
        {
            _stream = stream;
            _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        }

        public void Handshake(string token, string root)
        {
            _writer.Write(token);
            _writer.Write(OpOpen);
            _writer.Write(root);
            _writer.Flush();
            ReadStatus();
        }

        private void ReadStatus()
        {
            int code = _reader.ReadInt32();
            if (code != 0)
                throw new VolumeException(code, _reader.ReadString());
        }

        private T Call<T>(byte op, Action<BinaryWriter>? args, Func<BinaryReader, T> result)
        {
            try
            {
                _writer.Write(op);
                args?.Invoke(_writer);
                _writer.Flush();
                ReadStatus();
                return result(_reader);
            }
            catch (EndOfStreamException ex)
            {
                throw new IOException("관리자 권한 도우미와의 연결이 끊어졌습니다.", ex);
            }
        }

        public UsnJournalInfo QueryJournal() =>
            Call(OpQueryJournal, null, r => new UsnJournalInfo(r.ReadUInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64()));

        public void CreateJournal() => Call(OpCreateJournal, null, _ => 0);

        public IEnumerable<MftEntry> EnumMft(long highUsn)
        {
            Call(OpEnumMft, w => w.Write(highUsn), _ => 0);
            while (true)
            {
                int n = _reader.ReadInt32();
                if (n == 0)
                    yield break;
                if (n < 0)
                    throw new VolumeException(_reader.ReadInt32(), _reader.ReadString());

                for (int i = 0; i < n; i++)
                    yield return new MftEntry(_reader.ReadUInt64(), _reader.ReadUInt64(), _reader.ReadUInt32(), _reader.ReadString());
            }
        }

        public (long NextUsn, List<UsnRecord> Records) ReadJournal(long startUsn, ulong journalId) =>
            Call(OpReadJournal, w =>
            {
                w.Write(startUsn);
                w.Write(journalId);
            }, r =>
            {
                long next = r.ReadInt64();
                int count = r.ReadInt32();
                var list = new List<UsnRecord>(count);
                for (int i = 0; i < count; i++)
                    list.Add(new UsnRecord(r.ReadUInt64(), r.ReadUInt64(), r.ReadInt64(), r.ReadUInt32(), r.ReadUInt32(), r.ReadString()));
                return (next, list);
            });

        public IEnumerable<List<MftMeta>> ScanMetadata(int[]? wanted, CancellationToken cancel)
        {
            Call(OpScanMetadata, w =>
            {
                w.Write(wanted?.Length ?? -1);
                if (wanted is not null)
                {
                    foreach (int x in wanted)
                        w.Write(x);
                }
            }, _ => 0);

            while (true)
            {
                int n = _reader.ReadInt32();
                if (n == 0)
                    yield break;
                if (n < 0)
                    throw new VolumeException(_reader.ReadInt32(), _reader.ReadString());

                var batch = new List<MftMeta>(n);
                for (int i = 0; i < n; i++)
                    batch.Add(new MftMeta(_reader.ReadInt32(), _reader.ReadInt64(), _reader.ReadDouble(), _reader.ReadDouble(), _reader.ReadDouble()));
                yield return batch;
                // A cancelled scan still has to drain the stream so the connection stays usable; closing it is simpler.
                if (cancel.IsCancellationRequested)
                {
                    _stream.Dispose();
                    yield break;
                }
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}

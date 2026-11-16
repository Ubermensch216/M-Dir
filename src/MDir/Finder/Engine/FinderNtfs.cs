using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MDir.Finder.Engine;

/// <summary>Where the USN journal of a volume stands.</summary>
internal readonly record struct UsnJournalInfo(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);

/// <summary>A file of the volume as the MFT enumeration lists it.</summary>
internal readonly record struct MftEntry(ulong Frn, ulong ParentFrn, uint Attrs, string Name);

/// <summary>One USN journal record.</summary>
internal readonly record struct UsnRecord(ulong Frn, ulong ParentFrn, long Usn, uint Reason, uint Attrs, string Name);

/// <summary>Size and times of one MFT record (<c>Size</c> is -1 for folders).</summary>
internal readonly record struct MftMeta(int Record, long Size, double CTime, double MTime, double ATime);

/// <summary>A privileged view of one NTFS volume: direct (when elevated) or through the elevated helper process.</summary>
internal interface IVolumeAccess : IDisposable
{
    UsnJournalInfo QueryJournal();

    void CreateJournal();

    IEnumerable<MftEntry> EnumMft(long highUsn);

    (long NextUsn, List<UsnRecord> Records) ReadJournal(long startUsn, ulong journalId);

    /// <summary>Batches of metadata for the in-use records; <paramref name="wanted"/> (record number -> entry id or -1) skips the unneeded ones.</summary>
    IEnumerable<List<MftMeta>> ScanMetadata(int[]? wanted, CancellationToken cancel);
}

/// <summary>An error from Windows with its code (<see cref="Win32Exception.NativeErrorCode"/>), e.g. access denied or a wrapped journal.</summary>
internal sealed class VolumeException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

internal static class FinderNtfs
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    public const uint ReasonDataOverwrite = 0x1, ReasonDataExtend = 0x2, ReasonDataTruncation = 0x4,
        ReasonFileCreate = 0x100, ReasonFileDelete = 0x200, ReasonRenameNewName = 0x2000, ReasonBasicInfoChange = 0x8000;

    public const ulong MftRecordMask = 0x0000FFFFFFFFFFFF;
    public const ulong RootRecord = 5;
    public const ulong FirstUserRecord = 24; // records below are NTFS metafiles (the root, 5, is handled apart)

    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlCreateUsnJournal = 0x000900E7;
    private const int UsnHeaderSize = 60;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long newPosition, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int toRead, out int read, IntPtr overlapped);

    [DllImport("shell32.dll")]
    private static extern bool IsUserAnAdmin();

    public static bool IsElevated()
    {
        try
        {
            return IsUserAnAdmin();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Roots of the fixed drives, e.g. <c>C:\</c>.</summary>
    public static List<string> FixedDrives() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name)];

    /// <summary>True for a drive root (<c>C:</c> or <c>C:\</c>) formatted as NTFS.</summary>
    public static bool IsNtfsRoot(string path)
    {
        string p = path.TrimEnd('\\', '/');
        if (p.Length != 2 || p[1] != ':')
            return false;

        try
        {
            var drive = new DriveInfo(p);
            return drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static SafeFileHandle OpenVolume(string root)
    {
        string letter = root.TrimEnd('\\', '/')[..2];
        SafeFileHandle h = CreateFileW(@"\\.\" + letter, 0x80000000, 0x7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new VolumeException(error, error == ErrorAccessDenied ? $"{letter} 드라이브를 열려면 관리자 권한이 필요합니다." : $"{letter} 드라이브를 열 수 없습니다 (오류 {error}).");
        }

        return h;
    }

    internal static byte[] IoControl(SafeFileHandle h, uint code, byte[]? input, int outputSize)
    {
        byte[]? output = outputSize > 0 ? new byte[outputSize] : null;
        if (!DeviceIoControl(h, code, input, input?.Length ?? 0, output, outputSize, out int returned, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new VolumeException(error, new Win32Exception(error).Message);
        }

        return output is null ? [] : output.AsSpan(0, returned).ToArray();
    }

    internal static byte[] ReadAt(SafeFileHandle h, long offset, int length)
    {
        if (!SetFilePointerEx(h, offset, out _, 0))
            throw new VolumeException(Marshal.GetLastWin32Error(), "볼륨 위치를 지정하지 못했습니다.");

        byte[] buffer = new byte[length];
        if (!ReadFile(h, buffer, length, out int read, IntPtr.Zero))
            throw new VolumeException(Marshal.GetLastWin32Error(), "볼륨을 읽지 못했습니다.");

        // A read of an aligned block returns all of it, except at the end of the volume.
        return read == length ? buffer : buffer.AsSpan(0, read).ToArray();
    }

    // ---- USN records ----

    /// <summary>Reads a USN_RECORD_V2 stream (after <paramref name="offset"/> bytes of header).</summary>
    public static IEnumerable<UsnRecord> ParseUsnRecords(byte[] buffer, int offset = 0, int? length = null)
    {
        int n = length ?? buffer.Length;
        while (offset + UsnHeaderSize <= n)
        {
            ReadOnlySpan<byte> span = buffer.AsSpan(offset);
            uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(span);
            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            if (recordLength < UsnHeaderSize || offset + recordLength > n)
                yield break;

            if (major == 2)
            {
                ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
                ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
                long usn = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(span[52..]);
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
                int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[58..]);
                if (nameOffset + nameLength <= recordLength)
                    yield return new UsnRecord(frn, parent, usn, reason, attrs, Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength));
            }

            offset += (int)((recordLength + 7) & ~7u);
        }
    }

    internal static UsnJournalInfo QueryJournal(SafeFileHandle h)
    {
        byte[] data = IoControl(h, FsctlQueryUsnJournal, null, 80);
        return new UsnJournalInfo(BitConverter.ToUInt64(data, 0), BitConverter.ToInt64(data, 8), BitConverter.ToInt64(data, 16), BitConverter.ToInt64(data, 24));
    }

    internal static void CreateJournal(SafeFileHandle h)
    {
        byte[] input = new byte[16];
        BitConverter.TryWriteBytes(input.AsSpan(0, 8), 32UL << 20);
        BitConverter.TryWriteBytes(input.AsSpan(8, 8), 8UL << 20);
        IoControl(h, FsctlCreateUsnJournal, input, 0);
    }

    internal static IEnumerable<MftEntry> EnumMft(SafeFileHandle h, long highUsn)
    {
        ulong start = 0;
        while (true)
        {
            byte[] input = new byte[24];
            BitConverter.TryWriteBytes(input.AsSpan(0, 8), start);
            BitConverter.TryWriteBytes(input.AsSpan(16, 8), highUsn);
            byte[] data;
            try
            {
                data = IoControl(h, FsctlEnumUsnData, input, 1 << 20);
            }
            catch (VolumeException ex) when (ex.Code == ErrorHandleEof)
            {
                yield break;
            }

            if (data.Length <= 8)
                yield break;

            start = BitConverter.ToUInt64(data, 0);
            foreach (UsnRecord r in ParseUsnRecords(data, 8))
                yield return new MftEntry(r.Frn, r.ParentFrn, r.Attrs, r.Name);
        }
    }

    internal static (long, List<UsnRecord>) ReadJournal(SafeFileHandle h, long startUsn, ulong journalId)
    {
        byte[] input = new byte[40];
        BitConverter.TryWriteBytes(input.AsSpan(0, 8), startUsn);
        BitConverter.TryWriteBytes(input.AsSpan(8, 4), 0xFFFFFFFFu); // every reason
        BitConverter.TryWriteBytes(input.AsSpan(12, 4), 1u); // only when the file is closed
        BitConverter.TryWriteBytes(input.AsSpan(32, 8), journalId);
        byte[] data = IoControl(h, FsctlReadUsnJournal, input, 1 << 18);
        return (BitConverter.ToInt64(data, 0), [.. ParseUsnRecords(data, 8)]);
    }
}

/// <summary>Direct access to one volume through its device handle (needs administrator rights).</summary>
internal sealed class LocalVolume : IVolumeAccess
{
    private readonly string _root;
    private readonly SafeFileHandle _handle;

    public LocalVolume(string root)
    {
        _root = root;
        _handle = FinderNtfs.OpenVolume(root);
    }

    public UsnJournalInfo QueryJournal() => FinderNtfs.QueryJournal(_handle);

    public void CreateJournal() => FinderNtfs.CreateJournal(_handle);

    public IEnumerable<MftEntry> EnumMft(long highUsn) => FinderNtfs.EnumMft(_handle, highUsn);

    public (long NextUsn, List<UsnRecord> Records) ReadJournal(long startUsn, ulong journalId) => FinderNtfs.ReadJournal(_handle, startUsn, journalId);

    public IEnumerable<List<MftMeta>> ScanMetadata(int[]? wanted, CancellationToken cancel)
    {
        // The $MFT is read through a second handle: the journal calls above keep using this one.
        using SafeFileHandle raw = FinderNtfs.OpenVolume(_root);
        foreach (List<MftMeta> batch in FinderMftParser.ScanMetadata((offset, length) => FinderNtfs.ReadAt(raw, offset, length), wanted, cancel))
            yield return batch;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Reads size and timestamps straight from the $MFT (port of <c>mftparse.py</c>): no per-file lookup is needed.</summary>
internal static class FinderMftParser
{
    private const long EpochDelta = 116444736000000000; // FILETIME of 1970-01-01
    private const int Chunk = 4 << 20;

    internal sealed record Layout(int RecordSize, int Sector, List<(long Start, long Length)> Runs, int Cluster, long DataSize);

    internal static double FileTimeToEpoch(long ft) => ft <= 0 ? 0.0 : (ft - EpochDelta) / 10_000_000.0;

    internal static bool ApplyFixups(byte[] rec, int start, int length, int sector)
    {
        int off = BitConverter.ToUInt16(rec, start + 4), cnt = BitConverter.ToUInt16(rec, start + 6);
        if (cnt < 2 || off + (cnt * 2) > length)
            return false;

        byte u0 = rec[start + off], u1 = rec[start + off + 1];
        for (int k = 1; k < cnt; k++)
        {
            int end = k * sector;
            if (end > length)
                return false;
            if (rec[start + end - 2] != u0 || rec[start + end - 1] != u1)
                return false; // a torn write
            rec[start + end - 2] = rec[start + off + (k * 2)];
            rec[start + end - 1] = rec[start + off + (k * 2) + 1];
        }

        return true;
    }

    /// <summary>Decodes an NTFS runlist into (lcn, length in clusters) pairs; sparse runs are skipped.</summary>
    internal static List<(long Lcn, long Length)> ParseRuns(byte[] buf, int pos, int limit)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        while (pos < limit && buf[pos] != 0)
        {
            int hdr = buf[pos];
            int ls = hdr & 0xF, os = hdr >> 4;
            long length = 0;
            for (int k = 0; k < ls; k++)
                length |= (long)buf[pos + 1 + k] << (8 * k);
            long delta = 0;
            for (int k = 0; k < os; k++)
                delta |= (long)buf[pos + 1 + ls + k] << (8 * k);
            if (os > 0 && (buf[pos + ls + os] & 0x80) != 0)
                delta -= 1L << (8 * os); // sign extend
            pos += 1 + ls + os;
            if (os == 0)
                continue; // sparse
            lcn += delta;
            runs.Add((lcn, length));
        }

        return runs;
    }

    internal static Layout ReadLayout(Func<long, int, byte[]> read)
    {
        byte[] boot = read(0, 512);
        if (boot.Length < 512 || Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            throw new IOException("NTFS 부트 섹터가 아닙니다.");

        int bps = BitConverter.ToUInt16(boot, 0x0B);
        int spc = boot[0x0D];
        int cluster = bps * spc;
        long mftLcn = BitConverter.ToInt64(boot, 0x30);
        int cpr = (sbyte)boot[0x40];
        int recSize = cpr > 0 ? cpr * cluster : 1 << -cpr;
        byte[] raw = read(mftLcn * cluster, (recSize + bps - 1) / bps * bps);
        byte[] rec = raw.AsSpan(0, recSize).ToArray();
        if (rec[0] != 'F' || rec[1] != 'I' || rec[2] != 'L' || rec[3] != 'E' || !ApplyFixups(rec, 0, rec.Length, bps))
            throw new IOException("$MFT의 첫 레코드를 읽을 수 없습니다.");

        int pos = BitConverter.ToUInt16(rec, 0x14);
        while (pos + 8 <= rec.Length)
        {
            uint atype = BitConverter.ToUInt32(rec, pos), alen = BitConverter.ToUInt32(rec, pos + 4);
            if (atype == 0xFFFFFFFF || alen == 0)
                break;
            if (atype == 0x80 && rec[pos + 8] != 0) // non-resident $DATA
            {
                int runOffset = BitConverter.ToUInt16(rec, pos + 0x20);
                long dataSize = BitConverter.ToInt64(rec, pos + 0x30);
                var runs = ParseRuns(rec, pos + runOffset, rec.Length).Select(r => (r.Lcn * cluster, r.Length * cluster)).ToList();
                return new Layout(recSize, bps, runs, cluster, dataSize);
            }

            pos += (int)alen;
        }

        throw new IOException("$MFT에 데이터 스트림이 없습니다.");
    }

    /// <summary>(size, ctime, mtime, atime) of an in-use base record, else null. Size is -1 for a folder.</summary>
    internal static (long Size, double C, double M, double A)? ParseRecord(byte[] rec, int start, int length)
    {
        int flags = BitConverter.ToUInt16(rec, start + 0x16);
        if ((flags & 1) == 0 || BitConverter.ToUInt64(rec, start + 0x20) != 0)
            return null;

        int pos = BitConverter.ToUInt16(rec, start + 0x14);
        (double, double, double)? times = null;
        long size = -1;
        bool haveData = false;
        while (pos + 8 <= length)
        {
            uint atype = BitConverter.ToUInt32(rec, start + pos), alen = BitConverter.ToUInt32(rec, start + pos + 4);
            if (atype == 0xFFFFFFFF || alen == 0 || pos + alen > length)
                break;

            if (atype == 0x10 && rec[start + pos + 8] == 0)
            {
                int co = BitConverter.ToUInt16(rec, start + pos + 0x14);
                int at = start + pos + co;
                times = (FileTimeToEpoch(BitConverter.ToInt64(rec, at)), FileTimeToEpoch(BitConverter.ToInt64(rec, at + 8)), FileTimeToEpoch(BitConverter.ToInt64(rec, at + 24)));
            }
            else if (atype == 0x20)
            {
                return null; // attributes spread over extension records
            }
            else if (atype == 0x80 && rec[start + pos + 9] == 0 && !haveData) // the unnamed $DATA
            {
                haveData = true;
                size = rec[start + pos + 8] == 0 ? BitConverter.ToUInt32(rec, start + pos + 0x10) : BitConverter.ToInt64(rec, start + pos + 0x30);
            }

            pos += (int)alen;
        }

        if (times is null)
            return null;

        (double c, double m, double a) = times.Value;
        return ((flags & 2) != 0 ? -1 : (haveData ? size : 0), c, m, a);
    }

    public static IEnumerable<List<MftMeta>> ScanMetadata(Func<long, int, byte[]> read, int[]? wanted, CancellationToken cancel, int batch = 20000)
    {
        Layout lay = ReadLayout(read);
        int rs = lay.RecordSize;
        long recno = 0;
        var output = new List<MftMeta>();
        int perChunk = Math.Max(rs, Chunk / rs * rs);
        foreach ((long start, long length) in lay.Runs)
        {
            long done = 0;
            while (done < length)
            {
                if (cancel.IsCancellationRequested)
                    yield break;

                int n = (int)Math.Min(perChunk, length - done);
                byte[] buf = read(start + done, n);
                if (buf.Length == 0)
                    break;

                for (int o = 0; o + rs <= buf.Length; o += rs)
                {
                    long r = recno + (o / rs);
                    if (buf[o] != 'F' || buf[o + 1] != 'I' || buf[o + 2] != 'L' || buf[o + 3] != 'E')
                        continue;
                    if (wanted is not null && (r >= wanted.Length || wanted[r] < 0))
                        continue;
                    if (!ApplyFixups(buf, o, rs, lay.Sector))
                        continue;
                    if (ParseRecord(buf, o, rs) is { } p)
                    {
                        output.Add(new MftMeta((int)r, p.Size, p.C, p.M, p.A));
                        if (output.Count >= batch)
                        {
                            yield return output;
                            output = [];
                        }
                    }
                }

                recno += n / rs;
                done += n;
            }
        }

        if (output.Count > 0)
            yield return output;
    }
}

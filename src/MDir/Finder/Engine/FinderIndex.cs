using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MDir.Finder.Engine;

/// <summary>One indexed place: a folder, an NTFS volume (read through its MFT) or a drive scanned by walking it.</summary>
internal sealed class FinderRoot
{
    public string Path { get; set; } = string.Empty;

    /// <summary>"folder" (walked), "ntfs" (MFT + USN journal).</summary>
    public string Kind { get; set; } = "folder";

    public int RootId { get; set; } = -1;
    public ulong JournalId { get; set; }
    public long NextUsn { get; set; }
}

/// <summary>
/// The file index: every entry is a position in parallel arrays (name, parent, attributes, size, times, NTFS file
/// reference number). Deleted entries are tombstoned and dropped when the index is saved. Port of M-Finder's
/// <c>index.py</c>. <c>MTime == 0</c> means size and dates have not been read yet, <c>-1</c> that reading failed.
/// A folder's size is -1 until <see cref="ComputeFolderSizes"/> has run.
/// </summary>
internal sealed class FinderIndex
{
    public const uint DirectoryAttribute = 0x10;
    private static readonly byte[] Magic = "MDFI"u8.ToArray();
    private const int DatabaseVersion = 1;

    public readonly object Lock = new();
    public long Rev;
    public List<string> Names = [];
    public List<string> Lower = [];
    public List<int> Parent = [];
    public List<uint> Attrs = [];
    public List<long> Size = [];
    public List<double> MTime = [];
    public List<double> CTime = [];
    public List<double> ATime = [];
    public List<ulong> Frn = [];
    public List<ushort> Vol = [];
    public List<bool> Alive = [];
    public List<FinderRoot> Roots = [];
    public List<Dictionary<ulong, int>> FrnMaps = [];
    public bool FolderSizesReady;
    public long FolderSizesRev = -1;

    /// <summary>Settings the index was built with (see <see cref="FinderEngineSettings.Signature"/>), saved with it.</summary>
    public string Signature = string.Empty;

    private bool[]? _hasChild;
    private long _hasChildRev = -1;
    private Dictionary<int, Dictionary<string, int>>? _children;

    public int Count => Names.Count;

    public int LiveCount()
    {
        lock (Lock)
        {
            int n = 0;
            foreach (bool alive in Alive)
                n += alive ? 1 : 0;
            return n;
        }
    }

    public bool IsDir(int i) => (Attrs[i] & DirectoryAttribute) != 0;

    /// <summary>Registers an indexed root; returns its volume number and root entry id.</summary>
    public (int Vol, int RootId) AddRoot(string path, string kind)
    {
        lock (Lock)
        {
            int vol = Roots.Count;
            Roots.Add(new FinderRoot { Path = path, Kind = kind });
            FrnMaps.Add([]);
            string name = path.TrimEnd('\\', '/');
            if (name.Length == 0)
                name = path;
            int rid = Add(name, -1, DirectoryAttribute, mtime: -1.0, vol: vol);
            Roots[vol].RootId = rid;
            return (vol, rid);
        }
    }

    public int Add(string name, int parent, uint attrs, long size = -1, double mtime = 0.0, ulong frn = 0, int vol = 0, double ctime = 0.0, double atime = 0.0)
    {
        int i = Names.Count;
        Names.Add(name);
        string lower = name.ToLowerInvariant();
        Lower.Add(lower);
        Parent.Add(parent);
        Attrs.Add(attrs);
        Size.Add(size);
        MTime.Add(mtime);
        CTime.Add(ctime);
        ATime.Add(atime);
        Frn.Add(frn);
        Vol.Add((ushort)vol);
        Alive.Add(true);
        if (_children is not null && parent >= 0)
        {
            if (!_children.TryGetValue(parent, out Dictionary<string, int>? d))
                _children[parent] = d = [];
            d[lower] = i;
        }

        return i;
    }

    /// <summary>Renames and/or moves an entry, keeping the children map consistent.</summary>
    public void Relocate(int i, string name, int parent)
    {
        lock (Lock)
        {
            if (_children is not null && _children.TryGetValue(Parent[i], out Dictionary<string, int>? old) && old.TryGetValue(Lower[i], out int at) && at == i)
                old.Remove(Lower[i]);
            Names[i] = name;
            Lower[i] = name.ToLowerInvariant();
            Parent[i] = parent;
            if (_children is not null && parent >= 0)
            {
                if (!_children.TryGetValue(parent, out Dictionary<string, int>? d))
                    _children[parent] = d = [];
                d[Lower[i]] = i;
            }

            Rev++;
        }
    }

    public void Remove(int i)
    {
        lock (Lock)
        {
            if (!Alive[i])
                return;

            Alive[i] = false;
            if (_children is not null && _children.TryGetValue(Parent[i], out Dictionary<string, int>? d) && d.TryGetValue(Lower[i], out int at) && at == i)
                d.Remove(Lower[i]);
            int v = Vol[i];
            if (v < FrnMaps.Count && Frn[i] != 0 && FrnMaps[v].TryGetValue(Frn[i], out int mapped) && mapped == i)
                FrnMaps[v].Remove(Frn[i]);
            Rev++;
        }
    }

    /// <summary>Tombstones an entry and everything below it; returns how many were removed.</summary>
    public int RemoveTree(int i)
    {
        lock (Lock)
        {
            Dictionary<int, Dictionary<string, int>> kids = ChildrenMap();
            var stack = new Stack<int>();
            stack.Push(i);
            int n = 0;
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                // Collect the children first: Remove edits the map.
                List<int>? sub = kids.TryGetValue(cur, out Dictionary<string, int>? d) ? [.. d.Values] : null;
                if (Alive[cur])
                {
                    Remove(cur);
                    n++;
                }

                if (sub is not null)
                {
                    foreach (int child in sub)
                        stack.Push(child);
                }
            }

            return n;
        }
    }

    public void Touch() => Rev++;

    // ---- paths ----

    public string PathOf(int i)
    {
        var parts = new List<string>();
        while (i >= 0)
        {
            parts.Add(Names[i]);
            i = Parent[i];
        }

        parts.Reverse();
        return string.Join('\\', parts);
    }

    public string FullPath(int i)
    {
        string p = PathOf(i);
        if (Parent[i] < 0 && p.Length == 2 && p[1] == ':')
            p += "\\";
        return p;
    }

    public string ParentPath(int i) => Parent[i] < 0 ? string.Empty : FullPath(Parent[i]);

    // ---- children lookup (live watcher) ----

    public Dictionary<int, Dictionary<string, int>> ChildrenMap()
    {
        lock (Lock)
        {
            if (_children is null)
            {
                var m = new Dictionary<int, Dictionary<string, int>>();
                for (int i = 0; i < Parent.Count; i++)
                {
                    int p = Parent[i];
                    if (p >= 0 && Alive[i])
                    {
                        if (!m.TryGetValue(p, out Dictionary<string, int>? d))
                            m[p] = d = [];
                        d[Lower[i]] = i;
                    }
                }

                _children = m;
            }

            return _children;
        }
    }

    public int? ChildOf(int parent, string name) =>
        ChildrenMap().TryGetValue(parent, out Dictionary<string, int>? d) && d.TryGetValue(name.ToLowerInvariant(), out int i) ? i : null;

    /// <summary>Entry id of an absolute path under an indexed root, else null.</summary>
    public int? Resolve(string path)
    {
        string p = path.Replace('/', '\\');
        string low = p.ToLowerInvariant().TrimEnd('\\');
        FinderRoot? best = null;
        string bestPath = string.Empty;
        foreach (FinderRoot r in Roots)
        {
            string rp = r.Path.TrimEnd('\\', '/').ToLowerInvariant();
            if ((low == rp || low.StartsWith(rp + "\\", StringComparison.Ordinal)) && (best is null || rp.Length > bestPath.Length))
            {
                best = r;
                bestPath = rp;
            }
        }

        if (best is null)
            return null;

        int? cur = best.RootId;
        string rest = p.TrimEnd('\\')[bestPath.Length..].Trim('\\');
        foreach (string part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = ChildOf(cur.Value, part);
            if (cur is null)
                return null;
        }

        return Alive[cur.Value] ? cur : null;
    }

    // ---- lazy metadata (size / dates) ----

    public void Meta(int i)
    {
        if (MTime[i] != 0.0)
            return;

        try
        {
            string path = FullPath(i);
            FileSystemInfo info = (Attrs[i] & DirectoryAttribute) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                MTime[i] = -1.0;
                return;
            }

            if (info is FileInfo file)
                Size[i] = file.Length;
            CTime[i] = ToEpoch(info.CreationTimeUtc);
            ATime[i] = ToEpoch(info.LastAccessTimeUtc);
            double m = ToEpoch(info.LastWriteTimeUtc);
            MTime[i] = m == 0 ? 0.001 : m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MTime[i] = -1.0;
        }
    }

    public static double ToEpoch(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    public void InvalidateMeta(int i)
    {
        MTime[i] = 0.0;
        if ((Attrs[i] & DirectoryAttribute) == 0)
            Size[i] = -1;
    }

    /// <summary>Reads size and dates of <paramref name="ids"/> that are still unknown; false when cancelled.</summary>
    public bool EnsureMeta(IEnumerable<int> ids, CancellationToken cancel = default)
    {
        int n = 0;
        foreach (int i in ids)
        {
            if (MTime[i] != 0.0)
                continue;

            if (++n % 2048 == 0 && cancel.IsCancellationRequested)
                return false;
            Meta(i);
        }

        return true;
    }

    // ---- folder sizes ----

    /// <summary>Sums file sizes into every ancestor folder (bottom-up pass).</summary>
    public bool ComputeFolderSizes(CancellationToken cancel = default)
    {
        int n;
        long rev;
        int[] parent;
        uint[] attrs;
        long[] size;
        bool[] alive;
        bool complete;
        lock (Lock)
        {
            rev = Rev;
            n = Names.Count;
            parent = [.. Parent];
            attrs = [.. Attrs];
            size = [.. Size];
            alive = [.. Alive];
            complete = !MTime.Contains(0.0);
        }

        int[] pend = new int[n];
        long[] total = new long[n];
        for (int i = 0; i < n; i++)
        {
            if (!alive[i])
                continue;
            int p = parent[i];
            if (p >= 0)
                pend[p]++;
            if ((attrs[i] & DirectoryAttribute) == 0 && size[i] > 0)
                total[i] = size[i];
        }

        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (alive[i] && pend[i] == 0)
                stack.Push(i);
        }

        int done = 0;
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int p = parent[i];
            if (p >= 0)
            {
                total[p] += total[i];
                if (--pend[p] == 0)
                    stack.Push(p);
            }

            if (++done % 100000 == 0 && cancel.IsCancellationRequested)
                return false;
        }

        lock (Lock)
        {
            int m = Math.Min(n, Names.Count);
            for (int i = 0; i < m; i++)
            {
                if (alive[i] && (attrs[i] & DirectoryAttribute) != 0 && Alive[i])
                    Size[i] = total[i];
            }

            FolderSizesReady = complete;
            FolderSizesRev = rev;
        }

        return true;
    }

    /// <summary>True for every entry that has at least one live child.</summary>
    public bool[] ChildFlags()
    {
        lock (Lock)
        {
            if (_hasChild is null || _hasChildRev != Rev || _hasChild.Length != Names.Count)
            {
                bool[] has = new bool[Names.Count];
                for (int i = 0; i < Parent.Count; i++)
                {
                    int p = Parent[i];
                    if (p >= 0 && Alive[i])
                        has[p] = true;
                }

                _hasChild = has;
                _hasChildRev = Rev;
            }

            return _hasChild;
        }
    }

    /// <summary>How many entries still lack size and dates.</summary>
    public int Unfetched()
    {
        lock (Lock)
            return MTime.Count(t => t == 0.0);
    }

    // ---- persistence ----

    public void Save(string path)
    {
        byte[] body;
        byte[] meta;
        lock (Lock)
        {
            var keep = new List<int>(Names.Count);
            for (int i = 0; i < Names.Count; i++)
            {
                if (Alive[i])
                    keep.Add(i);
            }

            int[] remap = new int[Names.Count];
            Array.Fill(remap, -1);
            for (int n = 0; n < keep.Count; n++)
                remap[keep[n]] = n;

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(keep.Count);
                foreach (int i in keep)
                    w.Write(Names[i]);
                foreach (int i in keep)
                    w.Write(Parent[i] >= 0 ? remap[Parent[i]] : -1);
                foreach (int i in keep) w.Write(Attrs[i]);
                foreach (int i in keep) w.Write(Size[i]);
                foreach (int i in keep) w.Write(MTime[i]);
                foreach (int i in keep) w.Write(CTime[i]);
                foreach (int i in keep) w.Write(ATime[i]);
                foreach (int i in keep) w.Write(Frn[i]);
                foreach (int i in keep) w.Write(Vol[i]);
            }

            body = ms.ToArray();
            var roots = Roots.Select(r => new RootDto(r.Path, r.Kind, r.RootId >= 0 && r.RootId < remap.Length ? remap[r.RootId] : -1, r.JournalId, r.NextUsn)).ToList();
            meta = JsonSerializer.SerializeToUtf8Bytes(new MetaDto(roots, FolderSizesReady, Signature));
        }

        string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                f.Write(Magic);
                var header = new byte[8];
                BitConverter.TryWriteBytes(header.AsSpan(0, 4), DatabaseVersion);
                BitConverter.TryWriteBytes(header.AsSpan(4, 4), meta.Length);
                f.Write(header);
                f.Write(meta);
                using var z = new DeflateStream(f, CompressionLevel.Fastest, leaveOpen: true);
                z.Write(body);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new FinderDatabaseException($"색인 파일을 저장하지 못했습니다: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FinderDatabaseException($"색인 파일을 저장하지 못했습니다: {ex.Message}", ex);
        }
    }

    public static FinderIndex Load(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            throw new FinderDatabaseException("색인 파일이 없습니다.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new FinderDatabaseException("색인 파일이 없습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FinderDatabaseException($"색인 파일을 읽지 못했습니다: {ex.Message}", ex);
        }

        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new FinderDatabaseException("M-Dir 색인 파일이 아닙니다.");
        int version = BitConverter.ToInt32(data, 4);
        int metaLen = BitConverter.ToInt32(data, 8);
        if (version != DatabaseVersion)
            throw new FinderDatabaseException("지원하지 않는 색인 파일 버전입니다.");

        try
        {
            MetaDto meta = JsonSerializer.Deserialize<MetaDto>(data.AsSpan(12, metaLen)) ?? throw new FinderDatabaseException("색인 파일이 손상되었습니다.");
            using var raw = new MemoryStream(data, 12 + metaLen, data.Length - 12 - metaLen);
            using var z = new DeflateStream(raw, CompressionMode.Decompress);
            using var r = new BinaryReader(z, Encoding.UTF8);
            int count = r.ReadInt32();
            if (count < 0 || count > 500_000_000)
                throw new FinderDatabaseException("색인 파일이 손상되었습니다.");

            var idx = new FinderIndex();
            idx.Names = new List<string>(count);
            for (int i = 0; i < count; i++)
                idx.Names.Add(r.ReadString());
            idx.Lower = new List<string>(count);
            foreach (string name in idx.Names)
                idx.Lower.Add(name.ToLowerInvariant());
            idx.Parent = ReadColumn(r, count, x => x.ReadInt32());
            idx.Attrs = ReadColumn(r, count, x => x.ReadUInt32());
            idx.Size = ReadColumn(r, count, x => x.ReadInt64());
            idx.MTime = ReadColumn(r, count, x => x.ReadDouble());
            idx.CTime = ReadColumn(r, count, x => x.ReadDouble());
            idx.ATime = ReadColumn(r, count, x => x.ReadDouble());
            idx.Frn = ReadColumn(r, count, x => x.ReadUInt64());
            idx.Vol = ReadColumn(r, count, x => x.ReadUInt16());
            idx.Alive = new List<bool>(Enumerable.Repeat(true, count));
            idx.Roots = [.. meta.Roots.Select(x => new FinderRoot { Path = x.Path, Kind = x.Kind, RootId = x.RootId, JournalId = x.JournalId, NextUsn = x.NextUsn })];
            idx.FolderSizesReady = meta.FolderSizes;
            idx.Signature = meta.Signature ?? string.Empty;
            idx.FrnMaps = [.. idx.Roots.Select(_ => new Dictionary<ulong, int>())];
            for (int i = 0; i < count; i++)
            {
                ulong f = idx.Frn[i];
                int v = idx.Vol[i];
                if (f != 0 && v < idx.Roots.Count && idx.Roots[v].Kind == "ntfs")
                    idx.FrnMaps[v][f] = i;
            }

            return idx;
        }
        catch (Exception ex) when (ex is JsonException or EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            throw new FinderDatabaseException("색인 파일이 손상되었습니다.", ex);
        }
    }

    private static List<T> ReadColumn<T>(BinaryReader r, int count, Func<BinaryReader, T> read)
    {
        var list = new List<T>(count);
        for (int i = 0; i < count; i++)
            list.Add(read(r));
        return list;
    }

    private sealed record RootDto(string Path, string Kind, int RootId, ulong JournalId, long NextUsn);

    private sealed record MetaDto(List<RootDto> Roots, bool FolderSizes, string? Signature);
}

internal sealed class FinderDatabaseException : Exception
{
    public FinderDatabaseException(string message) : base(message)
    {
    }

    public FinderDatabaseException(string message, Exception inner) : base(message, inner)
    {
    }
}

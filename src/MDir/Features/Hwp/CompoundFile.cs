using System.Text;

namespace MDir.Features.Hwp;

/// <summary>
/// A lenient reader of OLE compound (CFB) files: lists storages and reads streams by path.
/// Hardened with kordoc's lenient CFB safeguards (FAT size capping against file length, cycle detection,
/// mini-stream to regular-FAT fallback, and unique-name fallback when the directory sibling tree is damaged).
/// </summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int EntrySize = 128;
    private const int MaxStreamBytes = 100 * 1024 * 1024;
    private const int MaxDirEntries = 100_000;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly List<Entry> _entries = [];
    private uint[] _miniFat = [];
    private byte[] _miniStream = [];

    private sealed record Entry(string Name, int Type, int Left, int Right, int Child, uint Start, long Size);

    private CompoundFile(byte[] data)
    {
        _data = data;
        int sectorShift = BitConverter.ToUInt16(data, 30);
        int miniShift = BitConverter.ToUInt16(data, 32);
        if (sectorShift is < 7 or > 16 || miniShift > 16)
            throw new InvalidDataException("Not a compound file.");

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        _miniCutoff = BitConverter.ToUInt32(data, 56);
        if (_sectorSize is not (512 or 4096) || _miniSectorSize > _sectorSize)
            throw new InvalidDataException("Not a compound file.");

        // Cap FAT sector count by actual file size to prevent huge allocations on malformed headers.
        uint headerFatCount = BitConverter.ToUInt32(data, 44);
        long maxRealSectors = Math.Max(1L, ((data.Length - 512L) + _sectorSize - 1) / _sectorSize);
        int entriesPerSector = _sectorSize / 4;
        long maxFatSectors = Math.Max(1L, (maxRealSectors + entriesPerSector - 1) / entriesPerSector);
        int fatLimit = (int)Math.Min(Math.Min(headerFatCount > 0 ? headerFatCount : maxFatSectors, maxFatSectors), 10_000);

        // The FAT sectors are listed in the header (109 of them) and then in the DIFAT chain.
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < fatLimit; i++)
        {
            uint sector = BitConverter.ToUInt32(data, 76 + (i * 4));
            if (sector is FreeSector or EndOfChain)
                break;

            fatSectors.Add(sector);
        }

        uint difat = BitConverter.ToUInt32(data, 68);
        int perSector = entriesPerSector - 1;
        var seenDifat = new HashSet<uint>();
        for (int guard = 0; difat is not (FreeSector or EndOfChain) && guard < 10_000 && fatSectors.Count < fatLimit; guard++)
        {
            if (!seenDifat.Add(difat))
                break;

            long offsetLong = SectorOffsetLong(difat);
            if (offsetLong < 0 || offsetLong + _sectorSize > data.Length)
                break;

            int offset = (int)offsetLong;
            for (int i = 0; i < perSector && fatSectors.Count < fatLimit; i++)
            {
                uint sector = BitConverter.ToUInt32(data, offset + (i * 4));
                if (sector is FreeSector or EndOfChain)
                    continue;

                fatSectors.Add(sector);
            }

            difat = BitConverter.ToUInt32(data, offset + (perSector * 4));
        }

        _fat = new uint[fatSectors.Count * entriesPerSector];
        Array.Fill(_fat, FreeSector);
        for (int i = 0; i < fatSectors.Count; i++)
        {
            long offsetLong = SectorOffsetLong(fatSectors[i]);
            if (offsetLong >= 0 && offsetLong + _sectorSize <= data.Length)
                Buffer.BlockCopy(data, (int)offsetLong, _fat, i * _sectorSize, _sectorSize);
        }
    }

    /// <summary>The file, or null when <paramref name="data"/> is not a compound file.</summary>
    public static CompoundFile? TryOpen(byte[] data)
    {
        ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(signature))
            return null;

        try
        {
            var file = new CompoundFile(data);
            file.Load();
            return file;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The names of the streams directly inside the storage at <paramref name="storage"/> (a path).</summary>
    public IReadOnlyList<string> ListStreams(string storage)
    {
        if (Find(storage) is not { } parent)
            return [];

        var names = new List<string>();
        Walk(parent.Child, entry =>
        {
            if (entry.Type == 2)
                names.Add(entry.Name);
        });
        return names;
    }

    /// <summary>The contents of the stream at <paramref name="path"/> ("BodyText/Section0"), or null.</summary>
    public byte[]? ReadStream(string path)
    {
        if (Find(path) is not { Type: 2 } entry)
            return null;

        if (entry.Size <= 0)
            return [];

        if (entry.Size > MaxStreamBytes)
            return null;

        if (entry.Size < _miniCutoff)
        {
            byte[] mini = ReadMini(entry.Start, entry.Size);
            if (mini.Length > 0)
                return mini;
        }

        return ReadChain(entry.Start, entry.Size);
    }

    private void Load()
    {
        byte[] directory = ReadChain(BitConverter.ToUInt32(_data, 48), (long)MaxDirEntries * EntrySize);
        bool v3 = BitConverter.ToUInt16(_data, 26) <= 3;
        for (int offset = 0; offset + EntrySize <= directory.Length && _entries.Count < MaxDirEntries; offset += EntrySize)
        {
            int rawNameLen = BitConverter.ToUInt16(directory, offset + 64);
            int nameLength = rawNameLen is > 0 and <= 64 ? Math.Max(0, rawNameLen - 2) : 0;
            string name = nameLength > 0 ? Encoding.Unicode.GetString(directory, offset, nameLength) : string.Empty;
            long size = v3
                ? BitConverter.ToUInt32(directory, offset + 120)
                : (long)BitConverter.ToUInt64(directory, offset + 120);
            _entries.Add(new Entry(
                name,
                directory[offset + 66],
                BitConverter.ToInt32(directory, offset + 68),
                BitConverter.ToInt32(directory, offset + 72),
                BitConverter.ToInt32(directory, offset + 76),
                BitConverter.ToUInt32(directory, offset + 116),
                size));
        }

        if (_entries.Count == 0)
            return;

        uint miniFatStart = BitConverter.ToUInt32(_data, 60);
        if (miniFatStart is not (FreeSector or EndOfChain))
        {
            byte[] raw = ReadChain(miniFatStart, MaxStreamBytes);
            _miniFat = new uint[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, _miniFat, 0, _miniFat.Length * 4);
        }

        Entry root = _entries[0];
        if (root.Type == 5)
            _miniStream = ReadChain(root.Start, root.Size > 0 ? Math.Min(root.Size, MaxStreamBytes) : MaxStreamBytes);
    }

    private long SectorOffsetLong(uint sector) => ((long)sector + 1L) * _sectorSize;

    /// <summary>Reads a chain of regular (FAT) sectors, at most <paramref name="size"/> bytes, guarding against cycles.</summary>
    private byte[] ReadChain(uint start, long size)
    {
        if (start is EndOfChain or FreeSector || size <= 0)
            return [];

        long limit = Math.Min(size, MaxStreamBytes);
        var output = new MemoryStream();
        var visited = new HashSet<uint>();
        uint sector = start;
        while (sector is not (EndOfChain or FreeSector) && sector < _fat.Length && output.Length < limit)
        {
            if (!visited.Add(sector))
                break;

            long offsetLong = SectorOffsetLong(sector);
            if (offsetLong < 0 || offsetLong >= _data.Length)
                break;

            int offset = (int)offsetLong;
            int count = Math.Min(_sectorSize, _data.Length - offset);
            if (count <= 0)
                break;

            output.Write(_data, offset, count);
            sector = _fat[sector];
        }

        return Trim(output, limit);
    }

    private byte[] ReadMini(uint start, long size)
    {
        if (start is EndOfChain or FreeSector || size <= 0 || _miniFat.Length == 0 || _miniStream.Length == 0)
            return [];

        long limit = Math.Min(size, MaxStreamBytes);
        var output = new MemoryStream();
        var visited = new HashSet<uint>();
        uint sector = start;
        while (sector is not (EndOfChain or FreeSector) && sector < _miniFat.Length && output.Length < limit)
        {
            if (!visited.Add(sector))
                break;

            long offset = (long)sector * _miniSectorSize;
            if (offset < 0 || offset >= _miniStream.Length)
                break;

            int count = (int)Math.Min(_miniSectorSize, _miniStream.Length - offset);
            if (count <= 0)
                break;

            output.Write(_miniStream, (int)offset, count);
            sector = _miniFat[sector];
        }

        return Trim(output, limit);
    }

    private static byte[] Trim(MemoryStream output, long size)
    {
        byte[] result = output.ToArray();
        return size < result.Length ? result[..(int)size] : result;
    }

    private Entry? Find(string path)
    {
        if (_entries.Count == 0)
            return null;

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return _entries[0];

        Entry? current = _entries[0];
        foreach (string part in parts)
        {
            Entry? next = null;
            Walk(current.Child, entry =>
            {
                if (next is null && string.Equals(entry.Name, part, StringComparison.OrdinalIgnoreCase))
                    next = entry;
            });
            if (next is null)
            {
                current = null;
                break;
            }

            current = next;
        }

        if (current is not null)
            return current;

        // Lenient fallback when the sibling tree is damaged: match only if the final name is unique across all entries
        // (avoids confusing BodyText/Section0 with ViewText/Section0).
        string leaf = parts[^1];
        List<Entry> matches = _entries.Where(e => e.Type is 1 or 2 or 5 && string.Equals(e.Name, leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Visits a storage's children (a binary tree of siblings) without recursion, guarding against cycles.</summary>
    private void Walk(int root, Action<Entry> visit)
    {
        var stack = new Stack<int>();
        var seen = new HashSet<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            if (index < 0 || index >= _entries.Count || !seen.Add(index))
                continue;

            Entry entry = _entries[index];
            visit(entry);
            stack.Push(entry.Left);
            stack.Push(entry.Right);
        }
    }
}

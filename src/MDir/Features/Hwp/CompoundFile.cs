using System.Text;

namespace MDir.Features.Hwp;

/// <summary>A minimal reader of OLE compound (CFB) files: enough to list a storage and read a stream by path.</summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int EntrySize = 128;

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
        _sectorSize = 1 << BitConverter.ToUInt16(data, 30);
        _miniSectorSize = 1 << BitConverter.ToUInt16(data, 32);
        _miniCutoff = BitConverter.ToUInt32(data, 56);
        if (_sectorSize is not (512 or 4096) || _miniSectorSize > _sectorSize)
            throw new InvalidDataException("Not a compound file.");

        // The FAT sectors are listed in the header (109 of them) and then in the DIFAT chain.
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109; i++)
        {
            uint sector = BitConverter.ToUInt32(data, 76 + (i * 4));
            if (sector is FreeSector or EndOfChain)
                break;

            fatSectors.Add(sector);
        }

        uint difat = BitConverter.ToUInt32(data, 68);
        int perSector = (_sectorSize / 4) - 1;
        for (int guard = 0; difat is not (FreeSector or EndOfChain) && guard < 100_000; guard++)
        {
            int offset = SectorOffset(difat);
            for (int i = 0; i < perSector; i++)
            {
                uint sector = BitConverter.ToUInt32(data, offset + (i * 4));
                if (sector is FreeSector or EndOfChain)
                    break;

                fatSectors.Add(sector);
            }

            difat = BitConverter.ToUInt32(data, offset + (perSector * 4));
        }

        _fat = new uint[fatSectors.Count * (_sectorSize / 4)];
        for (int i = 0; i < fatSectors.Count; i++)
        {
            int offset = SectorOffset(fatSectors[i]);
            if (offset + _sectorSize <= data.Length)
                Buffer.BlockCopy(data, offset, _fat, i * _sectorSize, _sectorSize);
        }
    }

    /// <summary>The file, or null when <paramref name="data"/> is not a compound file.</summary>
    public static CompoundFile? TryOpen(byte[] data)
    {
        ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(signature))
            return null;

        var file = new CompoundFile(data);
        file.Load();
        return file;
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

        return entry.Size < _miniCutoff ? ReadMini(entry.Start, entry.Size) : ReadChain(entry.Start, entry.Size);
    }

    private void Load()
    {
        byte[] directory = ReadChain(BitConverter.ToUInt32(_data, 48), long.MaxValue);
        for (int offset = 0; offset + EntrySize <= directory.Length; offset += EntrySize)
        {
            int nameLength = Math.Max(0, BitConverter.ToUInt16(directory, offset + 64) - 2);
            string name = Encoding.Unicode.GetString(directory, offset, Math.Min(nameLength, 64));
            _entries.Add(new Entry(
                name,
                directory[offset + 66],
                BitConverter.ToInt32(directory, offset + 68),
                BitConverter.ToInt32(directory, offset + 72),
                BitConverter.ToInt32(directory, offset + 76),
                BitConverter.ToUInt32(directory, offset + 116),
                (long)BitConverter.ToUInt64(directory, offset + 120)));
        }

        if (_entries.Count == 0)
            return;

        uint miniFatStart = BitConverter.ToUInt32(_data, 60);
        if (miniFatStart is not (FreeSector or EndOfChain))
        {
            byte[] raw = ReadChain(miniFatStart, long.MaxValue);
            _miniFat = new uint[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, _miniFat, 0, _miniFat.Length * 4);
        }

        Entry root = _entries[0];
        _miniStream = ReadChain(root.Start, root.Size);
    }

    private int SectorOffset(uint sector) => checked((int)((sector + 1L) * _sectorSize));

    /// <summary>Reads a chain of regular (FAT) sectors, at most <paramref name="size"/> bytes.</summary>
    private byte[] ReadChain(uint start, long size)
    {
        var output = new MemoryStream();
        uint sector = start;
        for (int guard = 0; sector is not (EndOfChain or FreeSector) && sector < _fat.Length && guard <= _fat.Length && output.Length < size; guard++)
        {
            int offset = SectorOffset(sector);
            int count = Math.Min(_sectorSize, _data.Length - offset);
            if (count <= 0)
                break;

            output.Write(_data, offset, count);
            sector = _fat[sector];
        }

        return Trim(output, size);
    }

    private byte[] ReadMini(uint start, long size)
    {
        var output = new MemoryStream();
        uint sector = start;
        for (int guard = 0; sector is not (EndOfChain or FreeSector) && sector < _miniFat.Length && guard <= _miniFat.Length && output.Length < size; guard++)
        {
            long offset = (long)sector * _miniSectorSize;
            int count = (int)Math.Min(_miniSectorSize, _miniStream.Length - offset);
            if (count <= 0)
                break;

            output.Write(_miniStream, (int)offset, count);
            sector = _miniFat[sector];
        }

        return Trim(output, size);
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

        Entry current = _entries[0];
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            Entry? next = null;
            Walk(current.Child, entry =>
            {
                if (next is null && string.Equals(entry.Name, part, StringComparison.OrdinalIgnoreCase))
                    next = entry;
            });
            if (next is null)
                return null;

            current = next;
        }

        return current;
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

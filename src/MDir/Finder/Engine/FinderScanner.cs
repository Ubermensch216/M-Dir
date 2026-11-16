using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace MDir.Finder.Engine;

/// <summary>What the built-in search leaves out of its index.</summary>
internal sealed class FinderExcludes
{
    private const uint Hidden = 0x2, System = 0x4, ReparsePoint = 0x400;

    private readonly bool _hidden;
    private readonly bool _system;
    private readonly string[] _folders;
    private readonly Regex[] _patterns;

    public FinderExcludes(bool hidden = false, bool system = false, IEnumerable<string>? folders = null, IEnumerable<string>? patterns = null)
    {
        _hidden = hidden;
        _system = system;
        _folders = [.. (folders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().TrimEnd('\\', '/').ToLowerInvariant())];
        _patterns = [.. (patterns ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => WildcardToRegex(p.Trim()))];
    }

    public static FinderExcludes None { get; } = new();

    public bool AnyNameRules => _hidden || _system || _patterns.Length > 0;

    public bool AnyFolderRules => _folders.Length > 0;

    public bool AttrExcluded(uint attrs) => (_hidden && (attrs & Hidden) != 0) || (_system && (attrs & System) != 0);

    public bool NameExcluded(string name)
    {
        foreach (Regex pattern in _patterns)
        {
            if (pattern.IsMatch(name))
                return true;
        }

        return false;
    }

    public bool FolderExcluded(string path)
    {
        string p = path.ToLowerInvariant();
        foreach (string f in _folders)
        {
            if (p == f || p.StartsWith(f + "\\", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool IsReparsePoint(uint attrs) => (attrs & ReparsePoint) != 0;

    /// <summary>A file-name wildcard (<c>*</c>, <c>?</c>) as a whole-name, case-insensitive expression.</summary>
    public static Regex WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (char ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}

/// <summary>Indexes a folder tree by walking it: used for folders, non-NTFS drives, and when the MFT cannot be read.</summary>
internal static class FinderScanner
{
    private readonly record struct Entry(string Name, uint Attrs, bool IsDir, long Length, double MTime, double CTime, double ATime);

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static int ScanFolder(FinderIndex index, string rootPath, FinderExcludes? excludes, Action<int>? progress, CancellationToken cancel, string kind = "folder")
    {
        (int vol, int rootId) = index.AddRoot(rootPath, kind);
        ScanInto(index, rootPath, rootId, vol, excludes, progress, cancel);
        return vol;
    }

    /// <summary>Walks <paramref name="dirPath"/> and adds its contents under entry <paramref name="dirId"/>; returns how many were added.</summary>
    public static int ScanInto(FinderIndex index, string dirPath, int dirId, int vol, FinderExcludes? excludes = null, Action<int>? progress = null, CancellationToken cancel = default)
    {
        excludes ??= FinderExcludes.None;
        var stack = new Stack<(string Path, int Id)>();
        stack.Push((dirPath, dirId));
        int count = 0;
        while (stack.Count > 0)
        {
            if (cancel.IsCancellationRequested)
                break;

            (string path, int pid) = stack.Pop();
            List<Entry> entries;
            try
            {
                entries = Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            string prefix = path.EndsWith('\\') ? path : path + "\\";
            lock (index.Lock)
            {
                foreach (Entry e in entries)
                {
                    if (excludes.AttrExcluded(e.Attrs) || excludes.NameExcluded(e.Name))
                        continue;

                    string childPath = prefix + e.Name;
                    if (e.IsDir && excludes.FolderExcluded(childPath))
                        continue;

                    int i = index.Add(e.Name, pid, e.Attrs, e.IsDir ? -1 : e.Length, e.MTime == 0 ? 0.001 : e.MTime, 0, vol, e.CTime, e.ATime);
                    count++;
                    if (e.IsDir && !FinderExcludes.IsReparsePoint(e.Attrs))
                        stack.Push((childPath, i));
                }
            }

            progress?.Invoke(count);
        }

        return count;
    }

    private static List<Entry> Read(string path)
    {
        var list = new List<Entry>();
        var enumerable = new FileSystemEnumerable<Entry>(path, (ref FileSystemEntry e) =>
        {
            bool isDir = e.IsDirectory;
            return new Entry(e.FileName.ToString(), (uint)e.Attributes | (isDir ? FinderIndex.DirectoryAttribute : 0), isDir,
                isDir ? -1 : e.Length,
                FinderIndex.ToEpoch(e.LastWriteTimeUtc.UtcDateTime),
                FinderIndex.ToEpoch(e.CreationTimeUtc.UtcDateTime),
                FinderIndex.ToEpoch(e.LastAccessTimeUtc.UtcDateTime));
        }, Options);
        list.AddRange(enumerable);
        return list;
    }
}

using MDir.Shell;

namespace MDir.Features;

/// <summary>A favorite: a shortcut file or a folder of favorites.</summary>
internal sealed record FavoriteEntry(string Path, bool IsFolder)
{
    /// <summary>The name shown to the user (shortcuts without ".lnk").</summary>
    public string Name => IsFolder ? System.IO.Path.GetFileName(Path) : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The file or folder name as stored on disk.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Favorites kept as a folder of <c>.lnk</c> shortcuts (like Explorer's Favorites and Q-Dir's <c>Favoriten</c>);
/// subfolders become menu levels. The shortcuts' order is not a file system property, so each folder may carry
/// an <c>.order</c> text file with one entry name per line; entries it does not list follow in a fixed order.
/// All operations stay inside <see cref="Root"/>.
/// </summary>
internal sealed class FavoritesStore
{
    public const string QuickLinkFolderName = "Quick-Link";
    public const string LinkExtension = ".lnk";
    private const string OrderFileName = ".order";

    public FavoritesStore(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    /// <summary>The always-present "Quick-Link" folder, Q-Dir's quick-access list.</summary>
    public string QuickLinkFolder => Path.Combine(Root, QuickLinkFolderName);

    /// <summary>True for the Quick-Link folder, which cannot be renamed, moved or deleted.</summary>
    public bool IsQuickLinkFolder(string path) => SamePath(path, QuickLinkFolder);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(QuickLinkFolder);
    }

    /// <summary>The entries of <paramref name="folder"/> in display order. A missing folder gives an empty list.</summary>
    public IReadOnlyList<FavoriteEntry> List(string folder)
    {
        EnsureInside(folder);
        if (!Directory.Exists(folder))
            return [];

        var entries = new List<FavoriteEntry>();
        foreach (string directory in Directory.EnumerateDirectories(folder))
            entries.Add(new FavoriteEntry(directory, IsFolder: true));
        foreach (string file in Directory.EnumerateFiles(folder, "*" + LinkExtension))
            entries.Add(new FavoriteEntry(file, IsFolder: false));

        string[] saved = ReadOrder(folder);
        bool isRoot = SamePath(folder, Root);
        return
        [
            .. entries
                .OrderBy(entry => Array.FindIndex(saved, name => string.Equals(name, entry.FileName, StringComparison.OrdinalIgnoreCase)) is var at && at >= 0 ? at : int.MaxValue)
                .ThenBy(entry => isRoot && entry.IsFolder && string.Equals(entry.FileName, QuickLinkFolderName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(entry => entry.IsFolder ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Adds a shortcut to <paramref name="target"/> at the end of <paramref name="folder"/> and returns its path.
    /// If the folder already has a shortcut to the same place, that one is returned and nothing is added.
    /// </summary>
    public string AddLink(string folder, ShellLocation target, string? name = null)
    {
        EnsureInside(folder);
        Directory.CreateDirectory(folder);
        foreach (FavoriteEntry entry in List(folder).Where(entry => !entry.IsFolder))
        {
            if (ShellLinks.Read(entry.Path) is { Target: { } existing } && existing.Equals(target))
                return entry.Path;
        }

        string linkPath = UniquePath(folder, SanitizeName(string.IsNullOrWhiteSpace(name) ? target.Name : name) + LinkExtension);
        ShellLinks.Create(linkPath, target);
        AppendToOrder(folder, Path.GetFileName(linkPath));
        return linkPath;
    }

    public string CreateFolder(string parent, string name)
    {
        EnsureInside(parent);
        string path = UniquePath(parent, SanitizeName(name));
        Directory.CreateDirectory(path);
        AppendToOrder(parent, Path.GetFileName(path));
        return path;
    }

    /// <summary>Renames an entry (a shortcut keeps its ".lnk"); throws <see cref="IOException"/> when the name is taken.</summary>
    public string Rename(string path, string newName)
    {
        EnsureInsideAndNotRoot(path);
        EnsureNotQuickLink(path);
        bool isFolder = Directory.Exists(path);
        string fileName = SanitizeName(newName) + (isFolder ? string.Empty : LinkExtension);
        string parent = Path.GetDirectoryName(path)!;
        string target = Path.Combine(parent, fileName);
        if (SamePath(path, target))
            return path;
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"'{Path.GetFileNameWithoutExtension(fileName)}' 이름이 이미 있습니다.");

        string[] order = ReadOrder(parent);
        Retry(() =>
        {
            if (isFolder)
                Directory.Move(path, target);
            else
                File.Move(path, target);
        });

        WriteOrder(parent, order.Select(name => string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) ? fileName : name));
        return target;
    }

    public void Delete(string path)
    {
        EnsureInsideAndNotRoot(path);
        EnsureNotQuickLink(path);
        string parent = Path.GetDirectoryName(path)!;
        Retry(() =>
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        });

        WriteOrder(parent, ReadOrder(parent).Where(name => !string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Moves an entry into <paramref name="targetFolder"/> at position <paramref name="index"/> (-1 = end), which may
    /// be its current folder (reordering). A folder cannot be moved into itself. Returns the new path.
    /// </summary>
    public string Move(string path, string targetFolder, int index = -1)
    {
        EnsureInsideAndNotRoot(path);
        EnsureNotQuickLink(path);
        EnsureInside(targetFolder);
        bool isFolder = Directory.Exists(path);
        if (isFolder && (SamePath(path, targetFolder) || IsInside(targetFolder, path)))
            throw new ArgumentException("A folder cannot be moved into itself.", nameof(targetFolder));

        string sourceFolder = Path.GetDirectoryName(path)!;
        string fileName = Path.GetFileName(path);
        string destination = path;
        if (!SamePath(sourceFolder, targetFolder))
        {
            Directory.CreateDirectory(targetFolder);
            destination = UniquePath(targetFolder, fileName);
            string moveTo = destination;
            Retry(() =>
            {
                if (isFolder)
                    Directory.Move(path, moveTo);
                else
                    File.Move(path, moveTo);
            });

            WriteOrder(sourceFolder, List(sourceFolder).Select(entry => entry.FileName));
        }

        // Rebuild the target's order with the entry at the requested place.
        List<string> names = [.. List(targetFolder).Select(entry => entry.FileName).Where(name => !string.Equals(name, Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase))];
        names.Insert(index < 0 ? names.Count : Math.Min(index, names.Count), Path.GetFileName(destination));
        WriteOrder(targetFolder, names);
        return destination;
    }

    /// <summary>Sets the icon a shortcut asks for (null restores the target's own icon).</summary>
    public void SetIcon(string linkPath, string? iconPath, int iconIndex)
    {
        EnsureInsideAndNotRoot(linkPath);
        ShellLinks.SetIcon(linkPath, iconPath, iconIndex);
    }

    public ShellLinkInfo? Read(string linkPath)
    {
        EnsureInside(linkPath);
        return ShellLinks.Read(linkPath);
    }

    /// <summary>
    /// Copies the shortcuts and folders of another favorites folder (for example Q-Dir's <c>Favoriten</c>) into
    /// <paramref name="folder"/>. A <c>Quick-Link</c> folder in the source is merged into this store's Quick-Link
    /// folder; other files and links to folders are left out. Returns the number of shortcuts copied.
    /// </summary>
    public int ImportFrom(string sourceDirectory, string folder)
    {
        EnsureInside(folder);
        if (!Directory.Exists(sourceDirectory) || SamePath(sourceDirectory, folder) || IsInside(folder, sourceDirectory) && !IsInside(sourceDirectory, Root))
            return 0;

        Directory.CreateDirectory(folder);
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*" + LinkExtension))
        {
            string target = UniquePath(folder, Path.GetFileName(file));
            File.Copy(file, target);
            AppendToOrder(folder, Path.GetFileName(target));
            copied++;
        }

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            string name = Path.GetFileName(directory);
            bool quickLink = SamePath(folder, Root) && string.Equals(name, QuickLinkFolderName, StringComparison.OrdinalIgnoreCase);
            string destination = quickLink ? QuickLinkFolder : CreateFolder(folder, name);
            copied += ImportFrom(directory, destination);
        }

        return copied;
    }

    /// <summary>Makes a text a valid, non-empty file name.</summary>
    public static string SanitizeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Favorite" : clean;
    }

    /// <summary>
    /// Runs a file operation, retrying briefly on sharing violations. The shell and virus scanners open files for a
    /// moment (for example when it scans a folder to pick its icon), and that must not make a rename or delete fail.
    /// </summary>
    private static void Retry(Action operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(75 * attempt);
            }
        }
    }

    private static string UniquePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Directory.Exists(candidate) ? string.Empty : Path.GetExtension(fileName);
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem} ({n}){extension}");

        return candidate;
    }

    private void AppendToOrder(string folder, string fileName) =>
        WriteOrder(folder, [.. List(folder).Select(entry => entry.FileName).Where(name => !string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)), fileName]);

    private static string[] ReadOrder(string folder)
    {
        string file = Path.Combine(folder, OrderFileName);
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray() : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void WriteOrder(string folder, IEnumerable<string> names)
    {
        if (!Directory.Exists(folder))
            return;

        string file = Path.Combine(folder, OrderFileName);

        // A hidden file cannot be overwritten, so the attribute is cleared for the write and set again.
        if (File.Exists(file))
            File.SetAttributes(file, FileAttributes.Normal);

        File.WriteAllLines(file, names);
        File.SetAttributes(file, FileAttributes.Hidden);
    }

    private void EnsureInside(string path)
    {
        if (!SamePath(path, Root) && !IsInside(path, Root))
            throw new ArgumentException($"'{path}' is outside the favorites folder.", nameof(path));
    }

    private void EnsureNotQuickLink(string path)
    {
        if (IsQuickLinkFolder(path))
            throw new ArgumentException("The Quick-Link folder cannot be renamed, moved or deleted.", nameof(path));
    }

    private void EnsureInsideAndNotRoot(string path)
    {
        if (!IsInside(path, Root))
            throw new ArgumentException($"'{path}' is not an entry of the favorites folder.", nameof(path));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> lies below <paramref name="folder"/> (not equal to it).</summary>
    private static bool IsInside(string path, string folder)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

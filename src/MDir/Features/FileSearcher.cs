using System.Text;
using System.Xml.Linq;

namespace MDir.Features;

/// <summary>What to look for. <see cref="NamePattern"/> uses the same wildcards as the file filter ("*.txt;*.md", or a plain word that matches anywhere).</summary>
/// <param name="ContentText">Text that must occur in the file (files only; binary files and files over 4 MB are not searched). Empty = name only.</param>
internal sealed record SearchOptions(
    string Root,
    string NamePattern = "",
    string ContentText = "",
    bool IncludeSubfolders = true,
    bool MatchCase = false,
    bool IncludeFolders = true,
    int MaxResults = 10_000);

/// <param name="Line">1-based line of the first content match, or 0 when the search was by name only.</param>
internal sealed record SearchHit(string Path, bool IsFolder, long Size, DateTime Modified, int Line);

internal sealed record SearchSummary(int Hits, int FoldersScanned, bool LimitReached);

/// <summary>
/// A simple search of a folder tree by name and (optionally) content, the built-in counterpart of the Windows
/// Search hand-over. Links to other folders (junctions, symbolic links) are not followed, so a cycle cannot make it
/// run forever; what cannot be read is skipped.
/// </summary>
internal static class FileSearcher
{
    /// <summary>The biggest file whose content is searched.</summary>
    public const int ContentLimit = 4 * 1024 * 1024;

    public static Task<SearchSummary> SearchAsync(SearchOptions options, IProgress<SearchHit>? hits = null,
        IProgress<string>? folders = null, CancellationToken cancel = default) =>
        Task.Run(() => Search(options, hits, folders, cancel), cancel);

    public static SearchSummary Search(SearchOptions options, IProgress<SearchHit>? hits = null,
        IProgress<string>? folders = null, CancellationToken cancel = default)
    {
        Func<string, bool> nameMatches = CompileName(options.NamePattern);
        string content = options.ContentText;
        StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int found = 0, scanned = 0;
        var pending = new Stack<string>();
        pending.Push(options.Root);
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            scanned++;
            folders?.Report(folder);

            DirectoryInfo directory = new(folder);
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                cancel.ThrowIfCancellationRequested();
                bool isFolder = entry is DirectoryInfo;
                if (isFolder)
                {
                    if (options.IncludeSubfolders && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        pending.Push(entry.FullName);

                    if (!options.IncludeFolders || content.Length > 0 || !nameMatches(entry.Name))
                        continue;

                    if (!Report(new SearchHit(entry.FullName, true, 0, SafeTime(entry), 0)))
                        return new SearchSummary(found, scanned, LimitReached: true);
                    continue;
                }

                if (!nameMatches(entry.Name))
                    continue;

                int line = 0;
                if (content.Length > 0 && (line = FindInFile(entry.FullName, content, comparison)) == 0)
                    continue;

                long size = entry is FileInfo file ? SafeLength(file) : 0;
                if (!Report(new SearchHit(entry.FullName, false, size, SafeTime(entry), line)))
                    return new SearchSummary(found, scanned, LimitReached: true);
            }
        }

        return new SearchSummary(found, scanned, LimitReached: false);

        bool Report(SearchHit hit)
        {
            found++;
            hits?.Report(hit);
            return found < options.MaxResults;
        }
    }

    /// <summary>Names matching the pattern list; empty matches everything, a lone plain word matches anywhere in the name.</summary>
    public static Func<string, bool> CompileName(string pattern)
    {
        string[] patterns = WildcardMatcher.SplitPatterns(pattern);
        if (patterns.Length == 0)
            return _ => true;

        if (patterns.Length == 1 && patterns[0].AsSpan().IndexOfAny('*', '?') < 0)
            patterns[0] = $"*{patterns[0]}*";

        return name => patterns.Any(p => WildcardMatcher.IsMatch(p, name));
    }

    /// <summary>The 1-based line of the first occurrence of <paramref name="text"/> in a text file, or 0 (not found, binary, too big, unreadable).</summary>
    internal static int FindInFile(string path, string text, StringComparison comparison)
    {
        try
        {
            if (new FileInfo(path).Length > ContentLimit)
                return 0;

            TextPreviewData data = TextFile.Read(path, ContentLimit);
            if (data.IsBinary)
                return 0;

            int index = data.Text.IndexOf(text, comparison);
            if (index < 0)
                return 0;

            int line = 1;
            for (int i = 0; i < index; i++)
            {
                if (data.Text[i] == '\n')
                    line++;
            }

            return line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime SafeTime(FileSystemInfo entry)
    {
        try
        {
            return entry.LastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
/// The hand-over to Windows Search. The file list cannot browse to a <c>search-ms:</c> address, but it can browse
/// to a saved search (a <c>.search-ms</c> file), which Explorer shows as a folder of results.
/// </summary>
internal static class WindowsSearch
{
    public const string Extension = ".search-ms";

    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>The saved-search document for <paramref name="query"/> inside <paramref name="folder"/> (the whole index when the folder is empty).</summary>
    public static string BuildXml(string folder, string query)
    {
        XElement search = new("query",
            new XElement("conditions",
                new XElement("condition",
                    new XAttribute("type", "leafCondition"),
                    new XAttribute("property", "System.Generic.String"),
                    new XAttribute("operator", "wordmatch"),
                    new XAttribute("propertyType", "string"),
                    new XAttribute("value", query))),
            new XElement("kindList", new XElement("kind", new XAttribute("name", "item"))));
        if (folder.Length > 0)
            search.Add(new XElement("scope", new XElement("include", new XAttribute("path", folder))));

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("persistedQuery", new XAttribute("version", "1.0"), search));
        return document.Declaration + Environment.NewLine + document.Root;
    }

    /// <summary>
    /// Writes the saved search into <paramref name="directory"/> (older ones there are removed) and returns its path.
    /// The file name becomes the tab title.
    /// </summary>
    public static string CreateSavedSearch(string directory, string folder, string query)
    {
        Directory.CreateDirectory(directory);
        RemoveOld(directory);

        string label = string.Concat(query.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (label.Length > 40)
            label = label[..40];

        string path = Path.Combine(directory, $"검색 - {(label.Length > 0 ? label : "결과")}{Extension}");
        File.WriteAllText(path, BuildXml(folder, query), new UTF8Encoding(false));
        return path;
    }

    private static void RemoveOld(string directory)
    {
        foreach (string old in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > KeepFor)
                    File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

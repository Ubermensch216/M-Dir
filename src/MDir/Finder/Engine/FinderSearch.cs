using System.Text.RegularExpressions;

namespace MDir.Finder.Engine;

/// <summary>Compiles a parsed search into a predicate over index entries, and runs and sorts searches (port of <c>query.py</c>).</summary>
internal static class FinderSearch
{
    public static readonly string[] SortKeys = ["name", "path", "size", "mtime", "ext"];

    private sealed class SearchCancelled : Exception;

    private sealed class Context(FinderIndex index, CancellationToken cancel, long contentMax)
    {
        private readonly Dictionary<int, string> _dirs = [];

        public FinderIndex Index { get; } = index;
        public CancellationToken Cancel { get; } = cancel;
        public long ContentMax { get; } = contentMax;

        public string DirPath(int d)
        {
            if (_dirs.TryGetValue(d, out string? p))
                return p;

            int par = Index.Parent[d];
            p = par < 0 ? Index.Names[d] : DirPath(par) + "\\" + Index.Names[d];
            _dirs[d] = p;
            return p;
        }

        public string Full(int i)
        {
            int par = Index.Parent[i];
            if (par < 0)
            {
                string n = Index.Names[i];
                return n.Length == 2 && n[1] == ':' ? n + "\\" : n;
            }

            return DirPath(par) + "\\" + Index.Names[i];
        }
    }

    private static string WildcardPattern(string term)
    {
        var sb = new System.Text.StringBuilder(@"\A");
        foreach (char ch in term)
            sb.Append(ch == '*' ? ".*" : ch == '?' ? "." : Regex.Escape(ch.ToString()));
        sb.Append(@"\z");
        return sb.ToString();
    }

    private static Regex MakeRegex(string pattern, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new FinderQueryException($"Invalid regular expression: {ex.Message}");
        }
    }

    private static Func<int, bool> CompileText(string term, FinderFlags flags, Context ctx)
    {
        FinderIndex idx = ctx.Index;
        bool isWild = !flags.Regex && (term.Contains('*') || term.Contains('?'));
        bool hasSep = !flags.Regex && (term.Contains('\\') || term.Contains('/'));
        bool usePath = flags.Path || hasSep;
        if (usePath && !flags.Regex)
            term = term.Replace('/', '\\');
        RegexOptions reOptions = flags.Case ? RegexOptions.None : RegexOptions.IgnoreCase;

        Func<int, string> get = usePath
            ? (flags.Case ? ctx.Full : i => ctx.Full(i).ToLowerInvariant())
            : (flags.Case ? i => idx.Names[i] : i => idx.Lower[i]);
        Func<int, string> target = usePath ? ctx.Full : i => idx.Names[i];

        if (flags.Regex)
        {
            Regex rx = MakeRegex(term, reOptions);
            return i => IsMatch(rx, target(i));
        }

        if (isWild)
        {
            Regex rx = MakeRegex(WildcardPattern(term), reOptions | RegexOptions.Singleline);
            return i => IsMatch(rx, target(i));
        }

        string t = flags.Case ? term : term.ToLowerInvariant();
        if (flags.WholeFileName)
            return i => string.Equals(get(i), t, StringComparison.Ordinal);
        if (flags.WholeWord)
        {
            Regex rx = MakeRegex(@"(?<!\w)" + Regex.Escape(term) + @"(?!\w)", reOptions);
            return i => IsMatch(rx, target(i));
        }

        if (flags.Start)
            return i => get(i).StartsWith(t, StringComparison.Ordinal);
        if (flags.End)
            return i => get(i).EndsWith(t, StringComparison.Ordinal);
        return i => get(i).Contains(t, StringComparison.Ordinal);
    }

    private static bool IsMatch(Regex rx, string text)
    {
        try
        {
            return rx.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Func<int, bool> Compile(QNode node, Context ctx)
    {
        FinderIndex idx = ctx.Index;
        List<uint> attrs = idx.Attrs;
        switch (node)
        {
            case QTrue:
                return _ => true;
            case QAnd and:
                {
                    Func<int, bool>[] fs = [.. and.Items.Select(n => Compile(n, ctx))];
                    return i =>
                    {
                        foreach (Func<int, bool> f in fs)
                        {
                            if (!f(i))
                                return false;
                        }

                        return true;
                    };
                }

            case QOr or:
                {
                    Func<int, bool>[] fs = [.. or.Items.Select(n => Compile(n, ctx))];
                    return i =>
                    {
                        foreach (Func<int, bool> f in fs)
                        {
                            if (f(i))
                                return true;
                        }

                        return false;
                    };
                }

            case QNot not:
                {
                    Func<int, bool> f = Compile(not.Item, ctx);
                    return i => !f(i);
                }

            case QText text:
                return CompileText(text.Term, text.Flags, ctx);
            case QIsDir:
                return i => (attrs[i] & FinderIndex.DirectoryAttribute) != 0;
            case QIsFile:
                return i => (attrs[i] & FinderIndex.DirectoryAttribute) == 0;
            case QExt ext:
                return i => (attrs[i] & FinderIndex.DirectoryAttribute) == 0 && ext.Extensions.Contains(SplitExt(idx.Names[i]));
            case QEmpty:
                {
                    bool[] has = idx.ChildFlags();
                    return i => (attrs[i] & FinderIndex.DirectoryAttribute) != 0 && i < has.Length && !has[i];
                }

            case QSize size:
                return i =>
                {
                    if ((attrs[i] & FinderIndex.DirectoryAttribute) == 0 && idx.MTime[i] == 0.0)
                        idx.Meta(i);
                    long s = idx.Size[i];
                    if (s < 0) // unknown (a folder before its size was computed, or a failed lookup)
                        return size.Lo == -1 && size.Hi == -1;
                    return size.Lo <= s && s <= size.Hi;
                };
            case QDate date:
                {
                    List<double> arr = date.Which switch { "ctime" => idx.CTime, "atime" => idx.ATime, _ => idx.MTime };
                    return i =>
                    {
                        if (idx.MTime[i] == 0.0)
                            idx.Meta(i);
                        return date.Lo <= arr[i] && arr[i] <= date.Hi;
                    };
                }

            case QContent content:
                return CompileContent(content.Term, content.Flags, ctx);
            case QLen len:
                return i => len.Lo <= idx.Names[i].Length && idx.Names[i].Length <= len.Hi;
            case QDepth depth:
                return i =>
                {
                    int d = 0;
                    while (idx.Parent[i] >= 0)
                    {
                        i = idx.Parent[i];
                        d++;
                    }

                    return depth.Lo <= d && d <= depth.Hi;
                };
            case QParent parent:
                return i => idx.Parent[i] >= 0 && string.Equals(ctx.DirPath(idx.Parent[i]), parent.Path, StringComparison.OrdinalIgnoreCase);
            case QAttrib attrib:
                return i => (attrs[i] & attrib.Mask) == attrib.Mask;
            default:
                throw new FinderQueryException($"Internal error: unknown node {node.GetType().Name}");
        }
    }

    private static Func<int, bool> CompileContent(string term, FinderFlags flags, Context ctx)
    {
        FinderIndex idx = ctx.Index;
        FinderContentMatcher matcher = FinderContentMatcher.Create(term, flags, ctx.ContentMax);
        return i =>
        {
            if ((idx.Attrs[i] & FinderIndex.DirectoryAttribute) != 0)
                return false;
            if (ctx.Cancel.IsCancellationRequested)
                throw new SearchCancelled();
            return matcher.Matches(ctx.Full(i), idx.Names[i], ctx.Cancel);
        };
    }

    /// <summary>The extension of a name without the dot, lower case ("" when there is none).</summary>
    public static string SplitExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : string.Empty;
    }

    private static string? PlainSubstring(QNode node)
    {
        if (node is not QText { Term.Length: > 0 } text)
            return null;

        FinderFlags f = text.Flags;
        if (f.Case || f.WholeWord || f.Path || f.Regex || f.WholeFileName || f.Start || f.End)
            return null;
        if (text.Term.IndexOfAny(['*', '?', '\\', '/']) >= 0)
            return null;
        return text.Term.ToLowerInvariant();
    }

    private static int Cost(QNode node) => node switch { QSize or QDate => 1, QContent => 2, _ => 0 };

    private static List<QNode> FlattenAnd(params QNode[] nodes)
    {
        var items = new List<QNode>();
        foreach (QNode n in nodes)
        {
            if (n is QAnd and)
                items.AddRange(and.Items);
            else
                items.Add(n);
        }

        items.RemoveAll(x => x is QTrue);
        return items.Count == 0 ? [QTrue.Instance] : items;
    }

    /// <summary>
    /// The ids of the matching entries in index order, or null when cancelled. <paramref name="filterText"/> is an
    /// extra search (a filter such as <c>pic:</c>) that is ANDed with <paramref name="text"/>.
    /// </summary>
    public static List<int>? Search(FinderIndex index, string text, FinderFlags flags, CancellationToken cancel = default, string filterText = "", FinderFlags? filterFlags = null, long contentMax = 64L << 20)
    {
        QNode ast = FinderQueryParser.Parse(text, flags);
        if (filterText.Trim().Length > 0)
            ast = new QAnd(FlattenAnd(FinderQueryParser.Parse(filterText, filterFlags ?? default), ast));

        lock (index.Lock)
        {
            var ctx = new Context(index, cancel, contentMax);
            int n = index.Count;
            List<bool> alive = index.Alive;
            List<int> parent = index.Parent;
            List<string> lower = index.Lower;
            const int Chunk = 4096;

            List<QNode> items = ast is QAnd and ? and.Items : [ast];
            string? sub = null;
            List<QNode> rest = items;
            for (int k = 0; k < items.Count; k++)
            {
                if (PlainSubstring(items[k]) is { } s)
                {
                    sub = s;
                    rest = [.. items.Take(k), .. items.Skip(k + 1)];
                    break;
                }
            }

            rest = [.. rest.OrderBy(Cost)]; // cheap predicates first, file reads last
            bool trivial = rest.All(nd => nd is QTrue);
            Func<int, bool> fn = Compile(rest.Count > 1 ? new QAnd(rest) : rest.Count == 1 ? rest[0] : QTrue.Instance, ctx);

            var output = new List<int>();
            try
            {
                for (int start = 0; start < n; start += Chunk)
                {
                    if (cancel.IsCancellationRequested)
                        return null;

                    int end = Math.Min(n, start + Chunk);
                    for (int i = start; i < end; i++)
                    {
                        if (!alive[i] || parent[i] < 0)
                            continue;
                        if (sub is not null && !lower[i].Contains(sub, StringComparison.Ordinal))
                            continue;
                        if (trivial || fn(i))
                            output.Add(i);
                    }
                }
            }
            catch (SearchCancelled)
            {
                return null;
            }

            return output;
        }
    }

    /// <summary>Sorts <paramref name="ids"/> by <paramref name="key"/>; folders come first when sorting by name. Null when cancelled.</summary>
    public static List<int>? Sort(FinderIndex index, List<int> ids, string key, bool ascending = true, CancellationToken cancel = default)
    {
        int sign = ascending ? 1 : -1;
        var result = new List<int>(ids);
        lock (index.Lock)
        {
            List<string> lower = index.Lower;
            List<uint> attrs = index.Attrs;
            bool Dir(int i) => (attrs[i] & FinderIndex.DirectoryAttribute) != 0;

            switch (key)
            {
                case "name":
                    result.Sort((a, b) =>
                    {
                        int da = Dir(a) ? 0 : 1, db = Dir(b) ? 0 : 1;
                        if (da != db)
                            return da - db;
                        int c = string.CompareOrdinal(lower[a], lower[b]) * sign;
                        return c != 0 ? c : a.CompareTo(b);
                    });
                    break;
                case "path":
                    {
                        var ctx = new Context(index, cancel, 0);
                        var dirs = new Dictionary<int, string>();
                        string DirOf(int i)
                        {
                            int p = index.Parent[i];
                            if (p < 0)
                                return string.Empty;
                            if (!dirs.TryGetValue(p, out string? d))
                                dirs[p] = d = ctx.DirPath(p).ToLowerInvariant();
                            return d;
                        }

                        result.Sort((a, b) =>
                        {
                            int c = string.CompareOrdinal(DirOf(a), DirOf(b));
                            if (c == 0)
                                c = string.CompareOrdinal(lower[a], lower[b]);
                            c *= sign;
                            return c != 0 ? c : a.CompareTo(b);
                        });
                        break;
                    }

                case "ext":
                    {
                        string Ext(int i) => Dir(i) ? string.Empty : SplitExt(lower[i]);
                        result.Sort((a, b) =>
                        {
                            int c = string.CompareOrdinal(Ext(a), Ext(b));
                            if (c == 0)
                                c = string.CompareOrdinal(lower[a], lower[b]);
                            c *= sign;
                            return c != 0 ? c : a.CompareTo(b);
                        });
                        break;
                    }

                case "size":
                case "mtime":
                    {
                        if (!index.EnsureMeta(result, cancel))
                            return null;

                        if (key == "size")
                        {
                            List<long> size = index.Size;
                            result.Sort((a, b) =>
                            {
                                int c = size[a].CompareTo(size[b]) * sign;
                                return c != 0 ? c : a.CompareTo(b);
                            });
                        }
                        else
                        {
                            List<double> mtime = index.MTime;
                            result.Sort((a, b) =>
                            {
                                int c = mtime[a].CompareTo(mtime[b]) * sign;
                                return c != 0 ? c : a.CompareTo(b);
                            });
                        }

                        break;
                    }

                default:
                    throw new FinderQueryException($"Unknown sort key '{key}'");
            }
        }

        return result;
    }
}

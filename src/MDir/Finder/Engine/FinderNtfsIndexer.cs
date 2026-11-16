namespace MDir.Finder.Engine;

/// <summary>Builds an index from an NTFS volume's MFT and keeps it current from the USN journal (port of <c>ntfs.py</c>/<c>mftparse.py</c>).</summary>
internal static class FinderNtfsIndexer
{
    /// <summary>Indexes the volume <paramref name="root"/> through its MFT. Throws <see cref="IOException"/> when the volume cannot be read; nothing is added to the index before that read succeeded.</summary>
    public static int IndexVolume(FinderIndex index, string root, IVolumeAccess access, FinderExcludes? excludes, Action<int>? progress, CancellationToken cancel)
    {
        excludes ??= FinderExcludes.None;
        UsnJournalInfo info;
        try
        {
            info = access.QueryJournal();
        }
        catch (VolumeException ex) when (ex.Code == FinderNtfs.ErrorJournalNotActive)
        {
            access.CreateJournal();
            info = access.QueryJournal();
        }

        var records = new Dictionary<ulong, (ulong Parent, uint Attrs, string Name)>();
        int n = 0;
        foreach (MftEntry e in access.EnumMft(info.NextUsn))
        {
            if ((e.Frn & FinderNtfs.MftRecordMask) < FinderNtfs.FirstUserRecord)
                continue;

            records[e.Frn] = (e.ParentFrn, e.Attrs, e.Name);
            if (++n % 50000 == 0)
                progress?.Invoke(n);
            if (cancel.IsCancellationRequested)
                break;
        }

        (int vol, int rootId) = index.AddRoot(root, "ntfs");
        index.Roots[vol].JournalId = info.JournalId;
        index.Roots[vol].NextUsn = info.NextUsn;
        Dictionary<ulong, int> fmap = index.FrnMaps[vol];

        // Assign ids, then link parents and prune unreachable / excluded subtrees.
        var ids = new Dictionary<ulong, int>(records.Count);
        foreach ((ulong frn, (ulong _, uint attrs, string name)) in records)
            ids[frn] = index.Add(name, -1, attrs, frn: frn, vol: vol);

        int total = index.Count;
        byte[] state = new byte[total]; // 0 unknown, 1 keep, 2 drop
        state[rootId] = 1;
        bool nameRules = excludes.AnyNameRules;

        int Decide(ulong frn)
        {
            var chain = new List<ulong>();
            ulong cur = frn;
            int verdict = 2;
            while (true)
            {
                if (!ids.TryGetValue(cur, out int i))
                {
                    if ((cur & FinderNtfs.MftRecordMask) == FinderNtfs.RootRecord)
                        verdict = 1;
                    break;
                }

                if (state[i] != 0)
                {
                    verdict = state[i];
                    break;
                }

                chain.Add(cur);
                cur = records[cur].Parent;
                if (chain.Count > 4096)
                    break;
            }

            // Unwind from the top of the chain downwards.
            int parentId = verdict == 1 ? (ids.TryGetValue(cur, out int top) ? top : rootId) : -1;
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int i = ids[chain[k]];
                if (verdict == 1)
                {
                    (ulong _, uint attrs, string name) = records[chain[k]];
                    if (nameRules && (excludes.AttrExcluded(attrs) || excludes.NameExcluded(name)))
                    {
                        state[i] = 2;
                        verdict = 2;
                        continue;
                    }

                    index.Parent[i] = parentId;
                    state[i] = 1;
                    parentId = i;
                }
                else
                {
                    state[i] = 2;
                }
            }

            return verdict;
        }

        foreach (ulong frn in records.Keys)
        {
            if (state[ids[frn]] == 0)
                Decide(frn);
        }

        foreach ((ulong frn, int i) in ids)
        {
            if (state[i] == 1)
                fmap[frn] = i;
            else
                index.Alive[i] = false;
        }

        if (excludes.AnyFolderRules)
            ApplyFolderExcludes(index, vol, rootId, excludes);

        index.Rev++;
        return vol;
    }

    private static void ApplyFolderExcludes(FinderIndex index, int vol, int rootId, FinderExcludes excludes)
    {
        var cache = new Dictionary<int, bool> { [rootId] = false };

        bool Excluded(int i)
        {
            if (cache.TryGetValue(i, out bool r))
                return r;

            int p = index.Parent[i];
            r = (p >= 0 && Excluded(p)) || (index.IsDir(i) && excludes.FolderExcluded(index.FullPath(i)));
            cache[i] = r;
            return r;
        }

        for (int i = 0; i < index.Count; i++)
        {
            if (index.Vol[i] == vol && index.Alive[i] && i != rootId && Excluded(i))
            {
                index.Alive[i] = false;
                ulong f = index.Frn[i];
                if (f != 0)
                    index.FrnMaps[vol].Remove(f);
            }
        }
    }

    /// <summary>Applies one journal record to the index; true when the index changed.</summary>
    public static bool ApplyUsnRecord(FinderIndex index, int vol, UsnRecord rec, FinderExcludes? excludes = null)
    {
        if ((rec.Frn & FinderNtfs.MftRecordMask) < FinderNtfs.FirstUserRecord)
            return false;

        Dictionary<ulong, int> fmap = index.FrnMaps[vol];
        lock (index.Lock)
        {
            bool hasExisting = fmap.TryGetValue(rec.Frn, out int existing);
            if ((rec.Reason & FinderNtfs.ReasonFileDelete) != 0)
            {
                if (!hasExisting)
                    return false;
                index.Remove(existing);
                return true;
            }

            if ((rec.Reason & (FinderNtfs.ReasonFileCreate | FinderNtfs.ReasonRenameNewName)) != 0)
            {
                if (excludes is not null && (excludes.AttrExcluded(rec.Attrs) || excludes.NameExcluded(rec.Name)))
                {
                    if (!hasExisting)
                        return false;
                    index.Remove(existing);
                    return true;
                }

                if (!fmap.TryGetValue(rec.ParentFrn, out int parentId))
                {
                    if ((rec.ParentFrn & FinderNtfs.MftRecordMask) != FinderNtfs.RootRecord)
                        return false;
                    parentId = index.Roots[vol].RootId;
                }

                if (!hasExisting)
                {
                    int i = index.Add(rec.Name, parentId, rec.Attrs, frn: rec.Frn, vol: vol);
                    fmap[rec.Frn] = i;
                }
                else
                {
                    index.Relocate(existing, rec.Name, parentId);
                    index.Attrs[existing] = rec.Attrs;
                    index.InvalidateMeta(existing);
                }

                index.Rev++;
                return true;
            }

            const uint dataChange = FinderNtfs.ReasonDataOverwrite | FinderNtfs.ReasonDataExtend | FinderNtfs.ReasonDataTruncation | FinderNtfs.ReasonBasicInfoChange;
            if ((rec.Reason & dataChange) != 0 && hasExisting)
            {
                index.InvalidateMeta(existing);
                index.Rev++;
                return true;
            }
        }

        return false;
    }

    /// <summary>Fills size and dates of the NTFS volume's entries from its $MFT; returns how many were filled.</summary>
    public static int FillMetadata(FinderIndex index, int vol, IVolumeAccess access, CancellationToken cancel)
    {
        int[] rec2id;
        lock (index.Lock)
        {
            Dictionary<ulong, int> fm = index.FrnMaps[vol];
            long top = fm.Count == 0 ? 0 : (long)fm.Keys.Max(k => k & FinderNtfs.MftRecordMask);
            rec2id = new int[top + 1];
            Array.Fill(rec2id, -1);
            foreach ((ulong frn, int i) in fm)
                rec2id[(long)(frn & FinderNtfs.MftRecordMask)] = i;
        }

        int total = 0;
        foreach (List<MftMeta> batch in access.ScanMetadata(rec2id, cancel))
        {
            lock (index.Lock)
            {
                foreach (MftMeta m in batch)
                {
                    if (m.Record >= rec2id.Length)
                        continue;
                    int i = rec2id[m.Record];
                    if (i < 0 || i >= index.Count || !index.Alive[i] || index.MTime[i] != 0.0)
                        continue;
                    if ((index.Attrs[i] & FinderIndex.DirectoryAttribute) == 0)
                        index.Size[i] = m.Size;
                    index.CTime[i] = m.CTime;
                    index.ATime[i] = m.ATime;
                    index.MTime[i] = m.MTime == 0 ? 0.001 : m.MTime;
                    total++;
                }
            }
        }

        index.Rev++;
        return total;
    }
}

/// <summary>Polls the USN journal of one NTFS volume and applies the changes to the index.</summary>
internal sealed class FinderUsnMonitor : IDisposable
{
    private readonly FinderIndex _index;
    private readonly int _vol;
    private readonly FinderExcludes? _excludes;
    private readonly Action<int> _onStale;
    private readonly Func<string, IVolumeAccess?> _open;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _task;

    public FinderUsnMonitor(FinderIndex index, int vol, FinderExcludes? excludes, Action<int> onStale, Func<string, IVolumeAccess?> open, TimeSpan? interval = null)
    {
        _index = index;
        _vol = vol;
        _excludes = excludes;
        _onStale = onStale;
        _open = open;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public void Start() => _task = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);

    private void Run()
    {
        FinderRoot root = _index.Roots[_vol];
        IVolumeAccess? access;
        try
        {
            access = _open(root.Path);
        }
        catch (IOException)
        {
            return;
        }

        if (access is null)
            return;

        using (access)
        {
            try
            {
                UsnJournalInfo info = access.QueryJournal();
                if (info.JournalId != root.JournalId || root.NextUsn < info.LowestValidUsn)
                {
                    _onStale(_vol);
                    return;
                }

                long next = root.NextUsn;
                while (!_stop.IsCancellationRequested)
                {
                    List<UsnRecord> records;
                    try
                    {
                        (next, records) = access.ReadJournal(next, root.JournalId);
                    }
                    catch (VolumeException ex) when (ex.Code is FinderNtfs.ErrorJournalEntryDeleted or FinderNtfs.ErrorJournalNotActive)
                    {
                        _onStale(_vol);
                        return;
                    }
                    catch (IOException)
                    {
                        _stop.Token.WaitHandle.WaitOne(_interval);
                        continue;
                    }

                    foreach (UsnRecord rec in records)
                        FinderNtfsIndexer.ApplyUsnRecord(_index, _vol, rec, _excludes);
                    root.NextUsn = next;
                    if (records.Count == 0)
                        _stop.Token.WaitHandle.WaitOne(_interval);
                }
            }
            catch (IOException)
            {
                _onStale(_vol);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

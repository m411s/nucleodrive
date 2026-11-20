namespace NucleoDrive.Sync.TwoWay;

/// <summary>Decides what to do for every item of one synced folder by comparing the local and remote
/// snapshots with the baseline (what both sides looked like at the last sync). Pure: no I/O.
///
/// Per item (files):
/// <code>
///  local \ remote | absent     new        unchanged    changed       deleted
///  absent         |  -         download   -            -             -
///  new            |  upload    conflict   -            -             -
///  unchanged      |  -         -          nothing      download      trash local
///  changed        |  -         -          upload       conflict      upload
///  deleted        |  -         -          trash remote download      forget
/// </code>
/// Conflicts: identical SHA-1 → record; otherwise the newer modification time wins. A losing local
/// file goes to the desktop Trash; a losing Drive version stays in its version history.
/// Folders are trashed as a whole only if nothing inside changed on the other side.
/// Renames/moves (same uid remotely, same inode locally) become moves, not delete + re-upload.
/// Too many deletions at once are held back until confirmed.</summary>
public static class SyncPlanner
{
    private enum State { Absent, New, Unchanged, Changed, Deleted }

    public static SyncPlan Plan(
        IReadOnlyDictionary<string, BaseItem> baseline,
        IReadOnlyDictionary<string, LocalItem> local,
        IReadOnlyDictionary<string, RemoteItem> remote,
        PlanOptions options)
    {
        var b = new Dictionary<string, BaseItem>(baseline, StringComparer.Ordinal);
        var l = new Dictionary<string, LocalItem>(local, StringComparer.Ordinal);
        // Proton Docs/Sheets can't be downloaded by the CLI: they stay online-only and untouched.
        var r = remote.Where(kv => !kv.Value.IsProtonDocument).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var moves = new List<SyncOp>();
        DetectRemoteMoves(b, l, r, moves);
        DetectLocalMoves(b, l, r, moves);

        State LocalState(string p) =>
            (l.GetValueOrDefault(p), b.GetValueOrDefault(p)) switch
            {
                (null, null) => State.Absent,
                (not null, null) => State.New,
                (null, not null) => State.Deleted,
                var (li, bi) => SameLocal(li!, bi!) ? State.Unchanged : State.Changed,
            };

        State RemoteState(string p) =>
            (r.GetValueOrDefault(p), b.GetValueOrDefault(p)) switch
            {
                (null, null) => State.Absent,
                (not null, null) => State.New,
                (null, not null) => State.Deleted,
                var (ri, bi) => SameRemote(ri!, bi!) ? State.Unchanged : State.Changed,
            };

        // Folders that contain changes (or files still settling) on each side: never trash those.
        var localDirty = new HashSet<string>(StringComparer.Ordinal);
        var remoteDirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var li in l.Values)
        {
            if (li.Ignored || LocalState(li.Path) is State.New or State.Changed)
                MarkAncestors(localDirty, li.Path);
        }
        foreach (var ri in r.Values)
        {
            if (RemoteState(ri.Path) is State.New or State.Changed)
                MarkAncestors(remoteDirty, ri.Path);
        }

        var ops = new List<SyncOp>();
        // Subtrees handled by a folder-level trash, or under any ignored local item: an unreadable
        // folder (we can't see inside, so nothing there may look deleted) or a symlink (never
        // followed: a Drive folder with the same name must not be written through it).
        var skipUnder = l.Values.Where(x => x.Ignored).Select(x => x.Path).ToList();
        var paths = new SortedSet<string>(b.Keys.Concat(l.Keys).Concat(r.Keys), StringComparer.Ordinal);

        foreach (var p in paths)
        {
            if (skipUnder.Any(s => IsUnder(p, s)))
                continue;
            var li = l.GetValueOrDefault(p);
            var ri = r.GetValueOrDefault(p);
            var bi = b.GetValueOrDefault(p);
            if (li is { Ignored: true })
                continue; // settling/symlink/unreadable: leave it, and never read it as deleted
            var ls = LocalState(p);
            var rs = RemoteState(p);

            // A file on one side and a folder on the other: Drive wins, the local item goes to the Trash.
            if (li is not null && ri is not null && li.IsDir != ri.IsDir)
            {
                ops.Add(new TrashLocalOp(p, li.IsDir, 1 + CountUnder(l.Keys, p)));
                ops.Add(ri.IsDir ? new CreateLocalFolderOp(p) : new DownloadOp(p, ri, TrashLocalFirst: false));
                if (li.IsDir)
                    skipUnder.Add(p);
                continue;
            }

            var isDir = li?.IsDir ?? ri?.IsDir ?? bi!.IsDir;
            if (isDir)
                PlanFolder(p, ls, rs, ops, skipUnder, localDirty, remoteDirty, b);
            else
                PlanFile(p, ls, rs, li, ri, ops, options);
        }

        // Deletion guard.
        var localDeletes = ops.OfType<TrashLocalOp>().Sum(o => o.Count);
        var remoteDeletes = ops.OfType<TrashRemoteOp>().Sum(o => o.Count);
        var held = !options.DeletionsConfirmed
                   && (localDeletes > options.MaxDeletesWithoutConfirm || remoteDeletes > options.MaxDeletesWithoutConfirm);
        if (held)
            ops.RemoveAll(o => o is TrashLocalOp or TrashRemoteOp);

        // Order: moves, folder creation (parents first), transfers, then trashing (children first).
        var ordered = moves
            .Concat(ops.Where(o => o is CreateRemoteFolderOp or CreateLocalFolderOp).OrderBy(o => Depth(o.Path)).ThenBy(o => o.Path, StringComparer.Ordinal))
            .Concat(ops.Where(o => o is UploadOp or DownloadOp or RecordOp or ForgetOp))
            .Concat(ops.Where(o => o is TrashLocalOp or TrashRemoteOp).OrderByDescending(o => Depth(o.Path)))
            .ToList();
        return new SyncPlan(ordered, localDeletes, remoteDeletes, held);
    }

    private static void PlanFolder(string p, State ls, State rs, List<SyncOp> ops, List<string> skipUnder,
        HashSet<string> localDirty, HashSet<string> remoteDirty, Dictionary<string, BaseItem> b)
    {
        switch (ls, rs)
        {
            case (State.New, State.Absent):
                ops.Add(new CreateRemoteFolderOp(p));
                break;
            case (State.Absent, State.New):
                ops.Add(new CreateLocalFolderOp(p));
                break;
            case (State.New, State.New):
                ops.Add(new RecordOp(p));
                break;
            case (State.Deleted, State.Deleted):
                ops.Add(new ForgetOp(p));
                break;
            case (State.Deleted, _):
                // Deleted locally. If something inside changed on Drive, keep the folder (those changes come down).
                if (remoteDirty.Contains(p))
                    ops.Add(new CreateLocalFolderOp(p));
                else
                {
                    ops.Add(new TrashRemoteOp(p, true, 1 + CountUnder(b.Keys, p)));
                    skipUnder.Add(p);
                }
                break;
            case (_, State.Deleted):
                if (localDirty.Contains(p))
                    ops.Add(new CreateRemoteFolderOp(p));
                else
                {
                    ops.Add(new TrashLocalOp(p, true, 1 + CountUnder(b.Keys, p)));
                    skipUnder.Add(p);
                }
                break;
        }
    }

    private static void PlanFile(string p, State ls, State rs, LocalItem? li, RemoteItem? ri, List<SyncOp> ops, PlanOptions options)
    {
        switch (ls, rs)
        {
            case (State.New, State.Absent):
            case (State.Changed, State.Unchanged):
            case (State.Changed, State.Deleted):
                ops.Add(new UploadOp(p));
                break;
            case (State.Absent, State.New):
            case (State.Deleted, State.Changed):
                ops.Add(new DownloadOp(p, ri!, TrashLocalFirst: false));
                break;
            case (State.Unchanged, State.Changed):
                // The local copy is the previous version, which Drive keeps in its history.
                ops.Add(new DownloadOp(p, ri!, TrashLocalFirst: false));
                break;
            case (State.New, State.New):
            case (State.Changed, State.Changed):
                ops.Add(ResolveConflict(p, li!, ri!, options));
                break;
            case (State.Deleted, State.Unchanged):
                ops.Add(new TrashRemoteOp(p, false, 1));
                break;
            case (State.Unchanged, State.Deleted):
                ops.Add(new TrashLocalOp(p, false, 1));
                break;
            case (State.Deleted, State.Deleted):
                ops.Add(new ForgetOp(p));
                break;
        }
    }

    /// <summary>Changed on both sides: same content → just record; otherwise the newer one wins.</summary>
    private static SyncOp ResolveConflict(string p, LocalItem li, RemoteItem ri, PlanOptions options)
    {
        if (ri.Sha1 is { } remoteSha && li.Size == ri.Size && options.LocalSha1(p) is { } localSha && localSha == remoteSha)
            return new RecordOp(p);
        var localTime = new DateTimeOffset(DateTime.UnixEpoch.Ticks + li.Mtime, TimeSpan.Zero);
        return ri.ModifiedAt is { } remoteTime && localTime > remoteTime
            ? new UploadOp(p)
            : new DownloadOp(p, ri, TrashLocalFirst: true);
    }

    // ---- renames and moves -------------------------------------------------------------------

    /// <summary>Same uid at a new remote path, local copy untouched → move it locally.</summary>
    private static void DetectRemoteMoves(Dictionary<string, BaseItem> b, Dictionary<string, LocalItem> l,
        Dictionary<string, RemoteItem> r, List<SyncOp> moves)
    {
        var byUid = UniqueBy(b.Values, x => x.Uid);
        foreach (var ri in r.Values.OrderBy(x => Depth(x.Path)).ThenBy(x => x.Path, StringComparer.Ordinal).ToList())
        {
            if (b.ContainsKey(ri.Path) || !byUid.TryGetValue(ri.Uid, out var bi) || bi.Path == ri.Path || bi.IsDir != ri.IsDir)
                continue;
            if (r.TryGetValue(bi.Path, out var stillThere) && stillThere.Uid == bi.Uid)
                continue;
            if (!l.TryGetValue(bi.Path, out var li) || li.Ignored || !SameLocal(li, bi) || l.ContainsKey(ri.Path) || IsUnder(ri.Path, bi.Path))
                continue;
            moves.Add(new MoveLocalOp(bi.Path, ri.Path));
            Rekey(b, bi.Path, ri.Path, (x, p) => x with { Path = p });
            Rekey(l, bi.Path, ri.Path, (x, p) => x with { Path = p });
            byUid = UniqueBy(b.Values, x => x.Uid);
        }
    }

    /// <summary>Same inode (and size/mtime for files) at a new local path, remote untouched → move it on Drive.</summary>
    private static void DetectLocalMoves(Dictionary<string, BaseItem> b, Dictionary<string, LocalItem> l,
        Dictionary<string, RemoteItem> r, List<SyncOp> moves)
    {
        var byInode = UniqueBy(b.Values.Where(x => x.Inode != 0), x => (string?)x.Inode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var li in l.Values.Where(x => !x.Ignored && x.Inode != 0).OrderBy(x => Depth(x.Path)).ThenBy(x => x.Path, StringComparer.Ordinal).ToList())
        {
            if (b.ContainsKey(li.Path)
                || !byInode.TryGetValue(li.Inode.ToString(System.Globalization.CultureInfo.InvariantCulture), out var bi)
                || bi.Path == li.Path || bi.IsDir != li.IsDir || l.ContainsKey(bi.Path))
                continue;
            if (!li.IsDir && (li.Size != bi.LocalSize || li.Mtime != bi.LocalMtime))
                continue;
            if (!r.TryGetValue(bi.Path, out var ri) || !SameRemote(ri, bi) || r.ContainsKey(li.Path) || IsUnder(li.Path, bi.Path))
                continue;
            moves.Add(new MoveRemoteOp(bi.Path, li.Path));
            Rekey(b, bi.Path, li.Path, (x, p) => x with { Path = p });
            Rekey(r, bi.Path, li.Path, (x, p) => x with { Path = p });
            byInode = UniqueBy(b.Values.Where(x => x.Inode != 0), x => (string?)x.Inode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool SameLocal(LocalItem li, BaseItem bi) =>
        li.IsDir == bi.IsDir && (li.IsDir || (li.Size == bi.LocalSize && li.Mtime == bi.LocalMtime));

    private static bool SameRemote(RemoteItem ri, BaseItem bi) =>
        ri.IsDir == bi.IsDir && ri.Uid == bi.Uid && (ri.IsDir || ri.RevisionUid == bi.RevisionUid);

    public static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.Ordinal) && path[folder.Length] == '/';

    private static int Depth(string path) => path.Count(c => c == '/');

    private static int CountUnder(IEnumerable<string> keys, string folder) => keys.Count(k => IsUnder(k, folder));

    private static void MarkAncestors(HashSet<string> set, string path)
    {
        for (var i = path.LastIndexOf('/'); i > 0; i = path.LastIndexOf('/', i - 1))
            set.Add(path[..i]);
    }

    private static Dictionary<string, T> UniqueBy<T>(IEnumerable<T> items, Func<T, string?> key) =>
        items.Where(x => key(x) is not null).GroupBy(x => key(x)!).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Moves an entry and everything under it to a new path.</summary>
    internal static void Rekey<T>(Dictionary<string, T> dict, string from, string to, Func<T, string, T> withPath)
    {
        foreach (var key in dict.Keys.Where(k => k == from || IsUnder(k, from)).ToList())
        {
            var moved = to + key[from.Length..];
            var value = dict[key];
            dict.Remove(key);
            dict[moved] = withPath(value, moved);
        }
    }
}

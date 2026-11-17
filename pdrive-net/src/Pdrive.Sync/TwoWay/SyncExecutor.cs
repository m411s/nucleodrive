using Pdrive.Cli;

namespace Pdrive.Sync.TwoWay;

public sealed record PassResult(
    int Uploaded, int Downloaded, int Moved, int TrashedLocal, int TrashedRemote,
    IReadOnlyList<string> Errors, bool DeletionsHeld, int HeldLocalDeletes, int HeldRemoteDeletes)
{
    public static readonly PassResult Empty = new(0, 0, 0, 0, 0, [], false, 0, 0);
    public int Changes => Uploaded + Downloaded + Moved + TrashedLocal + TrashedRemote;
}

/// <summary>Carries out a <see cref="SyncPlan"/> for one synced folder and keeps the baseline in step.
/// Every local overwrite or trash re-checks the file first: if it changed since the snapshot, that
/// item is skipped and the next pass sees the new state. Downloads land in a staging folder and are
/// renamed into place.</summary>
public sealed class SyncExecutor
{
    private readonly IDriveClient _client;
    private readonly StateStore _state;
    private readonly FreedesktopTrash _trash;
    private readonly string _root;
    private readonly string _folder;
    private readonly Dictionary<string, LocalItem> _local;
    private readonly Dictionary<string, RemoteItem> _remote;
    private readonly List<string> _errors = [];
    private readonly Action<string>? _log;
    private readonly int _parallelism;
    private int _uploaded, _downloaded, _moved, _trashedLocal, _trashedRemote;

    /// <param name="log">Receives one line per change made (for the journal).</param>
    public SyncExecutor(IDriveClient client, StateStore state, FreedesktopTrash trash, string root, string folder,
        Dictionary<string, LocalItem> local, Dictionary<string, RemoteItem> remote, Action<string>? log = null, int parallelism = 4)
    {
        _log = log;
        _parallelism = Math.Max(1, parallelism);
        _client = client;
        _state = state;
        _trash = trash;
        _root = root;
        _folder = folder;
        _local = new Dictionary<string, LocalItem>(local, StringComparer.Ordinal);
        _remote = new Dictionary<string, RemoteItem>(remote, StringComparer.Ordinal);
    }

    public async Task<PassResult> ExecuteAsync(SyncPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var ops = await UploadNewTreesAsync(plan.Ops, progress, ct);
        for (var i = 0; i < ops.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var op = ops[i];
            if (op is UploadOp)
            {
                // Batch consecutive uploads into the same remote folder: one CLI call instead of one per file.
                var batch = new List<UploadOp>();
                while (i < ops.Count && ops[i] is UploadOp u && SyncPaths.Parent(u.Path) == SyncPaths.Parent(op.Path))
                    batch.Add((UploadOp)ops[i++]);
                i--;
                progress?.Report($"Uploading {batch.Count} file(s) to {SyncPaths.Remote(SyncPaths.Parent(op.Path))}");
                await Guard(op.Path, () => UploadBatchAsync(batch, ct));
                continue;
            }
            if (op is DownloadOp)
            {
                var run = new List<DownloadOp>();
                while (i < ops.Count && ops[i] is DownloadOp d)
                {
                    run.Add(d);
                    i++;
                }
                i--;
                // one CLI run per Drive folder (each run costs ~1.5 s), at most MaxDownloadBatch files,
                // several folders at once
                var batches = run.GroupBy(d => SyncPaths.Parent(d.Path)).SelectMany(g => g.Chunk(MaxDownloadBatch)).ToList();
                progress?.Report(run.Count == 1 ? Describe(run[0])
                    : batches.Count == 1 ? $"Downloading {run.Count} files"
                    : $"Downloading {run.Count} files in {batches.Count} folders");
                await DownloadBatchesAsync(batches, ct);
                continue;
            }
            if (op is TrashRemoteOp)
            {
                var batch = new List<TrashRemoteOp>();
                while (i < ops.Count && ops[i] is TrashRemoteOp t)
                {
                    batch.Add(t);
                    i++;
                }
                i--;
                progress?.Report($"Moving {batch.Count} item(s) to the Drive trash");
                await Guard(op.Path, () => TrashRemoteAsync(batch, ct));
                continue;
            }
            progress?.Report(Describe(op));
            await Guard(op.Path, () => ExecuteOneAsync(op, ct));
        }
        return new PassResult(_uploaded, _downloaded, _moved, _trashedLocal, _trashedRemote, _errors,
            plan.DeletionsHeld, plan.DeletionsHeld ? plan.LocalDeletes : 0, plan.DeletionsHeld ? plan.RemoteDeletes : 0);
    }

    private async Task Guard(string path, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DriveException e) when (e is not AuthException)
        {
            _errors.Add($"{path}: {e.FirstLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _errors.Add($"{path}: {e.Message}");
        }
    }

    private Task ExecuteOneAsync(SyncOp op, CancellationToken ct) => op switch
    {
        MoveLocalOp m => MoveLocal(m),
        MoveRemoteOp m => MoveRemoteAsync(m, ct),
        CreateRemoteFolderOp c => CreateRemoteFolderAsync(c.Path, ct),
        CreateLocalFolderOp c => CreateLocalFolder(c.Path),
        DownloadOp d => DownloadBatchesAsync([[d]], ct),
        TrashLocalOp t => TrashLocal(t),
        RecordOp r => Record(r.Path),
        ForgetOp f => Forget(f.Path),
        _ => throw new InvalidOperationException($"unexpected op {op}"),
    };

    private static string Describe(SyncOp op) => op switch
    {
        DownloadOp d => $"Downloading {d.Path}",
        MoveLocalOp m => $"Moving {m.From} to {m.To}",
        MoveRemoteOp m => $"Moving {m.From} to {m.To} on Drive",
        CreateRemoteFolderOp c => $"Creating {c.Path} on Drive",
        CreateLocalFolderOp c => $"Creating {c.Path}",
        TrashLocalOp t => $"Moving {t.Path} to the Trash",
        _ => $"Updating {op.Path}",
    };

    // ---- moves ---------------------------------------------------------------------------------

    private Task MoveLocal(MoveLocalOp m)
    {
        var from = Safe(m.From);
        var to = Safe(m.To);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (File.Exists(to) || Directory.Exists(to))
            throw new IOException($"{m.To} already exists");
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
        _state.RekeyBaseline(_folder, m.From, m.To);
        SyncPlanner.Rekey(_local, m.From, m.To, (x, p) => x with { Path = p });
        SyncPlanner.Rekey(_remote, m.From, m.To, (x, p) => x with { Path = p });
        _log?.Invoke($"Renamed locally to match Drive: {m.From} → {m.To}");
        _moved++;
        return Task.CompletedTask;
    }

    private async Task MoveRemoteAsync(MoveRemoteOp m, CancellationToken ct)
    {
        string fromParent = SyncPaths.Parent(m.From), fromName = SyncPaths.Name(m.From);
        string toParent = SyncPaths.Parent(m.To), toName = SyncPaths.Name(m.To);
        var current = m.From;
        if (fromName != toName)
        {
            await _client.RenameAsync(SyncPaths.Remote(current), toName, ct);
            current = fromParent.Length > 0 ? fromParent + "/" + toName : toName;
        }
        if (fromParent != toParent)
            await _client.MoveAsync([SyncPaths.Remote(current)], SyncPaths.Remote(toParent), ct);
        _state.RekeyBaseline(_folder, m.From, m.To);
        SyncPlanner.Rekey(_local, m.From, m.To, (x, p) => x with { Path = p });
        SyncPlanner.Rekey(_remote, m.From, m.To, (x, p) => x with { Path = p });
        _log?.Invoke($"Renamed on Drive to match this computer: {m.From} → {m.To}");
        _moved++;
    }

    // ---- folders -------------------------------------------------------------------------------

    private async Task CreateRemoteFolderAsync(string rel, CancellationToken ct)
    {
        string? uid = null;
        try
        {
            uid = await _client.CreateFolderAsync(SyncPaths.Remote(rel), ct); // one call: the CLI returns the new ID
        }
        catch (DriveException e) when (e is not AuthException && e.Message.Contains("already exists", StringComparison.Ordinal))
        {
        }
        uid ??= (await _client.InfoAsync(SyncPaths.Remote(rel), ct)).Uid;
        var st = Posix.TryStat(SyncPaths.Local(_root, rel));
        _state.UpsertBaseline(_folder, [new BaseItem(rel, true, uid, null, 0, 0, st?.Inode ?? 0)]);
        _log?.Invoke($"Created folder on Drive: {rel}");
    }

    private Task CreateLocalFolder(string rel)
    {
        var path = Safe(rel);
        Directory.CreateDirectory(path);
        var st = Posix.TryStat(path);
        _state.UpsertBaseline(_folder, [new BaseItem(rel, true, _remote.GetValueOrDefault(rel)?.Uid, null, 0, 0, st?.Inode ?? 0)]);
        _log?.Invoke($"Created folder locally: {rel}");
        return Task.CompletedTask;
    }

    // ---- new folder trees ----------------------------------------------------------------------

    /// <summary>Folders created on this computer with subfolders (e.g. a folder tree copied in) go up in one
    /// CLI run, `upload -d merge folder parent`, instead of a create, an upload and a listing per folder;
    /// then one crawl records what was uploaded. Only when a fresh look at the folder finds exactly what the
    /// plan uploads: anything excluded, still being written, or a link makes it take the normal path (the
    /// CLI refuses links anyway). Returns the ops still to do.</summary>
    private async Task<IReadOnlyList<SyncOp>> UploadNewTreesAsync(IReadOnlyList<SyncOp> ops, IProgress<string>? progress, CancellationToken ct)
    {
        var creates = ops.OfType<CreateRemoteFolderOp>().Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
        var roots = creates.Where(p => !creates.Contains(SyncPaths.Parent(p))).ToList();
        var remaining = ops.ToList();
        foreach (var root in roots)
        {
            var under = remaining.Where(o => o.Path == root || SyncPlanner.IsUnder(o.Path, root)).ToList();
            var folders = under.OfType<CreateRemoteFolderOp>().Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
            var files = under.OfType<UploadOp>().Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
            if (folders.Count < 2 || under.Count != folders.Count + files.Count || !OnDiskExactly(root, folders, files))
                continue; // a single folder costs the same either way; anything else: the normal path

            progress?.Report($"Uploading the new folder {root} ({files.Count} file(s), {folders.Count} folder(s))");
            try
            {
                await _client.UploadAsync([SyncPaths.Local(_root, root)], SyncPaths.Remote(SyncPaths.Parent(root)), null, ct);
            }
            catch (DriveException e) when (e is not AuthException)
            {
                _log?.Invoke($"Uploading {root} in one go failed ({e.FirstLine}); uploading it folder by folder");
                continue; // the normal path; already uploaded files are skipped by the CLI (same content)
            }

            // Record what's there now: the new folder and everything in it.
            var rootInfo = await _client.InfoAsync(SyncPaths.Remote(root), ct);
            var listed = await RemoteSnapshot.TakeAsync(_client, root, [], _parallelism, ct);
            var records = new List<BaseItem>();
            if (Posix.TryStat(SyncPaths.Local(_root, root)) is { } rootStat)
                records.Add(new BaseItem(root, true, rootInfo.Uid, null, 0, 0, rootStat.Inode));
            foreach (var (rel, item) in listed)
            {
                var st = Posix.TryStat(SyncPaths.Local(_root, rel));
                if (item.IsDir && folders.Contains(rel) && st is { IsDir: true })
                    records.Add(new BaseItem(rel, true, item.Uid, null, 0, 0, st.Value.Inode));
                else if (!item.IsDir && files.Contains(rel) && st is { IsRegular: true } fs && Matches(_local.GetValueOrDefault(rel), fs))
                    records.Add(new BaseItem(rel, false, item.Uid, item.RevisionUid, fs.Size, fs.Mtime100ns, fs.Inode));
                // anything else (edited meanwhile, missing): the next pass compares both sides
            }
            _state.UpsertBaseline(_folder, records);
            var uploaded = records.Count(r => !r.IsDir);
            _uploaded += uploaded;
            _log?.Invoke($"Uploaded new folder: {root} ({uploaded} file(s))");
            remaining.RemoveAll(o => under.Contains(o));
        }
        return remaining;
    }

    /// <summary>The folder holds exactly these subfolders and files (by lstat: no links, sockets, …),
    /// all unchanged since the snapshot, and nothing else (e.g. excluded or still-settling files).</summary>
    private bool OnDiskExactly(string root, HashSet<string> folders, HashSet<string> files)
    {
        var seenFolders = 0;
        var seenFiles = 0;
        bool Walk(string rel)
        {
            if (Posix.TryStat(SyncPaths.Local(_root, rel)) is not { IsDir: true })
                return false;
            if (!folders.Contains(rel))
                return false;
            seenFolders++;
            IEnumerable<string> names;
            try
            {
                names = Directory.EnumerateFileSystemEntries(SyncPaths.Local(_root, rel)).Select(Path.GetFileName).OfType<string>().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            foreach (var name in names)
            {
                var child = rel + "/" + name;
                var st = Posix.TryStat(SyncPaths.Local(_root, child));
                if (st is { IsDir: true })
                {
                    if (!Walk(child))
                        return false;
                }
                else if (st is { IsRegular: true } fs && files.Contains(child) && Matches(_local.GetValueOrDefault(child), fs))
                {
                    seenFiles++;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }
        return Walk(root) && seenFolders == folders.Count && seenFiles == files.Count;
    }

    // ---- transfers -----------------------------------------------------------------------------

    private async Task UploadBatchAsync(List<UploadOp> batch, CancellationToken ct)
    {
        var parent = SyncPaths.Parent(batch[0].Path);
        // Upload only files that still look like the snapshot; anything edited since goes next pass.
        var ready = new List<(string Rel, Posix.Stat Stat)>();
        foreach (var op in batch)
        {
            if (Posix.TryStat(SyncPaths.Local(_root, op.Path)) is { IsRegular: true } st && Matches(_local.GetValueOrDefault(op.Path), st))
                ready.Add((op.Path, st));
        }
        if (ready.Count == 0)
            return;

        var done = new List<(string Rel, Posix.Stat Stat)>();
        try
        {
            await _client.UploadAsync(ready.Select(r => SyncPaths.Local(_root, r.Rel)).ToList(), SyncPaths.Remote(parent), null, ct);
            done.AddRange(ready);
        }
        catch (DriveException e) when (e is not AuthException)
        {
            if (ready.Count == 1)
                throw;
            foreach (var r in ready) // one bad file mustn't hold back the rest
            {
                try
                {
                    await _client.UploadAsync([SyncPaths.Local(_root, r.Rel)], SyncPaths.Remote(parent), null, ct);
                    done.Add(r);
                }
                catch (DriveException e2) when (e2 is not AuthException)
                {
                    _errors.Add($"{r.Rel}: {e2.FirstLine}");
                }
            }
        }
        if (done.Count == 0)
            return;

        // Record the new revisions: one listing of the folder covers the whole batch.
        var listed = (await _client.ListAsync(SyncPaths.Remote(parent), ct)).ToDictionary(e => e.Name, StringComparer.Ordinal);
        var records = new List<BaseItem>();
        foreach (var (rel, st) in done)
        {
            if (listed.TryGetValue(SyncPaths.Name(rel), out var e) && !e.IsDir)
                records.Add(new BaseItem(rel, false, e.Uid, e.RevisionUid, st.Size, st.Mtime100ns, st.Inode));
        }
        _state.UpsertBaseline(_folder, records);
        _uploaded += records.Count;
        foreach (var r in records)
            _log?.Invoke($"Uploaded: {r.Path}");
    }

    /// <summary>Files per CLI run: keeps the argument list and a failed run's retry small.</summary>
    internal const int MaxDownloadBatch = 50;

    /// <summary>Downloads each batch (files of one Drive folder) in one CLI run into its own staging folder,
    /// up to <c>parallelism</c> runs at once, then moves the files into place one by one after re-checking
    /// the local side. A failed run still places what arrived.</summary>
    private async Task DownloadBatchesAsync(IReadOnlyList<DownloadOp[]> batches, CancellationToken ct)
    {
        var work = new List<(List<DownloadOp> Ok, string Staging)>();
        foreach (var batch in batches)
        {
            var ok = new List<DownloadOp>();
            foreach (var d in batch)
                await Guard(d.Path, () =>
                {
                    Safe(d.Path); // never through a symlink
                    ok.Add(d);
                    return Task.CompletedTask;
                });
            if (ok.Count > 0)
                work.Add((ok, Path.Combine(_root, SyncPaths.StagingDir, Guid.NewGuid().ToString("N"))));
        }
        if (work.Count == 0)
            return;

        var failures = new DriveException?[work.Count];
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, work.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct },
                async (i, token) =>
                {
                    Directory.CreateDirectory(work[i].Staging);
                    try
                    {
                        await _client.DownloadManyAsync(work[i].Ok.Select(d => SyncPaths.Remote(d.Path)).ToList(), work[i].Staging,
                            work[i].Ok.Sum(d => d.Remote.Size), null, token);
                    }
                    catch (DriveException e) when (e is not AuthException)
                    {
                        failures[i] = e; // some files may still have arrived
                    }
                });
            for (var i = 0; i < work.Count; i++)
            {
                foreach (var d in work[i].Ok)
                    await Guard(d.Path, () =>
                    {
                        Place(d, work[i].Staging, failures[i]);
                        return Task.CompletedTask;
                    });
            }
        }
        finally
        {
            foreach (var (_, staging) in work)
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Place(DownloadOp d, string staging, DriveException? failure)
    {
        var target = Safe(d.Path);
        var staged = Path.Combine(staging, SyncPaths.Name(d.Path));
        if (!File.Exists(staged))
        {
            if (failure is not null)
                throw failure;
            throw new IOException("the CLI didn't produce the file");
        }
        if (d.Remote.ModifiedAt is { } mtime)
            File.SetLastWriteTimeUtc(staged, mtime.UtcDateTime);

        // Re-check the local side right before replacing anything.
        var current = Posix.TryStat(target);
        var known = _local.GetValueOrDefault(d.Path);
        if (current is not null)
        {
            if (known is null || current.Value.IsDir || !Matches(known, current.Value))
            {
                _errors.Add($"{d.Path}: changed locally during sync; will retry");
                return;
            }
            if (d.TrashLocalFirst)
            {
                var trashed = _trash.Trash(target);
                _log?.Invoke($"Conflict: Drive's newer version of {d.Path} wins; the local copy was moved to {trashed}");
            }
        }
        else if (known is not null)
        {
            return; // deleted locally meanwhile; the next pass decides
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Safe(d.Path); // again: a folder on the way may have been swapped for a link meanwhile
        File.Move(staged, target, overwrite: true);
        var st = Posix.TryStat(target)!.Value;
        _state.UpsertBaseline(_folder, [new BaseItem(d.Path, false, d.Remote.Uid, d.Remote.RevisionUid, st.Size, st.Mtime100ns, st.Inode)]);
        _downloaded++;
        _log?.Invoke($"Downloaded: {d.Path}");
    }

    // ---- deletions ----------------------------------------------------------------------------

    private async Task TrashRemoteAsync(List<TrashRemoteOp> batch, CancellationToken ct)
    {
        try
        {
            await _client.TrashAsync(batch.Select(t => SyncPaths.Remote(t.Path)).ToList(), ct);
            foreach (var t in batch)
                Trashed(t);
        }
        catch (DriveException e) when (e is not AuthException && batch.Count > 1)
        {
            foreach (var t in batch)
            {
                try
                {
                    await _client.TrashAsync([SyncPaths.Remote(t.Path)], ct);
                    Trashed(t);
                }
                catch (DriveException e2) when (e2 is not AuthException)
                {
                    _errors.Add($"{t.Path}: {e2.FirstLine}");
                }
            }
        }

        void Trashed(TrashRemoteOp t)
        {
            _state.RemoveBaseline(_folder, t.Path);
            _trashedRemote += t.Count;
            _log?.Invoke($"Moved to the Drive trash (deleted on this computer): {t.Path}{(t.IsDir ? $" ({t.Count} items)" : "")}");
        }
    }

    private Task TrashLocal(TrashLocalOp t)
    {
        var path = Safe(t.Path);
        if (Posix.TryStat(path) is not { } st)
        {
            _state.RemoveBaseline(_folder, t.Path); // already gone
            return Task.CompletedTask;
        }
        if (!t.IsDir && !Matches(_local.GetValueOrDefault(t.Path), st))
        {
            _errors.Add($"{t.Path}: changed locally during sync; will retry");
            return Task.CompletedTask;
        }
        var trashedTo = _trash.Trash(path);
        _state.RemoveBaseline(_folder, t.Path);
        _trashedLocal += t.Count;
        _log?.Invoke($"Moved to the desktop Trash (deleted on Drive): {t.Path} → {trashedTo}");
        return Task.CompletedTask;
    }

    // ---- bookkeeping ---------------------------------------------------------------------------

    private Task Record(string rel)
    {
        var st = Posix.TryStat(SyncPaths.Local(_root, rel));
        var r = _remote.GetValueOrDefault(rel);
        if (st is { } s && r is not null)
            _state.UpsertBaseline(_folder, [new BaseItem(rel, s.IsDir, r.Uid, r.RevisionUid, s.IsDir ? 0 : s.Size, s.IsDir ? 0 : s.Mtime100ns, s.Inode)]);
        return Task.CompletedTask;
    }

    private Task Forget(string rel)
    {
        _state.RemoveBaseline(_folder, rel);
        return Task.CompletedTask;
    }

    /// <summary>Refuses local paths that would pass through a symlink (or non-folder) below the sync
    /// folder, so nothing is ever written, moved or trashed outside it. The sync folder itself may be
    /// a symlink (your choice); everything under it is checked without following links.</summary>
    private string Safe(string rel)
    {
        var parts = rel.Split('/');
        if (parts.Any(p => p is "" or "." or ".."))
            throw new IOException($"unsafe path {rel}");
        var path = _root;
        for (var i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            var last = i == parts.Length - 1;
            if (Posix.TryStat(path) is not { } st)
                break; // doesn't exist yet: nothing below it can be a link
            if (st.IsSymlink)
                throw new IOException($"{string.Join('/', parts[..(i + 1)])} is a symlink; pdrive doesn't follow links");
            if (!last && !st.IsDir)
                throw new IOException($"{string.Join('/', parts[..(i + 1)])} isn't a folder");
        }
        return path;
    }

    private static bool Matches(LocalItem? known, Posix.Stat st) =>
        known is not null && !known.IsDir && known.Size == st.Size && known.Mtime == st.Mtime100ns;
}

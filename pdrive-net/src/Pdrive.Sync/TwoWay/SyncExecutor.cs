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
    private int _uploaded, _downloaded, _moved, _trashedLocal, _trashedRemote;

    /// <param name="log">Receives one line per change made (for the journal).</param>
    public SyncExecutor(IDriveClient client, StateStore state, FreedesktopTrash trash, string root, string folder,
        Dictionary<string, LocalItem> local, Dictionary<string, RemoteItem> remote, Action<string>? log = null)
    {
        _log = log;
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
        var ops = plan.Ops;
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
        DownloadOp d => DownloadAsync(d, ct),
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
        try
        {
            await _client.MkdirAsync(SyncPaths.Remote(rel), ct);
        }
        catch (DriveException e) when (e is not AuthException && e.Message.Contains("already exists", StringComparison.Ordinal))
        {
        }
        var info = await _client.InfoAsync(SyncPaths.Remote(rel), ct);
        var st = Posix.TryStat(SyncPaths.Local(_root, rel));
        _state.UpsertBaseline(_folder, [new BaseItem(rel, true, info.Uid, null, 0, 0, st?.Inode ?? 0)]);
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

    private async Task DownloadAsync(DownloadOp d, CancellationToken ct)
    {
        var target = Safe(d.Path);
        var staging = Path.Combine(_root, SyncPaths.StagingDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await _client.DownloadAsync(SyncPaths.Remote(d.Path), staging, d.Remote.Size, null, ct);
            var staged = Path.Combine(staging, SyncPaths.Name(d.Path));
            if (!File.Exists(staged))
                throw new IOException("the CLI didn't produce the file");
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
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
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

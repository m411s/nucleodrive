using System.Security.Cryptography;
using Pdrive.Cli;

namespace Pdrive.Sync.TwoWay;

/// <summary>A synced folder can't be synced right now (missing on one side, unmounted, …).
/// Nothing was changed; the message is meant for the user.</summary>
public sealed class SyncFolderException(string message) : Exception(message);

/// <summary>Runs passes for one synced folder: snapshot both sides, plan, execute.
/// <list type="bullet">
/// <item><b>Full pass</b>: crawls the Drive folder (the only way to see remote changes) and the local folder.</item>
/// <item><b>Local pass</b>: after local changes. Only the changed paths are checked on Drive
/// (`info`, or a crawl for a deleted folder), so local edits go up in seconds without a full crawl.</item>
/// <item><b>First sync</b> of an empty or missing local folder: one recursive download, then adopt.</item>
/// </list></summary>
public sealed class TwoWaySyncEngine(IDriveClient client, StateStore state, FreedesktopTrash trash)
{
    /// <summary>Local passes with more changed paths than this do a full pass instead.</summary>
    public const int MaxLocalPassChecks = 200;

    public async Task<PassResult> FullPassAsync(TwoWaySettings sync, SyncConfig cfg, string folder, bool confirmDeletes,
        IProgress<string>? progress, CancellationToken ct)
    {
        var root = sync.LocalPath;
        var baseline = state.LoadBaseline(folder);
        await EnsureRemoteFolderAsync(folder, baseline.Count, ct);
        var localFolder = SyncPaths.Local(root, folder);

        if (!Directory.Exists(localFolder))
        {
            if (baseline.Count > 0)
                throw new SyncFolderException($"{localFolder} is missing (deleted or not mounted?). Restore it, or remove \"{folder}\" from sync.");
            Directory.CreateDirectory(localFolder);
        }

        if (baseline.Count == 0 && !Directory.EnumerateFileSystemEntries(localFolder).Any())
            await InitialDownloadAsync(root, folder, cfg, progress, ct);

        progress?.Report($"Checking {SyncPaths.Remote(folder)} on Drive…");
        var remote = await RemoteSnapshot.TakeAsync(client, folder, cfg.Exclude, sync.ParallelListings, ct,
            n => progress?.Report($"Checking {SyncPaths.Remote(folder)} on Drive… ({n} folders)"));
        var local = LocalSnapshot.Take(root, folder, cfg.Exclude, cfg.MinAge);
        baseline = state.LoadBaseline(folder);
        return await PlanAndExecuteAsync(sync, root, folder, baseline, local, remote, confirmDeletes, progress, ct);
    }

    public async Task<PassResult> LocalPassAsync(TwoWaySettings sync, SyncConfig cfg, string folder, bool confirmDeletes,
        IProgress<string>? progress, CancellationToken ct)
    {
        var root = sync.LocalPath;
        var baseline = state.LoadBaseline(folder);
        if (baseline.Count == 0)
            return await FullPassAsync(sync, cfg, folder, confirmDeletes, progress, ct); // never synced: needs the full picture
        var localFolder = SyncPaths.Local(root, folder);
        if (!Directory.Exists(localFolder))
            throw new SyncFolderException($"{localFolder} is missing (deleted or not mounted?). Restore it, or remove \"{folder}\" from sync.");

        var local = LocalSnapshot.Take(root, folder, cfg.Exclude, cfg.MinAge);

        // Assume Drive still matches the baseline, except where something changed locally: check those.
        var remote = baseline.Values.Where(b => b.Uid is not null)
            .ToDictionary(b => b.Path, b => new RemoteItem(b.Path, b.IsDir, b.Uid!, b.RevisionUid, b.LocalSize, null, null), StringComparer.Ordinal);
        var changed = local.Values.Where(l => !l.Ignored && !l.IsDir && (!baseline.TryGetValue(l.Path, out var b) || b.LocalSize != l.Size || b.LocalMtime != l.Mtime))
            .Select(l => l.Path)
            .Concat(baseline.Keys.Where(p => !local.ContainsKey(p)))
            .ToList();
        if (changed.Count == 0)
            return PassResult.Empty;
        if (changed.Count > MaxLocalPassChecks)
            return await FullPassAsync(sync, cfg, folder, confirmDeletes, progress, ct);

        var deletedDirs = changed.Where(p => baseline.TryGetValue(p, out var b) && b.IsDir && !local.ContainsKey(p)).ToList();
        foreach (var p in changed)
        {
            if (deletedDirs.Any(d => SyncPlanner.IsUnder(p, d)))
                continue; // covered by that folder's crawl
            if (deletedDirs.Contains(p))
            {
                // A deleted folder: see what's really inside it on Drive before trashing anything.
                foreach (var key in remote.Keys.Where(k => SyncPlanner.IsUnder(k, p)).ToList())
                    remote.Remove(key);
                try
                {
                    foreach (var (k, v) in await RemoteSnapshot.TakeAsync(client, p, cfg.Exclude, sync.ParallelListings, ct))
                        remote[k] = v;
                }
                catch (IncompleteSnapshotException e) when (e.InnerException is DriveException d && d.Message.Contains("Node not found", StringComparison.Ordinal))
                {
                    remote.Remove(p);
                }
                continue;
            }
            try
            {
                var e = await client.InfoAsync(SyncPaths.Remote(p), ct);
                if (e.Uid is not null)
                    remote[p] = new RemoteItem(p, e.IsDir, e.Uid, e.RevisionUid, e.Size ?? 0, e.ModifiedAt, e.Sha1, e.IsProtonDocument);
            }
            catch (DriveException e) when (e is not AuthException && e.Message.Contains("Node not found", StringComparison.Ordinal))
            {
                remote.Remove(p);
            }
        }
        return await PlanAndExecuteAsync(sync, root, folder, baseline, local, remote, confirmDeletes, progress, ct);
    }

    private async Task<PassResult> PlanAndExecuteAsync(TwoWaySettings sync, string root, string folder,
        Dictionary<string, BaseItem> baseline, Dictionary<string, LocalItem> local, Dictionary<string, RemoteItem> remote,
        bool confirmDeletes, IProgress<string>? progress, CancellationToken ct)
    {
        var plan = SyncPlanner.Plan(baseline, local, remote, new PlanOptions
        {
            MaxDeletesWithoutConfirm = sync.MaxDeletesWithoutConfirm,
            DeletionsConfirmed = confirmDeletes,
            LocalSha1 = rel => Sha1Of(SyncPaths.Local(root, rel)),
        });
        if (plan.IsEmpty)
            return PassResult.Empty with { DeletionsHeld = plan.DeletionsHeld, HeldLocalDeletes = plan.DeletionsHeld ? plan.LocalDeletes : 0, HeldRemoteDeletes = plan.DeletionsHeld ? plan.RemoteDeletes : 0 };
        try
        {
            return await new SyncExecutor(client, state, trash, root, folder, local, remote).ExecuteAsync(plan, progress, ct);
        }
        finally
        {
            RemoveEmptyStaging(root);
        }
    }

    /// <summary>Removes the staging folder once nothing is left in it (never deletes contents).</summary>
    private static void RemoveEmptyStaging(string root)
    {
        try
        {
            Directory.Delete(Path.Combine(root, SyncPaths.StagingDir), recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // missing, or another pass is still using it
        }
    }

    private async Task EnsureRemoteFolderAsync(string folder, int baselineCount, CancellationToken ct)
    {
        DriveEntry info;
        try
        {
            info = await client.InfoAsync(SyncPaths.Remote(folder), ct);
        }
        catch (DriveException e) when (e is not AuthException && e.Message.Contains("Node not found", StringComparison.Ordinal))
        {
            throw new SyncFolderException(baselineCount > 0
                ? $"{SyncPaths.Remote(folder)} is gone from Drive (moved, renamed or trashed?). Local files were left alone; remove \"{folder}\" from sync or restore it on Drive."
                : $"{SyncPaths.Remote(folder)} doesn't exist on Drive.");
        }
        if (!info.IsDir)
            throw new SyncFolderException($"{SyncPaths.Remote(folder)} is a file, not a folder.");
    }

    /// <summary>First sync into an empty folder: one recursive download (much faster than file by file),
    /// then record everything that matches Drive as in sync, with Drive's modification times.</summary>
    private async Task InitialDownloadAsync(string root, string folder, SyncConfig cfg, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Downloading {SyncPaths.Remote(folder)} for the first time…");
        var staging = Path.Combine(root, SyncPaths.StagingDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await client.DownloadAsync(SyncPaths.Remote(folder), staging, null, null, ct);
            var downloaded = Path.Combine(staging, SyncPaths.Name(folder));
            var target = SyncPaths.Local(root, folder);
            if (Directory.Exists(downloaded))
            {
                Directory.Delete(target); // empty (checked by the caller)
                Directory.Move(downloaded, target);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
            RemoveEmptyStaging(root);
        }

        var remote = await RemoteSnapshot.TakeAsync(client, folder, cfg.Exclude, 4, ct);
        var records = new List<BaseItem>();
        foreach (var r in remote.Values)
        {
            var path = SyncPaths.Local(root, r.Path);
            if (r.IsDir)
            {
                if (Posix.TryStat(path) is { IsDir: true } d)
                    records.Add(new BaseItem(r.Path, true, r.Uid, null, 0, 0, d.Inode));
                continue;
            }
            if (Posix.TryStat(path) is not { IsRegular: true } st || st.Size != r.Size)
                continue; // left for the planner
            if (r.ModifiedAt is { } mtime)
                File.SetLastWriteTimeUtc(path, mtime.UtcDateTime);
            var after = Posix.TryStat(path)!.Value;
            records.Add(new BaseItem(r.Path, false, r.Uid, r.RevisionUid, after.Size, after.Mtime100ns, after.Inode));
        }
        state.UpsertBaseline(folder, records);
    }

    private static string? Sha1Of(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA1.HashData(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

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
/// <param name="log">Receives one line per change made, for the journal.</param>
public sealed class TwoWaySyncEngine(IDriveClient client, StateStore state, FreedesktopTrash trash, Action<string>? log = null)
{
    /// <summary>Local passes with more changed paths than this do a full pass instead.</summary>
    /// <summary>Above this many changed Drive folders, a local pass does a full check instead.</summary>
    public const int MaxLocalPassChecks = 50;

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
            await InitialDownloadAsync(root, folder, cfg, sync.ParallelListings, progress, ct);

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
        var deletedDirs = changed.Where(p => baseline.TryGetValue(p, out var b) && b.IsDir && !local.ContainsKey(p)).ToList();
        var files = changed.Where(p => !deletedDirs.Contains(p) && !deletedDirs.Any(d => SyncPlanner.IsUnder(p, d))).ToList();
        // One listing per Drive folder with changes (each CLI run costs ~1.5 s), not one check per file.
        var parents = files.GroupBy(SyncPaths.Parent).ToList();
        if (parents.Count > MaxLocalPassChecks)
            return await FullPassAsync(sync, cfg, folder, confirmDeletes, progress, ct);

        var listed = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<DriveEntry>?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(parents, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sync.ParallelListings), CancellationToken = ct },
            async (group, token) =>
            {
                try
                {
                    listed[group.Key] = await client.ListAsync(SyncPaths.Remote(group.Key), token);
                }
                catch (DriveException e) when (e is not AuthException && e.Message.Contains("Node not found", StringComparison.Ordinal))
                {
                    listed[group.Key] = null; // the folder isn't on Drive (yet): nothing in it is
                }
                catch (DriveException e) when (e is not AuthException)
                {
                    // e.g. it's a file on Drive: check its files one by one, as below
                }
            });
        foreach (var p in parents.Where(g => !listed.ContainsKey(g.Key)).SelectMany(g => g))
        {
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
        foreach (var group in parents)
        {
            if (!listed.ContainsKey(group.Key))
                continue; // see below
            var byName = listed[group.Key]?.Where(e => !e.NameUnavailable && e.Uid is not null)
                .GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var p in group)
            {
                if (byName is not null && byName.TryGetValue(SyncPaths.Name(p), out var e))
                    remote[p] = new RemoteItem(p, e.IsDir, e.Uid!, e.RevisionUid, e.Size ?? 0, e.ModifiedAt, e.Sha1, e.IsProtonDocument);
                else
                    remote.Remove(p);
            }
        }

        foreach (var p in deletedDirs)
        {
            if (deletedDirs.Any(d => d != p && SyncPlanner.IsUnder(p, d)))
                continue; // covered by the outer folder's crawl
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
            return await new SyncExecutor(client, state, trash, root, folder, local, remote, log).ExecuteAsync(plan, progress, ct);
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
    private async Task InitialDownloadAsync(string root, string folder, SyncConfig cfg, int parallelism, IProgress<string>? progress, CancellationToken ct)
    {
        var remotePath = SyncPaths.Remote(folder);
        progress?.Report($"Downloading {remotePath} for the first time…");
        var staging = Path.Combine(root, SyncPaths.StagingDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // The CLI prints nothing while downloading, so report what has arrived in staging.
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reporter = ReportStagingAsync(staging, remotePath, progress, watching.Token);
            try
            {
                await client.DownloadAsync(remotePath, staging, null, null, ct);
            }
            finally
            {
                await watching.CancelAsync();
                await reporter;
            }
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

        progress?.Report($"Matching the downloaded files with {remotePath}…");
        var remote = await RemoteSnapshot.TakeAsync(client, folder, cfg.Exclude, parallelism, ct,
            n => progress?.Report($"Matching the downloaded files with {remotePath}… ({n} folders checked)"));
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
        log?.Invoke($"Downloaded {remotePath} for the first time: {records.Count(r => !r.IsDir)} files in {records.Count(r => r.IsDir)} folders");
    }

    private static async Task ReportStagingAsync(string staging, string remotePath, IProgress<string>? progress, CancellationToken ct)
    {
        if (progress is null)
            return;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                long bytes = 0;
                var files = 0;
                foreach (var f in new DirectoryInfo(staging).EnumerateFiles("*", opts))
                {
                    bytes += f.Length;
                    files++;
                }
                progress.Report($"Downloading {remotePath} for the first time… ({bytes / 1048576.0:0.0} MB, {files} files so far)");
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // download finished (or staging changed underneath us); nothing to report
        }
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

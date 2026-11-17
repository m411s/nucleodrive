using Pdrive.Cli;

namespace Pdrive.Sync.TwoWay;

/// <summary>Maps sync-relative paths ("Photos/a.jpg") to local and remote paths.</summary>
public static class SyncPaths
{
    public const string StagingDir = ".pdrive-staging";

    public static string Local(string root, string rel) => Path.Combine(root, rel);

    public static string Remote(string rel) =>
        RemotePath.Join(RemotePath.Root, [.. rel.Split('/').Select(RemotePath.EscapeName)]);

    public static string Parent(string rel) => rel.LastIndexOf('/') is var i and > 0 ? rel[..i] : "";

    public static string Name(string rel) => rel[(rel.LastIndexOf('/') + 1)..];

    public static bool IsExcludedPath(string rel, IReadOnlyList<string> exclude) =>
        rel.Split('/').Any(seg => seg == StagingDir || Scanner.IsExcluded(seg, exclude));
}

public static class LocalSnapshot
{
    /// <summary>Everything under <paramref name="root"/>/<paramref name="folder"/> (not the folder itself),
    /// keyed by sync-relative path. Excluded names are left out; symlinks, unreadable entries and files
    /// modified less than <paramref name="minAge"/> ago are included as <see cref="LocalItem.Ignored"/>
    /// so they are never mistaken for deletions. Other filesystems mounted inside are not entered.</summary>
    public static Dictionary<string, LocalItem> Take(string root, string folder, IReadOnlyList<string> exclude, TimeSpan minAge,
        DateTimeOffset? now = null)
    {
        var items = new Dictionary<string, LocalItem>(StringComparer.Ordinal);
        var start = SyncPaths.Local(root, folder);
        var device = Posix.TryStat(start)?.Device;
        var cutoff = ((now ?? DateTimeOffset.UtcNow) - minAge).UtcTicks - DateTime.UnixEpoch.Ticks;
        var pending = new Stack<string>();
        pending.Push(folder);
        var opts = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

        while (pending.TryPop(out var dirRel))
        {
            List<string> names;
            try
            {
                names = Directory.EnumerateFileSystemEntries(SyncPaths.Local(root, dirRel), "*", opts).Select(Path.GetFileName).OfType<string>().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (dirRel != folder)
                    items[dirRel] = items[dirRel] with { Ignored = true };
                continue;
            }
            foreach (var name in names)
            {
                var rel = dirRel + "/" + name;
                if (SyncPaths.IsExcludedPath(name, exclude))
                    continue;
                if (Posix.TryStat(SyncPaths.Local(root, rel)) is not { } st)
                    continue; // vanished since the listing
                if (st.IsSymlink || (!st.IsDir && !st.IsRegular))
                    items[rel] = new LocalItem(rel, false, 0, st.Mtime100ns, st.Inode, Ignored: true);
                else if (st.IsDir)
                {
                    var otherFs = device is { } d && st.Device != d;
                    items[rel] = new LocalItem(rel, true, 0, 0, st.Inode, Ignored: otherFs);
                    if (!otherFs)
                        pending.Push(rel);
                }
                else
                {
                    items[rel] = new LocalItem(rel, false, st.Size, st.Mtime100ns, st.Inode, Ignored: st.Mtime100ns > cutoff);
                }
            }
        }
        return items;
    }
}

/// <summary>A remote listing failed part-way: the pass must not continue (missing items would look deleted).</summary>
public sealed class IncompleteSnapshotException(string message, Exception inner) : Exception(message, inner);

public static class RemoteSnapshot
{
    /// <summary>Crawls the Drive folder <paramref name="folder"/> (sync-relative) with up to
    /// <paramref name="parallelism"/> concurrent listings. Any listing that still fails after retries
    /// aborts the whole crawl with <see cref="IncompleteSnapshotException"/> (or AuthException).
    /// Excluded names and names that can't exist locally ('/' in a name) are left out.</summary>
    public static async Task<Dictionary<string, RemoteItem>> TakeAsync(IDriveClient client, string folder, IReadOnlyList<string> exclude,
        int parallelism, CancellationToken ct, Action<int>? progress = null)
    {
        var items = new System.Collections.Concurrent.ConcurrentDictionary<string, RemoteItem>(StringComparer.Ordinal);
        var limit = new SemaphoreSlim(Math.Max(1, parallelism));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listed = 0;
        var tasks = new List<Task>();
        var gate = new Lock();
        Exception? failure = null;

        void Enqueue(string rel)
        {
            lock (gate)
                tasks.Add(Task.Run(() => ListAsync(rel), cts.Token));
        }

        async Task ListAsync(string rel)
        {
            await limit.WaitAsync(cts.Token);
            try
            {
                var entries = await WithRetry(() => client.ListAsync(SyncPaths.Remote(rel), cts.Token), cts.Token);
                progress?.Invoke(Interlocked.Increment(ref listed));
                foreach (var e in entries)
                {
                    // Online-only: names that can't exist locally, names that couldn't be decrypted
                    // (syncing a placeholder would upload it back as a new file), excluded names.
                    if (e.NameUnavailable || e.Name.Contains('/') || e.Name.Contains('\0') || e.Name is "" or "." or ".."
                        || e.Uid is null || SyncPaths.IsExcludedPath(e.Name, exclude))
                        continue;
                    var childRel = rel + "/" + e.Name;
                    items[childRel] = new RemoteItem(childRel, e.IsDir, e.Uid, e.RevisionUid, e.Size ?? 0, e.ModifiedAt, e.Sha1, e.IsProtonDocument);
                    if (e.IsDir)
                        Enqueue(childRel);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (gate)
                    failure ??= e;
                await cts.CancelAsync();
            }
            finally
            {
                limit.Release();
            }
        }

        Enqueue(folder);
        while (true)
        {
            Task[] snapshot;
            lock (gate)
                snapshot = [.. tasks];
            try
            {
                await Task.WhenAll(snapshot);
            }
            catch (OperationCanceledException) when (failure is not null || ct.IsCancellationRequested)
            {
            }
            lock (gate)
            {
                if (tasks.Count == snapshot.Length)
                    break;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (failure is AuthException)
            throw failure;
        if (failure is not null)
            throw new IncompleteSnapshotException($"Couldn't list everything in {SyncPaths.Remote(folder)}: {(failure as DriveException)?.FirstLine ?? failure.Message}", failure);
        return new Dictionary<string, RemoteItem>(items, StringComparer.Ordinal);
    }

    /// <summary>Retries transient failures twice (1 s, 4 s); sign-in problems and "not found" fail at once.</summary>
    private static async Task<T> WithRetry<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (DriveException e) when (e is not AuthException && attempt < 2 && !e.Message.Contains("Node not found", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 4), ct);
            }
        }
    }
}

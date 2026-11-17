using Pdrive.Cli;

namespace Pdrive.Sync;

public sealed record SyncProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string RemoteDir, string? CliLine = null);

public sealed record SyncResult(int Uploaded, IReadOnlyList<string> Errors, bool Cancelled, DateTimeOffset? NextMatureAt)
{
    public bool Ok => Errors.Count == 0 && !Cancelled;
}

/// <summary>One-way backup: uploads new/changed files, never deletes anything on either side,
/// and remembers what it uploaded in <see cref="StateStore"/> (not by inspecting Drive).</summary>
public static class SyncEngine
{
    private sealed record Work(FolderPair Folder, string RemoteDir, string RelDir, List<string> Files, Dictionary<string, FileSig> Sigs);

    /// <param name="onlyFolders">Local paths to scan (from the watcher); null scans every folder.</param>
    /// <exception cref="AuthException">Signed out: the run stops, so the caller can ask for sign-in.</exception>
    public static async Task<SyncResult> RunOnceAsync(SyncConfig cfg, IDriveClient client, StateStore state,
        IProgress<SyncProgress>? progress = null, IReadOnlyCollection<string>? onlyFolders = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        DateTimeOffset? nextMature = null;

        // 1. Scan everything first, so progress totals are known up front.
        var work = new List<Work>();
        foreach (var folder in cfg.Folders)
        {
            var local = folder.LocalPath;
            if (onlyFolders is not null && !onlyFolders.Contains(local))
                continue;
            if (!Directory.Exists(local))
            {
                errors.Add($"Missing local folder: {local}");
                continue;
            }
            var scan = Scanner.Scan(local, cfg.Exclude, cfg.MinAge);
            if (scan.NextMatureAt is { } m && (nextMature is null || m < nextMature))
                nextMature = m;
            var known = state.GetFolder(local, folder.RemoteRoot);
            var changed = scan.Files.Where(kv => !known.TryGetValue(kv.Key, out var sig) || sig != kv.Value)
                .Select(kv => kv.Key).Order(StringComparer.Ordinal);
            foreach (var group in changed.GroupBy(RemotePath.Parent))
            {
                var remoteDir = group.Key.Length == 0 ? folder.RemoteRoot : RemotePath.Join(folder.RemoteRoot, group.Key);
                work.Add(new Work(folder, remoteDir, group.Key, group.ToList(), scan.Files));
            }
        }

        var filesTotal = work.Sum(w => w.Files.Count);
        var bytesTotal = work.Sum(w => w.Files.Sum(f => w.Sigs[f].Size));
        int filesDone = 0, uploaded = 0;
        long bytesDone = 0;
        var madeDirs = new HashSet<string>(StringComparer.Ordinal);

        void Report(string remoteDir, string? line = null) =>
            progress?.Report(new SyncProgress(filesDone, filesTotal, bytesDone, bytesTotal, remoteDir, line));

        void Done(Work w, IReadOnlyCollection<string> ok, IReadOnlyCollection<string> attempted)
        {
            if (ok.Count > 0)
                state.Record(w.Folder.LocalPath, w.Folder.RemoteRoot, ok.Select(r => KeyValuePair.Create(r, w.Sigs[r])));
            uploaded += ok.Count;
            filesDone += attempted.Count;
            bytesDone += attempted.Sum(r => w.Sigs[r].Size);
            Report(w.RemoteDir);
        }

        // 2. Upload, folder by folder, in batches.
        try
        {
            foreach (var w in work)
            {
                await EnsureRemoteDirsAsync(client, w.Folder.RemoteRoot, w.RelDir, madeDirs, ct);
                var output = progress is null ? null : new InlineProgress<string>(line => Report(w.RemoteDir, line));

                foreach (var chunk in Chunker.Chunks(w.Files, r => w.Sigs[r].Size))
                {
                    ct.ThrowIfCancellationRequested();
                    Report(w.RemoteDir);
                    var paths = chunk.Select(r => Path.Combine(w.Folder.LocalPath, r)).ToList();
                    try
                    {
                        await client.UploadAsync(paths, w.RemoteDir, output, ct);
                        Done(w, chunk, chunk);
                        continue;
                    }
                    catch (DriveException e) when (e is not AuthException)
                    {
                        if (chunk.Count == 1)
                        {
                            errors.Add($"{w.RemoteDir}/{RemotePath.Name(chunk[0])}: {e.FirstLine}");
                            Done(w, [], chunk);
                            continue;
                        }
                    }

                    // The batch failed: retry one by one so a single bad file doesn't hold back the rest.
                    foreach (var r in chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            await client.UploadAsync([Path.Combine(w.Folder.LocalPath, r)], w.RemoteDir, output, ct);
                            Done(w, [r], [r]);
                        }
                        catch (DriveException e) when (e is not AuthException)
                        {
                            errors.Add($"{w.RemoteDir}/{RemotePath.Name(r)}: {e.FirstLine}");
                            Done(w, [], [r]);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SyncResult(uploaded, errors, Cancelled: true, nextMature);
        }
        return new SyncResult(uploaded, errors, Cancelled: false, nextMature);
    }

    /// <summary>Creates the remote folder and its parents (shortest first). Failure is fine: it may exist already.</summary>
    private static async Task EnsureRemoteDirsAsync(IDriveClient client, string remoteRoot, string relDir, HashSet<string> made, CancellationToken ct)
    {
        var parts = relDir.Length == 0 ? [] : relDir.Split('/');
        for (var i = 0; i <= parts.Length; i++)
        {
            var dir = RemotePath.Join(remoteRoot, string.Join('/', parts[..i]));
            if (!made.Add(dir))
                continue;
            try
            {
                await client.MkdirAsync(dir, ct);
            }
            catch (DriveException e) when (e is not AuthException)
            {
                // probably exists already; a real problem shows up as an upload error
            }
        }
    }
}

/// <summary>IProgress that reports on the caller's thread (Progress&lt;T&gt; would post to the thread pool).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

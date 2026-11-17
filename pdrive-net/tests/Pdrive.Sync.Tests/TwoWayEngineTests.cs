using Pdrive.Cli;
using Pdrive.Sync.TwoWay;
using Pdrive.Tests;

namespace Pdrive.Sync.Tests;

/// <summary>A synced "Docs" folder between a temp ~/proton-drive and the persistent fake Drive.
/// "Remote" changes are made through the CLI, as another device would.</summary>
public sealed class TwoWayFixture : IDisposable
{
    public TempDir Tmp { get; } = new();
    public string Root { get; }
    public FakeCli Fake { get; }
    public ProtonDriveCli Cli { get; }
    public StateStore State { get; }
    public TwoWaySyncEngine Engine { get; }
    public SyncConfig Cfg { get; set; }
    public List<string> Log { get; } = [];
    private int _otherDevice;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static readonly DateTime Old = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public TwoWayFixture()
    {
        Root = Tmp["proton-drive"];
        Directory.CreateDirectory(Root);
        Fake = new FakeCli(Tmp["fake.log"]).UseTree(Tmp["tree.json"]);
        Cli = Fake.Client();
        State = new StateStore(Tmp["state.db"]);
        Engine = new TwoWaySyncEngine(Cli, State, new FreedesktopTrash(Tmp["data"], mountPoints: () => ["/"]), m => { lock (Log) Log.Add(m); });
        Cfg = new SyncConfig { MinAgeSeconds = 0, Sync = new TwoWaySettings { Local = Root, Folders = ["Docs"] } };
    }

    public void Dispose()
    {
        State.Dispose();
        Tmp.Dispose();
    }

    public string LocalPath(string rel) => Path.Combine(Root, rel);
    public string TrashFiles => Tmp["data/Trash/files"];

    public void WriteLocal(string rel, string content, DateTime? mtime = null)
    {
        var p = LocalPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, mtime ?? Old);
    }

    public string? ReadLocal(string rel) => File.Exists(LocalPath(rel)) ? File.ReadAllText(LocalPath(rel)) : null;

    /// <summary>Writes a file on "another device" and uploads it into Drive.</summary>
    public async Task WriteRemote(string rel, string content, DateTime? mtime = null)
    {
        var dir = Tmp[$"other-device/{_otherDevice++}"];
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SyncPaths.Name(rel));
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, mtime ?? Old);
        await EnsureRemoteFolders(SyncPaths.Parent(rel));
        await Cli.UploadAsync([file], SyncPaths.Remote(SyncPaths.Parent(rel)), ct: Ct);
    }

    public async Task EnsureRemoteFolders(string rel)
    {
        var parts = rel.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            try { await Cli.MkdirAsync(SyncPaths.Remote(string.Join('/', parts[..i])), Ct); }
            catch (DriveException) { }
        }
    }

    public async Task<string?> ReadRemote(string rel)
    {
        var dir = Tmp[$"read/{Guid.NewGuid():N}"];
        Directory.CreateDirectory(dir);
        try
        {
            await Cli.DownloadAsync(SyncPaths.Remote(rel), dir, ct: Ct);
        }
        catch (DriveException)
        {
            return null;
        }
        var f = Path.Combine(dir, SyncPaths.Name(rel));
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    public async Task<string[]> RemoteNames(string rel) => (await Cli.ListAsync(SyncPaths.Remote(rel), Ct)).Select(e => e.Name).ToArray();

    public Task<PassResult> Full(bool confirm = false) => Engine.FullPassAsync(Cfg.Sync, Cfg, "Docs", confirm, null, Ct);
    public Task<PassResult> LocalPass(bool confirm = false) => Engine.LocalPassAsync(Cfg.Sync, Cfg, "Docs", confirm, null, Ct);

    /// <summary>Drive has Docs/a.txt and Docs/sub/b.txt, synced to ~/proton-drive/Docs.</summary>
    public async Task SyncedStart()
    {
        await WriteRemote("Docs/a.txt", "A");
        await WriteRemote("Docs/sub/b.txt", "B");
        await Full();
    }
}

public sealed class TwoWayEngineTests : IDisposable
{
    private readonly TwoWayFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task First_sync_downloads_the_drive_folder_and_records_it()
    {
        await _f.SyncedStart();
        Assert.Equal("A", _f.ReadLocal("Docs/a.txt"));
        Assert.Equal("B", _f.ReadLocal("Docs/sub/b.txt"));
        Assert.Equal(TwoWayFixture.Old, File.GetLastWriteTimeUtc(_f.LocalPath("Docs/a.txt"))); // Drive's mtime, not "now"
        Assert.Equal(3, _f.State.LoadBaseline("Docs").Count);
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task First_sync_reports_the_matching_step()
    {
        await _f.WriteRemote("Docs/a.txt", "A");
        await _f.WriteRemote("Docs/sub/b.txt", "B");
        var messages = new List<string>();
        await _f.Engine.FullPassAsync(_f.Cfg.Sync, _f.Cfg, "Docs", false, new InlineProgress<string>(m => { lock (messages) messages.Add(m); }), TwoWayFixture.Ct);
        Assert.Equal("Downloading /my-files/Docs for the first time…", messages[0]);
        Assert.Contains(messages, m => m.StartsWith("Matching the downloaded files with /my-files/Docs… (", StringComparison.Ordinal) && m.EndsWith("folders checked)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Checking /my-files/Docs on Drive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_change_is_logged()
    {
        await _f.SyncedStart();
        Assert.Contains("Downloaded /my-files/Docs for the first time: 2 files in 1 folders", _f.Log);
        _f.Log.Clear();

        _f.WriteLocal("Docs/new.txt", "N");
        File.Move(_f.LocalPath("Docs/a.txt"), _f.LocalPath("Docs/a2.txt"));
        File.Delete(_f.LocalPath("Docs/sub/b.txt"));
        await _f.WriteRemote("Docs/from-phone.txt", "P");
        await _f.Full();
        Assert.Contains("Uploaded: Docs/new.txt", _f.Log);
        Assert.Contains("Renamed on Drive to match this computer: Docs/a.txt → Docs/a2.txt", _f.Log);
        Assert.Contains("Moved to the Drive trash (deleted on this computer): Docs/sub/b.txt", _f.Log);
        Assert.Contains("Downloaded: Docs/from-phone.txt", _f.Log);
        _f.Log.Clear();

        _f.WriteLocal("Docs/new.txt", "older local edit", TwoWayFixture.Old.AddHours(1));
        await _f.WriteRemote("Docs/new.txt", "newer Drive edit", TwoWayFixture.Old.AddHours(2));
        await _f.Cli.TrashAsync(["/my-files/Docs/from-phone.txt"], TwoWayFixture.Ct);
        await _f.Full();
        Assert.Contains(_f.Log, l => l.StartsWith("Conflict: Drive's newer version of Docs/new.txt wins; the local copy was moved to ", StringComparison.Ordinal));
        Assert.Contains(_f.Log, l => l.StartsWith("Moved to the desktop Trash (deleted on Drive): Docs/from-phone.txt → ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_new_and_edited_files_are_uploaded()
    {
        await _f.SyncedStart();
        _f.WriteLocal("Docs/new.txt", "N");
        _f.WriteLocal("Docs/a.txt", "A2", TwoWayFixture.Old.AddHours(1));
        _f.WriteLocal("Docs/fresh/c.txt", "C");
        var r = await _f.Full();
        Assert.Equal(3, r.Uploaded);
        Assert.Equal("N", await _f.ReadRemote("Docs/new.txt"));
        Assert.Equal("A2", await _f.ReadRemote("Docs/a.txt"));
        Assert.Equal("C", await _f.ReadRemote("Docs/fresh/c.txt"));
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task Remote_new_and_edited_files_are_downloaded()
    {
        await _f.SyncedStart();
        await _f.WriteRemote("Docs/a.txt", "A-from-phone", TwoWayFixture.Old.AddHours(1));
        await _f.WriteRemote("Docs/sub/deep/d.txt", "D");
        var r = await _f.Full();
        Assert.Equal(2, r.Downloaded);
        Assert.Equal("A-from-phone", _f.ReadLocal("Docs/a.txt"));
        Assert.Equal("D", _f.ReadLocal("Docs/sub/deep/d.txt"));
        Assert.False(Directory.Exists(_f.TrashFiles)); // an unedited local copy is just replaced
        Assert.False(Directory.Exists(_f.LocalPath(".pdrive-staging"))); // no leftovers
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task New_drive_files_in_one_folder_download_in_one_cli_run()
    {
        await _f.SyncedStart();
        for (var i = 0; i < 5; i++)
            await _f.WriteRemote($"Docs/photos/p{i}.jpg", $"photo {i}", TwoWayFixture.Old.AddMinutes(i));
        await _f.WriteRemote("Docs/sub/c.txt", "C");
        _f.Fake.ClearLog();

        var r = await _f.Full();
        Assert.Equal(6, r.Downloaded);
        var downloads = _f.Fake.Log().Where(l => l[0] == "download").ToList();
        // the fake logs one line per path; group them by the staging folder (one per CLI run)
        Assert.Equal(2, downloads.Select(l => l[^1]).Distinct().Count());
        for (var i = 0; i < 5; i++)
            Assert.Equal($"photo {i}", _f.ReadLocal($"Docs/photos/p{i}.jpg"));
        Assert.Equal(TwoWayFixture.Old.AddMinutes(3), File.GetLastWriteTimeUtc(_f.LocalPath("Docs/photos/p3.jpg")));
        Assert.False(Directory.Exists(_f.LocalPath(".pdrive-staging")));
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task Downloads_for_several_folders_run_at_the_same_time()
    {
        await _f.SyncedStart();
        for (var i = 0; i < 4; i++)
            await _f.WriteRemote($"Docs/day{i}/p.jpg", $"photo {i}");
        var cli = new GatedClient(_f.Cli, expected: 4);
        var engine = new TwoWaySyncEngine(cli, _f.State, new FreedesktopTrash(_f.Tmp["data"], mountPoints: () => ["/"]), null);

        var r = await engine.FullPassAsync(_f.Cfg.Sync, _f.Cfg, "Docs", false, null, TwoWayFixture.Ct);
        Assert.Equal(4, r.Downloaded);
        Assert.Equal(4, cli.MaxConcurrent); // all four folders' runs were in flight together
        for (var i = 0; i < 4; i++)
            Assert.Equal($"photo {i}", _f.ReadLocal($"Docs/day{i}/p.jpg"));
    }

    /// <summary>Holds each download until <c>expected</c> are running at once (or 5 s pass).</summary>
    private sealed class GatedClient(IDriveClient inner, int expected) : IDriveClient
    {
        private int _running;
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrent { get; private set; }

        public async Task DownloadManyAsync(IReadOnlyList<string> remotePaths, string localDir, long? totalSize = null,
            IProgress<string>? output = null, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref _running);
            lock (_all)
                MaxConcurrent = Math.Max(MaxConcurrent, now);
            if (now >= expected)
                _all.TrySetResult();
            await Task.WhenAny(_all.Task, Task.Delay(5000, ct));
            await inner.DownloadManyAsync(remotePaths, localDir, totalSize, output, ct);
            Interlocked.Decrement(ref _running);
        }
        public Task<IReadOnlyList<DriveEntry>> ListAsync(string path, CancellationToken ct = default) => inner.ListAsync(path, ct);
        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => inner.IsLoggedInAsync(ct);
        public Task LoginAsync(CancellationToken ct = default) => inner.LoginAsync(ct);
        public Task LogoutAsync(CancellationToken ct = default) => inner.LogoutAsync(ct);
        public Task MkdirAsync(string remotePath, CancellationToken ct = default) => inner.MkdirAsync(remotePath, ct);
        public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default) =>
            inner.UploadAsync(localPaths, remoteDir, output, ct);
        public Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default) =>
            inner.DownloadAsync(remotePath, localDir, size, output, ct);
        public Task<DriveEntry> InfoAsync(string remotePath, CancellationToken ct = default) => inner.InfoAsync(remotePath, ct);
        public Task TrashAsync(IReadOnlyList<string> remotePaths, CancellationToken ct = default) => inner.TrashAsync(remotePaths, ct);
        public Task RenameAsync(string remotePath, string newName, CancellationToken ct = default) => inner.RenameAsync(remotePath, newName, ct);
        public Task MoveAsync(IReadOnlyList<string> remotePaths, string targetParent, CancellationToken ct = default) => inner.MoveAsync(remotePaths, targetParent, ct);
    }

    [Fact]
    public async Task A_failed_batch_still_places_what_arrived_and_retries_the_rest()
    {
        await _f.SyncedStart();
        await _f.WriteRemote("Docs/x/1.txt", "one");
        await _f.WriteRemote("Docs/x/2.txt", "two");
        // Drive loses 2.txt between the listing and the download: the run fails part-way
        var cli = new VanishingClient(_f.Cli, "/my-files/Docs/x/2.txt");
        var engine = new TwoWaySyncEngine(cli, _f.State, new FreedesktopTrash(_f.Tmp["data"], mountPoints: () => ["/"]), null);

        var r = await engine.FullPassAsync(_f.Cfg.Sync, _f.Cfg, "Docs", false, null, TwoWayFixture.Ct);
        Assert.Equal("one", _f.ReadLocal("Docs/x/1.txt"));
        Assert.Null(_f.ReadLocal("Docs/x/2.txt"));
        Assert.True(r.Errors.Any(e => e.Contains("x/2.txt: ", StringComparison.Ordinal) && e.Contains("Node not found")), string.Join(" | ", r.Errors));

        Assert.Equal(1, (await _f.Full()).Downloaded); // next pass picks it up
        Assert.Equal("two", _f.ReadLocal("Docs/x/2.txt"));
    }

    [Fact]
    public async Task Local_changes_are_checked_with_one_listing_per_folder_not_one_call_per_file()
    {
        await _f.SyncedStart();
        for (var i = 0; i < 6; i++)
            _f.WriteLocal($"Docs/sub/new{i}.txt", $"n{i}");
        _f.WriteLocal("Docs/a.txt", "A-edited", TwoWayFixture.Old.AddHours(2));
        _f.WriteLocal("Docs/fresh/deep/x.txt", "X"); // a new folder tree: not on Drive yet

        var cli = new CountingClient(_f.Cli);
        var engine = new TwoWaySyncEngine(cli, _f.State, new FreedesktopTrash(_f.Tmp["data"], mountPoints: () => ["/"]), null);
        var r = await engine.LocalPassAsync(_f.Cfg.Sync, _f.Cfg, "Docs", false, null, TwoWayFixture.Ct);

        Assert.Equal(8, r.Uploaded);
        Assert.Empty(r.Errors);
        // files are checked by listing their folders: Docs, Docs/sub, Docs/fresh/deep (not found yet) …
        Assert.Equal(["Docs", "Docs/fresh/deep", "Docs/sub"], cli.Listed.Take(3).Order(StringComparer.Ordinal));
        // … and the new tree fresh/deep goes up in one upload; its top folder's ID is the only `info`
        Assert.Equal(1, cli.Count("info"));
        Assert.Equal(0, cli.Count("mkdir"));
        Assert.Equal(3, cli.Count("upload")); // Docs, Docs/sub, the fresh tree
        Assert.Equal("X", await _f.ReadRemote("Docs/fresh/deep/x.txt"));
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task A_new_folder_tree_goes_up_in_one_upload()
    {
        await _f.SyncedStart();
        _f.WriteLocal("Docs/copied/a.txt", "a");
        _f.WriteLocal("Docs/copied/sub/b.txt", "b");
        _f.WriteLocal("Docs/copied/sub/deep/c.txt", "c");
        Directory.CreateDirectory(_f.LocalPath("Docs/copied/empty"));

        var cli = new CountingClient(_f.Cli);
        var engine = new TwoWaySyncEngine(cli, _f.State, new FreedesktopTrash(_f.Tmp["data"], mountPoints: () => ["/"]), null);
        var r = await engine.FullPassAsync(_f.Cfg.Sync, _f.Cfg, "Docs", false, null, TwoWayFixture.Ct);

        Assert.Equal(3, r.Uploaded);
        Assert.Empty(r.Errors);
        Assert.Equal(1, cli.Count("upload"));
        Assert.Equal(0, cli.Count("mkdir"));
        Assert.Equal("c", await _f.ReadRemote("Docs/copied/sub/deep/c.txt"));
        Assert.Contains("empty", await _f.RemoteNames("Docs/copied"));
        Assert.Equal(0, (await _f.Full()).Changes); // all recorded: nothing to do next time
        await _f.WriteRemote("Docs/copied/sub/b.txt", "b-from-phone", TwoWayFixture.Old.AddDays(1));
        Assert.Equal(1, (await _f.Full()).Downloaded); // and tracked like any other file
        Assert.Equal("b-from-phone", _f.ReadLocal("Docs/copied/sub/b.txt"));
    }

    [Fact]
    public async Task A_new_tree_with_excluded_files_or_links_takes_the_normal_path()
    {
        await _f.SyncedStart();
        _f.WriteLocal("Docs/t1/a.txt", "a");
        _f.WriteLocal("Docs/t1/sub/.hidden", "excluded by .*");
        _f.WriteLocal("Docs/t2/x.txt", "x");
        Directory.CreateDirectory(_f.LocalPath("Docs/t2/sub"));
        File.WriteAllText(_f.Tmp["secret.txt"], "secret");
        File.CreateSymbolicLink(_f.LocalPath("Docs/t2/sub/link.txt"), _f.Tmp["secret.txt"]);

        var cli = new CountingClient(_f.Cli);
        var engine = new TwoWaySyncEngine(cli, _f.State, new FreedesktopTrash(_f.Tmp["data"], mountPoints: () => ["/"]), null);
        var r = await engine.FullPassAsync(_f.Cfg.Sync, _f.Cfg, "Docs", false, null, TwoWayFixture.Ct);

        Assert.Equal(2, r.Uploaded);
        Assert.Equal(4, cli.Count("mkdir")); // t1, t1/sub, t2, t2/sub one by one
        Assert.DoesNotContain(".hidden", await _f.RemoteNames("Docs/t1/sub"));
        Assert.Empty(await _f.RemoteNames("Docs/t2/sub"));
    }

    /// <summary>Counts calls by kind and records listed paths (relative to My files).</summary>
    private sealed class CountingClient(IDriveClient inner) : IDriveClient
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _calls = [];
        public List<string> Listed { get; } = [];
        public int Count(string kind) => _calls.Count(c => c == kind);

        public Task<IReadOnlyList<DriveEntry>> ListAsync(string path, CancellationToken ct = default)
        {
            _calls.Add("list");
            lock (Listed)
                Listed.Add(path["/my-files/".Length..]);
            return inner.ListAsync(path, ct);
        }
        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => inner.IsLoggedInAsync(ct);
        public Task LoginAsync(CancellationToken ct = default) => inner.LoginAsync(ct);
        public Task LogoutAsync(CancellationToken ct = default) => inner.LogoutAsync(ct);
        public Task MkdirAsync(string remotePath, CancellationToken ct = default)
        {
            _calls.Add("mkdir");
            return inner.MkdirAsync(remotePath, ct);
        }
        public Task<string?> CreateFolderAsync(string remotePath, CancellationToken ct = default)
        {
            _calls.Add("mkdir");
            return inner.CreateFolderAsync(remotePath, ct);
        }
        public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default)
        {
            _calls.Add("upload");
            return inner.UploadAsync(localPaths, remoteDir, output, ct);
        }
        public Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default) =>
            inner.DownloadAsync(remotePath, localDir, size, output, ct);
        public Task DownloadManyAsync(IReadOnlyList<string> remotePaths, string localDir, long? totalSize = null,
            IProgress<string>? output = null, CancellationToken ct = default) => inner.DownloadManyAsync(remotePaths, localDir, totalSize, output, ct);
        public Task<DriveEntry> InfoAsync(string remotePath, CancellationToken ct = default)
        {
            _calls.Add("info");
            return inner.InfoAsync(remotePath, ct);
        }
        public Task TrashAsync(IReadOnlyList<string> remotePaths, CancellationToken ct = default) => inner.TrashAsync(remotePaths, ct);
        public Task RenameAsync(string remotePath, string newName, CancellationToken ct = default) => inner.RenameAsync(remotePath, newName, ct);
        public Task MoveAsync(IReadOnlyList<string> remotePaths, string targetParent, CancellationToken ct = default) => inner.MoveAsync(remotePaths, targetParent, ct);
    }

    /// <summary>Downloads everything except one path, then fails like the CLI does for a missing node.</summary>
    private sealed class VanishingClient(IDriveClient inner, string vanished) : IDriveClient
    {
        public Task<IReadOnlyList<DriveEntry>> ListAsync(string path, CancellationToken ct = default) => inner.ListAsync(path, ct);
        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => inner.IsLoggedInAsync(ct);
        public Task LoginAsync(CancellationToken ct = default) => inner.LoginAsync(ct);
        public Task LogoutAsync(CancellationToken ct = default) => inner.LogoutAsync(ct);
        public Task MkdirAsync(string remotePath, CancellationToken ct = default) => inner.MkdirAsync(remotePath, ct);
        public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default) =>
            inner.UploadAsync(localPaths, remoteDir, output, ct);
        public Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default) =>
            inner.DownloadAsync(remotePath, localDir, size, output, ct);
        public async Task DownloadManyAsync(IReadOnlyList<string> remotePaths, string localDir, long? totalSize = null,
            IProgress<string>? output = null, CancellationToken ct = default)
        {
            await inner.DownloadManyAsync(remotePaths.Where(p => p != vanished).ToList(), localDir, totalSize, output, ct);
            if (remotePaths.Contains(vanished))
                throw new DriveException($"Node not found: {vanished}");
        }
        public Task<DriveEntry> InfoAsync(string remotePath, CancellationToken ct = default) => inner.InfoAsync(remotePath, ct);
        public Task TrashAsync(IReadOnlyList<string> remotePaths, CancellationToken ct = default) => inner.TrashAsync(remotePaths, ct);
        public Task RenameAsync(string remotePath, string newName, CancellationToken ct = default) => inner.RenameAsync(remotePath, newName, ct);
        public Task MoveAsync(IReadOnlyList<string> remotePaths, string targetParent, CancellationToken ct = default) => inner.MoveAsync(remotePaths, targetParent, ct);
    }

    [Fact]
    public async Task Deletions_go_to_the_trash_on_the_other_side()
    {
        await _f.SyncedStart();
        File.Delete(_f.LocalPath("Docs/a.txt"));
        await _f.Cli.TrashAsync(["/my-files/Docs/sub/b.txt"], TwoWayFixture.Ct);
        var r = await _f.Full();
        Assert.Equal((1, 1), (r.TrashedRemote, r.TrashedLocal));
        Assert.DoesNotContain("a.txt", await _f.RemoteNames("Docs"));
        Assert.Null(_f.ReadLocal("Docs/sub/b.txt"));
        Assert.Equal("B", File.ReadAllText(Path.Combine(_f.TrashFiles, "b.txt"))); // recoverable
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task Conflicts_newest_wins_and_a_losing_local_file_goes_to_the_trash()
    {
        await _f.SyncedStart();
        _f.WriteLocal("Docs/a.txt", "local-newer", TwoWayFixture.Old.AddHours(3));
        await _f.WriteRemote("Docs/a.txt", "remote-older", TwoWayFixture.Old.AddHours(2));
        _f.WriteLocal("Docs/sub/b.txt", "local-older", TwoWayFixture.Old.AddHours(2));
        await _f.WriteRemote("Docs/sub/b.txt", "remote-newer", TwoWayFixture.Old.AddHours(3));

        await _f.Full();
        Assert.Equal("local-newer", await _f.ReadRemote("Docs/a.txt"));
        Assert.Equal("local-newer", _f.ReadLocal("Docs/a.txt"));
        Assert.Equal("remote-newer", _f.ReadLocal("Docs/sub/b.txt"));
        Assert.Equal("local-older", File.ReadAllText(Path.Combine(_f.TrashFiles, "b.txt")));
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task Renames_are_moves_on_both_sides_without_transfers()
    {
        await _f.SyncedStart();
        var uid = (await _f.Cli.InfoAsync("/my-files/Docs/a.txt", TwoWayFixture.Ct)).Uid;
        await _f.Cli.RenameAsync("/my-files/Docs/sub/b.txt", "b-renamed.txt", TwoWayFixture.Ct);
        File.Move(_f.LocalPath("Docs/a.txt"), _f.LocalPath("Docs/a-renamed.txt"));

        var r = await _f.Full();
        Assert.Equal((2, 0, 0), (r.Moved, r.Uploaded, r.Downloaded));
        Assert.Equal("B", _f.ReadLocal("Docs/sub/b-renamed.txt"));
        Assert.Equal(uid, (await _f.Cli.InfoAsync("/my-files/Docs/a-renamed.txt", TwoWayFixture.Ct)).Uid); // version history kept
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task Local_folder_rename_is_one_move_on_drive()
    {
        await _f.SyncedStart();
        Directory.Move(_f.LocalPath("Docs/sub"), _f.LocalPath("Docs/moved"));
        var r = await _f.Full();
        Assert.Equal((1, 0), (r.Moved, r.Uploaded));
        Assert.Equal(["b.txt"], await _f.RemoteNames("Docs/moved"));
        Assert.Equal(0, (await _f.Full()).Changes);
    }

    [Fact]
    public async Task Missing_local_folder_is_an_error_not_a_mass_delete()
    {
        await _f.SyncedStart();
        Directory.Delete(_f.LocalPath("Docs"), recursive: true);
        var e = await Assert.ThrowsAsync<SyncFolderException>(() => _f.Full());
        Assert.Contains("missing", e.Message);
        Assert.Equal(["sub", "a.txt"], await _f.RemoteNames("Docs"));
    }

    [Fact]
    public async Task Folder_gone_from_drive_is_an_error_and_local_files_stay()
    {
        await _f.SyncedStart();
        await _f.Cli.TrashAsync(["/my-files/Docs"], TwoWayFixture.Ct);
        await Assert.ThrowsAsync<SyncFolderException>(() => _f.Full());
        Assert.Equal("A", _f.ReadLocal("Docs/a.txt"));
    }

    [Fact]
    public async Task A_failed_listing_aborts_the_pass_instead_of_looking_like_deletions()
    {
        await _f.SyncedStart();
        _f.Fake.Set("FAKE_LIST_FAIL", "Docs/sub");
        await Assert.ThrowsAsync<IncompleteSnapshotException>(() => _f.Full());
        Assert.Equal("B", _f.ReadLocal("Docs/sub/b.txt"));
        Assert.False(Directory.Exists(_f.TrashFiles));
    }

    [Fact]
    public async Task Mass_deletion_waits_for_confirmation()
    {
        _f.Cfg = _f.Cfg with { Sync = _f.Cfg.Sync with { MaxDeletesWithoutConfirm = 1 } };
        await _f.SyncedStart();
        File.Delete(_f.LocalPath("Docs/a.txt"));
        File.Delete(_f.LocalPath("Docs/sub/b.txt"));
        var held = await _f.Full();
        Assert.True(held.DeletionsHeld);
        Assert.Equal(2, held.HeldRemoteDeletes);
        Assert.Contains("a.txt", await _f.RemoteNames("Docs"));
        Assert.Null(_f.ReadLocal("Docs/a.txt")); // and not downloaded again meanwhile

        var confirmed = await _f.Full(confirm: true);
        Assert.Equal(2, confirmed.TrashedRemote);
        Assert.DoesNotContain("a.txt", await _f.RemoteNames("Docs"));
    }

    [Fact]
    public async Task Local_pass_uploads_without_crawling_and_respects_remote_edits()
    {
        await _f.SyncedStart();
        _f.WriteLocal("Docs/sub/b.txt", "B2", TwoWayFixture.Old.AddHours(1));
        _f.WriteLocal("Docs/n.txt", "N");
        var r = await _f.LocalPass();
        Assert.Equal(2, r.Uploaded);
        Assert.Equal("B2", await _f.ReadRemote("Docs/sub/b.txt"));

        // Edited on both sides: the local pass checks Drive before overwriting, and the newer wins.
        _f.WriteLocal("Docs/a.txt", "local-old", TwoWayFixture.Old.AddHours(1));
        await _f.WriteRemote("Docs/a.txt", "remote-new", TwoWayFixture.Old.AddHours(2));
        await _f.LocalPass();
        Assert.Equal("remote-new", _f.ReadLocal("Docs/a.txt"));
        Assert.Equal("remote-new", await _f.ReadRemote("Docs/a.txt"));
    }

    [Fact]
    public async Task Local_pass_checks_a_deleted_folder_on_drive_first()
    {
        await _f.SyncedStart();
        await _f.WriteRemote("Docs/sub/new-on-phone.txt", "P"); // not seen locally yet
        Directory.Delete(_f.LocalPath("Docs/sub"), recursive: true);
        await _f.LocalPass();
        // The folder isn't trashed as a whole: the new remote file comes down, the old one is trashed.
        Assert.Equal("P", _f.ReadLocal("Docs/sub/new-on-phone.txt"));
        Assert.Equal(["new-on-phone.txt"], await _f.RemoteNames("Docs/sub"));
    }

    [Fact]
    public async Task Hidden_and_settling_files_are_left_alone()
    {
        await _f.SyncedStart();
        await _f.WriteRemote("Docs/.hidden", "H");
        await _f.Full();
        Assert.Null(_f.ReadLocal("Docs/.hidden"));
        Assert.Contains(".hidden", await _f.RemoteNames("Docs"));

        _f.Cfg = _f.Cfg with { MinAgeSeconds = 3600 };
        _f.WriteLocal("Docs/a.txt", "being-written", DateTime.UtcNow);
        Assert.Equal(0, (await _f.Full()).Changes);
        Assert.Equal("A", await _f.ReadRemote("Docs/a.txt"));
    }

    [Fact]
    public async Task Existing_local_files_merge_with_drive_on_first_sync()
    {
        await _f.WriteRemote("Docs/same.txt", "S");
        await _f.WriteRemote("Docs/remote-only.txt", "R");
        _f.WriteLocal("Docs/same.txt", "S", TwoWayFixture.Old.AddHours(5));
        _f.WriteLocal("Docs/local-only.txt", "L");
        var r = await _f.Full();
        Assert.Equal((1, 1), (r.Uploaded, r.Downloaded)); // same.txt matched by SHA-1: no transfer
        Assert.Equal("L", await _f.ReadRemote("Docs/local-only.txt"));
        Assert.Equal("R", _f.ReadLocal("Docs/remote-only.txt"));
        Assert.Equal(0, (await _f.Full()).Changes);
    }
}

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
        Engine = new TwoWaySyncEngine(Cli, State, new FreedesktopTrash(Tmp["data"], mountPoints: () => ["/"]));
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

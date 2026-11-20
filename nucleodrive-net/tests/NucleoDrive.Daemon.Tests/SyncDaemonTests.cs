using NucleoDrive.Ipc;
using NucleoDrive.Sync;

namespace NucleoDrive.Daemon.Tests;

/// <summary>Two-way sync running inside the daemon, against the persistent fake Drive.</summary>
public sealed class SyncDaemonTests : IAsyncLifetime
{
    private readonly DaemonFixture _d = new();
    private static CancellationToken Ct => DaemonFixture.Ct;
    private string SyncRoot => _d.Tmp["proton-drive"];

    public async ValueTask InitializeAsync()
    {
        _d.Fake.UseTree(_d.Tmp["tree.json"]);
        await Remote("Docs/a.txt", "A");
        await Remote("Docs/sub/b.txt", "B");
    }

    public ValueTask DisposeAsync() => _d.DisposeAsync();

    private async Task Remote(string rel, string content)
    {
        var cli = _d.Fake.Client();
        var parts = rel.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            try { await cli.MkdirAsync("/my-files/" + string.Join('/', parts[..i]), Ct); }
            catch (NucleoDrive.Cli.DriveException) { }
        }
        var dir = _d.Tmp["device/" + Guid.NewGuid().ToString("N")];
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, parts[^1]);
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await cli.UploadAsync([file], "/my-files/" + string.Join('/', parts[..^1]), ct: Ct);
    }

    private async Task<string[]> RemoteNames(string rel) =>
        (await _d.Fake.Client().ListAsync("/my-files/" + rel, Ct)).Select(e => e.Name).ToArray();

    private void Configure(int maxDeletes = 50, params string[] folders) =>
        _d.WriteConfig(new SyncConfig
        {
            MinAgeSeconds = 0,
            Sync = new TwoWaySettings { Local = SyncRoot, Folders = folders.Length > 0 ? folders : ["Docs"], MaxDeletesWithoutConfirm = maxDeletes },
        });

    private Task<DaemonStatus> WaitForSync(SyncState state, int seconds = 15) =>
        _d.WaitForStatus(s => s.Sync?.State == state, seconds);

    [Fact]
    public async Task Syncs_the_folder_and_uploads_file_manager_changes_within_seconds()
    {
        Configure();
        await _d.StartAsync();
        await WaitForSync(SyncState.Idle);
        Assert.Equal("A", File.ReadAllText(Path.Combine(SyncRoot, "Docs/a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(SyncRoot, "Docs/sub/b.txt")));
        Assert.NotNull((await _d.Client!.GetStatusAsync(Ct)).Sync!.LastFullScan);

        File.WriteAllText(Path.Combine(SyncRoot, "Docs/from-file-manager.txt"), "F");
        File.Move(Path.Combine(SyncRoot, "Docs/a.txt"), Path.Combine(SyncRoot, "Docs/a-renamed.txt"));
        Assert.True(await DaemonFixture.WaitFor(() => RemoteNames("Docs").Result is var n
            && n.Contains("from-file-manager.txt") && n.Contains("a-renamed.txt") && !n.Contains("a.txt"), 15),
            "local changes didn't reach Drive");
    }

    [Fact]
    public async Task Sync_now_picks_up_changes_made_on_drive()
    {
        Configure();
        await _d.StartAsync();
        await WaitForSync(SyncState.Idle);
        await Remote("Docs/from-phone.txt", "P");
        await _d.Client!.SyncNowAsync(Ct);
        Assert.True(await DaemonFixture.WaitFor(() => File.Exists(Path.Combine(SyncRoot, "Docs/from-phone.txt")), 15));
    }

    [Fact]
    public async Task Mass_deletion_waits_for_confirmation_over_ipc()
    {
        Configure(maxDeletes: 1);
        await _d.StartAsync();
        await WaitForSync(SyncState.Idle);
        File.Delete(Path.Combine(SyncRoot, "Docs/a.txt"));
        File.Delete(Path.Combine(SyncRoot, "Docs/sub/b.txt"));

        var s = await WaitForSync(SyncState.NeedsConfirmation);
        Assert.Equal(2, s.Sync!.HeldDeletes);
        Assert.Contains("a.txt", await RemoteNames("Docs"));

        await _d.Client!.ConfirmDeletionsAsync(Ct);
        Assert.True(await DaemonFixture.WaitFor(() => !RemoteNames("Docs").Result.Contains("a.txt"), 15));
        await WaitForSync(SyncState.Idle);
    }

    [Fact]
    public async Task Folders_are_chosen_over_ipc_and_removing_one_keeps_files()
    {
        await Remote("Photos/p.jpg", "JPG");
        Configure();
        await _d.StartAsync();
        await WaitForSync(SyncState.Idle);

        await _d.Client!.SetSyncFoldersAsync(["Docs", "Photos"], Ct);
        Assert.Equal(["Docs", "Photos"], (await _d.Client.GetSyncSettingsAsync(Ct)).Folders);
        Assert.True(await DaemonFixture.WaitFor(() => File.Exists(Path.Combine(SyncRoot, "Photos/p.jpg")), 15));

        await _d.Client.SetSyncFoldersAsync(["Docs"], Ct);
        await WaitForSync(SyncState.Idle);
        Assert.True(File.Exists(Path.Combine(SyncRoot, "Photos/p.jpg")));
        Assert.Contains("p.jpg", await RemoteNames("Photos"));

        await Assert.ThrowsAsync<DaemonException>(() => _d.Client.SetSyncFoldersAsync(["../etc"], Ct));

        // how often Drive is checked: one of the offered choices, reported back in the status
        await _d.Client.SetSyncIntervalAsync(120, Ct);
        Assert.Equal(120, (await _d.Client.GetSyncSettingsAsync(Ct)).RemoteIntervalMinutes);
        var info = (await _d.Client.GetStatusAsync(Ct)).Sync!;
        Assert.Equal(120, info.IntervalMinutes);
        Assert.NotNull(info.NextCheck);
        await _d.Client.SetSyncIntervalAsync(0, Ct);
        info = (await _d.Client.GetStatusAsync(Ct)).Sync!;
        Assert.Equal(0, info.IntervalMinutes);
        Assert.Null(info.NextCheck); // only on Sync now
        await Assert.ThrowsAsync<DaemonException>(() => _d.Client.SetSyncIntervalAsync(5, Ct));
        Assert.Equal(0, (await _d.Client.GetSyncSettingsAsync(Ct)).RemoteIntervalMinutes);

        // a folder inside another listed one is dropped: the outer one already syncs it
        await _d.Client.SetSyncFoldersAsync(["Docs/sub", "Docs", "Photos/2026"], Ct);
        Assert.Equal(["Docs", "Photos/2026"], (await _d.Client.GetSyncSettingsAsync(Ct)).Folders);
    }

    [Fact]
    public async Task Signed_out_and_paused_show_in_the_sync_status()
    {
        Configure();
        _d.Fake.Set("FAKE_SIGNED_OUT", "1");
        await _d.StartAsync();
        await WaitForSync(SyncState.Login);
        _d.Fake.Set("FAKE_SIGNED_OUT", null);
        await _d.Client!.PauseAsync(Ct);
        await WaitForSync(SyncState.Paused);
        await _d.Client.ResumeAsync(Ct);
        await WaitForSync(SyncState.Idle);
    }

    [Fact]
    public async Task Sync_folder_location_can_be_set_until_folders_are_synced()
    {
        _d.WriteConfig(new SyncConfig { Sync = new TwoWaySettings { Local = SyncRoot } });
        await _d.StartAsync();
        var elsewhere = _d.Tmp["elsewhere"];
        await _d.Client!.SetSyncLocalAsync(elsewhere, Ct);
        Assert.True(Directory.Exists(elsewhere));
        Assert.Equal(elsewhere, (await _d.Client.GetSyncSettingsAsync(Ct)).Local);

        await _d.Client.SetSyncFoldersAsync(["Docs"], Ct);
        var e = await Assert.ThrowsAsync<DaemonException>(() => _d.Client.SetSyncLocalAsync(SyncRoot, Ct));
        Assert.Contains("Stop syncing", e.Message);
        Assert.Equal(elsewhere, (await _d.Client.GetSyncSettingsAsync(Ct)).Local);
    }

    [Fact]
    public async Task No_folders_means_sync_is_off()
    {
        _d.WriteConfig(new SyncConfig { Sync = new TwoWaySettings { Local = SyncRoot } });
        await _d.StartAsync();
        var s = await WaitForSync(SyncState.Off);
        Assert.False(Directory.Exists(SyncRoot));
        Assert.Contains("No Drive folders", s.Sync!.Message);
    }
}

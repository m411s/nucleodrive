using System.Diagnostics;
using Pdrive.Ipc;
using Pdrive.Sync;

namespace Pdrive.Daemon.Tests;

// Ports proton-drive-gui/test_daemon.py, plus IPC, watcher, sign-in and migration checks.
public sealed class DaemonTests : IAsyncLifetime
{
    private readonly DaemonFixture _d = new();
    private static CancellationToken Ct => DaemonFixture.Ct;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _d.DisposeAsync();

    [Fact]
    public async Task Broken_config_reports_error_and_daemon_keeps_running()
    {
        Directory.CreateDirectory(_d.Paths.ConfigDir);
        File.WriteAllText(_d.Paths.ConfigFile, "{bad");
        await _d.StartAsync();
        var s = await _d.WaitForStatus(s => s.State == DaemonState.Error);
        Assert.Contains("Can't read", s.Message);

        // Still serving requests; fixing the file and syncing recovers.
        _d.WriteConfig(new SyncConfig());
        await _d.Client!.SyncNowAsync(Ct);
        await _d.WaitForStatus(s => s.State == DaemonState.Idle && s.Message.StartsWith("No folders"));
    }

    [Fact]
    public async Task Adding_a_folder_over_ipc_refuses_to_overwrite_a_broken_config()
    {
        Directory.CreateDirectory(_d.Paths.ConfigDir);
        File.WriteAllText(_d.Paths.ConfigFile, "{bad");
        await _d.StartAsync();
        await Assert.ThrowsAsync<DaemonException>(() => _d.Client!.AddFolderAsync(_d.Src, "/my-files/x", Ct));
        Assert.Equal("{bad", File.ReadAllText(_d.Paths.ConfigFile));
    }

    [Fact]
    public async Task Second_instance_is_refused()
    {
        await _d.StartAsync();
        Assert.Null(DaemonLock.TryAcquire(_d.Paths.DaemonLock));

        // The real binary too: exits 1 with a message instead of fighting over the state.
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "pdrive-daemon"))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            Environment =
            {
                ["PDRIVE_CONFIG_DIR"] = _d.Paths.ConfigDir,
                ["PDRIVE_STATE_DIR"] = _d.Paths.StateDir,
                ["PDRIVE_RUNTIME_DIR"] = _d.Paths.RuntimeDir,
            },
        };
        using var p = Process.Start(psi)!;
        var stderr = await p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        Assert.Equal(1, p.ExitCode);
        Assert.Contains("already running", stderr);
    }

    [Fact]
    public async Task Sync_pause_and_resume_over_ipc()
    {
        _d.WriteConfig(_d.BackupSrc());
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Idle && s.Message == "Up to date (2 uploaded)");
        Assert.NotNull((await _d.Client!.GetStatusAsync(Ct)).LastSync);

        await _d.Client!.PauseAsync(Ct);
        Assert.Equal(DaemonState.Paused, (await _d.Client.GetStatusAsync(Ct)).State);
        _d.WriteFile("c.txt");
        await _d.Client.SyncNowAsync(Ct);
        await Task.Delay(500, Ct);
        Assert.DoesNotContain(_d.Fake.Uploads(), u => u.Any(x => x.EndsWith("c.txt")));

        await _d.Client.ResumeAsync(Ct);
        await _d.WaitForStatus(s => s.State == DaemonState.Idle && s.Message == "Up to date (1 uploaded)");
    }

    [Fact]
    public async Task Pause_survives_a_restart()
    {
        _d.WriteConfig(_d.BackupSrc());
        await _d.StartAsync();
        await _d.Client!.PauseAsync(Ct);
        await _d.StopAsync();
        _d.Fake.ClearLog();
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Paused);
        Assert.Empty(_d.Fake.Uploads());
    }

    [Fact]
    public async Task Status_changes_are_pushed_to_clients()
    {
        _d.WriteConfig(_d.BackupSrc(watch: false));
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Idle);
        var seen = new List<DaemonStatus>();
        _d.Client!.StatusChanged += s => { lock (seen) seen.Add(s); };
        _d.WriteFile("c.txt");
        await _d.Client.SyncNowAsync(Ct);
        Assert.True(await DaemonFixture.WaitFor(() => { lock (seen) return seen.Any(s => s.State == DaemonState.Idle); }));
        lock (seen)
        {
            Assert.Contains(seen, s => s.State == DaemonState.Syncing && s.Progress is { FilesTotal: 1 });
            Assert.Equal("Up to date (1 uploaded)", seen.Last(s => s.State == DaemonState.Idle).Message);
        }
    }

    [Fact]
    public async Task Monitor_reports_not_running_after_the_daemon_stops()
    {
        await _d.StartAsync();
        await using var monitor = new DaemonMonitor(_d.Paths.DaemonSocket, TimeSpan.FromMilliseconds(100));
        monitor.Start();
        Assert.True(await DaemonFixture.WaitFor(() => monitor.Status.State != DaemonState.Stopped && monitor.Client is not null));
        await _d.StopAsync();
        Assert.True(await DaemonFixture.WaitFor(() => monitor.Status.State == DaemonState.Stopped));
        Assert.Null(await DaemonClient.TryConnectAsync(_d.Paths.DaemonSocket, Ct));
        Assert.False(File.Exists(_d.Paths.DaemonSocket));
    }

    [Fact]
    public async Task Watched_change_uploads_without_waiting_for_the_interval()
    {
        _d.WriteConfig(_d.BackupSrc() with { IntervalMinutes = 60 });
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Idle && s.Message.Contains("uploaded"));
        _d.WriteFile("sub/live.txt", old: false);
        Assert.True(await DaemonFixture.WaitFor(() => _d.Fake.Uploads().Any(u => u.Any(x => x.EndsWith("live.txt")))),
            "watched file was not uploaded");
        Assert.Empty((await _d.Client!.GetStatusAsync(Ct)).Unwatched);
    }

    [Fact]
    public async Task Too_new_file_is_uploaded_once_old_enough()
    {
        _d.WriteConfig(_d.BackupSrc(minAge: 1, watch: false) with { IntervalMinutes = 60 });
        _d.WriteFile("fresh.txt", old: false);
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Idle && s.Message == "Up to date (2 uploaded)");
        Assert.True(await DaemonFixture.WaitFor(() => _d.Fake.Uploads().Any(u => u.Any(x => x.EndsWith("fresh.txt"))), 6));
    }

    [Fact]
    public async Task Signed_out_shows_login_state()
    {
        _d.WriteConfig(_d.BackupSrc());
        _d.Fake.Set("FAKE_SIGNED_OUT", "1");
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Login);
        _d.Fake.Set("FAKE_SIGNED_OUT", null);
        await _d.Client!.SyncNowAsync(Ct);
        await _d.WaitForStatus(s => s.State == DaemonState.Idle);
    }

    [Fact]
    public async Task Offline_is_an_error_not_a_sign_in_prompt()
    {
        _d.WriteConfig(_d.BackupSrc());
        _d.Fake.Set("FAKE_OFFLINE", "1");
        await _d.StartAsync();
        var s = await _d.WaitForStatus(s => s.State == DaemonState.Error);
        Assert.Contains("network", s.Message);
    }

    [Fact]
    public async Task Folder_rules_are_enforced_over_ipc()
    {
        await _d.StartAsync();
        var c = _d.Client!;
        Assert.Contains("Not a folder", (await Assert.ThrowsAsync<DaemonException>(() => c.AddFolderAsync(_d.Tmp["nope"], "/my-files/x", Ct))).Message);
        Assert.Contains("/my-files", (await Assert.ThrowsAsync<DaemonException>(() => c.AddFolderAsync(_d.Src, "/elsewhere", Ct))).Message);
        await c.AddFolderAsync(_d.Src, "/my-files/Backup/", Ct);
        Assert.Contains("already", (await Assert.ThrowsAsync<DaemonException>(() => c.AddFolderAsync(_d.Src, "/my-files/Backup", Ct))).Message);
        Assert.Contains("overlaps", (await Assert.ThrowsAsync<DaemonException>(() => c.AddFolderAsync(Path.Combine(_d.Src, "sub"), "/my-files/Sub", Ct))).Message);
        Assert.Equal([new FolderInfo(Path.GetFullPath(_d.Src), "/my-files/Backup")], await c.ListFoldersAsync(Ct));
        await _d.WaitForStatus(s => s.Message == "Up to date (2 uploaded)");
        await c.RemoveFolderAsync(_d.Src, Ct);
        Assert.Empty(await c.ListFoldersAsync(Ct));
    }

    [Fact]
    public async Task Python_config_and_state_are_imported_without_reuploading()
    {
        // What the Python prototype leaves behind: its config and a state.json in nanoseconds.
        var src = Path.GetFullPath(_d.Src);
        string Sig(string rel)
        {
            var f = new FileInfo(Path.Combine(src, rel));
            return $"[{(f.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100 + 99}, {f.Length}]";
        }
        Directory.CreateDirectory(_d.Paths.LegacyConfigDir);
        Directory.CreateDirectory(_d.Paths.LegacyStateDir);
        File.WriteAllText(_d.Paths.LegacyConfigFile, $$"""
            {"interval_minutes": 15, "min_age_seconds": 0, "exclude": [".*"], "folders": [{"local": "{{src}}", "remote": "/my-files/Backup"}]}
            """);
        File.WriteAllText(_d.Paths.LegacyStateFile, $$"""
            {"{{src}}::a.txt": {{Sig("a.txt")}}, "{{src}}::sub/b.txt": {{Sig("sub/b.txt")}}}
            """);
        await _d.StartAsync();
        await _d.WaitForStatus(s => s.State == DaemonState.Idle && s.Message == "Up to date");
        Assert.Empty(_d.Fake.Uploads());
        Assert.True(File.Exists(_d.Paths.ConfigFile));
    }
}

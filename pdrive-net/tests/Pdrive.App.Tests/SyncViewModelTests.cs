using Pdrive.Cli;
using Pdrive.App.Services;
using Pdrive.App.ViewModels;
using Pdrive.Ipc;

namespace Pdrive.App.Tests;

public sealed class SyncViewModelTests
{
    private readonly FakeDaemon _daemon = new();
    private readonly FakeDrive _drive = new();
    private readonly FakeToaster _toast = new();
    private readonly List<string> _opened = [];
    private readonly SyncViewModel _vm;

    public SyncViewModelTests() => _vm = new SyncViewModel(_daemon, _drive, _toast, a => a(), _opened.Add);

    [Fact]
    public async Task Lists_drive_folders_as_a_tree_with_synced_ones_switched_on_and_applies_changes()
    {
        _drive.Tree["/my-files"].Add(new("Documents", true, null, "/my-files/Documents"));
        _drive.Tree["/my-files/Documents"] = [new("Work", true, null, "/my-files/Documents/Work"), new("Old", true, null, "/my-files/Documents/Old")];
        _daemon.SyncFolders = ["Docs", "Documents/Work"];
        _daemon.Publish(FakeDaemon.WithSync(SyncState.Idle, "Up to date"));
        await _vm.LoadChoicesAsync();

        Assert.Equal(["Docs", "Documents"], _vm.Choices.Select(c => c.Path));
        var docs = _vm.Choices[0];
        var documents = _vm.Choices[1];
        Assert.True(docs.IsChecked);
        Assert.False(documents.IsChecked);
        Assert.True(documents.IsExpanded); // opened to show the synced subfolder
        Assert.Equal(["Old", "Work"], documents.Folders.Select(f => f.Name));
        Assert.True(documents.Folders.Single(f => f.Name == "Work").IsOn);
        Assert.Equal("1 subfolder syncs", documents.PartialText);
        Assert.False(_vm.ChoicesDirty);
        Assert.Equal(["Docs", "Documents/Work"], _vm.SelectedFolders());

        // subfolders are listed when a folder is opened; inside a synced folder they show on and locked
        Assert.True(docs.Children.Single().IsLoadingRow);
        await docs.EnsureLoadedAsync();
        var inner = Assert.Single(docs.Folders);
        Assert.Equal("Docs/Inner", inner.Path);
        Assert.True(inner.IsOn);
        Assert.False(inner.CanToggle);

        documents.Folders.Single(f => f.Name == "Work").IsChecked = false;
        _vm.NewPath = "/my-files/Photos/2026/";
        await _vm.AddPathCommand.ExecuteAsync(null);
        Assert.True(_vm.ChoicesDirty);
        Assert.Null(documents.PartialText);
        await _vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("sync-folders Docs,Photos/2026", _daemon.Calls);
        Assert.False(_vm.ChoicesDirty);
    }

    [Fact]
    public async Task Switching_a_parent_on_includes_its_subfolders()
    {
        _drive.Tree["/my-files"].Add(new("Documents", true, null, "/my-files/Documents"));
        _drive.Tree["/my-files/Documents"] = [new("Work", true, null, "/my-files/Documents/Work")];
        _daemon.SyncFolders = ["Documents/Work"];
        _daemon.Publish(FakeDaemon.WithSync(SyncState.Idle, "Up to date"));
        await _vm.LoadChoicesAsync();
        var documents = _vm.Choices.Single(c => c.Path == "Documents");
        var work = documents.Folders.Single();

        documents.IsOn = true;
        Assert.True(work.IsOn);
        Assert.False(work.CanToggle);
        Assert.Null(documents.PartialText);
        Assert.Equal(["Documents"], _vm.SelectedFolders()); // never both a folder and one inside it

        work.IsOn = false; // locked while the parent syncs
        Assert.True(work.IsChecked);
        documents.IsOn = false;
        Assert.True(work.CanToggle);
        Assert.Equal(["Documents/Work"], _vm.SelectedFolders());
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_can_be_opened_again()
    {
        _daemon.Publish(FakeDaemon.WithSync(SyncState.Idle, "Up to date"));
        await _vm.LoadChoicesAsync();
        var docs = _vm.Choices.Single();
        _drive.ListError = new DriveException("network down");
        await Assert.ThrowsAsync<DriveException>(docs.EnsureLoadedAsync);
        Assert.True(docs.Children.Single().IsLoadingRow);
        Assert.Contains(_toast.Shown, t => t.Message.Contains("network down", StringComparison.Ordinal));

        _drive.ListError = null;
        await docs.EnsureLoadedAsync();
        Assert.Equal(["Docs/Inner"], docs.Folders.Select(f => f.Path));
    }

    [Fact]
    public void Status_and_confirmation()
    {
        Assert.Equal("Not running", _vm.StateText);
        Assert.False(_vm.SyncNowCommand.CanExecute(null));

        _daemon.Publish(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "60 deletions are waiting", held: 60));
        Assert.Equal("Waiting for you", _vm.StateText);
        Assert.True(_vm.NeedsConfirmation);
        Assert.True(_vm.ConfirmDeletionsCommand.CanExecute(null));
        _vm.OpenFolderCommand.Execute(null);
        Assert.Equal(["/home/me/proton-drive"], _opened);
    }

    [Fact]
    public async Task Confirm_and_sync_now_reach_the_daemon()
    {
        _daemon.Publish(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "held", held: 3));
        await _vm.ConfirmDeletionsCommand.ExecuteAsync(null);
        await _vm.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(["confirm", "sync"], _daemon.Calls);
    }
}

public sealed class TrayStateTests
{
    [Fact]
    public void The_more_urgent_state_wins()
    {
        Assert.Equal(DaemonState.Stopped, TrayState.Combine(DaemonStatus.NotRunning).Icon);
        // backups only (sync off)
        Assert.Equal((DaemonState.Idle, "No folders configured"),
            Pick(TrayState.Combine(FakeDaemon.With(DaemonState.Idle, "No folders configured") with
                { Sync = new SyncInfo(SyncState.Off, "off", "/x", [], null, 0, []) })));
        // sync syncing beats backup idle, and its message is shown
        Assert.Equal((DaemonState.Syncing, "Uploading"), Pick(TrayState.Combine(FakeDaemon.WithSync(SyncState.Syncing, "Uploading"))));
        // waiting for confirmation shows as attention (amber)
        Assert.Equal(DaemonState.Login, TrayState.Combine(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "held")).Icon);
        // a backup error beats an idle sync
        var backupError = FakeDaemon.With(DaemonState.Error, "backup broke") with
            { Sync = new SyncInfo(SyncState.Idle, "Up to date", "/x", ["Docs"], null, 0, []) };
        Assert.Equal((DaemonState.Error, "backup broke"), Pick(TrayState.Combine(backupError)));
    }

    [Fact]
    public void Tray_menu_follows_the_combined_state()
    {
        var stopped = TrayState.Menu(DaemonStatus.NotRunning);
        Assert.False(stopped.SyncNowEnabled);
        Assert.False(stopped.PauseEnabled);
        Assert.Null(stopped.ReviewDeletions);

        Assert.True(TrayState.Menu(FakeDaemon.WithSync(SyncState.Idle, "Up to date")).SyncNowEnabled);

        // paused on the sync side only (backup idle) used to leave "Sync now" on
        var syncPaused = TrayState.Menu(FakeDaemon.WithSync(SyncState.Paused, "Paused"));
        Assert.False(syncPaused.SyncNowEnabled);
        Assert.Equal("Resume", syncPaused.PauseLabel);
        var backupPaused = TrayState.Menu(FakeDaemon.With(DaemonState.Paused, "Paused"));
        Assert.False(backupPaused.SyncNowEnabled);
        Assert.Equal("Resume", backupPaused.PauseLabel);

        Assert.False(TrayState.Menu(FakeDaemon.WithSync(SyncState.Login, "Sign in")).SyncNowEnabled);

        var held = TrayState.Menu(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "62 deletions wait", held: 62));
        Assert.Equal("Review 62 deletions…", held.ReviewDeletions);
        Assert.True(held.SyncNowEnabled);
        Assert.Equal("Pause", held.PauseLabel);
        Assert.Equal("Review deletions…", TrayState.Menu(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "held")).ReviewDeletions);
    }

    private static (DaemonState, string) Pick((DaemonState Icon, string Message, DateTimeOffset? Last) t) => (t.Icon, t.Message);
}

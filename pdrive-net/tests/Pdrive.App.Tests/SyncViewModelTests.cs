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
    public async Task Lists_drive_folders_with_synced_ones_checked_and_applies_changes()
    {
        _daemon.SyncFolders = ["Docs", "Documents/Work"];
        _daemon.Publish(FakeDaemon.WithSync(SyncState.Idle, "Up to date"));
        await _vm.LoadChoicesAsync();
        Assert.Equal(["Docs", "Documents/Work"], _vm.Choices.Select(c => c.Path)); // "Docs" is on Drive, the nested one is synced
        Assert.All(_vm.Choices, c => Assert.True(c.IsChecked));
        Assert.False(_vm.ChoicesDirty);

        _vm.Choices[1].IsChecked = false;
        _vm.NewPath = "/my-files/Photos/2026/";
        _vm.AddPathCommand.Execute(null);
        Assert.True(_vm.ChoicesDirty);
        await _vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("sync-folders Docs,Photos/2026", _daemon.Calls);
        Assert.False(_vm.ChoicesDirty);
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

    private static (DaemonState, string) Pick((DaemonState Icon, string Message, DateTimeOffset? Last) t) => (t.Icon, t.Message);
}

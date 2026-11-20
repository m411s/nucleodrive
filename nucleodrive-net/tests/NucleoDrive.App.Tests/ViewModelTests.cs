using NucleoDrive.App.ViewModels;
using NucleoDrive.Cli;
using NucleoDrive.Ipc;

namespace NucleoDrive.App.Tests;

public sealed class BrowserViewModelTests
{
    private readonly FakeDrive _drive = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToaster _toast = new();
    private readonly BrowserViewModel _vm;

    public BrowserViewModelTests() => _vm = new BrowserViewModel(_drive, null, _dialogs, _toast);

    private string[] Names => _vm.Entries.Select(e => e.Name).ToArray();

    [Fact]
    public async Task Refresh_lists_and_navigation_keeps_history()
    {
        await _vm.RefreshAsync();
        Assert.Equal(["Docs", "a.txt"], Names);
        Assert.Equal("2.0 KB", _vm.Entries[1].SizeText);
        Assert.False(_vm.BackCommand.CanExecute(null));
        Assert.False(_vm.UpCommand.CanExecute(null));

        await _vm.OpenAsync(_vm.Entries[0]);
        await _vm.OpenAsync(_vm.Entries[0]);
        Assert.Equal("/my-files/Docs/Inner", _vm.CurrentPath);
        Assert.Equal("This folder is empty", _vm.Placeholder);

        await _vm.UpCommand.ExecuteAsync(null);
        Assert.Equal("/my-files/Docs", _vm.CurrentPath);
        await _vm.BackCommand.ExecuteAsync(null);
        Assert.Equal("/my-files/Docs/Inner", _vm.CurrentPath);
        Assert.True(_vm.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_a_file_does_nothing()
    {
        await _vm.RefreshAsync();
        await _vm.OpenAsync(_vm.Entries[1]);
        Assert.Equal("/my-files", _vm.CurrentPath);
    }

    [Fact]
    public async Task Stale_listing_is_dropped()
    {
        await _vm.RefreshAsync();
        var hold = _drive.Hold("/my-files/Docs");
        var slow = _vm.NavigateAsync("/my-files/Docs");
        await _vm.BackCommand.ExecuteAsync(null); // back to root before Docs answers
        hold.SetResult();
        await slow;
        Assert.Equal("/my-files", _vm.CurrentPath);
        Assert.Equal(["Docs", "a.txt"], Names);
    }

    [Fact]
    public async Task Auth_error_shows_sign_in_and_sign_in_recovers()
    {
        _drive.ListError = new AuthException("not logged in");
        await _vm.RefreshAsync();
        Assert.True(_vm.ShowSignIn);
        Assert.Empty(_vm.Entries);
        await _vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(1, _drive.Logins);
        Assert.False(_vm.ShowSignIn);
        Assert.Equal(2, _vm.Entries.Count);
    }

    [Fact]
    public async Task Other_errors_are_toasted_not_sign_in()
    {
        _drive.ListError = new DriveException("network error: connection refused\nmore detail");
        await _vm.RefreshAsync();
        Assert.False(_vm.ShowSignIn);
        Assert.Equal("network error: connection refused", _vm.Placeholder);
        Assert.Contains(("network error: connection refused", true), _toast.Shown);
    }

    [Fact]
    public async Task Busy_stays_on_until_every_operation_finishes()
    {
        _drive.UploadGate = new TaskCompletionSource();
        var first = _vm.UploadPathsAsync(["/tmp/one"]);
        var second = _vm.UploadPathsAsync(["/tmp/two"]);
        Assert.True(_vm.IsBusy);
        Assert.Equal(2, _vm.BusyCount);
        _drive.UploadGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.False(_vm.IsBusy, $"count={_vm.BusyCount} first={first.Status} second={second.Status} uploads={_drive.Uploads.Count} toasts={string.Join(" | ", _toast.Shown)}");
    }

    [Fact]
    public async Task Upload_goes_to_the_current_folder_and_refreshes()
    {
        await _vm.NavigateAsync("/my-files/Docs");
        _dialogs.Files = ["/tmp/x", "/tmp/y"];
        await _vm.UploadCommand.ExecuteAsync(null);
        var (paths, dest) = Assert.Single(_drive.Uploads);
        Assert.Equal(["/tmp/x", "/tmp/y"], paths);
        Assert.Equal("/my-files/Docs", dest);
        Assert.Contains(_toast.Shown, t => t.Message == "Upload finished");
    }

    [Fact]
    public async Task Download_passes_size_and_respects_cancelled_picker()
    {
        await _vm.RefreshAsync();
        await _vm.DownloadCommand.ExecuteAsync(_vm.Entries[1]);
        Assert.Empty(_drive.Downloads); // picker cancelled
        _dialogs.Folder = "/home/me/Downloads";
        await _vm.DownloadCommand.ExecuteAsync(_vm.Entries[1]);
        Assert.Equal(("/my-files/a.txt", "/home/me/Downloads", 2048L), Assert.Single(_drive.Downloads));
    }

    [Fact]
    public async Task Proton_documents_cannot_be_downloaded()
    {
        _drive.Tree["/my-files"].Add(new DriveEntry("Notes", false, null, "/my-files/Notes", "application/vnd.proton.doc"));
        await _vm.RefreshAsync();
        var doc = _vm.Entries.Single(e => e.Name == "Notes");
        Assert.False(doc.CanDownload);
        _dialogs.Folder = "/tmp";
        await _vm.DownloadCommand.ExecuteAsync(doc);
        Assert.Empty(_drive.Downloads);
    }

    [Fact]
    public async Task Missing_cli_shows_its_error()
    {
        var vm = new BrowserViewModel(null, "proton-drive CLI not found. Download it…", _dialogs, _toast);
        await vm.RefreshAsync();
        Assert.False(vm.HasClient);
        Assert.StartsWith("proton-drive CLI not found", vm.Placeholder);
    }
}

public sealed class BackupViewModelTests
{
    private readonly FakeDaemon _daemon = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToaster _toast = new();
    private readonly BackupViewModel _vm;

    public BackupViewModelTests() => _vm = new BackupViewModel(_daemon, _dialogs, _toast, a => a());

    [Fact]
    public async Task Not_running_disables_actions_and_running_loads_folders()
    {
        Assert.Equal("Not running", _vm.StateText);
        Assert.False(_vm.SyncNowCommand.CanExecute(null));
        Assert.NotNull(_vm.NotRunningHint);

        _daemon.Publish(FakeDaemon.With(DaemonState.Idle, "Up to date"));
        await Task.Yield();
        Assert.Equal("Up to date", _vm.StateText);
        Assert.True(_vm.SyncNowCommand.CanExecute(null));
        Assert.Equal("/home/me/Docs", Assert.Single(_vm.Folders).Local);

        _daemon.Publish(DaemonStatus.NotRunning);
        Assert.Empty(_vm.Folders);
        Assert.False(_vm.AddFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pause_toggles_between_pause_and_resume()
    {
        _daemon.Publish(FakeDaemon.With(DaemonState.Idle));
        Assert.Equal("Pause", _vm.PauseLabel);
        await _vm.TogglePauseCommand.ExecuteAsync(null);
        _daemon.Publish(FakeDaemon.With(DaemonState.Paused, "Paused"));
        Assert.Equal("Resume", _vm.PauseLabel);
        await _vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal(["pause", "resume"], _daemon.Calls);
    }

    [Fact]
    public async Task Add_folder_suggests_a_drive_path_and_reports_refusals()
    {
        _daemon.Publish(FakeDaemon.With(DaemonState.Idle));
        _dialogs.Folder = "/home/me/Pictures/";
        await _vm.AddFolderCommand.ExecuteAsync(null);
        Assert.Contains("add /home/me/Pictures/ /my-files/Pictures", _daemon.Calls);

        _daemon.Fail = new DaemonException("/home/me/Pictures is already backed up");
        await _vm.AddFolderCommand.ExecuteAsync(null);
        Assert.Contains(("/home/me/Pictures is already backed up", true), _toast.Shown);
    }

    [Fact]
    public void Progress_percent_uses_bytes()
    {
        _daemon.Publish(FakeDaemon.With(DaemonState.Syncing, "Uploading", new TransferProgress(1, 4, 250, 1000, "/my-files/x")));
        Assert.True(_vm.ShowProgress);
        Assert.Equal(25, _vm.ProgressPercent);
    }
}

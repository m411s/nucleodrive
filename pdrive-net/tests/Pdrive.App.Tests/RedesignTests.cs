using Pdrive.App.ViewModels;
using Pdrive.Cli;
using Pdrive.Ipc;

namespace Pdrive.App.Tests;

public sealed class BrowserNavigationTests
{
    private readonly FakeDrive _drive = new();
    private readonly BrowserViewModel _vm;

    public BrowserNavigationTests() => _vm = new BrowserViewModel(_drive, null, new FakeDialogs(), new FakeToaster());

    [Fact]
    public async Task Breadcrumbs_follow_the_path_and_navigate()
    {
        _drive.Tree["/my-files/Docs/a\\/b"] = [];
        await _vm.NavigateAsync("/my-files/Docs/a\\/b");
        Assert.Equal(["My files", "Docs", "a/b"], _vm.Breadcrumbs.Select(c => c.Name));
        Assert.Equal(["/my-files", "/my-files/Docs", "/my-files/Docs/a\\/b"], _vm.Breadcrumbs.Select(c => c.Path));
        Assert.Equal([false, false, true], _vm.Breadcrumbs.Select(c => c.IsLast));
        Assert.Equal("a/b", _vm.FolderTitle);

        await _vm.GoToCommand.ExecuteAsync(_vm.Breadcrumbs[1]);
        Assert.Equal("/my-files/Docs", _vm.CurrentPath);
        Assert.Equal(["Inner"], _vm.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task Visited_folders_show_instantly_while_they_refresh()
    {
        await _vm.RefreshAsync();
        await _vm.OpenAsync(_vm.Entries[0]); // Docs
        _drive.Tree["/my-files"] = [.. _drive.Tree["/my-files"], new DriveEntry("new.txt", false, 1, "/my-files/new.txt")];
        var hold = _drive.Hold("/my-files");

        var back = _vm.NavigateAsync("/my-files");
        Assert.Equal(["Docs", "a.txt"], _vm.Entries.Select(e => e.Name)); // cached, before the CLI answers
        Assert.True(_vm.IsBusy);

        hold.SetResult();
        await back;
        Assert.Equal(["Docs", "a.txt", "new.txt"], _vm.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Dates_are_short_and_relative()
    {
        var now = new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
        Assert.StartsWith("Today ", EntryViewModel.FormatDate(now.AddHours(-2), now));
        Assert.Equal("Yesterday", EntryViewModel.FormatDate(now.AddDays(-1), now));
        Assert.DoesNotContain("2026", EntryViewModel.FormatDate(now.AddDays(-30), now));
        Assert.Contains("2024", EntryViewModel.FormatDate(now.AddYears(-2), now));
        Assert.Equal("", new EntryViewModel(new DriveEntry("x", false, 1, "/my-files/x")).ModifiedText);
        Assert.Equal("", new EntryViewModel(new DriveEntry("d", true, null, "/my-files/d")).SizeText);
    }
}

public sealed class SyncAutoApplyTests
{
    [Fact]
    public async Task Toggles_apply_by_themselves_once_you_stop_clicking()
    {
        var daemon = new FakeDaemon();
        var vm = new SyncViewModel(daemon, new FakeDrive(), new FakeToaster(), a => a(), _ => { }, TimeSpan.FromMilliseconds(80));
        daemon.Publish(FakeDaemon.WithSync(SyncState.Idle, "Up to date"));
        await vm.LoadChoicesAsync();
        Assert.False(vm.ShowApply);

        vm.Choices[0].IsChecked = true;
        vm.Choices[0].IsChecked = false;
        vm.Choices[0].IsChecked = true; // three clicks, one apply
        for (var i = 0; i < 50 && vm.ChoicesDirty; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.False(vm.ChoicesDirty);
        Assert.Equal(["sync-folders Docs"], daemon.Calls.Where(c => c.StartsWith("sync-folders", StringComparison.Ordinal)));
    }
}

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Status_combines_sync_and_backup_and_pages_switch()
    {
        var daemon = new FakeDaemon();
        var toast = new FakeToaster();
        var vm = new MainWindowViewModel(
            new BrowserViewModel(new FakeDrive(), null, new FakeDialogs(), toast),
            new BackupViewModel(daemon, new FakeDialogs(), toast, a => a()),
            new SyncViewModel(daemon, new FakeDrive(), toast, a => a(), _ => { }));
        Assert.True(vm.StatusOff);
        Assert.Same(vm.Browser, vm.CurrentPage);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        daemon.Publish(FakeDaemon.WithSync(SyncState.Syncing, "Downloading a.txt"));
        Assert.True(vm.StatusBusy);
        Assert.Equal("Syncing", vm.StatusTitle);
        Assert.Equal("Downloading a.txt", vm.StatusDetail);
        Assert.Contains(nameof(vm.StatusTitle), changed);

        daemon.Publish(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "62 deletions wait", held: 62));
        Assert.True(vm.StatusWarn);

        vm.PageIndex = 1;
        Assert.Same(vm.Sync, vm.CurrentPage);
    }
}

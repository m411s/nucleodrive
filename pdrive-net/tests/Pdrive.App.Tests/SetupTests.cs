using Pdrive.App.Services;
using Pdrive.App.ViewModels;
using Pdrive.Cli;
using Pdrive.Ipc;
using Pdrive.Sync;
using Pdrive.Tests;

namespace Pdrive.App.Tests;

public sealed class SetupViewModelTests : IDisposable
{
    private sealed class FakeCliSetup : ICliSetup
    {
        public string? Installed { get; set; }
        public string? FindInstalled() => Installed;
        public Task<string?> VersionOfAsync(string path) => Task.FromResult<string?>("0.9.0");
        public Task<CliRelease> GetLatestAsync() =>
            Task.FromResult(new CliRelease("0.9.0", "linux-x64", new Uri("https://proton.me/download/drive/cli/0.9.0/linux-x64/proton-drive"), new string('a', 128)));
        public Task<string> InstallAsync(CliRelease r, Func<string, Task<bool>> confirm, IProgress<string>? p)
        {
            Installed = "/x/proton-drive";
            return Task.FromResult(Installed);
        }
    }

    private sealed class FakeSyncSetup : ISyncSetup
    {
        public SyncSettingsInfo Current { get; set; } = new("/home/me/proton-drive", [], 15);
        public (string Local, string[] Folders)? Applied { get; private set; }
        public Exception? Refuse { get; set; }
        public Task<SyncSettingsInfo> CurrentAsync() => Task.FromResult(Current);
        public Task ApplyAsync(string local, string[] folders)
        {
            if (Refuse is { } e)
                throw e;
            Applied = (local, folders);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartup : IStartupSettings
    {
        public bool TrayAtLogin { get; private set; }
        public bool? Service { get; set; } = false;
        public void SetTrayAtLogin(bool enabled) => TrayAtLogin = enabled;
        public Task<bool?> IsServiceEnabledAsync() => Task.FromResult(Service);
        public Task SetServiceEnabledAsync(bool enabled)
        {
            Service = enabled;
            return Task.CompletedTask;
        }
    }

    private readonly TempDir _tmp = new();
    private readonly FakeCliSetup _cli = new();
    private readonly FakeSyncSetup _sync = new();
    private readonly FakeStartup _startup = new();
    private readonly FakeDaemon _daemon = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeDrive _drive = new();
    private readonly SetupState _state;
    private SetupViewModel _vm = null!;

    public SetupViewModelTests() => _state = new SetupState(_tmp.Path);

    public void Dispose() => _tmp.Dispose();

    private async Task<SetupViewModel> Start()
    {
        var provider = new DriveClientProvider(() => _cli.Installed is null ? throw new DriveException("not found") : _drive);
        _vm = new SetupViewModel(_cli, provider, _sync, _startup, _state, _daemon, _dialogs);
        await _vm.InitializeAsync();
        return _vm;
    }

    [Fact]
    public async Task Full_first_run_installs_signs_in_picks_folders_and_startup()
    {
        _drive.ListError = new AuthException("You need to login first");
        var vm = await Start();
        var completed = false;
        vm.Completed += () => completed = true;

        Assert.Equal(SetupStep.Cli, vm.Step);
        Assert.Equal("Step 1 of 4", vm.StepText);
        Assert.False(vm.NextCommand.CanExecute(null));
        await vm.InstallCliCommand.ExecuteAsync(null);
        Assert.True(vm.NextCommand.CanExecute(null));
        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupStep.SignIn, vm.Step);
        Assert.False(vm.SignedIn);
        Assert.False(vm.NextCommand.CanExecute(null));
        await vm.SignInCommand.ExecuteAsync(null); // FakeDrive.LoginAsync clears the auth error
        Assert.True(vm.SignedIn);
        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupStep.Folder, vm.Step);
        Assert.Equal("/home/me/proton-drive", vm.LocalPath);
        Assert.Equal(["Docs"], vm.DriveFolders.Select(f => f.Path)); // top-level Drive folders
        _dialogs.Folder = "/home/me/Cloud";
        await vm.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal("/home/me/Cloud", vm.LocalPath);
        vm.DriveFolders[0].IsChecked = true;
        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupStep.Startup, vm.Step);
        Assert.Equal("Finish", vm.NextLabel);
        Assert.True(vm.TrayAtLogin);
        Assert.True(vm.KeepSyncing);
        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(completed);
        Assert.Equal("/home/me/Cloud", _sync.Applied!.Value.Local);
        Assert.Equal(["Docs"], _sync.Applied.Value.Folders);
        Assert.True(_startup.TrayAtLogin);
        Assert.True(_startup.Service);
        Assert.True(_state.IsComplete);
        Assert.Contains("sync", _daemon.Calls);
    }

    [Fact]
    public async Task Skips_the_cli_step_when_installed_and_sign_in_can_be_skipped()
    {
        _cli.Installed = "/usr/bin/proton-drive";
        _drive.ListError = new AuthException("You need to login first");
        var vm = await Start();
        Assert.Equal(SetupStep.SignIn, vm.Step);
        Assert.Equal("Step 1 of 3", vm.StepText);
        await vm.SkipSignInCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Folder, vm.Step);
        Assert.Empty(vm.DriveFolders); // not signed in: chosen later in the Sync page
        await vm.BackCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.SignIn, vm.Step);
    }

    [Fact]
    public async Task Existing_synced_folders_lock_the_location_and_stay_selected()
    {
        _cli.Installed = "/usr/bin/proton-drive";
        _sync.Current = new SyncSettingsInfo("/home/me/proton-drive", ["Docs"], 15);
        var vm = await Start();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.FolderLocked);
        Assert.NotNull(vm.FolderNote);
        Assert.True(Assert.Single(vm.DriveFolders).IsChecked);
        _dialogs.Folder = "/elsewhere";
        await vm.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal("/home/me/proton-drive", vm.LocalPath);
    }

    [Fact]
    public async Task A_refused_folder_shows_the_reason_and_doesnt_finish()
    {
        _cli.Installed = "/usr/bin/proton-drive";
        _sync.Refuse = new ConfigException("Pick a dedicated folder (like ~/proton-drive), not / or your whole home folder.");
        var vm = await Start();
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.StartsWith("Pick a dedicated folder", vm.Error);
        Assert.False(_state.IsComplete);
    }

    [Fact]
    public void Wizard_runs_only_until_completed_and_only_when_nothing_is_synced()
    {
        Assert.True(_state.ShouldRun(new SyncSettingsInfo("/x", [], 15)));
        Assert.False(_state.ShouldRun(new SyncSettingsInfo("/x", ["Docs"], 15)));
        _state.MarkComplete();
        Assert.False(_state.ShouldRun(new SyncSettingsInfo("/x", [], 15)));
    }
}

public sealed class SyncRootRulesTests
{
    [Fact]
    public void Sync_folder_location_rules()
    {
        using var tmp = new TempDir();
        var cfg = new SyncConfig();
        Assert.Equal(tmp["Cloud"], SyncRootRules.SetLocal(cfg, tmp["Cloud"] + "/").Sync.Local);
        Assert.Throws<ConfigException>(() => SyncRootRules.SetLocal(cfg, "/"));
        Assert.Throws<ConfigException>(() => SyncRootRules.SetLocal(cfg, "~"));
        File.WriteAllText(tmp["file"], "x");
        Assert.Throws<ConfigException>(() => SyncRootRules.SetLocal(cfg, tmp["file"]));

        var syncing = cfg with { Sync = new TwoWaySettings { Local = tmp["A"], Folders = ["Docs"] } };
        Assert.Equal(tmp["A"], SyncRootRules.SetLocal(syncing, tmp["A"]).Sync.Local); // unchanged: fine
        Assert.Contains("Stop syncing", Assert.Throws<ConfigException>(() => SyncRootRules.SetLocal(syncing, tmp["B"])).Message);
    }
}

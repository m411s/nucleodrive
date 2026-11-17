using Pdrive.App.Services;
using Pdrive.App.ViewModels;
using Pdrive.Cli;
using Pdrive.Tests;

namespace Pdrive.App.Tests;

public sealed class StartupSettingsTests
{
    [Fact]
    public void Start_at_login_toggles_the_xdg_autostart_entry()
    {
        using var tmp = new TempDir();
        var s = new StartupSettings(tmp.Path, "/opt/pdrive/pdrive");
        Assert.False(s.TrayAtLogin);
        s.SetTrayAtLogin(true);
        Assert.True(s.TrayAtLogin);
        var text = File.ReadAllText(s.AutostartFile);
        Assert.Contains("Exec=\"/opt/pdrive/pdrive\" --tray", text);
        Assert.DoesNotContain("Hidden=true", text);
        s.SetTrayAtLogin(false);
        Assert.False(s.TrayAtLogin);
        Assert.Contains("\nHidden=true", File.ReadAllText(s.AutostartFile)); // what install.sh preserves
    }
}

public sealed class SettingsViewModelTests
{
    private sealed class FakeCliSetup : ICliSetup
    {
        public string? Installed { get; set; }
        public string? Version { get; set; } = "0.9.0";
        public string Latest { get; set; } = "0.9.0";
        public int Installs { get; private set; }
        public Func<string, Task<bool>>? LastConfirm { get; private set; }

        public string? FindInstalled() => Installed;
        public Task<string?> VersionOfAsync(string path) => Task.FromResult(Version);
        public Task<CliRelease> GetLatestAsync() =>
            Task.FromResult(new CliRelease(Latest, "linux-x64", new Uri($"https://proton.me/download/drive/cli/{Latest}/linux-x64/proton-drive"), new string('a', 128)));

        public Task<string> InstallAsync(CliRelease release, Func<string, Task<bool>> confirmReplace, IProgress<string>? progress)
        {
            Installs++;
            LastConfirm = confirmReplace;
            Installed = "/home/me/.local/share/pdrive/cli/proton-drive";
            Version = release.Version;
            return Task.FromResult(Installed);
        }
    }

    private sealed class FakeStartup : IStartupSettings
    {
        public bool TrayAtLogin { get; private set; } = true;
        public bool? Service { get; set; } = true;
        public void SetTrayAtLogin(bool enabled) => TrayAtLogin = enabled;
        public Task<bool?> IsServiceEnabledAsync() => Task.FromResult(Service);
        public Task SetServiceEnabledAsync(bool enabled)
        {
            Service = enabled;
            return Task.CompletedTask;
        }
    }

    private readonly FakeCliSetup _cli = new();
    private readonly FakeStartup _startup = new();
    private readonly FakeDaemon _daemon = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToaster _toast = new();
    private readonly FakeDrive _drive = new();
    private bool _haveClient;

    private SettingsViewModel Vm(out DriveClientProvider provider)
    {
        provider = new DriveClientProvider(() => _haveClient ? _drive : throw new DriveException("proton-drive CLI not found."));
        return new SettingsViewModel(_cli, provider, _startup, _daemon, _dialogs, _toast);
    }

    [Fact]
    public async Task Missing_cli_can_be_installed_and_the_app_picks_it_up()
    {
        var vm = Vm(out var provider);
        var changed = 0;
        provider.Changed += () => changed++;
        await vm.LoadAsync();
        Assert.False(vm.HasCli);
        Assert.Equal("Install the CLI", vm.InstallLabel);
        Assert.Null(vm.SignedIn);

        _haveClient = true; // the installer puts it where the provider looks
        await vm.InstallCliCommand.ExecuteAsync(null);
        Assert.Equal(1, _cli.Installs);
        Assert.Equal(1, changed);
        Assert.True(vm.HasCli);
        Assert.Equal("Reinstall", vm.InstallLabel);
        Assert.True(vm.SignedIn);
        Assert.Contains("sync", _daemon.Calls); // daemon nudged

        // Replacing a CLI the user put in ~/.local/bin is asked through a dialog.
        _dialogs.ConfirmAnswer = false;
        Assert.False(await _cli.LastConfirm!("/home/me/.local/bin/proton-drive"));
        Assert.Contains(_dialogs.Confirmations, c => c.Contains("/home/me/.local/bin/proton-drive"));
    }

    [Fact]
    public async Task Shows_an_available_update()
    {
        _cli.Installed = "/x/proton-drive";
        _cli.Latest = "0.10.0";
        _haveClient = true;
        var vm = Vm(out _);
        await vm.LoadAsync();
        Assert.Equal("Update to 0.10.0", vm.InstallLabel);
        Assert.Contains("0.10.0 is available", vm.CliStatus);
    }

    [Fact]
    public async Task Sign_out_asks_first_and_sign_in_works()
    {
        _haveClient = true;
        var vm = Vm(out _);
        await vm.LoadAsync();
        Assert.True(vm.IsSignedIn);

        _dialogs.ConfirmAnswer = false;
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(0, _drive.Logouts);

        _dialogs.ConfirmAnswer = true;
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(1, _drive.Logouts);
        Assert.True(vm.IsSignedOut);

        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(1, _drive.Logins);
        Assert.True(vm.IsSignedIn);
    }

    [Fact]
    public async Task Startup_toggles()
    {
        var vm = Vm(out _);
        await vm.LoadAsync();
        Assert.True(vm.TrayAtLogin);
        vm.TrayAtLogin = false;
        Assert.False(_startup.TrayAtLogin);

        _dialogs.ConfirmAnswer = false;
        vm.ServiceEnabled = false; // asks first; declined
        await Task.Yield();
        Assert.True(_startup.Service);
        _dialogs.ConfirmAnswer = true;
        vm.ServiceEnabled = false;
        await Task.Yield();
        Assert.False(_startup.Service);
        Assert.False(vm.ServiceEnabled);
    }
}

public sealed class SystemWideStartupTests
{
    [Fact]
    public void A_system_autostart_entry_counts_until_the_user_overrides_it()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp["etc/autostart"]);
        File.WriteAllText(tmp["etc/autostart/pdrive-tray.desktop"], "[Desktop Entry]\nExec=pdrive --tray\n");
        var s = new StartupSettings(tmp["home-config"], "/opt/pdrive/pdrive", [tmp["etc"]]);
        Assert.True(s.TrayAtLogin); // from the .deb's /etc/xdg/autostart
        s.SetTrayAtLogin(false);
        Assert.False(s.TrayAtLogin); // the user's Hidden=true entry wins
        Assert.True(File.Exists(tmp["etc/autostart/pdrive-tray.desktop"]));
    }
}

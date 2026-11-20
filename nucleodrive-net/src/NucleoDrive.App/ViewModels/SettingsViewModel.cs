using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NucleoDrive.App.Services;
using NucleoDrive.Cli;
using NucleoDrive.Ipc;

namespace NucleoDrive.App.ViewModels;

/// <summary>The Settings page: the proton-drive CLI (install/update), the Proton account (sign in/out)
/// and what starts at login (the tray, and the background service that syncs).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ICliSetup _cli;
    private readonly DriveClientProvider _provider;
    private readonly IStartupSettings _startup;
    private readonly IDaemonControl _daemon;
    private readonly IDialogs _dialogs;
    private readonly IToaster _toast;
    private CliRelease? _latest;
    private bool _trayAtLogin;
    private bool? _serviceEnabled;
    private bool _applyingService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel), nameof(HasCli))]
    private string? _cliPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    private string? _cliVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    private string? _latestVersion;

    [ObservableProperty] private string _cliStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCliCommand))]
    private bool _cliBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsSignedOut))]
    private bool? _signedIn;

    [ObservableProperty] private string _accountStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(SignOutCommand))]
    private bool _accountBusy;

    private readonly IAppearance? _appearance;

    public SettingsViewModel(ICliSetup cli, DriveClientProvider provider, IStartupSettings startup, IDaemonControl daemon,
        IDialogs dialogs, IToaster toast, IAppearance? appearance = null)
    {
        _appearance = appearance;
        _cli = cli;
        _provider = provider;
        _startup = startup;
        _daemon = daemon;
        _dialogs = dialogs;
        _toast = toast;
    }

    public bool HasAppearance => _appearance is not null;

    /// <summary>0 automatic (follow the system), 1 light, 2 dark.</summary>
    public int ThemeIndex
    {
        get => (int)(_appearance?.Theme ?? AppTheme.Auto);
        set
        {
            if (_appearance is null || value is < 0 or > 2 || value == ThemeIndex)
                return;
            _appearance.Theme = (AppTheme)value;
            OnPropertyChanged();
        }
    }

    public bool HasCli => CliPath is not null;
    public bool IsSignedIn => SignedIn == true;
    public bool IsSignedOut => SignedIn == false;

    public string InstallLabel =>
        CliPath is null ? "Install the CLI"
        : LatestVersion is { } l && CliVersion is { } v && CliInstaller.CompareVersions(l, v) > 0 ? $"Update to {l}"
        : "Reinstall";

    /// <summary>Start the tray app when you log in.</summary>
    public bool TrayAtLogin
    {
        get => _trayAtLogin;
        set
        {
            if (value == _trayAtLogin)
                return;
            try
            {
                _startup.SetTrayAtLogin(value);
                _trayAtLogin = value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _toast.Show("Couldn't change the login setting: " + e.Message, error: true);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>Keep syncing and backing up in the background (the systemd user service).
    /// Null when the service isn't installed.</summary>
    public bool? ServiceEnabled
    {
        get => _serviceEnabled;
        set
        {
            if (value is null || value == _serviceEnabled || _applyingService)
                return;
            _ = ApplyServiceAsync(value.Value);
        }
    }

    public bool ServiceInstalled => _serviceEnabled is not null;

    [RelayCommand]
    public async Task LoadAsync()
    {
        _trayAtLogin = _startup.TrayAtLogin;
        OnPropertyChanged(nameof(TrayAtLogin));
        _serviceEnabled = await _startup.IsServiceEnabledAsync();
        OnPropertyChanged(nameof(ServiceEnabled));
        OnPropertyChanged(nameof(ServiceInstalled));

        await RefreshCliAsync();
        await RefreshAccountAsync();
    }

    private async Task RefreshCliAsync()
    {
        CliPath = _cli.FindInstalled();
        CliVersion = CliPath is null ? null : await _cli.VersionOfAsync(CliPath);
        try
        {
            _latest = await _cli.GetLatestAsync();
            LatestVersion = _latest.Version;
        }
        catch (DriveException e)
        {
            LatestVersion = null;
            CliStatus = "Couldn't check for updates: " + e.FirstLine;
            return;
        }
        CliStatus = CliPath is null ? "Not installed. NucleoDrive needs Proton's official CLI to talk to Drive."
            : CliVersion is null ? $"Found at {CliPath}, but it doesn't run."
            : CliInstaller.CompareVersions(_latest.Version, CliVersion) > 0 ? $"Version {CliVersion}; {_latest.Version} is available."
            : $"Version {CliVersion} (latest).";
    }

    private async Task RefreshAccountAsync()
    {
        if (_provider.Client is not { } client)
        {
            SignedIn = null;
            AccountStatus = "Install the CLI first.";
            return;
        }
        try
        {
            SignedIn = await client.IsLoggedInAsync();
            AccountStatus = SignedIn == true ? "Signed in to Proton." : "Not signed in.";
        }
        catch (DriveException e)
        {
            SignedIn = null;
            AccountStatus = "Couldn't check: " + e.FirstLine;
        }
    }

    private bool CanInstall => !CliBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallCliAsync()
    {
        CliBusy = true;
        try
        {
            _latest ??= await _cli.GetLatestAsync();
            var path = await _cli.InstallAsync(_latest,
                what => _dialogs.ConfirmAsync("Replace proton-drive?",
                    $"{what} already exists. Replace it with a link to the CLI NucleoDrive just installed (Proton's official build, checksum verified)?",
                    "Replace", "Keep it"),
                new Progress<string>(m => CliStatus = m)); // reports on the UI thread
            _provider.Reset();
            await RefreshCliAsync();
            await RefreshAccountAsync();
            _toast.Show($"Installed proton-drive {_latest.Version} at {path}");
            await Nudge();
        }
        catch (DriveException e)
        {
            CliStatus = e.FirstLine;
            _toast.Show(e.FirstLine, error: true);
        }
        finally
        {
            CliBusy = false;
        }
    }

    private bool CanSignIn => !AccountBusy && _provider.Client is not null;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        if (_provider.Client is not { } client)
            return;
        AccountBusy = true;
        AccountStatus = "Finish signing in in your browser…";
        try
        {
            await client.LoginAsync();
            await RefreshAccountAsync();
            await Nudge();
        }
        catch (DriveException e)
        {
            AccountStatus = "Sign-in didn't complete: " + e.FirstLine;
        }
        finally
        {
            AccountBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignOutAsync()
    {
        if (_provider.Client is not { } client)
            return;
        if (!await _dialogs.ConfirmAsync("Sign out?", "Sync and backups stop until you sign in again. Files on this computer and on Drive are kept.", "Sign out"))
            return;
        AccountBusy = true;
        try
        {
            await client.LogoutAsync();
            await RefreshAccountAsync();
            await Nudge();
        }
        catch (DriveException e)
        {
            AccountStatus = "Sign-out failed: " + e.FirstLine;
        }
        finally
        {
            AccountBusy = false;
        }
    }

    private async Task ApplyServiceAsync(bool enable)
    {
        if (!enable && !await _dialogs.ConfirmAsync("Stop syncing in the background?",
                "Sync and backups stop until you turn this back on. Nothing is deleted.", "Stop"))
        {
            OnPropertyChanged(nameof(ServiceEnabled)); // put the checkbox back
            return;
        }
        _applyingService = true;
        try
        {
            await _startup.SetServiceEnabledAsync(enable);
            _serviceEnabled = enable;
        }
        catch (InvalidOperationException e)
        {
            _toast.Show(e.Message, error: true);
        }
        finally
        {
            _applyingService = false;
            OnPropertyChanged(nameof(ServiceEnabled));
        }
    }

    /// <summary>Opens the setup wizard again (set by the app).</summary>
    public Action? ShowSetup { get; set; }

    /// <summary>Opens the About dialog (set by the app).</summary>
    public Action? ShowAbout { get; set; }

    [RelayCommand]
    private void RunSetup() => ShowSetup?.Invoke();

    [RelayCommand]
    private void About() => ShowAbout?.Invoke();

    /// <summary>Lets the daemon notice a new CLI or sign-in straight away.</summary>
    private async Task Nudge()
    {
        try
        {
            await _daemon.SyncNowAsync();
        }
        catch (DaemonException)
        {
            // daemon not running; it'll notice when it starts
        }
    }
}

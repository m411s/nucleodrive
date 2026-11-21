using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NucleoDrive.App.Services;
using NucleoDrive.Cli;
using NucleoDrive.Ipc;
using NucleoDrive.Sync;

namespace NucleoDrive.App.ViewModels;

public enum SetupStep { Cli, SignIn, Folder, Startup }

/// <summary>First-run setup: (install the CLI if missing,) sign in, choose the sync folder and Drive
/// folders, choose what starts at login. Nothing is saved until Finish.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ICliSetup _cli;
    private readonly DriveClientProvider _provider;
    private readonly ISyncSetup _sync;
    private readonly IStartupSettings _startup;
    private readonly SetupState _state;
    private readonly IDaemonControl _daemon;
    private readonly IDialogs _dialogs;
    private readonly List<SetupStep> _steps = [];
    private bool? _serviceWasEnabled;

    public SetupViewModel(ICliSetup cli, DriveClientProvider provider, ISyncSetup sync, IStartupSettings startup,
        SetupState state, IDaemonControl daemon, IDialogs dialogs)
    {
        _cli = cli;
        _provider = provider;
        _sync = sync;
        _startup = startup;
        _state = state;
        _daemon = daemon;
        _dialogs = dialogs;
    }

    /// <summary>Raised after Finish saved everything.</summary>
    public event Action? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCliStep), nameof(IsSignInStep), nameof(IsFolderStep), nameof(IsStartupStep),
        nameof(StepTitle), nameof(StepText), nameof(IsLastStep), nameof(NextLabel), nameof(StepDots), nameof(IsFirstStep))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private SetupStep _step;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(InstallCliCommand), nameof(SignInCommand))]
    private bool _busy;

    // CLI
    [ObservableProperty] private string _cliStatus = "NucleoDrive talks to Drive through Proton's official command-line tool. It isn't installed yet.";

    /// <summary>Set once the wizard installed the CLI: the step then shows a success line instead of the Install button.</summary>
    [ObservableProperty] private bool _cliInstalled;

    [ObservableProperty] private string _cliInstalledTitle = "";

    // Sign in
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _signedIn;

    [ObservableProperty] private string _signInStatus = "";

    // Folder
    [ObservableProperty] private string _localPath = "";
    [ObservableProperty] private string? _folderNote;
    [ObservableProperty] private bool _folderLocked;
    public ObservableCollection<SyncFolderChoice> DriveFolders { get; } = [];

    // Startup
    [ObservableProperty] private bool _trayAtLogin = true;
    [ObservableProperty] private bool _keepSyncing = true;
    [ObservableProperty] private bool _serviceAvailable;

    [ObservableProperty] private string? _error;

    public bool IsCliStep => Step == SetupStep.Cli;
    public bool IsSignInStep => Step == SetupStep.SignIn;
    public bool IsFolderStep => Step == SetupStep.Folder;
    public bool IsStartupStep => Step == SetupStep.Startup;
    /// <summary>The first page also shows the third-party / own-risk notice.</summary>
    public bool IsFirstStep => _steps.Count == 0 || _steps.IndexOf(Step) == 0;
    public bool IsLastStep => _steps.Count > 0 && Step == _steps[^1];
    public string NextLabel => IsLastStep ? "Finish" : "Next";
    /// <summary>One dot per step: done, current or still to come.</summary>
    public IReadOnlyList<StepDot> StepDots => _steps.Select((s, i) => new StepDot(i < _steps.IndexOf(Step), s == Step)).ToList();

    public string StepText => _steps.Count == 0 ? "" : $"Step {_steps.IndexOf(Step) + 1} of {_steps.Count}";

    public string StepTitle => Step switch
    {
        SetupStep.Cli => "Install the Proton Drive CLI",
        SetupStep.SignIn => "Sign in to Proton",
        SetupStep.Folder => "Choose your sync folder",
        _ => "Start with your computer",
    };

    public async Task InitializeAsync()
    {
        _steps.Clear();
        if (_cli.FindInstalled() is null)
            _steps.Add(SetupStep.Cli);
        _steps.AddRange([SetupStep.SignIn, SetupStep.Folder, SetupStep.Startup]);

        var current = await _sync.CurrentAsync();
        LocalPath = current.Local;
        FolderLocked = current.Folders.Length > 0;
        FolderNote = FolderLocked ? "Folders are already being synced here, so the location can't change. Stop syncing them in the Sync page first." : null;
        _preselected = current.Folders;

        TrayAtLogin = true;
        _serviceWasEnabled = await _startup.IsServiceEnabledAsync();
        ServiceAvailable = _serviceWasEnabled is not null;
        KeepSyncing = true;

        Step = _steps[0];
        await EnterStepAsync();
    }

    private string[] _preselected = [];

    private bool CanBack => !Busy && _steps.IndexOf(Step) > 0;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private async Task BackAsync()
    {
        Error = null;
        Step = _steps[_steps.IndexOf(Step) - 1];
        await EnterStepAsync();
    }

    private bool CanNext => !Busy && Step switch
    {
        SetupStep.Cli => _provider.Client is not null,
        SetupStep.SignIn => SignedIn,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        Error = null;
        if (IsLastStep)
        {
            await FinishAsync();
            return;
        }
        Step = _steps[_steps.IndexOf(Step) + 1];
        await EnterStepAsync();
    }

    /// <summary>Continue without signing in (e.g. offline); Settings and the Files page offer it later.</summary>
    [RelayCommand]
    private async Task SkipSignInAsync()
    {
        Error = null;
        Step = SetupStep.Folder;
        await EnterStepAsync();
    }

    private async Task EnterStepAsync()
    {
        switch (Step)
        {
            case SetupStep.SignIn:
                await RefreshSignInAsync();
                break;
            case SetupStep.Folder:
                await LoadDriveFoldersAsync();
                break;
        }
    }

    // ---- CLI ----------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task InstallCliAsync()
    {
        Busy = true;
        try
        {
            var release = await _cli.GetLatestAsync();
            await _cli.InstallAsync(release,
                what => _dialogs.ConfirmAsync("Replace proton-drive?", $"{what} already exists. Replace it with a link to the CLI NucleoDrive just installed?", "Replace", "Keep it"),
                new Progress<string>(m => CliStatus = m));
            _provider.Reset();
            CliInstalledTitle = $"Proton Drive CLI {release.Version} installed";
            CliInstalled = true;
            NextCommand.NotifyCanExecuteChanged();
        }
        catch (DriveException e)
        {
            Error = e.FirstLine;
        }
        finally
        {
            Busy = false;
        }
    }

    private bool NotBusy => !Busy;

    // ---- sign in ------------------------------------------------------------------------------

    private async Task RefreshSignInAsync()
    {
        if (_provider.Client is not { } client)
        {
            SignedIn = false;
            SignInStatus = "Install the CLI first.";
            return;
        }
        try
        {
            SignedIn = await client.IsLoggedInAsync();
            SignInStatus = SignedIn ? "You're signed in." : "Sign in with your Proton account. Your browser opens; your password never passes through NucleoDrive.";
        }
        catch (DriveException e)
        {
            SignedIn = false;
            SignInStatus = "Couldn't check: " + e.FirstLine;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SignInAsync()
    {
        if (_provider.Client is not { } client)
            return;
        Busy = true;
        SignInStatus = "Finish signing in in your browser…";
        try
        {
            await client.LoginAsync();
            await RefreshSignInAsync();
        }
        catch (DriveException e)
        {
            SignInStatus = "Sign-in didn't complete: " + e.FirstLine;
        }
        finally
        {
            Busy = false;
        }
    }

    // ---- folder -------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (FolderLocked)
            return;
        if (await _dialogs.PickFolderAsync("Choose the folder to sync Proton Drive into") is { } picked)
            LocalPath = picked;
    }

    private async Task LoadDriveFoldersAsync()
    {
        if (DriveFolders.Count > 0 || !SignedIn || _provider.Client is not { } client)
            return;
        try
        {
            var names = (await client.ListAsync(RemotePath.Root)).Where(e => e.IsDir).Select(e => e.Name);
            foreach (var n in names.Concat(_preselected).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase))
                DriveFolders.Add(new SyncFolderChoice(n, _preselected.Contains(n)));
        }
        catch (DriveException e)
        {
            Error = "Couldn't list your Drive folders: " + e.FirstLine;
        }
    }

    // ---- finish -------------------------------------------------------------------------------

    private async Task FinishAsync()
    {
        Busy = true;
        try
        {
            var folders = DriveFolders.Where(f => f.IsChecked).Select(f => f.Path).ToArray();
            if (DriveFolders.Count == 0)
                folders = _preselected; // folders couldn't be listed: keep what was there
            await _sync.ApplyAsync(LocalPath, folders);
            _startup.SetTrayAtLogin(TrayAtLogin);
            if (ServiceAvailable && KeepSyncing != _serviceWasEnabled)
                await _startup.SetServiceEnabledAsync(KeepSyncing);
            _state.MarkComplete();
            try
            {
                await _daemon.SyncNowAsync();
            }
            catch (DaemonException)
            {
                // not running; it syncs when it starts
            }
            Completed?.Invoke();
        }
        catch (Exception e) when (e is ConfigException or DaemonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed record StepDot(bool IsDone, bool IsCurrent);

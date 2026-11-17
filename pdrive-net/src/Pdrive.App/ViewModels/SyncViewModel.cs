using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdrive.App.Services;
using Pdrive.Cli;
using Pdrive.Ipc;

namespace Pdrive.App.ViewModels;

/// <summary>One Drive folder in the "what to sync" list.</summary>
public sealed partial class SyncFolderChoice(string path, bool isChecked) : ObservableObject
{
    public string Path { get; } = path;

    [ObservableProperty] private bool _isChecked = isChecked;
}

/// <summary>The Sync page: two-way sync of chosen Drive folders into the local sync folder.</summary>
public sealed partial class SyncViewModel : ObservableObject
{
    private readonly IDaemonControl _daemon;
    private readonly DriveClientProvider _provider;
    private readonly IToaster _toast;
    private readonly Action<string> _openFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Message), nameof(Detail), nameof(LastScanText), nameof(LocalRoot), nameof(Errors),
        nameof(NeedsConfirmation), nameof(IsRunning), nameof(HasFolders))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(ConfirmDeletionsCommand), nameof(ApplyCommand),
        nameof(LoadChoicesCommand), nameof(AddPathCommand), nameof(OpenFolderCommand))]
    private DaemonStatus _status;

    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private bool _choicesDirty;

    private readonly TimeSpan? _autoApply;
    private CancellationTokenSource? _pendingApply;

    /// <summary>Toggling a folder takes effect by itself after a short pause (no Apply button).</summary>
    public bool AutoApply => _autoApply is not null;
    public bool ShowApply => !AutoApply;

    /// <param name="onUi">Runs an action on the UI thread.</param>
    /// <param name="openFolder">Opens a local folder in the file manager.</param>
    public SyncViewModel(IDaemonControl daemon, IDriveClient? drive, IToaster toast, Action<Action> onUi, Action<string> openFolder,
        TimeSpan? autoApply = null)
        : this(daemon, new DriveClientProvider(() => drive ?? throw new DriveException("proton-drive CLI not found.")), toast, onUi, openFolder, autoApply)
    {
    }

    /// <param name="autoApply">If set, folder choices are applied this long after the last change.</param>
    public SyncViewModel(IDaemonControl daemon, DriveClientProvider provider, IToaster toast, Action<Action> onUi, Action<string> openFolder,
        TimeSpan? autoApply = null)
    {
        _autoApply = autoApply;
        _daemon = daemon;
        _provider = provider;
        _toast = toast;
        _openFolder = openFolder;
        _status = daemon.Status;
        daemon.StatusChanged += s => onUi(() => Apply(s));
    }

    public ObservableCollection<SyncFolderChoice> Choices { get; } = [];

    private SyncInfo? Info => Status.Sync;

    public bool IsRunning => Status.State != DaemonState.Stopped && Info is not null;
    public bool HasFolders => Info is { Folders.Count: > 0 };
    public bool NeedsConfirmation => Info?.State == SyncState.NeedsConfirmation;

    public string StateText => !IsRunning ? "Not running" : Info!.State switch
    {
        SyncState.Off => "Off",
        SyncState.Idle => "Up to date",
        SyncState.Syncing => "Syncing",
        SyncState.Paused => "Paused",
        SyncState.Login => "Sign in required",
        SyncState.NeedsConfirmation => "Waiting for you",
        _ => "Problem",
    };

    public string Message => IsRunning ? Info!.Message : "Start the daemon with: systemctl --user start pdrive";

    /// <summary>The message, unless it only repeats the state ("Up to date").</summary>
    public string? Detail => string.Equals(Message.TrimEnd('.'), StateText, StringComparison.OrdinalIgnoreCase) ? null : Message;
    public string LastScanText => "Last check of Drive: " + Units.Ago(Info?.LastFullScan);
    public string LocalRoot => Info?.LocalRoot ?? "";
    public IReadOnlyList<string> Errors => Info?.Errors ?? [];

    private void Apply(DaemonStatus s)
    {
        var wasRunning = IsRunning;
        Status = s;
        if (IsRunning && !wasRunning && Choices.Count == 0)
            _ = LoadChoicesAsync();
    }

    /// <summary>Lists the top-level Drive folders, checked if synced; nested synced folders are listed too.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    public async Task LoadChoicesAsync()
    {
        try
        {
            var synced = (await _daemon.GetSyncSettingsAsync()).Folders;
            var names = new List<string>();
            if (_provider.Client is { } drive)
            {
                try
                {
                    names.AddRange((await drive.ListAsync(RemotePath.Root)).Where(e => e.IsDir).Select(e => e.Name));
                }
                catch (DriveException e)
                {
                    _toast.Show("Couldn't list Drive folders: " + e.FirstLine, error: true);
                }
            }
            Choices.Clear();
            foreach (var n in names.Concat(synced).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase))
            {
                var choice = new SyncFolderChoice(n, synced.Contains(n));
                choice.PropertyChanged += (_, _) => Changed();
                Choices.Add(choice);
            }
            ChoicesDirty = false;
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
    }

    /// <summary>Adds a nested Drive folder (e.g. "Documents/Work") to the list, checked.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void AddPath()
    {
        var path = NewPath.Trim().Trim('/');
        if (path.StartsWith("my-files/", StringComparison.Ordinal))
            path = path["my-files/".Length..];
        if (path.Length == 0)
            return;
        if (Choices.FirstOrDefault(c => c.Path == path) is { } existing)
            existing.IsChecked = true;
        else
        {
            var choice = new SyncFolderChoice(path, true);
            choice.PropertyChanged += (_, _) => Changed();
            Choices.Add(choice);
        }
        Changed();
        NewPath = "";
    }

    private async void Changed()
    {
        ChoicesDirty = true;
        if (_autoApply is not { } delay)
            return;
        _pendingApply?.Cancel();
        var cts = _pendingApply = new CancellationTokenSource();
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // another toggle came in; that one applies
        }
        if (ApplyCommand.CanExecute(null))
            await ApplyCommand.ExecuteAsync(null);
    }

    public int SyncedCount => Choices.Count(c => c.IsChecked);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task ApplyAsync()
    {
        var folders = Choices.Where(c => c.IsChecked).Select(c => c.Path).ToArray();
        try
        {
            await _daemon.SetSyncFoldersAsync(folders);
            ChoicesDirty = false;
            OnPropertyChanged(nameof(SyncedCount));
            _toast.Show(folders.Length == 0 ? "Sync turned off (files are kept)" : $"Syncing {folders.Length} folder(s) into {LocalRoot}");
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task SyncNowAsync() => Try(_daemon.SyncNowAsync);

    [RelayCommand(CanExecute = nameof(NeedsConfirmation))]
    private Task ConfirmDeletionsAsync() => Try(_daemon.ConfirmDeletionsAsync);

    [RelayCommand(CanExecute = nameof(HasFolders))]
    private void OpenFolder()
    {
        if (LocalRoot.Length > 0)
            _openFolder(LocalRoot);
    }

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
    }
}

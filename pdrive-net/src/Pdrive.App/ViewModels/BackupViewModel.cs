using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdrive.App.Services;
using Pdrive.Ipc;

namespace Pdrive.App.ViewModels;

/// <summary>The Backup tab: daemon status, backed-up folders, pause/resume, sync now.</summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly IDaemonControl _daemon;
    private readonly IDialogs _dialogs;
    private readonly IToaster _toast;
    private readonly Action<Action> _onUi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Message), nameof(LastSyncText), nameof(IsRunning), nameof(IsPaused),
        nameof(PauseLabel), nameof(ShowProgress), nameof(ProgressPercent), nameof(Errors), nameof(Unwatched), nameof(NotRunningHint))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(TogglePauseCommand), nameof(AddFolderCommand), nameof(RemoveFolderCommand))]
    private DaemonStatus _status;

    /// <param name="onUi">Runs an action on the UI thread (Dispatcher.UIThread.Post in the app).</param>
    public BackupViewModel(IDaemonControl daemon, IDialogs dialogs, IToaster toast, Action<Action> onUi)
    {
        _daemon = daemon;
        _dialogs = dialogs;
        _toast = toast;
        _onUi = onUi;
        _status = daemon.Status;
        daemon.StatusChanged += s => _onUi(() => Apply(s));
    }

    public ObservableCollection<FolderInfo> Folders { get; } = [];

    public string StateText => Status.State switch
    {
        DaemonState.Idle => "Up to date",
        DaemonState.Syncing => "Syncing",
        DaemonState.Paused => "Paused",
        DaemonState.Error => "Problem",
        DaemonState.Login => "Sign in required",
        DaemonState.Starting => "Starting",
        _ => "Not running",
    };

    public string Message => Status.Message;
    public string LastSyncText => "Last sync: " + Units.Ago(Status.LastSync);
    public bool IsRunning => Status.State != DaemonState.Stopped;
    public bool IsPaused => Status.State == DaemonState.Paused;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    public bool ShowProgress => Status.Progress is { FilesTotal: > 0 };
    public double ProgressPercent => Status.Progress is { BytesTotal: > 0 } p ? 100.0 * p.BytesDone / p.BytesTotal
        : Status.Progress is { FilesTotal: > 0 } f ? 100.0 * f.FilesDone / f.FilesTotal : 0;
    public IReadOnlyList<string> Errors => Status.RecentErrors;
    public IReadOnlyList<string> Unwatched => Status.Unwatched.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
    public string? NotRunningHint => IsRunning ? null
        : "Start it with: systemctl --user start pdrive   (backups only run while the daemon is running)";

    private void Apply(DaemonStatus s)
    {
        var wasRunning = IsRunning;
        Status = s;
        if (IsRunning && !wasRunning)
            _ = ReloadFoldersAsync();
        else if (!IsRunning)
            Folders.Clear();
    }

    public async Task ReloadFoldersAsync()
    {
        try
        {
            var folders = await _daemon.ListFoldersAsync();
            Folders.Clear();
            foreach (var f in folders)
                Folders.Add(f);
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task SyncNowAsync() => Try(_daemon.SyncNowAsync);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task TogglePauseAsync() => Try(IsPaused ? _daemon.ResumeAsync : _daemon.PauseAsync);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task AddFolderAsync()
    {
        var local = await _dialogs.PickFolderAsync("Back up this folder");
        if (local is null)
            return;
        var remote = Pdrive.Cli.RemotePath.Join(Pdrive.Cli.RemotePath.Root, Path.GetFileName(Path.TrimEndingDirectorySeparator(local)));
        if (await Try(() => _daemon.AddFolderAsync(local, remote)))
        {
            _toast.Show($"Backing up {local} to {remote}");
            await ReloadFoldersAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task RemoveFolderAsync(FolderInfo? folder)
    {
        if (folder is null)
            return;
        if (await Try(() => _daemon.RemoveFolderAsync(folder.Local)))
        {
            _toast.Show($"Stopped backing up {folder.Local} (files on Drive are kept)");
            await ReloadFoldersAsync();
        }
    }

    private async Task<bool> Try(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
            return false;
        }
    }
}

public sealed class MainWindowViewModel(BrowserViewModel browser, BackupViewModel backup, SyncViewModel sync, SettingsViewModel? settings = null)
{
    public BrowserViewModel Browser { get; } = browser;
    public BackupViewModel Backup { get; } = backup;
    public SyncViewModel Sync { get; } = sync;
    public SettingsViewModel? Settings { get; } = settings;
}

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pdrive.App.Services;
using Pdrive.Ipc;

namespace Pdrive.App.ViewModels;

/// <summary>The main window: sidebar navigation between the pages and the combined status at the bottom of it.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private bool _settingsLoaded;

    public MainWindowViewModel(BrowserViewModel browser, BackupViewModel backup, SyncViewModel sync, SettingsViewModel? settings = null)
    {
        Browser = browser;
        Backup = backup;
        Sync = sync;
        Settings = settings;
        Backup.PropertyChanged += OnBackupChanged;
    }

    public BrowserViewModel Browser { get; }
    public BackupViewModel Backup { get; }
    public SyncViewModel Sync { get; }
    public SettingsViewModel? Settings { get; }

    /// <summary>One-way backup is hidden in the window for now (it still works through pdrivectl and the daemon).</summary>
    public bool ShowBackup { get; init; }

    public const int FilesPage = 0, SyncPage = 1, BackupPage = 2, SettingsPage = 3;

    /// <summary>0 Files, 1 Sync, 2 Backup, 3 Settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private int _pageIndex;

    public object? CurrentPage => PageIndex switch
    {
        1 => Sync,
        2 => Backup,
        3 => Settings,
        _ => Browser,
    };

    partial void OnPageIndexChanged(int value)
    {
        // Settings asks the CLI for its version and sign-in state: only when first opened.
        if (value == 3 && !_settingsLoaded && Settings is { } s)
        {
            _settingsLoaded = true;
            _ = s.LoadAsync();
        }
    }

    // ---- combined status (sync + backup), as in the tray -------------------------

    private (DaemonState Icon, string Message, DateTimeOffset? Last) Combined => TrayState.Combine(Backup.Status);

    public string StatusTitle => Combined.Icon switch
    {
        DaemonState.Idle => "Up to date",
        DaemonState.Syncing => "Syncing",
        DaemonState.Starting => "Starting",
        DaemonState.Paused => "Paused",
        DaemonState.Error => "Problem",
        DaemonState.Login => "Needs attention",
        _ => "Service stopped",
    };

    public string StatusDetail => Combined.Icon == DaemonState.Stopped ? "Background sync isn't running"
        : Combined.Icon == DaemonState.Idle ? (Combined.Last is null ? "Everything is in place" : "Checked " + Units.Ago(Combined.Last))
        : Combined.Message;

    public bool StatusOk => Combined.Icon == DaemonState.Idle;
    public bool StatusBusy => Combined.Icon is DaemonState.Syncing or DaemonState.Starting;
    public bool StatusWarn => Combined.Icon is DaemonState.Login or DaemonState.Paused;
    public bool StatusError => Combined.Icon == DaemonState.Error;
    public bool StatusOff => Combined.Icon == DaemonState.Stopped;

    private void OnBackupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BackupViewModel.Status))
            return;
        foreach (var name in (string[])[nameof(StatusTitle), nameof(StatusDetail), nameof(StatusOk), nameof(StatusBusy),
                     nameof(StatusWarn), nameof(StatusError), nameof(StatusOff)])
            OnPropertyChanged(name);
    }
}

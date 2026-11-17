using Pdrive.Ipc;

namespace Pdrive.App.Services;

/// <summary>File/folder pickers, so view models can be tested without a window.</summary>
public interface IDialogs
{
    Task<IReadOnlyList<string>> PickFilesAsync(string title);
    Task<string?> PickFolderAsync(string title);

    /// <summary>Yes/no question; true for yes.</summary>
    Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel");
}

public interface IToaster
{
    void Show(string message, bool error = false);
}

/// <summary>What the Backup page and tray need from the daemon.</summary>
public interface IDaemonControl
{
    DaemonStatus Status { get; }

    /// <summary>Raised on any thread.</summary>
    event Action<DaemonStatus>? StatusChanged;

    Task SyncNowAsync();
    Task PauseAsync();
    Task ResumeAsync();
    Task<FolderInfo[]> ListFoldersAsync();
    Task AddFolderAsync(string local, string remote);
    Task RemoveFolderAsync(string local);
    Task<SyncSettingsInfo> GetSyncSettingsAsync();
    Task SetSyncFoldersAsync(string[] folders);
    Task ConfirmDeletionsAsync();
    Task SetSyncLocalAsync(string local);
}

/// <summary><see cref="IDaemonControl"/> over the auto-reconnecting <see cref="DaemonMonitor"/>.</summary>
public sealed class MonitorDaemonControl(DaemonMonitor monitor) : IDaemonControl
{
    public DaemonStatus Status => monitor.Status;

    public event Action<DaemonStatus>? StatusChanged
    {
        add => monitor.StatusChanged += value;
        remove => monitor.StatusChanged -= value;
    }

    private DaemonClient Client => monitor.Client ?? throw new DaemonException("The backup daemon isn't running.");

    public Task SyncNowAsync() => Client.SyncNowAsync();
    public Task PauseAsync() => Client.PauseAsync();
    public Task ResumeAsync() => Client.ResumeAsync();
    public Task<FolderInfo[]> ListFoldersAsync() => Client.ListFoldersAsync();
    public Task AddFolderAsync(string local, string remote) => Client.AddFolderAsync(local, remote);
    public Task RemoveFolderAsync(string local) => Client.RemoveFolderAsync(local);
    public Task<SyncSettingsInfo> GetSyncSettingsAsync() => Client.GetSyncSettingsAsync();
    public Task SetSyncFoldersAsync(string[] folders) => Client.SetSyncFoldersAsync(folders);
    public Task ConfirmDeletionsAsync() => Client.ConfirmDeletionsAsync();
    public Task SetSyncLocalAsync(string local) => Client.SetSyncLocalAsync(local);
}

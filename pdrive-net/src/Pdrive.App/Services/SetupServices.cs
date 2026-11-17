using Pdrive.Ipc;
using Pdrive.Sync;

namespace Pdrive.App.Services;

/// <summary>Remembers whether the first-run setup wizard was completed (a marker file next to config.json).</summary>
public sealed class SetupState(string configDir)
{
    private string Marker => Path.Combine(configDir, "setup-done");

    public bool IsComplete => File.Exists(Marker);

    /// <summary>Show the wizard on startup: never completed, and nothing is synced yet
    /// (so people who set pdrive up by hand or with pdrivectl aren't interrupted).</summary>
    public bool ShouldRun(SyncSettingsInfo current) => !IsComplete && current.Folders.Length == 0;

    public void MarkComplete()
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Marker, DateTimeOffset.Now.ToString("O") + "\n");
    }
}

/// <summary>Reads and applies the two-way sync settings: through the daemon when it runs (it validates
/// and starts syncing straight away), otherwise directly in config.json (picked up when it starts).</summary>
public interface ISyncSetup
{
    Task<SyncSettingsInfo> CurrentAsync();

    /// <exception cref="ConfigException">or <see cref="DaemonException"/>: refused, with a message for the user.</exception>
    Task ApplyAsync(string local, string[] folders);
}

public sealed class SyncSetup(IDaemonControl daemon, ConfigStore config) : ISyncSetup
{
    private bool DaemonRunning => daemon.Status.State != DaemonState.Stopped;

    public async Task<SyncSettingsInfo> CurrentAsync()
    {
        if (DaemonRunning)
        {
            try
            {
                return await daemon.GetSyncSettingsAsync();
            }
            catch (DaemonException)
            {
            }
        }
        try
        {
            var s = config.Load().Sync;
            return new SyncSettingsInfo(s.LocalPath, [.. s.Folders], s.RemoteIntervalMinutes);
        }
        catch (ConfigException)
        {
            var s = new TwoWaySettings();
            return new SyncSettingsInfo(s.LocalPath, [], s.RemoteIntervalMinutes);
        }
    }

    public async Task ApplyAsync(string local, string[] folders)
    {
        if (DaemonRunning)
        {
            await daemon.SetSyncLocalAsync(local);
            await daemon.SetSyncFoldersAsync(folders);
            return;
        }
        var cfg = config.Update(c =>
        {
            var moved = SyncRootRules.SetLocal(c, local);
            return moved with { Sync = moved.Sync with { Folders = folders } };
        });
        Directory.CreateDirectory(cfg.Sync.LocalPath);
    }
}

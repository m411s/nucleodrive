using Pdrive.Ipc;

namespace Pdrive.App.Services;

/// <summary>What the tray shows when both the backups and the two-way sync report a state:
/// the more urgent one wins (sign-in/attention > problem > syncing > paused > idle).</summary>
public static class TrayState
{
    public static (DaemonState Icon, string Message, DateTimeOffset? Last) Combine(DaemonStatus s)
    {
        if (s.State == DaemonState.Stopped)
            return (DaemonState.Stopped, s.Message, null);
        if (s.Sync is not { State: not SyncState.Off } sync)
            return (s.State, s.Message, s.LastSync);

        var syncIcon = sync.State switch
        {
            SyncState.Idle => DaemonState.Idle,
            SyncState.Syncing => DaemonState.Syncing,
            SyncState.Paused => DaemonState.Paused,
            SyncState.Error => DaemonState.Error,
            _ => DaemonState.Login, // sign-in needed, or deletions waiting for confirmation: amber
        };
        return Rank(s.State) > Rank(syncIcon)
            ? (s.State, s.Message, s.LastSync)
            : (syncIcon, sync.Message, sync.LastFullScan);
    }

    private static int Rank(DaemonState state) => state switch
    {
        DaemonState.Login => 5,
        DaemonState.Error => 4,
        DaemonState.Syncing or DaemonState.Starting => 3,
        DaemonState.Paused => 2,
        _ => 1,
    };
}

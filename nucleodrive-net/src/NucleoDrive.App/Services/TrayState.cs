using NucleoDrive.Ipc;

namespace NucleoDrive.App.Services;

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

    /// <summary>What the tray menu offers for a status. "Sync now" follows the combined state (as the
    /// icon does): off while stopped, paused or signed out. "Review deletions…" appears while sync
    /// holds back a large deletion.</summary>
    public static TrayMenu Menu(DaemonStatus s)
    {
        var running = s.State != DaemonState.Stopped;
        var paused = s.State == DaemonState.Paused || s.Sync?.State == SyncState.Paused;
        var signedOut = s.State == DaemonState.Login || s.Sync?.State == SyncState.Login;
        string? review = s.Sync is { State: SyncState.NeedsConfirmation } sync
            ? sync.HeldDeletes > 0 ? $"Review {sync.HeldDeletes} deletions…" : "Review deletions…"
            : null;
        return new TrayMenu(
            SyncNowEnabled: running && !paused && !signedOut,
            PauseEnabled: running,
            PauseLabel: paused ? "Resume" : "Pause",
            ReviewDeletions: running ? review : null);
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

/// <param name="ReviewDeletions">Menu label, or null to hide the item.</param>
public sealed record TrayMenu(bool SyncNowEnabled, bool PauseEnabled, string PauseLabel, string? ReviewDeletions);

using Microsoft.Extensions.Logging;
using NucleoDrive.Cli;
using NucleoDrive.Sync;

namespace NucleoDrive.Daemon;

public sealed record DaemonOptions
{
    /// <summary>Creates the Drive client for each cycle (so conflict_strategy changes apply without restart).</summary>
    public Func<SyncConfig, IDriveClient> ClientFactory { get; init; } =
        cfg => new ProtonDriveCli(null, new DriveCliOptions { FileConflictStrategy = cfg.FileConflictStrategy });

    /// <summary>Quiet period before a watched change triggers a scan; default max(min_age, 5 s).</summary>
    public TimeSpan? WatchQuiet { get; init; }

    /// <summary>Longest a stream of changes can postpone a scan.</summary>
    public TimeSpan WatchMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum interval between progress pushes to clients.</summary>
    public TimeSpan ProgressThrottle { get; init; } = TimeSpan.FromMilliseconds(250);

    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;

    /// <summary>Where the desktop Trash lives ($XDG_DATA_HOME by default). Tests point this at a temp dir.</summary>
    public string? TrashDataHome { get; init; }
}

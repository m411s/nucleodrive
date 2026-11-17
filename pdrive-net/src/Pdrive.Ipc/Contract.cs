using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace Pdrive.Ipc;

public enum DaemonState { Starting, Idle, Syncing, Paused, Error, Login, Stopped }

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string RemoteDir);

public sealed record FolderInfo(string Local, string Remote);

public enum SyncState { Off, Idle, Syncing, Paused, Error, Login, NeedsConfirmation }

/// <summary>Two-way sync status (the ~/proton-drive folder), part of <see cref="DaemonStatus"/>.</summary>
public sealed record SyncInfo(
    SyncState State,
    string Message,
    string LocalRoot,
    IReadOnlyList<string> Folders,
    DateTimeOffset? LastFullScan,
    int HeldDeletes,
    IReadOnlyList<string> Errors);

public sealed record SyncSettingsInfo(string Local, string[] Folders, double RemoteIntervalMinutes);

public sealed record DaemonStatus(
    DaemonState State,
    string Message,
    DateTimeOffset? LastSync,
    TransferProgress? Progress,
    IReadOnlyList<string> RecentErrors,
    IReadOnlyDictionary<string, string> Unwatched,
    int Pid)
{
    /// <summary>Two-way sync status; null if the daemon has no sync worker.</summary>
    public SyncInfo? Sync { get; init; }

    /// <summary>What clients show when they can't reach the daemon.</summary>
    public static readonly DaemonStatus NotRunning =
        new(DaemonState.Stopped, "Backup daemon not running", null, null, [], new Dictionary<string, string>(), 0);
}

/// <summary>JSON-RPC method names served by the daemon. Requests: GetStatus, SyncNow, Pause, Resume,
/// ListFolders, AddFolder(local, remote), RemoveFolder(local). Notification to clients: StatusChanged(status).</summary>
public static class Rpc
{
    public const string GetStatus = nameof(GetStatus);
    public const string SyncNow = nameof(SyncNow);
    public const string Pause = nameof(Pause);
    public const string Resume = nameof(Resume);
    public const string ListFolders = nameof(ListFolders);
    public const string AddFolder = nameof(AddFolder);
    public const string RemoveFolder = nameof(RemoveFolder);
    public const string GetSyncSettings = nameof(GetSyncSettings);
    public const string SetSyncFolders = nameof(SetSyncFolders);
    public const string ConfirmDeletions = nameof(ConfirmDeletions);
    public const string StatusChanged = nameof(StatusChanged);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The message handler both ends use over a socket stream.</summary>
    public static IJsonRpcMessageHandler CreateHandler(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonOptions.PropertyNamingPolicy;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        return new HeaderDelimitedMessageHandler(stream, stream, formatter);
    }
}

public static class SocketPath
{
    /// <summary>sun_path is 108 bytes on Linux, including the terminating NUL.</summary>
    public const int MaxBytes = 107;

    /// <summary>Throws <see cref="DaemonException"/> with a fix-it hint if the path can't be a Unix socket.</summary>
    public static void Validate(string path)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(path) > MaxBytes)
            throw new DaemonException(
                $"Socket path is too long for a Unix socket ({path.Length} > {MaxBytes} bytes): {path}. " +
                "Set PDRIVE_RUNTIME_DIR (or XDG_RUNTIME_DIR) to a shorter directory.");
    }
}

/// <summary>A request the daemon refused (bad folder, etc.); the message is meant for the user.</summary>
public sealed class DaemonException(string message, Exception? inner = null) : Exception(message, inner);

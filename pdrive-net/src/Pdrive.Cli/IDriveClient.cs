namespace Pdrive.Cli;

/// <summary>Operations on Proton Drive. Failures throw <see cref="DriveException"/>
/// (<see cref="AuthException"/> when signed out); cancellation throws OperationCanceledException.</summary>
public interface IDriveClient
{
    Task<IReadOnlyList<DriveEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>False only if the CLI reports we're signed out. Other failures (offline,
    /// CLI crash, timeout) throw, so callers can show the real cause.</summary>
    Task<bool> IsLoggedInAsync(CancellationToken ct = default);

    /// <summary>Opens the browser for sign-in; completes when the CLI does.</summary>
    Task LoginAsync(CancellationToken ct = default);

    Task MkdirAsync(string remotePath, CancellationToken ct = default);

    /// <param name="output">Receives the CLI's output lines as they arrive (progress, if it prints any).</param>
    Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default);

    /// <param name="size">Expected bytes, if known; sets the timeout. Without it (e.g. folders) there is none.</param>
    Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default);
}

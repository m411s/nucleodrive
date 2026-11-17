using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrapCli = CliWrap.Cli;

namespace Pdrive.Cli;

public sealed record DriveCliOptions
{
    /// <summary>What upload does when a file of the same name exists (`-f`). Always passed: without a
    /// strategy the CLI prompts, which would stall a non-interactive call until its timeout.
    /// create-new-revision keeps the old content in the file's version history.</summary>
    public string FileConflictStrategy { get; init; } = UploadFileStrategies.Default;

    /// <summary>What upload does when a folder of the same name exists (`-d`); merge adds to it.</summary>
    public string FolderConflictStrategy { get; init; } = "merge";

    /// <summary>Extra environment variables for the CLI process (used by tests).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>How long a cancelled CLI call gets to exit after SIGINT before it is killed.</summary>
    public TimeSpan KillAfter { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Values of `filesystem upload -f` (checked against cli-drive 0.9.0).</summary>
public static class UploadFileStrategies
{
    public const string Default = "create-new-revision";
    public static readonly IReadOnlyList<string> All = ["create-new-revision", "rename", "replace", "skip"];
}

/// <summary>Thin wrapper around Proton's official `proton-drive` CLI (checked against cli-drive 0.9.0,
/// see docs/cli-notes.md).</summary>
public sealed class ProtonDriveCli : IDriveClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(300);

    private readonly DriveCliOptions _options;

    public ProtonDriveCli(string? binary = null, DriveCliOptions? options = null)
    {
        Binary = binary ?? BinaryLocator.Find() ?? throw new DriveException(
            "proton-drive CLI not found. Download it from https://proton.me/download/drive/cli, " +
            "chmod +x it, and put it on your PATH (or set PROTON_DRIVE_BIN).");
        _options = options ?? new DriveCliOptions();
    }

    public string Binary { get; }

    public async Task<IReadOnlyList<DriveEntry>> ListAsync(string path, CancellationToken ct = default) =>
        ListingParser.Parse(await RunAsync(["filesystem", "list", path, "--json"], ShortTimeout, null, ct), path);

    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        try
        {
            await ListAsync(RemotePath.Root, ct);
            return true;
        }
        catch (AuthException)
        {
            return false;
        }
    }

    public Task LoginAsync(CancellationToken ct = default) => RunAsync(["auth", "login"], LoginTimeout, null, ct);

    /// <summary>`filesystem create-folder parentPath name`. Callers treat failure as "may already exist".</summary>
    public Task MkdirAsync(string remotePath, CancellationToken ct = default) =>
        RunAsync(["filesystem", "create-folder", RemotePath.Parent(remotePath), RemotePath.UnescapeName(RemotePath.Name(remotePath))],
            ShortTimeout, null, ct);

    public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default)
    {
        List<string> args =
        [
            "filesystem", "upload",
            "-f", _options.FileConflictStrategy,
            "-d", _options.FolderConflictStrategy,
            .. localPaths, remoteDir,
        ];
        return RunAsync(args, TransferTimeout.For(TransferTimeout.LocalSize(localPaths)), output, ct);
    }

    /// <summary>Never deletes anything locally: name clashes get a unique suffix, folders are merged.</summary>
    public Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default) =>
        RunAsync(["filesystem", "download", "-f", "rename", "-d", "merge", remotePath, localDir],
            size is { } s ? TransferTimeout.For(s) : null, output, ct);

    private async Task<string> RunAsync(IEnumerable<string> args, TimeSpan? timeout, IProgress<string>? output, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t)
            timeoutCts.CancelAfter(t);
        using var graceful = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var forceful = new CancellationTokenSource();
        await using var _ = graceful.Token.Register(() => forceful.CancelAfter(_options.KillAfter));

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var lines = output is null ? PipeTarget.Null : PipeTarget.ToDelegate(output.Report);
        var cmd = CliWrapCli.Wrap(Binary)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Merge(PipeTarget.ToStringBuilder(stdout), lines))
            .WithStandardErrorPipe(PipeTarget.Merge(PipeTarget.ToStringBuilder(stderr), lines));
        if (_options.Environment is { } env)
            cmd = cmd.WithEnvironmentVariables(env);

        CommandResult result;
        try
        {
            result = await cmd.ExecuteAsync(forceful.Token, graceful.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new DriveException($"The proton-drive command timed out after {timeout!.Value.TotalSeconds:0} s.");
        }
        catch (Win32Exception e)
        {
            throw new DriveException($"Could not run {Binary}: {e.Message}", e);
        }

        if (result.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            var msg = err.Length > 0 ? err : stdout.ToString().Trim();
            if (msg.Length == 0)
                msg = $"exit code {result.ExitCode}";
            throw AuthHeuristics.LooksLikeAuthError(msg) ? new AuthException(msg) : new DriveException(msg);
        }
        return stdout.ToString();
    }
}

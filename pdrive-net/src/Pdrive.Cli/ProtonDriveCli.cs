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
            var msg = CliErrorText.From(stdout.ToString(), stderr.ToString(), result.ExitCode);
            throw AuthHeuristics.LooksLikeAuthError(msg) ? new AuthException(msg) : new DriveException(msg);
        }
        return stdout.ToString();
    }
}

/// <summary>Builds a useful error message from a failed CLI run. Observed with cli-drive 0.9.0:
/// <list type="bullet">
/// <item>plain errors: one line on stderr ("You need to login first", "Node not found: x");</item>
/// <item>partial transfer failure: stderr "1 item(s) failed to upload", details in the stdout summary
/// ("Failed: 1 items" then "  - name: Error: EACCES: …");</item>
/// <item>crash: stderr is only a "=====" rule; the cause ("ENOENT: …") is in a stack trace on stdout.</item>
/// </list>
/// The most specific line comes first, since status lines and toasts show only the first line.</summary>
public static class CliErrorText
{
    public static string From(string stdout, string stderr, int exitCode)
    {
        static bool Meaningful(string line) => line.Any(char.IsLetter);
        var outLines = stdout.Split('\n').Select(l => l.TrimEnd()).ToList();

        var details = new List<string>();
        var failedAt = outLines.FindIndex(l => l.TrimStart().StartsWith("Failed:", StringComparison.Ordinal));
        if (failedAt >= 0)
        {
            details.AddRange(outLines.Skip(failedAt + 1)
                .TakeWhile(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                .Select(l => l.TrimStart()[2..]));
        }
        else if (outLines.FirstOrDefault(IsErrorLine) is { } crash)
        {
            details.Add(crash.Trim());
        }

        var errLines = stderr.Split('\n').Select(l => l.Trim()).Where(Meaningful);
        var all = details.Concat(errLines).Distinct().ToList();
        if (all.Count == 0)
            all.AddRange(outLines.Select(l => l.Trim()).Where(Meaningful).Take(5));
        return all.Count > 0 ? string.Join('\n', all) : $"exit code {exitCode}";
    }

    // "ENOENT: no such file…", "EACCES: …", "TypeError: …", "Error: …" at the start of a line.
    private static bool IsErrorLine(string line)
    {
        var t = line.TrimStart();
        var colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0)
            return false;
        var head = t[..colon];
        return head.All(char.IsLetter) && (head.EndsWith("Error", StringComparison.Ordinal) || (head.Length >= 4 && head[0] == 'E' && head.All(char.IsUpper)));
    }
}

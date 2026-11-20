using NucleoDrive.Cli;
using NucleoDrive.Tests;

namespace NucleoDrive.Cli.Tests;

// Ports proton-drive-gui/test_backend.py, plus cancellation and conflict-strategy checks.
public sealed class ProtonDriveCliTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeCli _fake;
    private readonly ProtonDriveCli _cli;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ProtonDriveCliTests()
    {
        _fake = new FakeCli(_tmp["log"]);
        _cli = _fake.Client();
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task List_sorts_folders_first()
    {
        var es = await _cli.ListAsync("/my-files", Ct);
        Assert.Equal(["Documents", "b.txt"], es.Select(e => e.Name));
        Assert.True(es[0].IsDir);
        Assert.Equal(2048, es[1].Size);
        Assert.Equal("/my-files/Documents", es[0].Path);
    }

    [Fact]
    public async Task List_accepts_wrapped_object() => Assert.Empty(await _cli.ListAsync("/my-files/Documents", Ct));

    [Fact]
    public async Task Error_text_surfaces()
    {
        var e = await Assert.ThrowsAnyAsync<DriveException>(() => _cli.ListAsync("/nope", Ct));
        Assert.Contains("You need to login first", e.Message);
    }

    [Fact]
    public async Task Upload_download_login_succeed()
    {
        await _cli.UploadAsync(["/tmp/x"], "/my-files", ct: Ct);
        await _cli.DownloadAsync("/my-files/b.txt", "/tmp", ct: Ct);
        await _cli.LoginAsync(Ct);
        Assert.Equal(["upload", "/tmp/x", "/my-files"], _fake.Uploads().Single());
    }

    [Fact]
    public async Task Auth_errors_are_separate_from_other_failures()
    {
        await Assert.ThrowsAsync<AuthException>(() => _cli.ListAsync("/nope", Ct));
        var e = await Assert.ThrowsAsync<DriveException>(() => _cli.ListAsync("/offline", Ct));
        Assert.IsNotType<AuthException>(e);
    }

    [Fact]
    public async Task IsLoggedIn_is_false_only_when_signed_out()
    {
        Assert.True(await _cli.IsLoggedInAsync(Ct));
        Assert.False(await _fake.Set("FAKE_SIGNED_OUT", "1").Client().IsLoggedInAsync(Ct));
        var offline = new FakeCli(_tmp["log2"]).Set("FAKE_OFFLINE", "1").Client();
        await Assert.ThrowsAsync<DriveException>(() => offline.IsLoggedInAsync(Ct)); // not reported as "sign in"
    }

    [Fact]
    public async Task Conflict_strategy_is_passed_to_upload()
    {
        await _fake.Client(conflictStrategy: "replace").UploadAsync(["/tmp/x"], "/my-files", ct: Ct);
        Assert.Contains(["strategy", "replace", "merge"], _fake.Log());
        Assert.Equal(["upload", "/tmp/x", "/my-files"], _fake.Uploads().Single());
    }

    [Fact]
    public async Task Upload_and_download_always_pass_strategies_so_the_cli_never_prompts()
    {
        await _cli.UploadAsync(["/tmp/x"], "/my-files", ct: Ct);
        Assert.Contains(["strategy", "create-new-revision", "merge"], _fake.Log());
        await _cli.DownloadAsync("/my-files/b.txt", "/tmp", ct: Ct);
        Assert.Contains(["download", "rename", "merge", "/my-files/b.txt", "/tmp"], _fake.Log());
    }

    [Fact]
    public async Task Mkdir_uses_create_folder_with_parent_and_unescaped_name()
    {
        await _cli.MkdirAsync("/my-files/Backup/sub", Ct);
        await _cli.MkdirAsync("/my-files/a\\/b", Ct); // a name containing '/'
        Assert.Contains(["mkdir", "/my-files/Backup/sub"], _fake.Log());
        Assert.Contains(["mkdir", "/my-files/a/b"], _fake.Log());
    }

    [Fact]
    public async Task Cancellation_stops_the_cli_promptly()
    {
        var slow = _fake.Set("FAKE_SLOW", "30").Client();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.UploadAsync(["/tmp/x"], "/my-files", ct: cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Empty(_fake.Uploads());
    }

    [Fact]
    public async Task Output_lines_are_reported()
    {
        var lines = new List<string>();
        await _cli.UploadAsync(["/tmp/x"], "/my-files", new SyncProgress<string>(lines.Add), Ct);
        Assert.Contains(lines, l => l.StartsWith("Transfer summary"));
    }

    [Fact]
    public void Missing_binary_is_not_found_anywhere()
    {
        // An empty home and PATH, so the result doesn't depend on this machine's ~/.local/bin.
        var env = new Dictionary<string, string?> { ["PATH"] = "", ["HOME"] = _tmp.Path };
        Assert.Null(BinaryLocator.Find(k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public async Task Unrunnable_binary_is_a_drive_error()
    {
        var cli = new ProtonDriveCli(_tmp["does-not-exist"]);
        await Assert.ThrowsAsync<DriveException>(() => cli.ListAsync("/my-files", Ct));
    }
}

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

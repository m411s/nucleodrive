using Pdrive.Cli;
using Pdrive.Tests;

namespace Pdrive.Cli.Tests;

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
        Assert.Contains("not logged in", e.Message);
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
        Assert.Contains(["conflict-strategy", "replace"], _fake.Log());
        Assert.Equal(["upload", "/tmp/x", "/my-files"], _fake.Uploads().Single());
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
        Assert.Contains(lines, l => l.StartsWith("uploaded"));
    }

    [Fact]
    public void Missing_binary_gives_helpful_error()
    {
        var e = Assert.Throws<DriveException>(() => new ProtonDriveCli(BinaryLocator.Find(_ => null) ?? throw new DriveException("proton-drive CLI not found")));
        Assert.Contains("not found", e.Message);
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

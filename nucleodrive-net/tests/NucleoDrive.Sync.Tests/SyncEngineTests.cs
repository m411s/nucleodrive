using NucleoDrive.Cli;
using NucleoDrive.Tests;

namespace NucleoDrive.Sync.Tests;

// Ports proton-drive-gui/test_sync.py, plus remote-change, auth, progress and migration checks.
public sealed class SyncEngineTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _src;
    private readonly FakeCli _fake;
    private readonly StateStore _state;
    private SyncConfig _cfg;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SyncEngineTests()
    {
        _src = _tmp["src"];
        Directory.CreateDirectory(Path.Combine(_src, "sub"));
        foreach (var n in new[] { "a.txt", "sub/b.txt", "draft.tmp", ".hidden" })
            Write(n, n);
        _fake = new FakeCli(_tmp["log"]);
        _state = new StateStore(_tmp["state.db"]);
        _cfg = new SyncConfig { Folders = [new FolderPair(_src, "/my-files/Backup")] };
    }

    public void Dispose()
    {
        _state.Dispose();
        _tmp.Dispose();
    }

    private void Write(string rel, string content, bool old = true)
    {
        var p = Path.Combine(_src, rel);
        File.WriteAllText(p, content);
        if (old)
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-1));
    }

    private Task<SyncResult> Run(IDriveClient? client = null, IProgress<SyncProgress>? progress = null, CancellationToken? ct = null) =>
        SyncEngine.RunOnceAsync(_cfg, client ?? _fake.Client(), _state, progress, null, ct ?? Ct);

    [Fact]
    public async Task First_run_uploads_and_excludes()
    {
        var r = await Run();
        Assert.Equal((2, 0), (r.Uploaded, r.Errors.Count));
        var names = _fake.Uploads().SelectMany(u => u[1..^1]).Select(Path.GetFileName).Order();
        Assert.Equal(["a.txt", "b.txt"], names);
        Assert.Equal(["/my-files/Backup", "/my-files/Backup/sub"], _fake.Uploads().Select(u => u[^1]).Order());
        Assert.Contains(["mkdir", "/my-files/Backup/sub"], _fake.Log());
    }

    [Fact]
    public async Task Second_run_is_noop_then_detects_change()
    {
        await Run();
        _fake.ClearLog();
        Assert.Equal(0, (await Run()).Uploaded);
        Write("a.txt", "changed!");
        Assert.Equal(1, (await Run()).Uploaded);
    }

    [Fact]
    public async Task Failure_not_recorded_and_retried()
    {
        var r = await Run(_fake.Set("FAKE_FAIL", "b.txt").Client());
        Assert.Equal(1, r.Uploaded);
        Assert.Single(r.Errors);
        _fake.Set("FAKE_FAIL", null);
        Assert.Equal(1, (await Run()).Uploaded);
    }

    [Fact]
    public async Task Recent_files_skipped_and_next_scan_time_given()
    {
        Write("new.txt", "fresh", old: false);
        var r = await Run();
        Assert.Equal(2, r.Uploaded);
        Assert.NotNull(r.NextMatureAt);
        Assert.InRange(r.NextMatureAt!.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(6));
    }

    [Fact]
    public async Task Cancellation_returns_partial_result()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();
        var r = await Run(ct: cts.Token);
        Assert.True(r.Cancelled);
        Assert.Equal(0, r.Uploaded);
    }

    [Fact]
    public async Task One_bad_file_does_not_block_its_batch()
    {
        Write("c.txt", "c");
        var r = await Run(_fake.Set("FAKE_FAIL", "a.txt").Client()); // a.txt and c.txt share a batch
        Assert.Equal(2, r.Uploaded); // c.txt + sub/b.txt
        Assert.Contains("a.txt", Assert.Single(r.Errors));
        var known = _state.GetFolder(Path.GetFullPath(_src), "/my-files/Backup");
        Assert.Contains("c.txt", known.Keys);
        Assert.DoesNotContain("a.txt", known.Keys);
    }

    [Fact]
    public async Task Changing_the_remote_uploads_again()
    {
        await Run();
        _cfg = _cfg with { Folders = [new FolderPair(_src, "/my-files/Elsewhere")] };
        Assert.Equal(2, (await Run()).Uploaded);
    }

    [Fact]
    public async Task Signed_out_stops_the_run_with_auth_exception()
    {
        await Assert.ThrowsAsync<AuthException>(() => Run(_fake.Set("FAKE_SIGNED_OUT", "1").Client()));
        Assert.Equal(0, _state.Count());
    }

    [Fact]
    public async Task Missing_folder_is_an_error_not_a_crash()
    {
        _cfg = _cfg with { Folders = [.. _cfg.Folders, new FolderPair(_tmp["gone"], "/my-files/Gone")] };
        var r = await Run();
        Assert.Equal(2, r.Uploaded);
        Assert.Contains("Missing local folder", Assert.Single(r.Errors));
    }

    [Fact]
    public async Task Progress_reports_totals()
    {
        var seen = new List<SyncProgress>();
        await Run(progress: new InlineProgress<SyncProgress>(seen.Add));
        var last = seen[^1];
        Assert.Equal((2, 2), (last.FilesDone, last.FilesTotal));
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Contains(seen, p => p.CliLine?.StartsWith("Transfer summary") == true);
    }

    [Fact]
    public async Task Conflict_strategy_from_config_reaches_the_cli()
    {
        await Run(_fake.Client(conflictStrategy: "replace"));
        Assert.Contains(["strategy", "replace", "merge"], _fake.Log());
    }

    [Fact]
    public async Task Legacy_state_import_means_nothing_is_uploaded_again()
    {
        // Python stored mtime in nanoseconds; add sub-100ns noise that .NET can't see.
        string Sig(string rel)
        {
            var f = new FileInfo(Path.Combine(_src, rel));
            var ns = (f.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100 + 37;
            return $"[{ns}, {f.Length}]";
        }
        var src = Path.GetFullPath(_src);
        File.WriteAllText(_tmp["state.json"], $$"""
            {"{{src}}::a.txt": {{Sig("a.txt")}}, "{{src}}::sub/b.txt": {{Sig("sub/b.txt")}}, "/not/configured::x": [1, 2]}
            """);
        Assert.Equal(2, _state.ImportLegacy(_tmp["state.json"], _cfg));
        Assert.Null(_state.ImportLegacy(_tmp["state.json"], _cfg)); // only once
        Assert.Equal(0, (await Run()).Uploaded);
    }
}

public sealed class RemoteDirTests
{
    [Theory]
    [InlineData("", "/my-files/B")]
    [InlineData("a/b", "/my-files/B/a/b")]
    [InlineData("we\\ird", "/my-files/B/we\\ird")] // a backslash in a local name is kept as is
    public void Maps_local_folders_to_remote_paths(string relDir, string expected) =>
        Assert.Equal(expected, SyncEngine.RemoteDirFor("/my-files/B", relDir));
}

public sealed class ChunkerTests
{
    [Fact]
    public void Splits_by_count_and_bytes()
    {
        var sizes = new Dictionary<string, long> { ["a"] = 1, ["B"] = Chunker.MaxBytes, ["c"] = 1 };
        Assert.Equal([["a"], ["B"], ["c"]], Chunker.Chunks(["a", "B", "c"], r => sizes[r]));
        var many = Enumerable.Range(0, Chunker.MaxFiles + 1).Select(i => i.ToString()).ToList();
        Assert.Equal([Chunker.MaxFiles, 1], Chunker.Chunks(many, _ => 1).Select(c => c.Count));
    }
}

public sealed class ScannerTests
{
    [Fact]
    public void Skips_symlinks_excluded_folders_and_uses_forward_slashes()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp["a/b"]);
        Directory.CreateDirectory(tmp[".git"]);
        File.WriteAllText(tmp["a/b/f.txt"], "x");
        File.WriteAllText(tmp[".git/config"], "x");
        File.CreateSymbolicLink(tmp["link.txt"], tmp["a/b/f.txt"]);
        Directory.CreateSymbolicLink(tmp["dirlink"], tmp["a"]);
        var r = Scanner.Scan(tmp.Path, SyncConfig.DefaultExclude, TimeSpan.Zero);
        Assert.Equal(["a/b/f.txt"], r.Files.Keys);
    }
}

using Pdrive.Tests;

namespace Pdrive.Cli.Tests;

/// <summary>The new CLI operations against the persistent fake Drive.</summary>
public sealed class TreeModeTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ProtonDriveCli _cli;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TreeModeTests() => _cli = new FakeCli(_tmp["log"]).UseTree(_tmp["tree.json"]).Client();

    public void Dispose() => _tmp.Dispose();

    private string Local(string name, string content, DateTime? mtime = null)
    {
        var p = _tmp[name];
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, mtime ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        return p;
    }

    [Fact]
    public async Task Upload_list_info_carry_uid_revision_time_and_sha1()
    {
        await _cli.MkdirAsync("/my-files/A", Ct);
        await _cli.UploadAsync([Local("f.txt", "one")], "/my-files/A", ct: Ct);
        var e = Assert.Single(await _cli.ListAsync("/my-files/A", Ct));
        Assert.Equal("/my-files/A/f.txt", e.Path);
        Assert.NotNull(e.Uid);
        Assert.NotNull(e.RevisionUid);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), e.ModifiedAt);
        Assert.Equal("fe05bcdcdc4928012781a5f1a2a77cbb5398e106", e.Sha1); // sha1("one")
        var info = await _cli.InfoAsync("/my-files/A/f.txt", Ct);
        Assert.Equal(e.Uid, info.Uid);
        Assert.Equal(e.RevisionUid, info.RevisionUid);
        Assert.Equal("/my-files/A/f.txt", info.Path);
    }

    [Fact]
    public async Task Same_content_is_skipped_and_new_content_gets_a_new_revision()
    {
        var f = Local("f.txt", "one");
        await _cli.UploadAsync([f], "/my-files", ct: Ct);
        var first = await _cli.InfoAsync("/my-files/f.txt", Ct);
        await _cli.UploadAsync([f], "/my-files", ct: Ct);
        Assert.Equal(first.RevisionUid, (await _cli.InfoAsync("/my-files/f.txt", Ct)).RevisionUid);
        Local("f.txt", "two");
        await _cli.UploadAsync([f], "/my-files", ct: Ct);
        var second = await _cli.InfoAsync("/my-files/f.txt", Ct);
        Assert.Equal(first.Uid, second.Uid);
        Assert.NotEqual(first.RevisionUid, second.RevisionUid);
    }

    [Fact]
    public async Task Rename_move_and_trash_keep_uids()
    {
        await _cli.MkdirAsync("/my-files/A", Ct);
        await _cli.MkdirAsync("/my-files/B", Ct);
        await _cli.UploadAsync([Local("f.txt", "x")], "/my-files/A", ct: Ct);
        var uid = (await _cli.InfoAsync("/my-files/A/f.txt", Ct)).Uid;

        await _cli.RenameAsync("/my-files/A/f.txt", "g.txt", Ct);
        await _cli.MoveAsync(["/my-files/A/g.txt"], "/my-files/B", Ct);
        Assert.Equal(uid, (await _cli.InfoAsync("/my-files/B/g.txt", Ct)).Uid);
        Assert.Empty(await _cli.ListAsync("/my-files/A", Ct));

        await _cli.TrashAsync(["/my-files/B/g.txt"], Ct);
        Assert.Empty(await _cli.ListAsync("/my-files/B", Ct));
        var e = await Assert.ThrowsAsync<DriveException>(() => _cli.InfoAsync("/my-files/B/g.txt", Ct));
        Assert.Contains("Node not found", e.Message);
    }

    [Fact]
    public async Task Folder_upload_and_download_are_recursive()
    {
        Directory.CreateDirectory(_tmp["src/sub"]);
        Local("src/a.txt", "a");
        Local("src/sub/b.txt", "b");
        await _cli.UploadAsync([_tmp["src"]], "/my-files", ct: Ct);
        Assert.Single(await _cli.ListAsync("/my-files/src/sub", Ct));

        Directory.CreateDirectory(_tmp["out"]);
        await _cli.DownloadAsync("/my-files/src", _tmp["out"], ct: Ct);
        Assert.Equal("b", File.ReadAllText(_tmp["out/src/sub/b.txt"]));
        await _cli.DownloadAsync("/my-files/src/a.txt", _tmp["out/src"], ct: Ct);
        Assert.True(File.Exists(_tmp["out/src/a (1).txt"])); // -f rename never overwrites
    }

    [Fact]
    public async Task Names_with_slashes_resolve_through_escaped_paths()
    {
        await _cli.MkdirAsync("/my-files/a\\/b", Ct);
        var e = Assert.Single(await _cli.ListAsync("/my-files", Ct));
        Assert.Equal("a/b", e.Name);
        Assert.Equal("/my-files/a\\/b", e.Path);
        await _cli.UploadAsync([Local("f.txt", "x")], e.Path, ct: Ct);
        Assert.Single(await _cli.ListAsync(e.Path, Ct));
    }
}

public sealed class OptionInjectionTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-rf")]
    public async Task Names_that_look_like_options_are_passed_after_the_end_of_options(string name)
    {
        using var tmp = new TempDir();
        var cli = new FakeCli(tmp["log"]).UseTree(tmp["tree.json"]).Client();
        var ct = TestContext.Current.CancellationToken;
        await cli.MkdirAsync("/my-files/" + name, ct);
        await cli.MkdirAsync("/my-files/x", ct);
        await cli.RenameAsync("/my-files/x", name + "2", ct);
        var names = (await cli.ListAsync("/my-files", ct)).Select(e => e.Name).ToList();
        Assert.Contains(name, names);
        Assert.Contains(name + "2", names);
    }
}

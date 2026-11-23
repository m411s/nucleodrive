using NucleoDrive.Cli;
using NucleoDrive.Tests;

namespace NucleoDrive.Cli.Tests;

public sealed class ListingParserTests
{
    [Fact]
    public void Bad_json_throws() => Assert.Throws<DriveException>(() => ListingParser.Parse("not json", "/"));

    [Fact]
    public void Unrecognised_object_throws_instead_of_looking_empty() =>
        Assert.Throws<DriveException>(() => ListingParser.Parse("""{"error": "something"}""", "/my-files"));

    [Fact]
    public void Bare_strings_become_files()
    {
        var es = ListingParser.Parse("""["b", "A"]""", "/my-files");
        Assert.Equal(["A", "b"], es.Select(e => e.Name));
        Assert.All(es, e => Assert.False(e.IsDir));
        Assert.Equal("/my-files/A", es[0].Path);
    }

    [Theory]
    [InlineData("""{"name": "x", "isFolder": true}""", true)]
    [InlineData("""{"name": "x", "is_dir": 1}""", true)]
    [InlineData("""{"name": "x", "nodeType": "Directory"}""", true)]
    [InlineData("""{"name": "x", "kind": "FOLDER"}""", true)]
    [InlineData("""{"name": "x", "isFolder": "false"}""", false)]
    [InlineData("""{"name": "x", "type": "file"}""", false)]
    public void Folder_detection(string item, bool isDir) =>
        Assert.Equal(isDir, ListingParser.Parse($"[{item}]", "/").Single().IsDir);

    [Fact]
    public void Wrapped_lists_explicit_paths_and_odd_sizes()
    {
        var es = ListingParser.Parse("""
            {"data": [
              {"Name": "a", "size": "12", "fullPath": "/my-files/elsewhere/a"},
              {"title": "b", "claimedSize": 5},
              {"name": null},
              42
            ]}
            """, "/my-files/x");
        Assert.Equal(2, es.Count);
        Assert.Null(es[0].Size);
        Assert.Equal("/my-files/elsewhere/a", es[0].Path);
        Assert.Equal(5, es[1].Size);
        Assert.Equal("/my-files/x/b", es[1].Path);
    }
}

public sealed class AuthHeuristicsTests
{
    [Theory]
    [InlineData("You need to login first", true)] // cli-drive 0.9.0, signed out
    [InlineData("not logged in: run auth login", true)]
    [InlineData("Error: session expired, please sign in again", true)]
    [InlineData("401 Unauthorized", true)]
    [InlineData("network error: connection refused", false)]
    [InlineData("boom", false)]
    [InlineData("", false)]
    public void Classifies(string msg, bool auth) => Assert.Equal(auth, AuthHeuristics.LooksLikeAuthError(msg));
}

public sealed class TransferTimeoutTests
{
    [Fact]
    public void Scales_with_size()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), TransferTimeout.For(0));
        Assert.True(TransferTimeout.For(10L * 1024 * 1024 * 1024) > TimeSpan.FromSeconds(40000)); // 10 GB ≈ 11 h
    }

    [Fact]
    public void LocalSize_counts_files_and_folders_but_not_symlinks()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp["d/sub"]);
        File.WriteAllBytes(tmp["d/sub/a"], new byte[100]);
        File.WriteAllBytes(tmp["b"], new byte[20]);
        File.CreateSymbolicLink(tmp["d/link"], tmp["b"]);
        Assert.Equal(120, TransferTimeout.LocalSize([tmp["d"], tmp["b"], tmp["missing"]]));
    }
}

public sealed class BinaryLocatorTests
{
    [Fact]
    public void Env_var_wins_when_executable_and_non_executables_are_skipped()
    {
        using var tmp = new TempDir();
        var exe = tmp["proton-drive"];
        File.WriteAllText(exe, "#!/bin/sh\n");
        var env = new Dictionary<string, string?> { ["PROTON_DRIVE_BIN"] = exe, ["PATH"] = "", ["HOME"] = tmp.Path };

        Assert.Null(BinaryLocator.Find(k => env.GetValueOrDefault(k))); // not executable yet
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(exe, BinaryLocator.Find(k => env.GetValueOrDefault(k)));

        env["PROTON_DRIVE_BIN"] = null;
        env["PATH"] = tmp.Path;
        Assert.Equal(exe, BinaryLocator.Find(k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public void Broken_symlinks_are_skipped_not_thrown()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp["bin"]);
        File.CreateSymbolicLink(Path.Combine(tmp["bin"], "proton-drive"), tmp["gone/proton-drive"]);
        var env = new Dictionary<string, string?> { ["PATH"] = tmp["bin"], ["HOME"] = tmp.Path };

        Assert.Null(BinaryLocator.Find(k => env.GetValueOrDefault(k)));
    }
}

public sealed class RemotePathTests
{
    [Theory]
    [InlineData("/my-files", "a", "/my-files/a")]
    [InlineData("/my-files/", "a", "/my-files/a")]
    [InlineData("/my-files", "", "/my-files")]
    [InlineData("/", "a", "/a")]
    public void Join(string parent, string part, string expected) => Assert.Equal(expected, RemotePath.Join(parent, part));

    [Theory]
    [InlineData("/my-files/a/b", "/my-files/a")]
    [InlineData("/my-files", "/")]
    [InlineData("a/b", "a")]
    [InlineData("a", "")]
    public void Parent(string path, string expected) => Assert.Equal(expected, RemotePath.Parent(path));

    [Fact]
    public void Names_with_slashes_are_escaped_the_way_the_cli_expects()
    {
        Assert.Equal("foo\\/bar", RemotePath.EscapeName("foo/bar"));
        Assert.Equal("foo/bar", RemotePath.UnescapeName("foo\\/bar"));
        Assert.Equal("/my-files", RemotePath.Parent("/my-files/foo\\/bar"));
        Assert.Equal("foo\\/bar", RemotePath.Name("/my-files/foo\\/bar"));
        var e = ListingParser.Parse("""[{"name": "a/b", "type": "file"}]""", "/my-files").Single();
        Assert.Equal("/my-files/a\\/b", e.Path);
    }
}

/// <summary>Formats observed from the real CLI (cli-drive 0.9.0).</summary>
public sealed class RealCliFormatTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parses_a_real_listing()
    {
        var es = ListingParser.Parse(Fixture("list-folder.json"), "/my-files/nucleodrive-test");
        Assert.Equal(["sub", "back\\slash.txt", "big.bin", "hello.txt"], es.Select(e => e.Name));
        Assert.True(es[0].IsDir);
        Assert.Null(es[0].Size);
        // claimedSize (content), not totalStorageSize (all revisions + encryption overhead: 190)
        Assert.Equal(21, es.Single(e => e.Name == "hello.txt").Size);
        Assert.Equal(40_000_000, es.Single(e => e.Name == "big.bin").Size);
        Assert.Equal("/my-files/nucleodrive-test/back\\slash.txt", es[1].Path);
        Assert.Equal("text/plain;charset=utf-8", es[3].MediaType);
        Assert.Equal("VOLUME~NODE08", es[3].Uid);
        Assert.Equal("VOLUME~NODE09", es[3].RevisionUid);
        Assert.Equal(DateTimeOffset.Parse("2026-11-17T09:40:18.176Z", System.Globalization.CultureInfo.InvariantCulture), es[3].ModifiedAt);
        Assert.Equal("<sha1>", es[3].Sha1);
        Assert.False(es[3].IsProtonDocument);
    }

    [Fact]
    public void Undecryptable_names_fall_back_to_the_uid_in_paths()
    {
        var e = ListingParser.Parse("""
            [{"uid": "VOL~NODEABCDEFGHIJ", "name": {"ok": false, "error": {"message": "x"}}, "type": "file"}]
            """, "/my-files").Single();
        Assert.Equal("/my-files/VOL~NODEABCDEFGHIJ", e.Path);
        Assert.StartsWith("(name unavailable) NODEABCD", e.Name);
    }

    [Fact]
    public void Proton_documents_are_recognised()
    {
        var e = ListingParser.Parse("""
            [{"uid": "V~N", "name": {"ok": true, "value": "Notes"}, "type": "file", "mediaType": "application/vnd.proton.doc"}]
            """, "/my-files").Single();
        Assert.True(e.IsProtonDocument);
    }

    [Fact]
    public void Partial_upload_failure_puts_the_failing_file_first()
    {
        var msg = CliErrorText.From(
            "Transfer summary:\n  Uploaded: 0 items (0 B)\n  Failed: 1 items\n  - noread.txt: Error: EACCES: permission denied, open '/home/user/noread.txt'\n",
            "1 item(s) failed to upload\n", 1);
        Assert.Equal("noread.txt: Error: EACCES: permission denied, open '/home/user/noread.txt'", msg.Split('\n')[0]);
        Assert.Contains("1 item(s) failed to upload", msg);
    }

    [Fact]
    public void Crash_with_only_a_rule_on_stderr_uses_the_error_from_stdout()
    {
        var msg = CliErrorText.From("""
            Transfer summary:
              Uploaded: 0 items (0 B)
            157 |         const stats = await lstat(absolutePath);
                                              ^
            ENOENT: no such file or directory, lstat '/home/user/missing.txt'
                path: "/home/user/missing.txt",
                  at async enqueueLocalPath (src/commands/fileSystem/transferQueue.ts:157:29)
            """, "===============================================\n", 1);
        Assert.Equal("ENOENT: no such file or directory, lstat '/home/user/missing.txt'", msg);
    }

    [Theory]
    [InlineData("", "You need to login first\n", 1, "You need to login first")]
    [InlineData("", "Node not found: nope.txt\n", 1, "Node not found: nope.txt")]
    [InlineData("", "", 3, "exit code 3")]
    public void Plain_errors(string stdout, string stderr, int code, string expected) =>
        Assert.Equal(expected, CliErrorText.From(stdout, stderr, code));
}

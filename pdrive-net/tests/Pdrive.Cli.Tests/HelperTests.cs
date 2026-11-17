using Pdrive.Cli;
using Pdrive.Tests;

namespace Pdrive.Cli.Tests;

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
}

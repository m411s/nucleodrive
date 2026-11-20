using NucleoDrive.Sync.TwoWay;
using NucleoDrive.Tests;

namespace NucleoDrive.Sync.Tests;

/// <summary>Things a remote (or a local mistake) must never be able to do.</summary>
public sealed class SecurityTests : IDisposable
{
    private readonly TwoWayFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_local_symlink_is_never_written_through()
    {
        await _f.SyncedStart();
        var outside = _f.Tmp["outside"]; // e.g. ~/.ssh
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(_f.LocalPath("Docs/link"), outside);
        await _f.WriteRemote("Docs/link/authorized_keys", "attacker key"); // a Drive folder with the same name

        await _f.Full();
        await _f.Full();
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task Undecryptable_remote_names_stay_online_only()
    {
        var client = new ListingStub("""
            [{"uid": "V~ok", "name": {"ok": true, "value": "fine.txt"}, "type": "file", "activeRevision": {"uid": "R1", "claimedSize": 1}},
             {"uid": "V~bad", "name": {"ok": false, "error": {}}, "type": "file", "activeRevision": {"uid": "R2", "claimedSize": 1}},
             {"uid": "V~dots", "name": {"ok": true, "value": ".."}, "type": "folder"}]
            """);
        var remote = await RemoteSnapshot.TakeAsync(client, "Docs", [], 1, TestContext.Current.CancellationToken);
        Assert.Equal(["Docs/fine.txt"], remote.Keys);
    }

    [Fact]
    public void Planner_never_plans_anything_under_a_symlink()
    {
        var plan = SyncPlanner.Plan(
            new Dictionary<string, BaseItem>(),
            new Dictionary<string, LocalItem> { ["d/link"] = new("d/link", false, 0, 0, 5, Ignored: true) },
            new Dictionary<string, RemoteItem>
            {
                ["d/link"] = new("d/link", true, "u1", null, 0, null, null),
                ["d/link/x"] = new("d/link/x", false, "u2", "r", 1, null, null),
            },
            new PlanOptions());
        Assert.True(plan.IsEmpty);
    }

    /// <summary>Returns the same listing for the folder and nothing below it.</summary>
    private sealed class ListingStub(string json) : NucleoDrive.Cli.IDriveClient
    {
        public Task<IReadOnlyList<NucleoDrive.Cli.DriveEntry>> ListAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(path == "/my-files/Docs" ? NucleoDrive.Cli.ListingParser.Parse(json, path) : (IReadOnlyList<NucleoDrive.Cli.DriveEntry>)[]);
        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task LoginAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MkdirAsync(string remotePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<NucleoDrive.Cli.DriveEntry> InfoAsync(string remotePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task TrashAsync(IReadOnlyList<string> remotePaths, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string remotePath, string newName, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAsync(IReadOnlyList<string> remotePaths, string targetParent, CancellationToken ct = default) => Task.CompletedTask;
    }
}

using Pdrive.App.Services;
using Pdrive.Cli;
using Pdrive.Ipc;

namespace Pdrive.App.Tests;

/// <summary>In-memory Drive. Listings can be held back with <see cref="Hold"/> to test races.</summary>
public sealed class FakeDrive : IDriveClient
{
    public Dictionary<string, List<DriveEntry>> Tree { get; } = new()
    {
        ["/my-files"] = [new("Docs", true, null, "/my-files/Docs"), new("a.txt", false, 2048, "/my-files/a.txt")],
        ["/my-files/Docs"] = [new("Inner", true, null, "/my-files/Docs/Inner")],
        ["/my-files/Docs/Inner"] = [],
    };

    public Dictionary<string, TaskCompletionSource> Holds { get; } = new();
    public Exception? ListError { get; set; }
    public TaskCompletionSource? UploadGate { get; set; }
    public List<(IReadOnlyList<string> Paths, string Dest)> Uploads { get; } = [];
    public List<(string Remote, string Local, long? Size)> Downloads { get; } = [];
    public int Logins { get; private set; }

    public TaskCompletionSource Hold(string path) => Holds[path] = new TaskCompletionSource();

    public async Task<IReadOnlyList<DriveEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        if (Holds.TryGetValue(path, out var hold))
            await hold.Task;
        if (ListError is { } e)
            throw e;
        return Tree.TryGetValue(path, out var list) ? list : [];
    }

    public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(ListError is not AuthException);

    public Task LoginAsync(CancellationToken ct = default)
    {
        Logins++;
        ListError = null;
        return Task.CompletedTask;
    }

    public Task MkdirAsync(string remotePath, CancellationToken ct = default) => Task.CompletedTask;

    public int Logouts { get; private set; }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        Logouts++;
        ListError = new AuthException("You need to login first");
        return Task.CompletedTask;
    }

    public async Task UploadAsync(IReadOnlyList<string> localPaths, string remoteDir, IProgress<string>? output = null, CancellationToken ct = default)
    {
        Uploads.Add((localPaths, remoteDir));
        if (UploadGate is { } gate)
            await gate.Task;
    }

    public Task DownloadAsync(string remotePath, string localDir, long? size = null, IProgress<string>? output = null, CancellationToken ct = default)
    {
        Downloads.Add((remotePath, localDir, size));
        return Task.CompletedTask;
    }

    public Task<DriveEntry> InfoAsync(string remotePath, CancellationToken ct = default) =>
        Task.FromResult(Tree.Values.SelectMany(l => l).First(e => e.Path == remotePath));

    public Task TrashAsync(IReadOnlyList<string> remotePaths, CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameAsync(string remotePath, string newName, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveAsync(IReadOnlyList<string> remotePaths, string targetParent, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeDialogs : IDialogs
{
    public IReadOnlyList<string> Files { get; set; } = [];
    public string? Folder { get; set; }

    public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult(Files);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folder);

    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Confirmations { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel")
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }
}

public sealed class FakeToaster : IToaster
{
    public List<(string Message, bool Error)> Shown { get; } = [];
    public void Show(string message, bool error = false) => Shown.Add((message, error));
}

public sealed class FakeDaemon : IDaemonControl
{
    public DaemonStatus Status { get; private set; } = DaemonStatus.NotRunning;
    public event Action<DaemonStatus>? StatusChanged;
    public List<string> Calls { get; } = [];
    public List<FolderInfo> Folders { get; } = [new("/home/me/Docs", "/my-files/Docs")];
    public Exception? Fail { get; set; }

    public void Publish(DaemonStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    public static DaemonStatus With(DaemonState state, string message = "", TransferProgress? progress = null) =>
        new(state, message, null, progress, [], new Dictionary<string, string>(), 1);

    private Task Record(string call)
    {
        if (Fail is { } e)
            throw e;
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public Task SyncNowAsync() => Record("sync");
    public Task PauseAsync() => Record("pause");
    public Task ResumeAsync() => Record("resume");
    public Task<FolderInfo[]> ListFoldersAsync() => Task.FromResult(Folders.ToArray());
    public Task AddFolderAsync(string local, string remote) => Record($"add {local} {remote}");
    public Task RemoveFolderAsync(string local) => Record($"remove {local}");

    public string[] SyncFolders { get; set; } = ["Docs"];
    public Task<SyncSettingsInfo> GetSyncSettingsAsync() => Task.FromResult(new SyncSettingsInfo("/home/me/proton-drive", SyncFolders, 15));

    public async Task SetSyncFoldersAsync(string[] folders)
    {
        await Record("sync-folders " + string.Join(',', folders));
        SyncFolders = folders;
    }

    public Task ConfirmDeletionsAsync() => Record("confirm");

    public bool Running { get; set; } = true;
    public string SyncLocal { get; set; } = "/home/me/proton-drive";

    public async Task SetSyncLocalAsync(string local)
    {
        if (!Running)
            throw new DaemonException("The backup daemon isn't running.");
        await Record("sync-local " + local);
        SyncLocal = local;
    }

    public static DaemonStatus WithSync(SyncState state, string message = "", int held = 0, params string[] folders) =>
        With(DaemonState.Idle, "No folders configured") with
        {
            Sync = new SyncInfo(state, message, "/home/me/proton-drive", folders.Length > 0 ? folders : ["Docs"], null, held, []),
        };
}

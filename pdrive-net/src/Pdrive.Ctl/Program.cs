// pdrivectl: control the Proton Drive backup daemon (port of syncd.py's control commands).
// Talks to the daemon over its socket; when the daemon isn't running, folder and pause changes
// are written directly to config.json / state.db and take effect when it starts.
using System.CommandLine;
using Pdrive.Ipc;
using Pdrive.Sync;

var paths = PdrivePaths.FromEnvironment();

var localArg = new Argument<string>("local") { Description = "Local folder, e.g. ~/Documents" };
var remoteArg = new Argument<string>("remote") { Description = "Drive folder, e.g. /my-files/Documents" };
var removeArg = new Argument<string>("local") { Description = "Local folder to stop backing up" };

var status = new Command("status", "Show what the daemon is doing");
var list = new Command("list", "List backed-up folders");
var add = new Command("add", "Back up a folder") { localArg, remoteArg };
var remove = new Command("remove", "Stop backing up a folder (nothing is deleted on Drive)") { removeArg };
var now = new Command("now", "Sync now");
var pause = new Command("pause", "Pause backups");
var resume = new Command("resume", "Resume backups");

status.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    var s = client is null ? DaemonStatus.NotRunning : await client.GetStatusAsync(ct);
    Console.WriteLine($"{s.State.ToString().ToLowerInvariant()}: {s.Message}");
    if (client is null)
        return 3;
    Console.WriteLine($"last sync: {Units.Ago(s.LastSync)}");
    foreach (var e in s.RecentErrors.Skip(1).Take(10))
        Console.WriteLine($"  error: {e}");
    foreach (var (folder, reason) in s.Unwatched)
        Console.WriteLine($"  not watched (interval scan only): {folder} ({reason})");
    return 0;
});

list.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    var folders = client is not null
        ? (await client.ListFoldersAsync(ct)).Select(f => (f.Local, f.Remote))
        : new ConfigStore(paths).Load().Folders.Select(f => (f.Local, f.Remote));
    foreach (var (l, r) in folders)
        Console.WriteLine($"{l} -> {r}");
    return 0;
});

add.SetAction(async (pr, ct) =>
{
    var local = pr.GetValue(localArg)!;
    var remote = pr.GetValue(remoteArg)!;
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is not null)
        await client.AddFolderAsync(local, remote, ct);
    else
        new ConfigStore(paths).Update(c => FolderRules.Add(c, local, remote));
    Console.WriteLine($"Added {Path.GetFullPath(PdrivePaths.ExpandHome(local))} -> {remote.TrimEnd('/')}");
    return 0;
});

remove.SetAction(async (pr, ct) =>
{
    var local = pr.GetValue(removeArg)!;
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is not null)
        await client.RemoveFolderAsync(local, ct);
    else
        new ConfigStore(paths).Update(c => FolderRules.Remove(c, local));
    Console.WriteLine($"Removed {local} (files already on Drive are kept)");
    return 0;
});

now.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is null)
    {
        Console.Error.WriteLine("Daemon doesn't appear to be running.");
        return 3;
    }
    await client.SyncNowAsync(ct);
    return 0;
});

// ---- two-way sync -------------------------------------------------------------------------------
var syncFolderArg = new Argument<string>("folder") { Description = "Drive folder relative to /my-files, e.g. Photos or Documents/Work" };
var syncStatus = new Command("status", "Show the sync folder's state");
var syncList = new Command("list", "List the Drive folders that are synced");
var syncAdd = new Command("add", "Start syncing a Drive folder into the local sync folder") { syncFolderArg };
var syncRemove = new Command("remove", "Stop syncing a Drive folder (nothing is deleted on either side)") { syncFolderArg };
var syncNow = new Command("now", "Check Drive for changes now");
var syncConfirm = new Command("confirm", "Let held-back deletions go through");
var sync = new Command("sync", "Two-way sync of chosen Drive folders into ~/proton-drive")
    { syncStatus, syncList, syncAdd, syncRemove, syncNow, syncConfirm };

syncStatus.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is null)
    {
        Console.WriteLine("stopped: Backup daemon not running");
        return 3;
    }
    var s = (await client.GetStatusAsync(ct)).Sync;
    if (s is null)
    {
        Console.WriteLine("This daemon has no two-way sync.");
        return 1;
    }
    Console.WriteLine($"{s.State.ToString().ToLowerInvariant()}: {s.Message}");
    Console.WriteLine($"local folder: {s.LocalRoot}");
    Console.WriteLine($"last Drive check: {Units.Ago(s.LastFullScan)}");
    foreach (var e in s.Errors.Take(10))
        Console.WriteLine($"  problem: {e}");
    return 0;
});

syncList.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    var settings = client is not null ? await client.GetSyncSettingsAsync(ct) : LocalSyncSettings();
    foreach (var f in settings.Folders)
        Console.WriteLine($"/my-files/{f} <-> {Path.Combine(settings.Local, f)}");
    return 0;
});

syncAdd.SetAction((pr, ct) => ChangeSyncFolders(pr.GetValue(syncFolderArg)!, add: true, ct));
syncRemove.SetAction((pr, ct) => ChangeSyncFolders(pr.GetValue(syncFolderArg)!, add: false, ct));

syncNow.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is null)
    {
        Console.Error.WriteLine("Daemon doesn't appear to be running.");
        return 3;
    }
    await client.SyncNowAsync(ct);
    return 0;
});

syncConfirm.SetAction(async (_, ct) =>
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is null)
    {
        Console.Error.WriteLine("Daemon doesn't appear to be running.");
        return 3;
    }
    await client.ConfirmDeletionsAsync(ct);
    Console.WriteLine("Confirmed; the held deletions go through on the next pass.");
    return 0;
});

pause.SetAction((_, ct) => SetPaused(true, ct));
resume.SetAction((_, ct) => SetPaused(false, ct));

var root = new RootCommand("Control the Proton Drive backup and sync daemon") { status, list, add, remove, now, pause, resume, sync };

try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception e) when (e is DaemonException or ConfigException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

SyncSettingsInfo LocalSyncSettings()
{
    var s = new ConfigStore(paths).Load().Sync;
    return new SyncSettingsInfo(s.LocalPath, [.. s.Folders], s.RemoteIntervalMinutes);
}

async Task<int> ChangeSyncFolders(string folder, bool add, CancellationToken ct)
{
    folder = folder.Trim().Trim('/');
    if (folder.StartsWith("my-files/", StringComparison.Ordinal))
        folder = folder["my-files/".Length..];
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    var current = (client is not null ? await client.GetSyncSettingsAsync(ct) : LocalSyncSettings()).Folders.ToList();
    if (add == current.Contains(folder))
    {
        Console.Error.WriteLine(add ? $"{folder} is already synced." : $"{folder} isn't synced.");
        return 1;
    }
    var updated = add ? [.. current, folder] : current.Where(f => f != folder).ToArray();
    if (client is not null)
    {
        await client.SetSyncFoldersAsync(updated, ct);
    }
    else
    {
        new ConfigStore(paths).Update(c => c with { Sync = c.Sync with { Folders = updated } });
        if (!add)
        {
            using var state = new StateStore(paths.StateDb);
            state.ClearBaseline(folder);
        }
    }
    var root = (client is not null ? await client.GetSyncSettingsAsync(ct) : LocalSyncSettings()).Local;
    Console.WriteLine(add
        ? $"Syncing /my-files/{folder} <-> {Path.Combine(root, folder)}"
        : $"Stopped syncing /my-files/{folder}; files on both sides are kept.");
    return 0;
}

async Task<int> SetPaused(bool paused, CancellationToken ct)
{
    await using var client = await DaemonClient.TryConnectAsync(paths.DaemonSocket, ct);
    if (client is not null)
    {
        await (paused ? client.PauseAsync(ct) : client.ResumeAsync(ct));
    }
    else
    {
        using var state = new StateStore(paths.StateDb);
        state.Paused = paused;
        Console.Error.WriteLine("Daemon not running; the setting applies when it starts.");
    }
    return 0;
}

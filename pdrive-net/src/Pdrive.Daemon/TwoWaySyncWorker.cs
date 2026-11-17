using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pdrive.Cli;
using Pdrive.Ipc;
using Pdrive.Sync;
using Pdrive.Sync.TwoWay;

namespace Pdrive.Daemon;

/// <summary>Keeps the chosen Drive folders and ~/proton-drive in sync.
/// <list type="bullet">
/// <item>Local changes (inotify on the sync folder) → a quick local pass after a short quiet period.</item>
/// <item>Every <c>sync.remote_interval_minutes</c>, at start, on SyncNow/Resume or folder changes → a full pass,
/// the only way to see changes made on Drive elsewhere.</item>
/// </list>
/// Problems (missing folders, sign-in, network, held deletions) show in the status; the loop keeps running.</summary>
public sealed class TwoWaySyncWorker : BackgroundService
{
    private readonly PdrivePaths _paths;
    private readonly DaemonOptions _options;
    private readonly SyncWorker _backup;
    private readonly ILogger<TwoWaySyncWorker> _log;
    private readonly ConfigStore _configStore;
    private readonly StateStore _state;
    private readonly FreedesktopTrash _trash;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _fullRequested = true;
    private volatile bool _localDirty;
    private volatile bool _confirmRequested;
    private CancellationTokenSource? _runCts;
    private ChangeWatcher? _watcher;
    private string? _watcherKey;
    private DateTimeOffset _nextFull = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastFull;
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public TwoWaySyncWorker(PdrivePaths paths, DaemonOptions options, SyncWorker backup, ILogger<TwoWaySyncWorker> log)
    {
        _paths = paths;
        _options = options;
        _backup = backup;
        _log = log;
        _configStore = new ConfigStore(paths);
        _state = new StateStore(paths.StateDb);
        _trash = new FreedesktopTrash(options.TrashDataHome);
        backup.SyncNowRequested += RequestFull;
        backup.PauseChanged += paused =>
        {
            if (paused)
                lock (_gate)
                    _runCts?.Cancel();
            RequestFull();
        };
    }

    // ---- control (IPC) ---------------------------------------------------------------------------

    public SyncSettingsInfo GetSettings()
    {
        var s = LoadConfigOrDefault().Sync;
        return new SyncSettingsInfo(s.LocalPath, [.. s.Folders], s.RemoteIntervalMinutes);
    }

    /// <summary>Sets the synced Drive folders. Folders no longer listed stop syncing: their local files
    /// and Drive files are both left as they are, and their baseline is dropped.</summary>
    /// <exception cref="ConfigException">Invalid folder names, or config.json is broken.</exception>
    public void SetFolders(IReadOnlyList<string> folders)
    {
        var normalized = folders.Select(f => f.Trim().Trim('/')).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var before = LoadConfigOrDefault().Sync.Folders;
        _configStore.Update(c => c with { Sync = c.Sync with { Folders = normalized } });
        foreach (var removed in before.Except(normalized, StringComparer.Ordinal))
        {
            _state.ClearBaseline(removed);
            lock (_gate)
                _held.Remove(removed);
        }
        RequestFull();
    }

    /// <summary>Lets the held-back deletions go through on the next pass.</summary>
    public void ConfirmDeletions()
    {
        _confirmRequested = true;
        RequestFull();
    }

    private void RequestFull()
    {
        _fullRequested = true;
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    // ---- loop -----------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await Task.Yield();
        while (!stopping.IsCancellationRequested)
        {
            await CycleAsync(stopping);
            var delay = _nextFull - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
            try
            {
                await _wake.WaitAsync(delay, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CycleAsync(CancellationToken stopping)
    {
        SyncConfig cfg;
        try
        {
            cfg = _configStore.Load();
        }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException)
        {
            Publish(SyncState.Error, e.Message, new TwoWaySettings(), []);
            _nextFull = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
            return;
        }
        var sync = cfg.Sync;
        if (sync.Folders.Count == 0)
        {
            UpdateWatcher(null, cfg);
            Publish(SyncState.Off, "No Drive folders chosen for sync", sync, []);
            _nextFull = DateTimeOffset.MaxValue;
            _fullRequested = false;
            return;
        }
        if (_state.Paused)
        {
            Publish(SyncState.Paused, "Paused", sync, []);
            _nextFull = DateTimeOffset.UtcNow + sync.RemoteInterval;
            return;
        }

        var full = _fullRequested || DateTimeOffset.UtcNow >= _nextFull;
        var local = _localDirty;
        if (!full && !local)
            return;
        _fullRequested = false;
        _localDirty = false;
        var confirm = _confirmRequested;
        _confirmRequested = false;

        Directory.CreateDirectory(sync.LocalPath);
        UpdateWatcher(sync, cfg);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lock (_gate)
            _runCts = runCts;

        var errors = new List<string>();
        int changes = 0, heldTotal = 0;
        var anyFullOk = false;
        try
        {
            var client = _options.ClientFactory(cfg);
            foreach (var folder in sync.Folders)
            {
                runCts.Token.ThrowIfCancellationRequested();
                var progress = new InlineProgress<string>(m => Publish(SyncState.Syncing, m, sync, errors, throttle: true));
                Publish(SyncState.Syncing, full ? $"Checking {folder}…" : $"Syncing local changes in {folder}…", sync, errors);
                try
                {
                    var engine = new TwoWaySyncEngine(client, _state, _trash, change => _log.LogInformation("{Folder}: {Change}", folder, change));
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    var result = full
                        ? await engine.FullPassAsync(sync, cfg, folder, confirm, progress, runCts.Token)
                        : await engine.LocalPassAsync(sync, cfg, folder, confirm, progress, runCts.Token);
                    if (full || result.Changes > 0)
                        _log.LogInformation("{Folder}: {Kind} in {Seconds:0} s: {Summary}", folder,
                            full ? "checked Drive" : "synced local changes", started.Elapsed.TotalSeconds, Summarize(result));
                    changes += result.Changes;
                    errors.AddRange(result.Errors.Select(e => $"{folder}/{e}"));
                    lock (_gate)
                    {
                        if (result.DeletionsHeld)
                            _held.Add(folder);
                        else
                            _held.Remove(folder);
                    }
                    heldTotal += result.HeldLocalDeletes + result.HeldRemoteDeletes;
                    anyFullOk |= full;
                }
                catch (SyncFolderException e)
                {
                    errors.Add(e.Message);
                }
                catch (IncompleteSnapshotException e)
                {
                    errors.Add(e.Message);
                }
            }

            if (full && anyFullOk)
                _lastFull = DateTimeOffset.UtcNow;
            if (heldTotal > 0)
                Publish(SyncState.NeedsConfirmation,
                    $"{heldTotal} deletions are waiting for your confirmation (more than {sync.MaxDeletesWithoutConfirm} at once)", sync, errors, held: heldTotal);
            else if (errors.Count > 0)
                Publish(SyncState.Error, $"{errors.Count} problem(s): {errors[0]}", sync, errors);
            else
                Publish(SyncState.Idle, changes > 0 ? $"Up to date ({changes} change(s) synced)" : "Up to date", sync, errors);
            foreach (var e in errors)
                _log.LogWarning("Sync: {Error}", e);
        }
        catch (AuthException)
        {
            Publish(SyncState.Login, "Sign in required (open Proton Drive)", sync, errors);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            Publish(_state.Paused ? SyncState.Paused : SyncState.Idle, _state.Paused ? "Paused" : "Interrupted", sync, errors);
        }
        catch (OperationCanceledException)
        {
        }
        catch (DriveException e)
        {
            Publish(SyncState.Error, e.FirstLine, sync, errors);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Publish(SyncState.Error, e.Message, sync, errors);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Unexpected error in two-way sync");
            Publish(SyncState.Error, $"Unexpected: {e.Message}", sync, errors);
        }
        finally
        {
            lock (_gate)
                _runCts = null;
            if (full)
                _nextFull = DateTimeOffset.UtcNow + sync.RemoteInterval;
        }
    }

    private static string Summarize(PassResult r)
    {
        var parts = new List<string>();
        if (r.Uploaded > 0) parts.Add($"{r.Uploaded} uploaded");
        if (r.Downloaded > 0) parts.Add($"{r.Downloaded} downloaded");
        if (r.Moved > 0) parts.Add($"{r.Moved} renamed/moved");
        if (r.TrashedRemote > 0) parts.Add($"{r.TrashedRemote} moved to the Drive trash");
        if (r.TrashedLocal > 0) parts.Add($"{r.TrashedLocal} moved to the desktop Trash");
        if (r.DeletionsHeld) parts.Add($"{r.HeldLocalDeletes + r.HeldRemoteDeletes} deletions waiting for confirmation");
        if (r.Errors.Count > 0) parts.Add($"{r.Errors.Count} problem(s)");
        return parts.Count == 0 ? "up to date" : string.Join(", ", parts);
    }

    private void UpdateWatcher(TwoWaySettings? sync, SyncConfig cfg)
    {
        var quiet = _options.WatchQuiet ?? (cfg.MinAge > TimeSpan.FromSeconds(3) ? cfg.MinAge : TimeSpan.FromSeconds(3));
        var key = sync is null ? "off" : $"{sync.LocalPath}|{quiet}|{string.Join('\n', cfg.Exclude)}";
        if (key == _watcherKey)
            return;
        _watcher?.Dispose();
        _watcher = null;
        _watcherKey = key;
        if (sync is null)
            return;
        _watcher = new ChangeWatcher([sync.LocalPath], [.. cfg.Exclude, SyncPaths.StagingDir], quiet, _options.WatchMaxDelay, _ =>
        {
            _localDirty = true;
            Wake();
        });
        foreach (var (folder, reason) in _watcher.Unwatched)
            _log.LogWarning("Not watching {Folder} ({Reason}); local changes are picked up by the periodic scan", folder, reason);
    }

    private void Publish(SyncState state, string message, TwoWaySettings sync, IReadOnlyList<string> errors, int held = 0, bool throttle = false) =>
        _backup.PublishSync(new SyncInfo(state, message, sync.LocalPath, [.. sync.Folders], _lastFull, held, [.. errors.Take(20)]), throttle);

    private SyncConfig LoadConfigOrDefault()
    {
        try
        {
            return _configStore.Load();
        }
        catch (ConfigException)
        {
            return new SyncConfig();
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _state.Dispose();
        _wake.Dispose();
        base.Dispose();
    }
}

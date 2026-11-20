using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using NucleoDrive.Cli;
using NucleoDrive.Ipc;
using NucleoDrive.Sync;

namespace NucleoDrive.Daemon;

/// <summary>The backup loop. Runs a cycle when something asks for one (interval timer, watcher, a
/// file becoming old enough, SyncNow/Resume/folder changes), and publishes status to IPC clients.
/// A broken config, a missing CLI or a sign-in problem shows up in the status; the loop keeps running.</summary>
public sealed class SyncWorker : BackgroundService
{
    private readonly NucleoDrivePaths _paths;
    private readonly DaemonOptions _options;
    private readonly ILogger<SyncWorker> _log;
    private readonly ISystemdNotifier? _notifier;
    private readonly ConfigStore _configStore;
    private readonly StateStore _state;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, byte> _dirtyFolders = new(StringComparer.Ordinal);
    private int _fullRequested = 1;
    private CancellationTokenSource? _runCts;
    private ChangeWatcher? _watcher;
    private string? _watcherKey;
    private DateTimeOffset _nextFullScan = DateTimeOffset.MinValue;
    private DateTimeOffset? _nextMature;
    private SyncConfig _cfg = new();
    private DaemonStatus _status;
    private DateTimeOffset _lastPush;
    private SyncInfo? _syncInfo;
    private bool _legacyChecked;

    public SyncWorker(NucleoDrivePaths paths, DaemonOptions options, ILogger<SyncWorker> log, ISystemdNotifier? notifier = null)
    {
        _paths = paths;
        _options = options;
        _log = log;
        _notifier = notifier;
        _configStore = new ConfigStore(paths);
        _state = new StateStore(paths.StateDb);
        _status = new DaemonStatus(DaemonState.Starting, "Starting…", _state.LastSync, null, [], new Dictionary<string, string>(), Environment.ProcessId);
    }

    public DaemonStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    /// <summary>Raised on every status change (progress updates are throttled).</summary>
    public event Action<DaemonStatus>? StatusChanged;

    // ---- control (called from IPC) -------------------------------------------------------

    public void SyncNow()
    {
        RequestFull();
        SyncNowRequested?.Invoke();
    }

    /// <summary>Raised by SyncNow so the two-way worker scans too.</summary>
    public event Action? SyncNowRequested;

    /// <summary>The two-way worker's status, merged into <see cref="Status"/> and pushed to clients.</summary>
    public void PublishSync(SyncInfo info, bool throttle = false)
    {
        DaemonStatus status;
        lock (_gate)
        {
            _syncInfo = info;
            var now = DateTimeOffset.UtcNow;
            var skip = throttle && _status.Sync?.State == info.State && now - _lastPush < _options.ProgressThrottle;
            _status = _status with { Sync = info };
            status = _status;
            if (skip)
                return;
            _lastPush = now;
        }
        StatusChanged?.Invoke(status);
    }

    /// <summary>Raised when backups/sync are paused (true) or resumed (false).</summary>
    public event Action<bool>? PauseChanged;

    public void Pause()
    {
        _state.Paused = true;
        PauseChanged?.Invoke(true);
        lock (_gate)
            _runCts?.Cancel();
        SetStatus(DaemonState.Paused, "Paused");
        Wake();
    }

    public void Resume()
    {
        _state.Paused = false;
        PauseChanged?.Invoke(false);
        RequestFull();
    }

    public IReadOnlyList<FolderPair> Folders
    {
        get
        {
            try
            {
                return _configStore.Load().Folders;
            }
            catch (ConfigException)
            {
                return _cfg.Folders;
            }
        }
    }

    /// <exception cref="ConfigException">Invalid folder, or config.json is broken.</exception>
    public void AddFolder(string local, string remote)
    {
        _configStore.Update(c => FolderRules.Add(c, local, remote));
        RequestFull();
    }

    /// <exception cref="ConfigException">Not backed up, or config.json is broken.</exception>
    public void RemoveFolder(string local)
    {
        _configStore.Update(c => FolderRules.Remove(c, local));
        RequestFull();
    }

    private void RequestFull()
    {
        Interlocked.Exchange(ref _fullRequested, 1);
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    // ---- loop -----------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await Task.Yield();
        while (!stopping.IsCancellationRequested)
        {
            await CycleAsync(stopping);
            if (!await WaitAsync(stopping))
                break;
        }
    }

    private async Task<bool> WaitAsync(CancellationToken stopping)
    {
        var next = _nextFullScan;
        if (_nextMature is { } m && m < next)
            next = m;
        var delay = next - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromDays(1))
            delay = TimeSpan.FromDays(1);
        try
        {
            await _wake.WaitAsync(delay, stopping);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CycleAsync(CancellationToken stopping)
    {
        var now = DateTimeOffset.UtcNow;
        var full = Interlocked.Exchange(ref _fullRequested, 0) == 1 || now >= _nextFullScan;
        if (_nextMature is { } m && m <= now)
        {
            full = true; // files that were too new to upload last time are old enough now
            _nextMature = null;
        }
        var folders = _dirtyFolders.Keys.ToList();
        foreach (var f in folders)
            _dirtyFolders.TryRemove(f, out _);
        if (!full && folders.Count == 0)
            return;

        try
        {
            _cfg = _configStore.Load();
            if (!_legacyChecked)
            {
                _legacyChecked = true;
                if (_state.ImportLegacy(_paths.LegacyStateFile, _cfg) is { } n)
                    _log.LogInformation("Imported {Count} entries from {File}", n, _paths.LegacyStateFile);
            }
            UpdateWatcher(_cfg);

            if (_state.Paused)
            {
                SetStatus(DaemonState.Paused, "Paused");
                return;
            }
            if (_cfg.Folders.Count == 0)
            {
                SetStatus(DaemonState.Idle, "No folders configured (nucleodrivectl add LOCAL REMOTE)");
                return;
            }

            var client = _options.ClientFactory(_cfg);
            if (full && !await client.IsLoggedInAsync(stopping))
            {
                SetStatus(DaemonState.Login, "Sign in required (open Proton Drive)");
                return;
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            lock (_gate)
                _runCts = runCts;
            SetStatus(DaemonState.Syncing, "Checking for changes…");
            var progress = new InlineProgress<SyncProgress>(p => SetStatus(DaemonState.Syncing,
                $"Uploading {p.FilesDone}/{p.FilesTotal} files ({Units.Bytes(p.BytesDone)} of {Units.Bytes(p.BytesTotal)})",
                new TransferProgress(p.FilesDone, p.FilesTotal, p.BytesDone, p.BytesTotal, p.RemoteDir),
                throttle: p.CliLine is not null));

            var result = await SyncEngine.RunOnceAsync(_cfg, client, _state, progress, full ? null : folders, runCts.Token);
            _nextMature = result.NextMatureAt;

            if (result.Cancelled)
            {
                if (_state.Paused)
                    SetStatus(DaemonState.Paused, "Paused");
                return;
            }
            _state.LastSync = DateTimeOffset.UtcNow;
            if (result.Errors.Count > 0)
            {
                SetStatus(DaemonState.Error, $"{result.Errors.Count} error(s): {result.Errors[0]}", errors: result.Errors);
                foreach (var e in result.Errors)
                    _log.LogWarning("Upload error: {Error}", e);
            }
            else
            {
                SetStatus(DaemonState.Idle, result.Uploaded > 0 ? $"Up to date ({result.Uploaded} uploaded)" : "Up to date");
            }
            if (result.Uploaded > 0)
                _log.LogInformation("Backup: uploaded {Count} new or changed file(s)", result.Uploaded);
        }
        catch (ConfigException e)
        {
            // keep running on the last good settings; the next cycle re-reads the file
            SetStatus(DaemonState.Error, e.Message);
            _log.LogError("{Error}", e.Message);
        }
        catch (AuthException)
        {
            SetStatus(DaemonState.Login, "Sign in required (open Proton Drive)");
        }
        catch (DriveException e)
        {
            SetStatus(DaemonState.Error, e.FirstLine);
            _log.LogError("{Error}", e.Message);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            SetStatus(DaemonState.Error, $"Unexpected: {e.Message}");
            _log.LogError(e, "Unexpected error in sync cycle");
        }
        finally
        {
            lock (_gate)
                _runCts = null;
            if (full)
                _nextFullScan = DateTimeOffset.UtcNow + _cfg.Interval;
        }
    }

    private void UpdateWatcher(SyncConfig cfg)
    {
        var quiet = _options.WatchQuiet ?? (cfg.MinAge > TimeSpan.FromSeconds(5) ? cfg.MinAge : TimeSpan.FromSeconds(5));
        var roots = cfg.Folders.Select(f => f.LocalPath).Order(StringComparer.Ordinal).ToList();
        var key = cfg.Watch ? $"{quiet}|{string.Join('\n', roots)}|{string.Join('\n', cfg.Exclude)}" : "off";
        if (key == _watcherKey)
            return;
        _watcher?.Dispose();
        _watcher = null;
        _watcherKey = key;
        if (!cfg.Watch || roots.Count == 0)
            return;
        _watcher = new ChangeWatcher(roots, cfg.Exclude, quiet, _options.WatchMaxDelay, changed =>
        {
            foreach (var f in changed)
                _dirtyFolders.TryAdd(f, 0);
            Wake();
        });
        foreach (var (folder, reason) in _watcher.Unwatched)
            _log.LogWarning("Not watching {Folder} ({Reason}); relying on the {Interval} scan", folder, reason, cfg.Interval);
    }

    private void SetStatus(DaemonState state, string message, TransferProgress? progress = null,
        IReadOnlyList<string>? errors = null, bool throttle = false)
    {
        DaemonStatus status;
        lock (_gate)
        {
            status = new DaemonStatus(state, message, _state.LastSync, progress,
                errors ?? (state == DaemonState.Syncing ? _status.RecentErrors : []),
                _watcher?.Unwatched ?? new Dictionary<string, string>(), Environment.ProcessId) { Sync = _syncInfo };
            var now = DateTimeOffset.UtcNow;
            var skip = throttle && _status.State == state && now - _lastPush < _options.ProgressThrottle;
            _status = status;
            if (skip)
                return;
            _lastPush = now;
        }
        _notifier?.Notify(new ServiceState("STATUS=" + message.ReplaceLineEndings(" ")));
        StatusChanged?.Invoke(status);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _state.Dispose();
        _wake.Dispose();
        base.Dispose();
    }
}

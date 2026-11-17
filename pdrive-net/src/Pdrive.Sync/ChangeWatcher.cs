namespace Pdrive.Sync;

/// <summary>Watches backup folders (inotify via FileSystemWatcher) and reports which folders changed,
/// once events have been quiet for <c>quiet</c> (capped at <c>maxDelay</c> after the first event, so a
/// file that never stops changing can't postpone syncing forever). Changes to excluded names are
/// ignored. If a folder can't be watched (e.g. inotify limits), it is listed in
/// <see cref="Unwatched"/> and the caller relies on the periodic scan for it.</summary>
public sealed class ChangeWatcher : IDisposable
{
    private readonly List<(string Root, FileSystemWatcher Watcher)> _watchers = [];
    private readonly IReadOnlyList<string> _exclude;
    private readonly TimeSpan _quiet, _maxDelay;
    private readonly Action<IReadOnlyCollection<string>> _onChanged;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private readonly Dictionary<string, string> _unwatched = new(StringComparer.Ordinal);
    private DateTimeOffset? _firstEvent;
    private bool _disposed;

    public ChangeWatcher(IEnumerable<string> roots, IReadOnlyList<string> exclude, TimeSpan quiet, TimeSpan maxDelay,
        Action<IReadOnlyCollection<string>> onChanged)
    {
        _exclude = exclude;
        _quiet = quiet;
        _maxDelay = maxDelay;
        _onChanged = onChanged;
        _timer = new Timer(_ => Fire());

        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(root))
            {
                _unwatched[root] = "folder missing";
                continue;
            }
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => OnEvent(root, e.FullPath);
            w.Changed += (_, e) => OnEvent(root, e.FullPath);
            w.Renamed += (_, e) => OnEvent(root, e.FullPath);
            // Deletions never upload anything, but a deleted folder can be replaced by a new one.
            w.Deleted += (_, e) => OnEvent(root, e.FullPath);
            // Overflow or watch failure: we may have missed events, so rescan the whole folder.
            w.Error += (_, e) =>
            {
                lock (_gate)
                    _unwatched[root] = e.GetException().Message;
                MarkDirty(root);
            };
            try
            {
                w.EnableRaisingEvents = true;
                _watchers.Add((root, w));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                w.Dispose();
                _unwatched[root] = e.Message;
            }
        }
    }

    /// <summary>Folders not (or no longer reliably) watched, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Unwatched
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_unwatched);
        }
    }

    private void OnEvent(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (rel == "." || !Scanner.IsExcludedPath(rel, _exclude))
            MarkDirty(root);
    }

    private void MarkDirty(string root)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _dirty.Add(root);
            var now = DateTimeOffset.UtcNow;
            _firstEvent ??= now;
            var remaining = _maxDelay - (now - _firstEvent.Value);
            var due = remaining < _quiet ? remaining : _quiet;
            _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        string[] changed;
        lock (_gate)
        {
            if (_disposed || _dirty.Count == 0)
                return;
            changed = [.. _dirty];
            _dirty.Clear();
            _firstEvent = null;
        }
        _onChanged(changed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _timer.Dispose();
        foreach (var (_, w) in _watchers)
            w.Dispose();
    }
}

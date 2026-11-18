namespace DiskLens.Core.Scanning;

public sealed record DirectoryEntryChange(string Path, bool ReadSubtree = false);
public sealed record DirectoryChangeBatch(IReadOnlyList<DirectoryEntryChange> Entries, bool NeedsCurrentDirectoryCheck);

/// <summary>Bounded event coalescing. Inactive tasks retain names only and perform no scans.</summary>
public sealed class DirectoryChangeMonitor : IDisposable
{
    public const int MaximumMonitors = 8;
    public const int MaximumBufferedPaths = 2048;
    public const int MaximumBatchPaths = 128;
    private static int _monitorCount;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private readonly TimeSpan _quietDelay;
    private readonly TimeSpan _maximumDelay;
    private FileSystemWatcher? _watcher;
    private DateTimeOffset? _burstStarted;
    private bool _needsCurrentCheck;
    private bool _active;
    private bool _disposed;
    private bool _signalPending;
    private bool _watcherFaulted;

    public DirectoryChangeMonitor(string rootPath, TimeSpan? quietDelay = null, TimeSpan? maximumDelay = null)
    {
        RootPath = rootPath;
        _quietDelay = quietDelay ?? TimeSpan.FromMilliseconds(1200);
        _maximumDelay = maximumDelay ?? TimeSpan.FromSeconds(3);
        _timer = new Timer(_ => Signal(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        TryStartWatcher();
    }

    public string RootPath { get; }
    public bool IsWatching { get { lock (_gate) return _watcher is not null; } }
    public event EventHandler? BatchReady;

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _active = active;
            if (!active) { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); _signalPending = false; return; }
            if (_pending.Count > 0 || _needsCurrentCheck) Schedule();
        }
    }

    public void TryStartWatcher()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_watcherFaulted && _watcher is not null)
            {
                _watcher.Dispose(); _watcher = null; _watcherFaulted = false;
                Interlocked.Decrement(ref _monitorCount);
            }
            if (_watcher is not null) return;
            if (Interlocked.Increment(ref _monitorCount) > MaximumMonitors)
            { Interlocked.Decrement(ref _monitorCount); return; }
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(RootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 32768,
                };
                watcher.Created += (_, e) => RecordChange(e.FullPath, readSubtree: true);
                watcher.Deleted += (_, e) => RecordChange(e.FullPath);
                watcher.Changed += (_, e) => RecordChange(e.FullPath);
                watcher.Renamed += (_, e) => { RecordChange(e.OldFullPath); RecordChange(e.FullPath, readSubtree: true); };
                watcher.Error += (_, _) => { lock (_gate) _watcherFaulted = true; RequestCurrentDirectoryCheck(); };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                Interlocked.Decrement(ref _monitorCount);
            }
        }
    }

    public void RecordChange(string path, bool readSubtree = false)
    {
        lock (_gate)
        {
            if (_disposed || !IsWithinRoot(path)) return;
            if (_pending.TryGetValue(path, out var old)) _pending[path] = old || readSubtree;
            else if (_pending.Count < MaximumBufferedPaths) _pending.Add(path, readSubtree);
            else _needsCurrentCheck = true;
            _burstStarted ??= DateTimeOffset.UtcNow;
            Schedule();
        }
    }

    public void RequestCurrentDirectoryCheck()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _needsCurrentCheck = true;
            _burstStarted ??= DateTimeOffset.UtcNow;
            Schedule();
        }
    }

    public DirectoryChangeBatch TakeBatch()
    {
        lock (_gate)
        {
            var entries = _pending.Take(MaximumBatchPaths).Select(pair => new DirectoryEntryChange(pair.Key, pair.Value)).ToArray();
            foreach (var entry in entries) _pending.Remove(entry.Path);
            var check = _needsCurrentCheck;
            _needsCurrentCheck = false;
            _signalPending = false;
            _burstStarted = _pending.Count > 0 ? DateTimeOffset.UtcNow : null;
            Schedule();
            return new DirectoryChangeBatch(entries, check);
        }
    }

    public void Requeue(DirectoryChangeBatch batch)
    {
        foreach (var entry in batch.Entries) RecordChange(entry.Path, entry.ReadSubtree);
        if (batch.NeedsCurrentDirectoryCheck) RequestCurrentDirectoryCheck();
    }

    public void ResumePending()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _signalPending = false;
            Schedule();
        }
    }

    private void Schedule()
    {
        if (!_active || _disposed || _signalPending || (_pending.Count == 0 && !_needsCurrentCheck)) return;
        var elapsed = DateTimeOffset.UtcNow - (_burstStarted ?? DateTimeOffset.UtcNow);
        var remaining = _maximumDelay - elapsed;
        var delay = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < _quietDelay ? remaining : _quietDelay;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Signal()
    {
        EventHandler? callback;
        lock (_gate)
        {
            if (_disposed || !_active || _signalPending) return;
            _signalPending = true;
            callback = BatchReady;
        }
        callback?.Invoke(this, EventArgs.Empty);
    }

    private bool IsWithinRoot(string path) => string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            if (_watcher is not null) { _watcher.Dispose(); _watcher = null; Interlocked.Decrement(ref _monitorCount); }
            _pending.Clear();
            BatchReady = null;
        }
    }
}

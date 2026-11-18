using System.ComponentModel;
using System.Windows.Threading;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private DirectoryChangeMonitor? _directoryChangeMonitor;
    private Dispatcher? _automaticRefreshDispatcher;
    private DispatcherTimer? _automaticRefreshFallbackTimer;
    private CancellationTokenSource? _automaticRefreshCancellation;
    private bool _automaticRefreshActive;
    private bool _automaticRefreshRunning;
    private bool _automaticRefreshApplying;
    private bool _automaticRefreshDisposed;
    private int _automaticRefreshBatchCount;

    public bool IsAutomaticallyRefreshing => _automaticRefreshRunning;
    public int AutomaticRefreshBatchCount => _automaticRefreshBatchCount;

    private void InitializeAutomaticRefresh()
    {
        _automaticRefreshDispatcher = Dispatcher.CurrentDispatcher;
        PropertyChanged += AutomaticRefresh_OnStateChanged;
    }

    /// <summary>The owning visible window activates only its selected task. Inactive monitors collect bounded names, not scans.</summary>
    public void SetAutomaticRefreshActive(bool active)
    {
        if (_isDisposed || _automaticRefreshDisposed) return;
        _automaticRefreshActive = active;
        if (!active)
        {
            _directoryChangeMonitor?.SetActive(false);
            _automaticRefreshFallbackTimer?.Stop();
            _automaticRefreshCancellation?.Cancel();
            return;
        }
        EnsureAutomaticRefreshMonitor();
        _directoryChangeMonitor?.SetActive(true);
        _directoryChangeMonitor?.ResumePending();
        if (_directoryChangeMonitor?.IsWatching == false)
        {
            _directoryChangeMonitor.RequestCurrentDirectoryCheck();
            _automaticRefreshFallbackTimer?.Start();
        }
    }

    private void AutomaticRefresh_OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_automaticRefreshDisposed || _isDisposed) return;
        if (_automaticRefreshApplying)
        {
            if (IsScanning || IsAnalyzing || IsRefreshing) _automaticRefreshCancellation?.Cancel();
            return;
        }
        if (e.PropertyName is nameof(IsScanning) or nameof(IsScanQueued))
        {
            if (IsScanning || IsScanQueued)
            {
                _automaticRefreshCancellation?.Cancel();
                _directoryChangeMonitor?.Dispose();
                _directoryChangeMonitor = null;
                return;
            }
            EnsureAutomaticRefreshMonitor();
        }
        if (e.PropertyName == nameof(ScanRoot)) EnsureAutomaticRefreshMonitor();
        if (e.PropertyName is nameof(IsAnalyzing) or nameof(IsRefreshing))
            if (IsAnalyzing || IsRefreshing) _automaticRefreshCancellation?.Cancel();
        if (e.PropertyName == nameof(CurrentRoot))
            _directoryChangeMonitor?.RequestCurrentDirectoryCheck();
        if (_automaticRefreshActive && !AutomaticRefreshBusy)
            _directoryChangeMonitor?.ResumePending();
    }

    private bool AutomaticRefreshBusy => _automaticRefreshRunning || _isVerifyingVisibleEntries || IsScanning || IsScanQueued
        || IsAnalyzing || IsRefreshing || _sharedLookupCancellation is not null || !CanNavigateVisualTree;

    private void EnsureAutomaticRefreshMonitor()
    {
        if (!_automaticRefreshActive || _automaticRefreshDisposed || _isDisposed || IsScanning || IsScanQueued || ScanRoot is not { IsDirectory: true } root) return;
        if (_directoryChangeMonitor is not null && _directoryChangeMonitor.RootPath == root.Path) return;
        _directoryChangeMonitor?.Dispose();
        var monitor = new DirectoryChangeMonitor(root.Path);
        _directoryChangeMonitor = monitor;
        monitor.BatchReady += (_, _) =>
        {
            var dispatcher = _automaticRefreshDispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            _ = dispatcher.BeginInvoke(new Action(() => _ = ProcessAutomaticRefreshAsync(monitor)), DispatcherPriority.Background);
        };
        monitor.SetActive(true);
        // First activation may happen long after a background scan completed.
        monitor.RequestCurrentDirectoryCheck();
        _automaticRefreshFallbackTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) =>
            {
                if (!_automaticRefreshActive || _directoryChangeMonitor is null) return;
                _directoryChangeMonitor.TryStartWatcher();
                // Reopening a watcher cannot replay events from the disconnected interval.
                // Always catch up this directory once, including on the successful retry.
                _directoryChangeMonitor.RequestCurrentDirectoryCheck();
                if (_directoryChangeMonitor.IsWatching) _automaticRefreshFallbackTimer?.Stop();
            }, _automaticRefreshDispatcher!);
        if (monitor.IsWatching) _automaticRefreshFallbackTimer.Stop();
        else _automaticRefreshFallbackTimer.Start();
    }

    private async Task ProcessAutomaticRefreshAsync(DirectoryChangeMonitor monitor)
    {
        if (_automaticRefreshDisposed || _isDisposed || !_automaticRefreshActive || !ReferenceEquals(_directoryChangeMonitor, monitor)) return;
        if (AutomaticRefreshBusy) return; // The next idle state notification resumes this pending signal.
        if (ScanRoot is not { } originalRoot) return;
        var batch = monitor.TakeBatch();
        if (batch.Entries.Count == 0 && !batch.NeedsCurrentDirectoryCheck) return;
        var cancellation = new CancellationTokenSource();
        _automaticRefreshCancellation = cancellation;
        _refreshCancellation = cancellation;
        _automaticRefreshRunning = true;
        OnPropertyChanged(nameof(IsAutomaticallyRefreshing));
        try
        {
            monitor.TryStartWatcher();
            if (!monitor.IsWatching) _automaticRefreshFallbackTimer?.Start();
            var service = new DirectoryRefreshService(_scanner);
            IEnumerable<DirectoryEntryChange> changes = batch.Entries;
            if (batch.NeedsCurrentDirectoryCheck && GetRefreshTarget() is { } target)
                changes = changes.Concat(await service.ReadCurrentDirectoryEntriesAsync(target, cancellation.Token).ConfigureAwait(true));
            var result = await service.RefreshEntriesAsync(originalRoot, _scanNodeIndex, changes, CreateScanOptions(), cancellation.Token).ConfigureAwait(true);
            if (!_automaticRefreshActive || !CanCommitRefresh(originalRoot, cancellation))
            { monitor.Requeue(batch); return; }
            _automaticRefreshApplying = true;
            try { await ApplyRefreshResultAsync(originalRoot, result, cancellation).ConfigureAwait(true); }
            finally { _automaticRefreshApplying = false; }
            if (!CanFinishRefresh(cancellation)) { monitor.Requeue(batch); return; }
            if (result.HasChangesFrom(originalRoot) && !ReferenceEquals(ScanRoot, result.Root))
            { monitor.Requeue(batch); return; }
            _automaticRefreshBatchCount++;
            OnPropertyChanged(nameof(AutomaticRefreshBatchCount));
            // Routine synchronization stays quiet; preserve the user's current status.
            if (result.HasChangesFrom(originalRoot) && result.PreservedUnreadableCount > 0)
                StatusText = L.T("已自动更新可读取的变更；暂时无法读取的内容仍保留原记录。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { monitor.Requeue(batch); }
        catch (Exception) { monitor.RequestCurrentDirectoryCheck(); }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
            if (ReferenceEquals(_automaticRefreshCancellation, cancellation)) _automaticRefreshCancellation = null;
            _automaticRefreshRunning = false;
            cancellation.Dispose();
            OnPropertyChanged(nameof(IsAutomaticallyRefreshing));
            if (_automaticRefreshActive && !_automaticRefreshDisposed && !AutomaticRefreshBusy) monitor.ResumePending();
        }
    }

    public void DisposeAutomaticRefresh()
    {
        if (_automaticRefreshDisposed) return;
        _automaticRefreshDisposed = true;
        _automaticRefreshActive = false;
        PropertyChanged -= AutomaticRefresh_OnStateChanged;
        _automaticRefreshFallbackTimer?.Stop();
        _automaticRefreshFallbackTimer = null;
        _directoryChangeMonitor?.Dispose();
        _directoryChangeMonitor = null;
        _automaticRefreshCancellation?.Cancel();
    }
}

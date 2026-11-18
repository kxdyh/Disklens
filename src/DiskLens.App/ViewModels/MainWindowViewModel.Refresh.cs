using System.IO;
using DiskLens.App.Infrastructure;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _refreshCancellation;
    private bool _isRefreshing;
    private bool _isVerifyingVisibleEntries;
    private string _refreshStatusText = string.Empty;
    private DateTimeOffset _lastVisibleVerification;
    private string? _lastVerifiedDisplayPath;

    public AsyncRelayCommand RefreshCurrentDirectoryCommand { get; private set; } = null!;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                OnPropertyChanged(nameof(ShowCancelAction));
            }
        }
    }

    public string RefreshStatusText
    {
        get => _refreshStatusText;
        private set => SetProperty(ref _refreshStatusText, value);
    }

    private void InitializeRefreshCommands()
    {
        RefreshCurrentDirectoryCommand = new AsyncRelayCommand(RefreshCurrentDirectoryAsync,
            () => ScanRoot is not null && CanNavigateVisualTree && !IsAnalyzing && !IsRefreshing && !_isVerifyingVisibleEntries);
        InitializeAutomaticRefresh();
    }

    private void RaiseRefreshCommandStateChanged() => RefreshCurrentDirectoryCommand?.RaiseCanExecuteChanged();

    internal void CancelRefresh()
    {
        _refreshCancellation?.Cancel();
        _automaticRefreshCancellation?.Cancel();
    }

    /// <summary>Explicitly rescan the current real directory, preserving other task trees and the old view until commit.</summary>
    public async Task RefreshCurrentDirectoryAsync()
    {
        if (_isDisposed || ScanRoot is not { } originalRoot || IsScanning || IsAnalyzing || IsRefreshing || _isVerifyingVisibleEntries || _automaticRefreshRunning) return;
        var target = GetRefreshTarget();
        if (target is null) return;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        IsRefreshing = true;
        RefreshStatusText = L.T("正在重新读取当前目录及其子目录，耗时取决于目录大小…");
        StatusText = RefreshStatusText;
        var progress = new Progress<ScanProgressSnapshot>(snapshot =>
        {
            if (_isDisposed || !ReferenceEquals(_refreshCancellation, cancellation) || cancellation.IsCancellationRequested) return;
            RefreshStatusText = L.F($"正在刷新 · 已读取 {snapshot.FilesScanned:N0} 个文件 / {snapshot.DirectoriesScanned:N0} 个目录");
            StatusText = RefreshStatusText;
        });
        try
        {
            var service = new DirectoryRefreshService(_scanner);
            var result = await service.RefreshAsync(originalRoot, target, CreateScanOptions(), progress, cancellation.Token).ConfigureAwait(true);
            if (!CanCommitRefresh(originalRoot, cancellation)) return;
            await ApplyRefreshResultAsync(originalRoot, result, cancellation).ConfigureAwait(true);
            if (!CanFinishRefresh(cancellation)) return;
            RefreshVolumeCapacity(RootPath);
            var incomplete = result.RefreshedScan?.IsPartial == true || result.PreservedUnreadableCount > 0;
            RefreshStatusText = incomplete
                ? L.F($"部分刷新完成：部分内容因访问权限或扫描策略未能重新读取，已保留 {result.PreservedUnreadableCount:N0} 项旧记录。可检查权限后再次刷新。")
                : result.HasChangesFrom(originalRoot) ? L.T("刷新完成，文件块、容量和分析列表已同步更新。") : L.T("刷新完成，当前目录没有变化。");
            StatusText = RefreshStatusText;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_isDisposed) StatusText = RefreshStatusText = L.T("已取消刷新，原扫描结果已保留。");
        }
        catch (Exception ex)
        {
            if (!_isDisposed) StatusText = RefreshStatusText = L.F($"刷新未完成，原扫描结果已保留：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                IsRefreshing = false;
            }
            cancellation.Dispose();
        }
    }

    /// <summary>Called on reactivation/tab selection: at most 50 metadata probes, no recursive disk scan.</summary>
    public async Task VerifyVisibleEntriesAsync()
    {
        if (_isDisposed || ScanRoot is not { } originalRoot || !CanNavigateVisualTree || IsAnalyzing || IsRefreshing || _isVerifyingVisibleEntries || _automaticRefreshRunning) return;
        var displayPath = CurrentRoot?.Path;
        if (_lastVerifiedDisplayPath == displayPath && DateTimeOffset.UtcNow - _lastVisibleVerification < TimeSpan.FromSeconds(3)) return;
        _lastVerifiedDisplayPath = displayPath;
        _lastVisibleVerification = DateTimeOffset.UtcNow;
        var target = GetRefreshTarget();
        var paths = (target is not null && target.Path != originalRoot.Path ? new[] { target.Path } : [])
            .Concat((TreemapDisplayRoot?.Children ?? []).Where(node => !IsAggregateNode(node)).Select(node => node.Path))
            .Take(DirectoryRefreshService.MaximumVerifiedEntries).ToArray();
        if (paths.Length == 0) return;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _isVerifyingVisibleEntries = true;
        RaiseRefreshCommandStateChanged();
        try
        {
            var service = new DirectoryRefreshService(_scanner);
            var result = await service.VerifyMissingAsync(originalRoot, paths, cancellation.Token).ConfigureAwait(true);
            if (!CanCommitRefresh(originalRoot, cancellation) || IsAnalyzing || !CanNavigateVisualTree) return;
            if (!result.HasChangesFrom(originalRoot)) return;
            await ApplyRefreshResultAsync(originalRoot, result, cancellation).ConfigureAwait(true);
            // Presence checks update the tree quietly instead of replacing user-facing status.
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { /* A quiet presence check must not interrupt the user or discard the snapshot. */ }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
            _isVerifyingVisibleEntries = false;
            RaiseRefreshCommandStateChanged();
            _directoryChangeMonitor?.ResumePending();
            cancellation.Dispose();
        }
    }

    private FileSystemNode? GetRefreshTarget()
    {
        var path = CurrentRoot?.Path;
        if (path is null) return ScanRoot;
        var marker = path.IndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
        if (marker >= 0)
        {
            path = path[..marker];
            if (path.Length == 2 && path[1] == ':') path += Path.DirectorySeparatorChar;
        }
        return _scanNodeIndex.FindNode(path) ?? ScanRoot;
    }

    private bool CanCommitRefresh(FileSystemNode originalRoot, CancellationTokenSource cancellation) =>
        CanFinishRefresh(cancellation) && ReferenceEquals(ScanRoot, originalRoot) && !IsScanning && !IsAnalyzing;

    private bool CanFinishRefresh(CancellationTokenSource cancellation) =>
        !_isDisposed && !cancellation.IsCancellationRequested && ReferenceEquals(_refreshCancellation, cancellation);

    private async Task ApplyRefreshResultAsync(FileSystemNode originalRoot, DirectoryRefreshResult result, CancellationTokenSource cancellation)
    {
        if (!result.HasChangesFrom(originalRoot)) return;
        var prepared = await Task.Run(() => (Index: FileSystemNodeIndex.Build(result.Root),
            Estimated: result.Root.EnumerateFiles().Count(node => node.StorageInfo?.IsEstimated == true)), cancellation.Token).ConfigureAwait(true);
        if (!CanCommitRefresh(originalRoot, cancellation)) return;
        var currentPath = CurrentRoot?.Path;
        var selectedPath = SelectedNode?.Path;
        if (currentPath is not null && currentPath.Contains(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal))
        {
            var physicalPath = currentPath[..currentPath.IndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal)];
            if (physicalPath.Length == 2 && physicalPath[1] == ':') physicalPath += Path.DirectorySeparatorChar;
            if (prepared.Index.FindNode(physicalPath) is null) currentPath = physicalPath;
        }
        while (currentPath is not null && !currentPath.Contains(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal)
            && prepared.Index.FindNode(currentPath) is null)
            currentPath = _scanNodeIndex.FindParentPath(currentPath);
        _filesScanned = result.Root.DescendantFileCount;
        _directoriesScanned = result.Root.DescendantDirectoryCount;
        _bytesDiscovered = result.Root.SizeBytes;
        _estimatedStorageItemCount = prepared.Estimated;
        ClearAnalysisFeedback();
        ApplySnapshot(result.Root, prepared.Index, currentPath ?? result.Root.Path, selectedPath);
        RebuildAnalysisItems(result.Root);
        RefreshVolumeCapacity(RootPath);
        foreach (var property in new[] { nameof(FooterText), nameof(ScanFilesCountText), nameof(ScanDirectoriesCountText),
            nameof(ScanDiscoveredSizeText), nameof(ScanStatusDetailText) }) OnPropertyChanged(property);
    }
}

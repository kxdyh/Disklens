using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Networking;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _sharedLookupCancellation;
    private SharedLookupProgress _sharedLookupProgress = new(SharedLookupStage.Preparing, 0, 0, 0, 0);
    private bool _sharedLookupIncomplete;
    private bool _sharedLookupStopped;
    private string? _sharedLookupScopePath;
    private bool _isPreparingSharedLookup;
    private bool _isLoadingSharedExperience;
    public bool IsLoadingSharedExperience
    {
        get => _isLoadingSharedExperience;
        private set
        {
            if (!SetProperty(ref _isLoadingSharedExperience, value)) return;
            NotifySharedRematchState();
            NotifySharedUploadFooter();
        }
    }

    public bool CanRematchCurrentSharedExperience => !_isDisposed && ScanRoot is not null
        && SharedRuntime is not null && _sharedConsent is { CanShare: true }
        && !_isPreparingSharedLookup && !IsLoadingSharedExperience && !IsScanning && !IsScanQueued && !IsAnalyzing
        && !IsRefreshing && !IsReanalysisSelectionMode && !IsAiSetupOpen && !IsScanReportOpen;

    public bool IsSharedLookupRetryVisible => _sharedLookupIncomplete && !IsLoadingSharedExperience;
    public bool CanRetrySharedExperienceLookup => IsSharedLookupRetryVisible && CanRematchCurrentSharedExperience;

    private void NotifySharedRematchState()
    {
        OnPropertyChanged(nameof(CanRematchCurrentSharedExperience));
        OnPropertyChanged(nameof(IsSharedLookupRetryVisible));
        OnPropertyChanged(nameof(CanRetrySharedExperienceLookup));
    }

    private static bool IsWithinSharedLookupScope(string path, string scopePath)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var scope = scopePath.Replace('/', '\\').TrimEnd('\\');
        return normalized.Equals(scope, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public Task RematchCurrentSharedExperienceAsync() => CanRematchCurrentSharedExperience
        ? RefreshSharedExperienceAsync(currentRegionOnly: true) : Task.CompletedTask;

    public Task RetrySharedExperienceLookupAsync() => CanRetrySharedExperienceLookup
        ? RefreshSharedExperienceAsync(currentRegionOnly: _sharedLookupScopePath is not null,
            requestedScopePath: _sharedLookupScopePath, forceRefreshPathIndex: true)
        : Task.CompletedTask;

    public void StopSharedExperienceLookup()
    {
        if (_sharedLookupCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        MarkSharedLookupIncomplete(stopped: true);
        IsLoadingSharedExperience = false;
        SetSharedSummary(() => L.T("云端经验匹配已停止"));
        StatusText = SharedExperienceSummaryText;
        NotifySharedLookupSummary();
        if (SharedRuntime is { } runtime) RecordSharedLookupEvent(runtime,
            _sharedLookupScopePath is null ? "FullScan" : "Region", "Stopped");
    }

    public string SharedLookupStageText => L.T(_sharedLookupProgress.Stage switch
    {
        SharedLookupStage.DownloadingIndex => "正在下载云端路径索引",
        SharedLookupStage.Matching => "正在匹配云端经验",
        SharedLookupStage.Retrying => "正在重试未读取的云端经验",
        SharedLookupStage.Completed => "云端经验匹配已结束",
        _ => "正在准备云端经验匹配",
    });

    public double SharedLookupProgressValue => _sharedLookupProgress.Total > 0
        && _sharedLookupProgress.Stage is not (SharedLookupStage.Preparing or SharedLookupStage.DownloadingIndex)
        ? Math.Clamp(100d * _sharedLookupProgress.Processed / _sharedLookupProgress.Total, 0, 100)
        : 0;

    public string SharedLookupProgressDetailText => _sharedLookupProgress.Total > 0
        ? L.F($"已检查本机 {_sharedLookupProgress.Processed:N0} / {_sharedLookupProgress.Total:N0} 项 · 已匹配 {_sharedLookupProgress.Matched:N0} 项 · 未能读取 {_sharedLookupProgress.Failed:N0} 项")
            + (_sharedLookupProgress.Failed > 0 ? L.T("。部分内容尚未读取，已取得的结果仍会保留。") : string.Empty)
        : L.T(_sharedLookupProgress.Stage == SharedLookupStage.Completed
            ? "云端读取已结束；未取得完整计数，请检查读取结果。"
            : "总量尚未确定，收到索引后显示实际匹配进度。");

    private void SetSharedLookupProgress(SharedLookupProgress progress)
    {
        _sharedLookupProgress = progress;
        OnPropertyChanged(nameof(SharedLookupStageText));
        OnPropertyChanged(nameof(SharedLookupProgressValue));
        OnPropertyChanged(nameof(SharedLookupProgressDetailText));
        NotifySharedUploadFooter();
    }

    // This is a read-only lookup. It must never configure or invoke an AI provider.
    public Task RefreshSharedExperienceAfterScanAsync() => RefreshSharedExperienceAsync(currentRegionOnly: false);

    private async Task RefreshSharedExperienceAsync(bool currentRegionOnly, string? requestedScopePath = null,
        bool forceRefreshPathIndex = false)
    {
        if (_isPreparingSharedLookup || IsLoadingSharedExperience) return;
        _isPreparingSharedLookup = true;
        NotifySharedRematchState();
        try { await RunSharedExperienceLookupAsync(currentRegionOnly, requestedScopePath, forceRefreshPathIndex); }
        finally
        {
            _isPreparingSharedLookup = false;
            NotifySharedRematchState();
            TryQueuePendingSharedConsentLookup();
        }
    }

    private async Task RunSharedExperienceLookupAsync(bool currentRegionOnly, string? requestedScopePath,
        bool forceRefreshPathIndex)
    {
        if (_isDisposed || ScanRoot is null || IsScanning || IsAnalyzing || IsRefreshing || IsReanalysisSelectionMode) return;
        if (currentRegionOnly) requestedScopePath ??= GetRefreshTarget()?.Path;
        if (currentRegionOnly && requestedScopePath is null) return;
        ConsumePendingSharedConsentLookup();
        if (SharedRuntime is not { } runtime)
        {
            if (!currentRegionOnly) await RestoreLocalHistoryAfterScanAsync();
            return;
        }
        await RefreshSharedConsentAsync();
        if (_isDisposed || IsScanning || IsAnalyzing || IsRefreshing || IsReanalysisSelectionMode
            || ScanRoot is not { } snapshot || !ReferenceEquals(runtime, SharedRuntime)
            || _sharedConsent is not { CanShare: true } consent)
        {
            if (!currentRegionOnly) await RestoreLocalHistoryAfterScanAsync();
            return;
        }
        var lookupRoot = currentRegionOnly ? _scanNodeIndex.FindNode(requestedScopePath!) : snapshot;
        if (lookupRoot is null)
        {
            CancelSharedLookup();
            StatusText = L.T("要匹配的目录已不在扫描结果中。");
            return;
        }
        // Refreshing consent may itself discover the just-enabled state. This
        // invocation already serves that intent, including its deferred callback.
        ConsumePendingSharedConsentLookup();
        CancelSharedLookup();
        _sharedLookupScopePath = currentRegionOnly ? lookupRoot.Path : null;
        var phase = currentRegionOnly ? "Region" : "FullScan";
        if (NetworkFailureClassifier.IsNetworkUnavailable(runtime.Endpoint.AbsoluteUri, runtime.NetworkAvailability))
        {
            MarkSharedLookupIncomplete();
            RecordSharedLookupEvent(runtime, phase, "Offline");
            await HandleLookupOfflineAsync();
            if (!currentRegionOnly) await RestoreLocalHistoryAfterScanAsync();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _sharedLookupCancellation = cancellation;
        IsLoadingSharedExperience = true;
        RecordSharedLookupEvent(runtime, phase, "Started");
        // A watcher refresh may already be in flight before the lookup became busy.
        // Cancel that refresh so its older tree cannot replace this lookup's snapshot.
        _automaticRefreshCancellation?.Cancel();
        // Cloud purposes cover eligible scanned paths regardless of the paid AI
        // analysis threshold or visual size filter.
        SetSharedSummary(() => L.T("正在匹配云端经验库，无需配置 API…"));
        NotifySharedLookupSummary();
        var lookupSettled = false;
        try
        {
            // Progress<T> captures the UI context. Recheck ownership when each
            // callback is applied: queued callbacks may outlive a canceled scan.
            var progress = new Progress<SharedLookupProgress>(value => RunOnLanguageThread(() =>
            {
                if (!CanApplyLookup()) return;
                SetSharedLookupProgress(value);
            }));
            var capturedLocalEntries = await runtime.Memory.LoadEntriesAsync(snapshot.Path, cancellation.Token);
            var resolution = await Task.Run(() => runtime.ResolveScannedExperienceAsync(lookupRoot,
                cancellation.Token, progress, forceRefreshPathIndex: currentRegionOnly || forceRefreshPathIndex,
                recoverLegacyContributions: !currentRegionOnly && !forceRefreshPathIndex), cancellation.Token);
            if (!CanApplyLookup()) return;
            if (!await ApplySharedLookupResultAsync(snapshot, consent, resolution, capturedLocalEntries,
                lookupScopePath: _sharedLookupScopePath, cancellationToken: cancellation.Token)
                || !OwnsLookup()) return;
            // An unavailable index has no candidate counts. Retain that failure
            // explicitly instead of turning Completed(0, 0, 0, 0) into success.
            _sharedLookupIncomplete = resolution.Unavailable || resolution.FailedCount > 0;
            SetSharedLookupProgress(_sharedLookupProgress with
            {
                Stage = SharedLookupStage.Completed,
                Matched = resolution.MatchedCount,
                Failed = resolution.FailedCount,
            });
            lookupSettled = true;
            RecordSharedLookupEvent(runtime, phase, _sharedLookupIncomplete ? "Incomplete" : "Completed");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (OwnsLookup())
            {
                if (NetworkFailureClassifier.IsOffline(ex, runtime.NetworkAvailability))
                {
                    lookupSettled = true;
                    MarkSharedLookupIncomplete();
                    RecordSharedLookupEvent(runtime, phase, "Offline");
                    await HandleLookupOfflineAsync();
                }
                else ShowLookupFailure();
            }
        }
        finally
        {
            if (ReferenceEquals(_sharedLookupCancellation, cancellation))
            {
                if (!lookupSettled && OwnsLookup()) ShowLookupFailure("Interrupted");
                _sharedLookupCancellation = null;
                IsLoadingSharedExperience = false;
                // A valid cloud answer owns its path. Personal history fills only
                // unresolved paths, including offline or unavailable lookups.
                if (!currentRegionOnly && !_isDisposed && ReferenceEquals(runtime, SharedRuntime) && !IsAnalyzing && !IsScanning
                    && !IsRefreshing && !IsReanalysisSelectionMode)
                    await RestoreLocalHistoryAfterScanAsync();
                if (_automaticRefreshActive && !AutomaticRefreshBusy)
                    _directoryChangeMonitor?.ResumePending();
            }
        }

        bool OwnsLookup() => !_isDisposed
            && ReferenceEquals(_sharedLookupCancellation, cancellation)
            && !cancellation.IsCancellationRequested && ReferenceEquals(runtime, SharedRuntime)
            && !IsScanning && !IsAnalyzing && !IsRefreshing && !IsReanalysisSelectionMode
            && SameSharedConsent(consent, _sharedConsent);
        bool CanApplyLookup() => OwnsLookup() && ReferenceEquals(snapshot, ScanRoot);
        void ShowLookupFailure(string outcome = "Failed")
        {
            lookupSettled = true;
            MarkSharedLookupIncomplete();
            SetSharedSummary(() => L.T("云端经验库查询失败，请检查网络后重试。扫描结果仍可正常查看。"));
            StatusText = SharedExperienceSummaryText;
            NotifySharedLookupSummary();
            RecordSharedLookupEvent(runtime, phase, outcome);
        }

        async Task HandleLookupOfflineAsync()
        {
            if (!currentRegionOnly) { await HandleOfflineAsync(); return; }
            SetSharedSummary(() => L.T(NetworkFailureClassifier.OfflineMessage));
            StatusText = SharedExperienceSummaryText;
            IsOfflineAlertVisible = true;
        }
    }

    private void MarkSharedLookupIncomplete(bool stopped = false)
    {
        _sharedLookupIncomplete = true;
        _sharedLookupStopped = stopped;
        SetSharedLookupProgress(_sharedLookupProgress with { Stage = SharedLookupStage.Completed });
        NotifySharedRematchState();
    }

    private void NotifySharedLookupSummary()
    {
        OnPropertyChanged(nameof(SharedExperienceSummaryText));
        OnPropertyChanged(nameof(HasSharedExperienceSummary));
    }

    private void CancelSharedLookup()
    {
        var pending = _sharedLookupCancellation;
        _sharedLookupCancellation = null;
        _sharedLookupIncomplete = false;
        _sharedLookupStopped = false;
        _sharedLookupScopePath = null;
        IsLoadingSharedExperience = false;
        NotifySharedRematchState();
        SetSharedLookupProgress(new(SharedLookupStage.Preparing, 0, 0, 0, 0));
        pending?.Cancel(); // The owning lookup disposes its own source after settling.
    }
}

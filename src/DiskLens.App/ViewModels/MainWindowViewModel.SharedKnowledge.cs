using DiskLens.App.Infrastructure;
using DiskLens.App.Models;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static long _snapshotCounter = DateTimeOffset.UtcNow.UtcTicks;
    private long _snapshotGeneration;
    private SharedKnowledgeRuntime? _sharedRuntime;
    private SharedConsentSnapshot? _sharedConsent;
    private long _sharedConsentRefreshRevision;
    private bool _sharedConsentLookupPending;
    private bool _sharedConsentLookupPosted;
    private long _sharedConsentLookupRevision;
    private SharedConsentSnapshot? _sharedMissingConsent;
    private FileSystemNode? _sharedMissingSnapshot;
    private DateTimeOffset _sharedLookupCapturedAt;
    private bool _sharedMissingIsPathPurpose;
    private string? _sharedMissingReuseScopePath;
    private readonly HashSet<string> _sharedMissingPaths = new(StringComparer.Ordinal);
    private string _lastSharedOutboxNotice = string.Empty;
    private Func<string>? _sharedSummaryRenderer;
    private Func<string>? _sharedOutboxNoticeRenderer;
    private string _sharedSummaryBaseText = string.Empty;
    private string _sharedSummaryText = string.Empty;
    private bool _includeSharedOutboxNotice;
    private long _sharedOutboxRefreshRevision;
    private SynchronizationContext? _sharedOutboxUiContext;
    public SharedKnowledgeRuntime? SharedRuntime
    {
        get => _sharedRuntime;
        internal set
        {
            if (ReferenceEquals(_sharedRuntime, value)) return;
            ConsumePendingSharedConsentLookup();
            CancelSharedLookup();
            Interlocked.Increment(ref _sharedOutboxRefreshRevision);
            if (_sharedRuntime is not null)
            {
                _sharedRuntime.Settings.Changed -= SharedSettings_OnChanged;
                _sharedRuntime.Outbox.Changed -= SharedOutbox_OnChanged;
                _sharedRuntime.NetworkUnavailable -= SharedRuntime_OnNetworkUnavailable;
                _sharedRuntime.ConnectionChanged -= SharedRuntime_OnConnectionChanged;
                PropertyChanged -= SharedOutbox_OnViewModelChanged;
            }
            _sharedRuntime = value;
            _sharedConsent = null;
            Interlocked.Increment(ref _sharedConsentRefreshRevision);
            if (_sharedRuntime is not null)
            {
                _sharedRuntime.Settings.Changed += SharedSettings_OnChanged;
                _sharedRuntime.Outbox.Changed += SharedOutbox_OnChanged;
                _sharedRuntime.NetworkUnavailable += SharedRuntime_OnNetworkUnavailable;
                _sharedRuntime.ConnectionChanged += SharedRuntime_OnConnectionChanged;
                PropertyChanged += SharedOutbox_OnViewModelChanged;
            }
            _sharedOutboxUiContext = SynchronizationContext.Current;
            _lastSharedOutboxNotice = string.Empty;
            _sharedOutboxNoticeRenderer = null;
            _includeSharedOutboxNotice = false;
            _sharedUploadStatus = new(SharedUploadStage.Idle, 0);
            NotifySharedUploadFooter();
            ClearSharedMissingState();
            NotifyAnalysisVisibilityChanged();
            NotifySharedRematchState();
            _ = RefreshSharedConsentAsync();
            _ = RefreshSharedOutboxStatusAsync();
        }
    }
    public AsyncRelayCommand ReanalyzeWithoutMemoryCommand { get; private set; } = null!;
    public AsyncRelayCommand ReportSharedExperienceCommand { get; private set; } = null!;
    public string SharedExperienceSummaryText
    {
        get => _sharedSummaryText;
        private set
        {
            _sharedSummaryRenderer = null;
            _sharedSummaryBaseText = value;
            _sharedSummaryText = value;
            _includeSharedOutboxNotice = false;
        }
    }
    public bool HasSharedExperienceSummary => !string.IsNullOrWhiteSpace(SharedExperienceSummaryText);

    private void SetSharedSummary(Func<string> render)
    {
        _sharedSummaryRenderer = render;
        _includeSharedOutboxNotice = false;
        _sharedSummaryText = render();
        NotifySharedLookupSummary();
    }

    private void SetSharedAnalysisSummary(AiAnalysisReport report)
    {
        CaptureAiOutcome(report);
        var sharedCount = report.Results.Count(result => result.Origin == AiAnalysisResultOrigin.SharedExperience);
        var historyCount = report.Results.Count(result => result.Origin == AiAnalysisResultOrigin.CacheReused);
        var protectedCount = report.Results.Count(result => result.Origin == AiAnalysisResultOrigin.ProtectedByRule);
        SetSharedSummary(() =>
        {
            var text = L.F($"达到阈值 {report.TotalCandidates} 项，实际提交 AI {report.SubmittedCandidateCount} 项\n")
                + L.F($"本地历史 {historyCount} 项，共享复用 {sharedCount} 项，规则保护 {protectedCount} 项");
            text += "\n" + RenderAiAnalysisOutcome(report, includeDiagnostics: true);
            if (report.SharedSkippedItems > 0) text += L.F($"\n{report.SharedSkippedItems} 项已去重、已贡献或不符合共享条件。");
            if (!string.IsNullOrWhiteSpace(report.SharedKnowledgeWarning))
                text += "\n" + string.Join("\n", report.SharedKnowledgeWarning.Split('\n').Select(L.T));
            return text;
        });
    }

    private void RefreshSharedSummaryLanguage()
    {
        var previousSummary = _sharedSummaryText;
        var previousNotice = _lastSharedOutboxNotice;
        var summary = _sharedSummaryRenderer?.Invoke() ?? L.T(_sharedSummaryBaseText);
        if (_includeSharedOutboxNotice)
        {
            summary = RemoveSharedOutboxNotice(summary);
            _lastSharedOutboxNotice = _sharedOutboxNoticeRenderer?.Invoke() ?? string.Empty;
            if (_lastSharedOutboxNotice.Length > 0)
                summary = summary.Length == 0 ? _lastSharedOutboxNotice : summary + "\n" + _lastSharedOutboxNotice;
        }
        _sharedSummaryText = summary;
        if (StatusText == previousSummary && previousSummary.Length > 0) StatusText = summary;
        else if (StatusText == previousNotice && previousNotice.Length > 0) StatusText = _lastSharedOutboxNotice;
        NotifySharedLookupSummary();
    }

    private void InitializeSharedKnowledgeCommands()
    {
        PropertyChanged += SharedUploadFooter_OnViewModelChanged;
        InitializeReanalysisSelection();
        ReportSharedExperienceCommand = new AsyncRelayCommand(ReportSelectedSharedAsync,
            () => SelectedVisibleAnalysis?.SharedEntry is not null && SharedRuntime is not null && !IsAnalyzing);
    }

    public bool IsSelectedSharedExperience => SelectedVisibleAnalysis?.Origin == AiAnalysisResultOrigin.SharedExperience;
    public bool HasSelectedSharedMissing => ShowSharedExperienceAnalysis && SelectedNode is { } node
        && (node.Analysis is null || node.Analysis.LooksLikeFallback())
        && ReferenceEquals(_sharedMissingSnapshot, ScanRoot)
        && SameSharedConsent(_sharedMissingConsent, _sharedConsent)
        && _sharedMissingPaths.Contains(node.Path);
    public string SelectedSharedMissingText => HasSelectedSharedMissing
        ? L.T("经验库没有这个文件哦，快使用ai分析顺便贡献出来吧") : string.Empty;
    public string SelectedAiConfidenceText => SelectedVisibleAnalysis?.Origin == AiAnalysisResultOrigin.ProtectedByRule
        ? L.T("本地规则保护") : SelectedVisibleAnalysis is { } result ?
            (result.Origin == AiAnalysisResultOrigin.SharedExperience ? L.T("云端经验置信等级：") : L.T("AI 自评置信度：")) + (result.EffectiveConfidenceLevel switch
        {
            AiConfidenceLevel.High => L.T("高"), AiConfidenceLevel.Medium => L.T("中"), AiConfidenceLevel.Low => L.T("低"), _ => L.T("未知")
        }) + L.T("，仅供参考") : string.Empty;
    public bool HasSelectedAiOriginBadge => SelectedVisibleAnalysis is not null;
    public string SelectedAiOriginText => HasSelectedAiOriginBadge
        ? AnalysisSourcePresentation.LabelFor(SelectedVisibleAnalysis)
        : IsSelectedBelowAnalysisThreshold ? L.T("未提交 AI") : L.T("尚无有效结论");
    public string SelectedAiOriginIcon => HasSelectedAiOriginBadge
        ? AnalysisSourcePresentation.IconFor(SelectedVisibleAnalysis) : string.Empty;
    public string SelectedSharedExplanation => SelectedVisibleAnalysis?.SharedEntry is { } shared
        ? L.F($"{L.T(shared.MatchExplanation)}\n规则 {shared.RuleVersion} / 修订 {shared.Revision}\n更新 {shared.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n")
          + string.Join("\n", shared.Conditions.Select(L.T)) + L.T("\n匿名类别和结构匹配不代表文件内容相同。自动匹配受有效期与授权范围限制，离线时无法重新核验。")
        : string.Empty;

    public Task<IReadOnlyList<string>> PreviewSharedScopeAsync(IReadOnlyList<string> exclusions)
    {
        var root = ScanRoot;
        var runtime = SharedRuntime;
        if (root is null || runtime is null) return Task.FromResult<IReadOnlyList<string>>([]);
        var options = CreateScanOptions();
        var scope = exclusions.ToArray();
        return Task.Run(() => runtime.PreviewAsync(root, options, scope));
    }

    public async Task<SharedContributionReceipt> ImportSharedHistoryAsync()
    {
        var root = ScanRoot;
        var runtime = SharedRuntime;
        if (root is null || runtime is null) return new SharedContributionReceipt(0, 0, 0);
        var options = CreateScanOptions() with { AnalysisMemoryRootPath = root.Path };
        var provider = CreateProviderOptions();
        var receipt = await Task.Run(() => runtime.ImportHistoryAsync(root, options, provider));
        if (receipt.NetworkUnavailable) await HandleOfflineAsync();
        return receipt;
    }

    private async Task ReportSelectedSharedAsync()
    {
        if (SelectedVisibleAnalysis?.SharedEntry is not { } shared || SharedRuntime is null) return;
        try
        {
            if (NetworkFailureClassifier.IsNetworkUnavailable(SharedRuntime.Endpoint.AbsoluteUri, SharedRuntime.NetworkAvailability))
            {
                await HandleOfflineAsync();
                return;
            }
            var consent = await SharedRuntime.Store.GetSnapshotAsync();
            if (!consent.CanShare) { StatusText = L.T("文件用途经验共享已停用，未发送反馈。"); return; }
            await SharedRuntime.Client.FeedbackAsync(consent.Installation!, consent.Epoch, shared.Key, "unsafe-advice");
            StatusText = L.T("已反馈此结论需要复核。请自行确认用途，也可重新分析。");
        }
        catch (Exception ex)
        {
            if (NetworkFailureClassifier.IsOffline(ex, SharedRuntime.NetworkAvailability)) await HandleOfflineAsync();
            else StatusText = L.T("暂时无法提交反馈；本次扫描不受影响，请稍后重试。");
        }
    }

    internal async Task HandleOfflineAsync(string? analysisPath = null)
    {
        var runtime = SharedRuntime;
        // A link outage pauses transport; it must not revoke consent or delete
        // the durable contribution queue that should resume after reconnection.
        await RefreshSharedConsentAsync();
        await ApplyOfflineStatusAsync(runtime, analysisPath);
    }

    private async Task ApplyOfflineStatusAsync(SharedKnowledgeRuntime? runtime, string? analysisPath = null)
    {
        void Apply()
        {
            if (_isDisposed || !ReferenceEquals(runtime, SharedRuntime)) return;
            Interlocked.Increment(ref _sharedOutboxRefreshRevision);
            _lastSharedOutboxNotice = string.Empty;
            ClearSharedMissingState();
            SetSharedSummary(() => L.T(NetworkFailureClassifier.OfflineMessage));
            NotifySharedLookupSummary();
            StatusText = NetworkFailureClassifier.OfflineMessage;
            IsOfflineAlertVisible = true;
            if (analysisPath is not null) SetAnalysisFeedback(analysisPath, StatusText);
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(Apply).Task.ConfigureAwait(false);
        else if (_sharedOutboxUiContext is { } uiContext
            && !ReferenceEquals(SynchronizationContext.Current, uiContext))
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            uiContext.Post(_ =>
            {
                try { Apply(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }, null);
            await completion.Task.ConfigureAwait(false);
        }
        else Apply();
    }

    public async Task<string> GetLocalMemorySummaryAsync()
    {
        if (SharedRuntime is null) return L.T("缓存管理尚未就绪。");
        var statistics = await SharedRuntime.Memory.GetStatisticsAsync();
        return $"{statistics}";
    }

    public async Task RefreshSharedConsentAsync()
    {
        var revision = Interlocked.Increment(ref _sharedConsentRefreshRevision);
        var runtime = SharedRuntime;
        SharedConsentSnapshot? consent = null;
        try { if (runtime is not null) consent = await runtime.Store.GetSnapshotAsync(); }
        catch (Exception) { /* A missing local consent snapshot must fail closed. */ }
        void Apply()
        {
            if (_isDisposed || revision != Interlocked.Read(ref _sharedConsentRefreshRevision)
                || !ReferenceEquals(runtime, SharedRuntime)) return;
            var consentChanged = (_sharedConsent?.CanShare == true || consent?.CanShare == true)
                && !SameSharedConsent(_sharedConsent, consent);
            _sharedConsent = consent;
            NotifySharedRematchState();
            if (consentChanged)
            {
                ConsumePendingSharedConsentLookup();
                CancelSharedLookup();
                // A completed scan can predate the user's decision to enable sharing.
                // Queue the read-only lookup once for this consent, never on every refresh.
                _sharedConsentLookupPending = consent?.CanShare == true;
            }
            if (!SameSharedConsent(_sharedMissingConsent, consent)) ClearSharedMissingState();
            NotifyAnalysisVisibilityChanged();
            TryQueuePendingSharedConsentLookup();
        }
        // Offline suspension can originate in the background outbox worker.
        // Keep result-list selection and treemap binding notifications on the owning UI.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(Apply).Task.ConfigureAwait(false);
        else if (_sharedOutboxUiContext is { } uiContext
            && !ReferenceEquals(SynchronizationContext.Current, uiContext))
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            uiContext.Post(_ =>
            {
                try { Apply(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }, null);
            await completion.Task.ConfigureAwait(false);
        }
        else Apply();
    }

    private async void SharedSettings_OnChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !ReferenceEquals(sender, SharedRuntime?.Settings)) return;
        await RefreshSharedConsentAsync();
    }

    // A scan-completion lookup consumes the same intent, so it cannot race a
    // queued enable-after-scan callback into sending a second cloud query.
    private void ConsumePendingSharedConsentLookup()
    {
        _sharedConsentLookupPending = false;
        _sharedConsentLookupPosted = false;
        ++_sharedConsentLookupRevision;
    }

    private bool CanStartPendingSharedConsentLookup => !_isDisposed && SharedRuntime is not null
        && _sharedConsent is { CanShare: true } && ScanRoot is not null
        && !IsScanning && !IsScanQueued && !IsAnalyzing && !IsRefreshing
        && !IsReanalysisSelectionMode && !_isPreparingSharedLookup && !IsLoadingSharedExperience;

    private void TryQueuePendingSharedConsentLookup()
    {
        if (!_sharedConsentLookupPending || _sharedConsentLookupPosted || !CanStartPendingSharedConsentLookup) return;
        var revision = _sharedConsentLookupRevision;
        _sharedConsentLookupPosted = true;
        async void StartLookup()
        {
            if (_isDisposed || revision != _sharedConsentLookupRevision) return;
            _sharedConsentLookupPosted = false;
            if (!_sharedConsentLookupPending || !CanStartPendingSharedConsentLookup) return;
            ConsumePendingSharedConsentLookup();
            try { await RefreshSharedExperienceAfterScanAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Optional lookup failure must not break a settings event. */ }
        }

        // Settings changes can be raised by the background outbox. Apply already
        // marshals consent to the UI; defer the actual lookup until that update settles.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                _sharedConsentLookupPosted = false;
                return;
            }
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(StartLookup));
        }
        else if (_sharedOutboxUiContext is { } uiContext) uiContext.Post(_ => StartLookup(), null);
        else StartLookup();
    }

    private async void SharedRuntime_OnNetworkUnavailable(object? sender, EventArgs e)
    {
        if (sender is not SharedKnowledgeRuntime runtime || !ReferenceEquals(runtime, SharedRuntime) || _isDisposed) return;
        await RefreshSharedConsentAsync();
        await ApplyOfflineStatusAsync(runtime);
    }

    private async void SharedOutbox_OnChanged(object? sender, EventArgs e)
        => await RefreshSharedOutboxStatusAsync();

    private async void SharedOutbox_OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsScanning) or nameof(IsScanQueued) or nameof(IsAnalyzing)
            or nameof(IsRefreshing) or nameof(IsReanalysisSelectionMode) or nameof(IsLoadingSharedExperience))
            TryQueuePendingSharedConsentLookup();
        if (e.PropertyName is nameof(IsAnalyzing) or nameof(IsScanning) or nameof(IsRefreshing) or nameof(AiPhaseText))
            await RefreshSharedOutboxStatusAsync();
    }

    public async Task RefreshSharedOutboxStatusAsync()
    {
        // Starting work also invalidates an older read that is already waiting to reach the UI.
        var revision = Interlocked.Increment(ref _sharedOutboxRefreshRevision);
        var runtime = SharedRuntime;
        var uiContext = _sharedOutboxUiContext;
        if (runtime is null || _isDisposed) return;
        try
        {
            var uploadStatus = await runtime.Outbox.GetUploadStatusAsync().ConfigureAwait(false);
            var consent = await runtime.Store.GetSnapshotAsync().ConfigureAwait(false);
            void Apply()
            {
                if (_isDisposed || revision != Interlocked.Read(ref _sharedOutboxRefreshRevision)
                    || !ReferenceEquals(runtime, SharedRuntime)) return;
                _sharedUploadStatus = uploadStatus;
                NotifySharedUploadFooter();
                // Queue state is global to the runtime, not to a scan snapshot.
                // Keep the independent pending reminder live in every tab while
                // preserving scan/analysis progress and offline details below.
                if (!CanPresentSharedUpload || HasFooterOfflineStatus) return;
                string RenderNotice() => RenderSharedUploadNotice(uploadStatus);
                var notice = RenderNotice();
                var status = RemoveSharedOutboxNotice(StatusText);
                if (notice.Length > 0)
                {
                    status = notice;
                }
                else if (status.Length == 0 && StatusText.Length > 0)
                    status = consent.CanShare ? L.T("本机待上传队列已清空。") : L.T("共享已停用，旧待上传记录已清除。");
                _sharedOutboxNoticeRenderer = RenderNotice;
                _includeSharedOutboxNotice = true;
                RefreshSharedSummaryLanguage();
                if (status != StatusText)
                {
                    StatusText = status;
                    if (CurrentRoot is not null) SetAnalysisFeedback(CurrentRoot.Path, status);
                }
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(Apply).Task.ConfigureAwait(false);
            else if (uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, uiContext))
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                uiContext.Post(_ =>
                {
                    try { Apply(); completion.SetResult(); }
                    catch (Exception ex) { completion.SetException(ex); }
                }, null);
                await completion.Task.ConfigureAwait(false);
            }
            else Apply();
        }
        catch (Exception) { /* Optional upload feedback must not interrupt scanning or analysis. */ }
    }

    private string RemoveSharedOutboxNotice(string text)
    {
        if (_lastSharedOutboxNotice.Length > 0) text = text.Replace(_lastSharedOutboxNotice, "", StringComparison.Ordinal);
        // A report can contain a pending snapshot even when every busy-time UI read was deferred.
        // Only this exact durable-pending template is live state; retain other failure evidence.
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"上传至云端经验库尚未完成：[0-9]+ 项已保存到本机待上传队列，将在网络恢复后自动重试。", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"Upload to the shared experience library is incomplete\. [0-9]+ items are saved in the local upload queue and will retry automatically when the connection returns\.", "");
        return string.Join("\n", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    private static bool SameSharedConsent(SharedConsentSnapshot? left, SharedConsentSnapshot? right)
        => left is { CanShare: true } && right is { CanShare: true }
            && left.Epoch == right.Epoch && left.EnabledAt == right.EnabledAt
            && left.AgreementVersion == right.AgreementVersion && left.PolicyRevision == right.PolicyRevision
            && left.ShareHistory == right.ShareHistory && left.Installation == right.Installation
            && left.ExcludedPaths.SequenceEqual(right.ExcludedPaths, StringComparer.OrdinalIgnoreCase);

    private void ClearSharedMissingState()
    {
        _sharedMissingPaths.Clear();
        _sharedMissingSnapshot = null;
        _sharedMissingConsent = null;
        _sharedLookupCapturedAt = default;
        _sharedMissingIsPathPurpose = false;
        _sharedMissingReuseScopePath = null;
        OnPropertyChanged(nameof(HasSelectedSharedMissing));
        OnPropertyChanged(nameof(SelectedSharedMissingText));
    }

    private async Task BindSharedMissingPathsAsync(FileSystemNode snapshot, SharedConsentSnapshot? capturedConsent,
        IReadOnlyList<string> missingPaths, bool pathPurposeLookup = false, string? lookupScopePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshSharedConsentAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(ScanRoot, snapshot) || !SameSharedConsent(capturedConsent, _sharedConsent)) return;
        _sharedMissingPaths.Clear();
        foreach (var path in missingPaths)
            if (_scanNodeIndex.FindNode(path) is not null
                && !SharedFingerprintExtractor.IsExcluded(path, _sharedConsent!.ExcludedPaths)) _sharedMissingPaths.Add(path);
        _sharedMissingSnapshot = snapshot;
        _sharedMissingConsent = capturedConsent;
        if (pathPurposeLookup) _sharedLookupCapturedAt = DateTimeOffset.UtcNow;
        _sharedMissingIsPathPurpose = pathPurposeLookup;
        _sharedMissingReuseScopePath = pathPurposeLookup ? lookupScopePath : null;
        NotifySelectedAiCard();
    }

    private AiSharedLookupReuse? CaptureSharedAnalysisReuse(FileSystemNode snapshot)
    {
        if (!ReferenceEquals(snapshot, _sharedMissingSnapshot)
            || !SameSharedConsent(_sharedMissingConsent, _sharedConsent)
            || _sharedLookupCapturedAt < DateTimeOffset.UtcNow.AddMinutes(-2)) return null;
        var results = AnalysisItems.Select(item => item.Node.Analysis)
            .Where(result => result is not null && IsUsableKnownCloudResult(result))
            .ToDictionary(result => result!.Path, result => result!, StringComparer.Ordinal);
        return new(_sharedConsent!, results, _sharedMissingIsPathPurpose
            ? _sharedMissingPaths.Where(path => _sharedMissingReuseScopePath is null
                || IsWithinSharedLookupScope(path, _sharedMissingReuseScopePath)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal), _sharedLookupCapturedAt);
    }

    public async Task<bool> ApplySharedLookupResultAsync(FileSystemNode scanSnapshot, SharedConsentSnapshot capturedConsent,
        SharedResolutionBatch resolution, IReadOnlyDictionary<string, AiAnalysisMemoryEntry>? capturedLocalEntries = null,
        string? lookupScopePath = null, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || IsAnalyzing || IsReanalysisSelectionMode || cancellationToken.IsCancellationRequested) return false;
        await RefreshSharedConsentAsync();
        if (_isDisposed || IsAnalyzing || IsReanalysisSelectionMode || !ReferenceEquals(ScanRoot, scanSnapshot)
            || !SameSharedConsent(capturedConsent, _sharedConsent) || cancellationToken.IsCancellationRequested) return false;
        // A local rematch replaces only that region's negative answers. Snapshot
        // mapping resets the binding, so capture unrelated answers before mapping.
        var preservedMissing = lookupScopePath is not null && ReferenceEquals(_sharedMissingSnapshot, scanSnapshot)
            && SameSharedConsent(_sharedMissingConsent, capturedConsent)
            ? _sharedMissingPaths.Where(path => !IsWithinSharedLookupScope(path, lookupScopePath)).ToArray()
            : Array.Empty<string>();
        // A partial lookup can still provide valid cloud hits. Retain them
        // while the user decides whether to retry the unread items.
        var applicable = resolution.ResultsByPath.Values.Where(result =>
            IsUsableKnownCloudResult(result)
            && (lookupScopePath is null || IsWithinSharedLookupScope(result.Path, lookupScopePath))
            && _scanNodeIndex.FindNode(result.Path) is { } node
            && (node.Analysis is null || node.Analysis.LooksLikeFallback()
                || node.Analysis.Origin is AiAnalysisResultOrigin.SharedExperience or AiAnalysisResultOrigin.AiAnalyzed
                    or AiAnalysisResultOrigin.CacheReused)).ToArray();
        var mappedRoot = applicable.Length == 0 ? scanSnapshot
            : await Task.Run(() => ApplyAnalysisResults(scanSnapshot, applicable, cancellationToken), cancellationToken);
        await RefreshSharedConsentAsync();
        if (_isDisposed || IsAnalyzing || IsReanalysisSelectionMode || !ReferenceEquals(ScanRoot, scanSnapshot)
            || !SameSharedConsent(capturedConsent, _sharedConsent) || cancellationToken.IsCancellationRequested) return false;
        if (!ReferenceEquals(mappedRoot, scanSnapshot))
        {
            if (!await ApplySnapshotAsync(mappedRoot, canApplySnapshot: () => !_isDisposed && !IsAnalyzing && !IsReanalysisSelectionMode
                && !cancellationToken.IsCancellationRequested && ReferenceEquals(ScanRoot, scanSnapshot)
                && SameSharedConsent(capturedConsent, _sharedConsent), cancellationToken: cancellationToken)) return false;
            RebuildAnalysisItems(mappedRoot);
        }
        if (capturedLocalEntries is not null && SharedRuntime is { } runtime)
        {
            var duplicates = applicable.Where(result => IsUsableKnownCloudResult(result, requireOriginalText: true)
                    && capturedLocalEntries.ContainsKey(result.Path))
                .Select(result => capturedLocalEntries[result.Path])
                // The original still owns crash recovery until its contribution is
                // durably queued or resolved; a cloud hit is not its upload receipt.
                .Where(entry => !entry.SharedContributionPending).ToArray();
            if (duplicates.Length > 0)
            {
                await RefreshSharedConsentAsync();
                if (!_isDisposed && !cancellationToken.IsCancellationRequested && !IsAnalyzing && !IsReanalysisSelectionMode && ReferenceEquals(ScanRoot, mappedRoot)
                    && ReferenceEquals(runtime, SharedRuntime) && SameSharedConsent(capturedConsent, _sharedConsent))
                {
                    try { await runtime.Memory.RemoveEntriesIfUnchangedAsync(scanSnapshot.Path, duplicates, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { /* A cloud read remains usable when optional cache cleanup cannot be saved. */ }
                }
            }
        }
        await BindSharedMissingPathsAsync(mappedRoot, capturedConsent,
            preservedMissing.Concat(resolution.Unavailable ? [] : resolution.MissingPaths).ToArray(),
            pathPurposeLookup: true, lookupScopePath: lookupScopePath, cancellationToken: cancellationToken);
        if (_isDisposed || IsAnalyzing || IsReanalysisSelectionMode || !ReferenceEquals(ScanRoot, mappedRoot)
            || !SameSharedConsent(capturedConsent, _sharedConsent) || cancellationToken.IsCancellationRequested) return false;
        SetSharedSummary(() => L.F($"云端经验库匹配 {applicable.Length} 项。")
            + (resolution.Unavailable ? L.T(" 部分查询失败，请稍后重试。") : string.Empty));
        if (resolution.Unavailable) StatusText = SharedExperienceSummaryText;
        if (!resolution.Unavailable && resolution.FailedCount == 0)
        {
            IsOfflineAlertVisible = false;
            ConsumePendingSharedConsentLookup();
        }
        return true;
    }

    private static bool IsUsableKnownCloudResult(AiAnalysisResult result, bool requireOriginalText = false)
    {
        if (result.Origin != AiAnalysisResultOrigin.SharedExperience || result.LooksLikeFallback()
            || result.SharedEntry is not { } shared || shared.RuleVersion is not (2 or 3) || shared.Revision <= 0
            || shared.Purpose == "unknown" || !SharedFingerprintProtocol.Purposes.Contains(shared.Purpose)
            || shared.ConfidenceLevel is not ("High" or "Medium")
            || shared.ExpiresAt <= DateTimeOffset.UtcNow || shared.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(1)) return false;
        var parts = shared.Key.Split('|');
        if (parts.Length != 4) return false;
        var fingerprint = new SharedFingerprint(parts[0], parts[1], parts[2], parts[3]);
        if (!(SharedFingerprintProtocol.CanReuse(fingerprint) && !shared.IsOwnObservation
            || shared.IsOwnObservation && SharedFingerprintProtocol.IsOwnObservation(fingerprint))) return false;
        return !requireOriginalText || result.ShortPurpose.Length > 0 && result.Reason.Length > 0
            && result.ShortPurposeEn.Length > 0 && result.ReasonEn.Length > 0
            && SharedPurposeText.IsValidPair(result.ShortPurpose, result.Reason)
            && SharedPurposeText.IsValidEnglishPair(result.ShortPurposeEn, result.ReasonEn);
    }
}

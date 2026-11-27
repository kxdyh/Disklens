using DiskLens.App.Models;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isHomeWorkspace;
    private bool _isScanQueued;
    private bool _isScanNavigationNoticeOpen;
    private bool _isDisposed;
    private double _scanProgressPercent;
    private bool _isScanProgressIndeterminate = true;
    private long? _scanVolumeUsedBytes;
    private long _scanProgressBytes;

    public ScanWorkspaceViewModel? Workspace { get; private set; }

    public bool IsHomeWorkspace => _isHomeWorkspace;

    public bool IsScanQueued => _isScanQueued;

    public double ScanProgressPercent => _scanProgressPercent;

    public bool IsScanProgressIndeterminate => IsScanning && _isScanProgressIndeterminate;

    public string ScanProgressDescriptionText => _scanVolumeUsedBytes is > 0
        ? L.F($"已扫描 {FormatBytes(_scanProgressBytes)} / 磁盘已用 {FormatBytes(_scanVolumeUsedBytes.Value)}（{_scanProgressPercent:0.##}%）\n按扫描开始时的磁盘已用空间计算；文件夹扫描同样使用所在磁盘的已用空间。done 表示遍历完成，不代表已覆盖整盘全部占用。")
        : L.T("无法读取所在磁盘的已用空间，暂不显示百分比；done 表示所选位置遍历完成。");

    private void ResetScanProgress(long? volumeUsedBytes = null)
    {
        // Freeze each task's denominator at scan start. Navigating, other tasks,
        // and later drive-capacity refreshes must not change this task's ratio.
        _scanVolumeUsedBytes = volumeUsedBytes;
        _scanProgressBytes = 0;
        _scanProgressPercent = 0;
        _isScanProgressIndeterminate = ScanStorageProgress.CalculatePercent(0, volumeUsedBytes) is null;
        OnPropertyChanged(nameof(ScanProgressPercent));
        OnPropertyChanged(nameof(IsScanProgressIndeterminate));
        OnPropertyChanged(nameof(ScanProgressDescriptionText));
    }

    private void ApplyScanStorageProgress(long discoveredBytes)
    {
        if (_isDisposed || !IsScanning) return;
        _scanProgressBytes = Math.Max(0, discoveredBytes);
        var percent = ScanStorageProgress.CalculatePercent(_scanProgressBytes, _scanVolumeUsedBytes);
        _isScanProgressIndeterminate = percent is null;
        SetProperty(ref _scanProgressPercent, percent ?? 0, nameof(ScanProgressPercent));
        OnPropertyChanged(nameof(IsScanProgressIndeterminate));
        OnPropertyChanged(nameof(ScanProgressDescriptionText));
    }

    private void CompleteScanProgress()
    {
        _isScanProgressIndeterminate = false;
        OnPropertyChanged(nameof(IsScanProgressIndeterminate));
    }

    public bool IsScanNavigationNoticeOpen
    {
        get => _isScanNavigationNoticeOpen;
        set
        {
            if (SetProperty(ref _isScanNavigationNoticeOpen, value && IsScanning && !_isDisposed))
            {
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
            }
        }
    }

    private bool CanStartNewScan => !_isDisposed && (Workspace is not null || (!IsScanning && !IsAnalyzing));

    internal ScanTaskState LastScanOutcome { get; private set; } = ScanTaskState.Completed;

    internal void AttachWorkspace(ScanWorkspaceViewModel workspace, bool isHome)
    {
        Workspace = workspace;
        _isHomeWorkspace = isHome;
        if (!isHome)
        {
            IsExplorerGuideOpen = false;
        }
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(IsHomeWorkspace));
        OnPropertyChanged(nameof(HasScanWorkspace));
        RaiseCommandStateChanged();
    }

    internal void PrepareQueuedScan(string path)
    {
        ResetScanProgress();
        // The path belongs to this task. It is never copied to other open results.
        _rootPath = path;
        _scanCurrentPathText = path;
        _isScanQueued = true;
        StatusText = L.T("扫描任务已排队，最多同时扫描 3 个位置。");
        OnPropertyChanged(nameof(RootPath));
        NotifyQueuedStateChanged();
    }

    internal bool TakePendingScanGuide()
    {
        var shouldContinue = IsExplorerGuideOpen && ExplorerGuideStep == 1;
        if (shouldContinue)
        {
            IsExplorerGuideOpen = false;
            SetExplorerGuideStep(0);
        }
        return shouldContinue;
    }

    internal void ContinueScanGuide(bool shouldContinue) => _isAwaitingGuideScan = shouldContinue;

    internal Task RunOwnedScanAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.IO.Directory.Exists(path))
        {
            throw new System.IO.DirectoryNotFoundException(L.T("扫描位置已不可用。"));
        }
        LastScanOutcome = ScanTaskState.Completed;
        return RunScanAtPathAsync(path, cancellationToken);
    }

    internal void BeginScheduledScan()
    {
        _isScanQueued = false;
        IsScanning = true;
        NotifyQueuedStateChanged();
    }

    internal void FinishScheduledScan(ScanTaskState state, string? error = null)
    {
        if (state == ScanTaskState.Completed)
        {
            CompleteScanProgress();
        }
        else
        {
            _isScanProgressIndeterminate = false;
        }
        _isScanQueued = false;
        IsScanning = false;
        LastScanOutcome = state;
        if (!_isDisposed && state == ScanTaskState.Canceled)
        {
            StatusText = L.T("扫描已取消。");
            SetScanTerminalStatus(L.T("扫描已取消"), L.T("本次扫描已取消。可以从新扫描选择其他位置。"));
        }
        else if (!_isDisposed && state == ScanTaskState.Failed && (error is not null || _scanTerminalHeadlineText is null))
        {
            StatusText = L.F($"扫描失败：{error ?? "所选位置无法读取"}");
            SetScanTerminalStatus(L.T("扫描失败"), StatusText);
        }
        if (!_isDisposed)
        {
            if (state is ScanTaskState.Completed or ScanTaskState.Partial)
            {
                CompleteGuideScan();
            }
            else if (state is ScanTaskState.Canceled or ScanTaskState.Failed)
            {
                RestoreGuideAfterUnsuccessfulScan();
            }
        }
        NotifyQueuedStateChanged();
    }

    private void NotifyQueuedStateChanged()
    {
        OnPropertyChanged(nameof(IsScanQueued));
        OnPropertyChanged(nameof(IsScanProgressIndeterminate));
        OnPropertyChanged(nameof(HasScanWorkspace));
        OnPropertyChanged(nameof(CurrentLocationText));
        OnPropertyChanged(nameof(ScanCurrentPathText));
        OnPropertyChanged(nameof(ScanStatusHeadlineText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
        OnPropertyChanged(nameof(ShowCancelAction));
        OnPropertyChanged(nameof(VisualExplorerEmptyText));
        OnPropertyChanged(nameof(FooterText));
        RaiseCommandStateChanged();
    }

    private void ReportUnavailableScanLocation()
    {
        _scanLocationErrorText = L.T("所选位置已不可用，请重新选择位置。");
        OnPropertyChanged(nameof(ScanLocationPickerMessageText));
        StatusText = _scanLocationErrorText;
        RefreshAvailableDrives();
        IsScanLocationPickerOpen = true;
    }

    internal AppSettings CaptureSettings() => new(
        RootPath, AnalysisThresholdMb, VisualFolderDisplayThresholdMb,
        SelectedProviderKind, AiEndpoint, AiModel, MaxAiBatchSize,
        IsAiTokenSavingModeEnabled, SelectedThemeId, HasCompletedExplorerGuide,
        IsReduceMotionEnabled, MaxAiAnalysisTokens, AiReasoningEffort, SelectedAiConnectionId, HideAiDisclaimer, SelectedLanguage);

    internal void ApplySharedSettings(AppSettings settings, string apiKey)
    {
        var thresholdChanged = _visualFolderDisplayThresholdMb != settings.VisualFolderDisplayThresholdMb;
        var hadLegacyOllamaConfiguration = _legacyOllamaConfiguration is not null;
        if (_selectedProviderKind == AiProviderKind.Ollama)
        {
            _legacyOllamaConfiguration = (_aiEndpoint, _aiModel, _aiReasoningEffort);
        }
        _analysisThresholdMb = settings.AnalysisThresholdMb;
        _visualFolderDisplayThresholdMb = settings.VisualFolderDisplayThresholdMb;
        _selectedProviderKind = settings.ProviderKind;
        _selectedAiConnectionId = settings.AiConnectionProfileId;
        _aiEndpoint = settings.AiEndpoint;
        _aiModel = settings.AiModel;
        _aiReasoningEffort = settings.AiReasoningEffort;
        if (settings.ProviderKind == AiProviderKind.Ollama)
        {
            _legacyOllamaConfiguration = (_aiEndpoint, _aiModel, _aiReasoningEffort);
        }
        _apiKey = apiKey;
        _maxAiBatchSize = AppSettings.NormalizeAiBatchSize(settings.MaxAiBatchSize);
        _isAiTokenSavingModeEnabled = settings.IsAiTokenSavingModeEnabled;
        _maxAiAnalysisTokens = settings.MaxAiAnalysisTokens;
        _selectedThemeId = ThemeCatalog.Normalize(settings.ThemeId);
        _hasCompletedExplorerGuide = settings.HasCompletedExplorerGuide;
        _isReduceMotionEnabled = settings.IsReduceMotionEnabled ?? _isReduceMotionEnabled;
        _hideAiDisclaimer = settings.HideAiDisclaimer;
        if (!hadLegacyOllamaConfiguration && _legacyOllamaConfiguration is not null)
        {
            // Publish the newly available choice before the selected connection.
            OnPropertyChanged(nameof(AiConnectionOptions));
        }
        // Explicit notification lets the window refresh application theme resources.
        OnPropertyChanged(nameof(SelectedThemeId));
        OnPropertyChanged(string.Empty);
        RaiseCommandStateChanged();
        if (thresholdChanged && !_isDisposed)
        {
            RebuildVisualExplorerItems();
        }
    }

    internal void ReportWorkspaceSettingsError(string? message) => ReportSettingsSaveError(message);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _recycleBinService.StateChanged -= RecycleService_OnStateChanged;
        CancelSharedLookup();
        if (SharedRuntime is not null)
        {
            SharedRuntime.Settings.Changed -= SharedSettings_OnChanged;
            SharedRuntime.Outbox.Changed -= SharedOutbox_OnChanged;
            SharedRuntime.ConnectionChanged -= SharedRuntime_OnConnectionChanged;
        }
        PropertyChanged -= SharedOutbox_OnViewModelChanged;
        ClearSharedMissingState();
        ResetReanalysisSelection();
        DisposeAutomaticRefresh();
        CancelRefresh();
        EndAiProgress();
        _acceptIntermediateSnapshots = false;
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource = null; // Its scan owns disposal after cancellation settles.
        _analysisCancellationTokenSource?.Cancel();
        _navigationHistory.Clear();
        _scanRoot = null;
        _currentRoot = null;
        _selectedNode = null;
        _selectedAnalysisItem = null;
        _scanNodeIndex = FileSystemNodeIndex.Empty;
        _visibleAnalysisItems = [];
        _visualExplorerItems = [];
        _visibleVisualExplorerItems = [];
        _treemapDisplayRoot = null;
        _conclusionFilterRoot = null;
        _conclusionFilterItems = [];
        _filteredOccupancyBytes = 0;
        _cachedConclusionFilterMode = null;
        _expandedAggregateRoot = null;
        AnalysisItems.Clear();
        _analysisItemIndex.Clear();
        NotifySelectedAiCard();
        IsScanNavigationNoticeOpen = false;
    }
}

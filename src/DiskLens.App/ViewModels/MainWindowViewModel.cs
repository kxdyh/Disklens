using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DiskLens.App.Infrastructure;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;
using DiskLens.Core.SharedKnowledge;
using DiskLens.Core.Visualization;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DirectoryScanner _scanner;
    private readonly IFolderPickerService _folderPickerService;
    private readonly DeletionPolicyService _deletionPolicyService;
    private readonly IFileLocationService _fileLocationService;
    private readonly IRecycleBinService _recycleBinService;
    private readonly AiAnalysisService _aiAnalysisService;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSecretStore _secretStore;
    private readonly VisualExplorerBuilder _visualExplorerBuilder = new();
    private CancellationTokenSource? _scanCancellationTokenSource;
    private CancellationTokenSource? _analysisCancellationTokenSource;
    private bool _acceptIntermediateSnapshots;
    private FileSystemNodeIndex _scanNodeIndex = FileSystemNodeIndex.Empty;
    private readonly Stack<FileSystemNode> _navigationHistory = new();
    private FileSystemNode? _scanRoot;
    private FileSystemNode? _currentRoot;
    private FileSystemNode? _selectedNode;
    private AnalysisListItem? _selectedAnalysisItem;
    private IReadOnlyList<AnalysisListItem> _visibleAnalysisItems = [];
    private IReadOnlyList<VisualExplorerItem> _visualExplorerItems = [];
    private IReadOnlyList<VisualExplorerItem> _visibleVisualExplorerItems = [];
    private FileSystemNode? _treemapDisplayRoot;
    private string _rootPath;
    private long _analysisThresholdMb;
    private long _visualFolderDisplayThresholdMb;
    private AiProviderKind _selectedProviderKind;
    private string _selectedAiConnectionId;
    private (string Endpoint, string Model, string ReasoningEffort)? _legacyOllamaConfiguration;
    private string _aiEndpoint;
    private string _aiModel;
    private string _aiReasoningEffort;
    private string _apiKey = string.Empty;
    private bool _suspendSettingsSave;
    private bool _isSettingsOpen;
    private bool _isHelpOpen;
    private bool _isAiSetupOpen;
    private bool _isAiSetupSaving;
    private bool _aiSetupReturnsToSettings;
    private string _aiSetupPage = "Channels";
    private string _aiSetupEndpoint = string.Empty;
    private string _aiSetupModel = string.Empty;
    private string _aiSetupApiKey = string.Empty;
    private string _aiSetupReasoningEffort = string.Empty;
    private string _aiSetupErrorText = string.Empty;
    private bool _isExplorerGuideOpen;
    private bool _hasCompletedExplorerGuide;
    private bool _isDontShowExplorerGuideChecked;
    private bool _isScanLocationPickerOpen;
    private bool _isExternalLocationPickerOpen;
    private bool _isAwaitingGuideScan;
    private int _explorerGuideStep;
    private bool _isReduceMotionEnabled;
    private string _scanLocationErrorText = string.Empty;
    private int _maxAiBatchSize;
    private int _maxAiAnalysisTokens;
    private bool _isAiTokenSavingModeEnabled;
    private string _statusText = L.T("准备就绪");
    private string? _scanTerminalHeadlineText;
    private string? _scanTerminalDetailText;
    private string _settingsSaveErrorText = string.Empty;
    private bool _isScanning;
    private bool _isAnalyzing;
    private bool _isFocusMode;
    private bool _isSynchronizingSelection;
    private int _filesScanned;
    private int _directoriesScanned;
    private long _bytesDiscovered;
    private int _estimatedStorageItemCount;
    private string _scanCurrentPathText = L.T("等待扫描任务...");
    private TimeSpan _lastScanDuration;
    private long? _volumeUsedBytes;
    private long? _volumeCapacityBytes;
    private string _lastAnalyzedRootPath = string.Empty;
    private string _lastAnalysisSummaryText = string.Empty;
    private string? _expandedVisualPath;
    private FileSystemNode? _expandedAggregateRoot;
    private string _displaySortMode = VisualExplorerPresentation.DefaultSortMode;
    private string _displayFilterMode = VisualExplorerPresentation.DefaultFilterMode;
    private string _selectedThemeId;

    public MainWindowViewModel(
        DirectoryScanner scanner,
        IFolderPickerService folderPickerService,
        DeletionPolicyService deletionPolicyService,
        IFileLocationService fileLocationService,
        IRecycleBinService recycleBinService,
        AiAnalysisService aiAnalysisService,
        AppSettingsStore settingsStore,
        AppSecretStore secretStore,
        AppSettings settings,
        string initialApiKey)
    {
        _scanner = scanner;
        _folderPickerService = folderPickerService;
        _deletionPolicyService = deletionPolicyService;
        _fileLocationService = fileLocationService;
        _recycleBinService = recycleBinService;
        _aiAnalysisService = aiAnalysisService;
        _settingsStore = settingsStore;
        _secretStore = secretStore;

        _rootPath = settings.RootPath;
        _analysisThresholdMb = Math.Max(1, settings.AnalysisThresholdMb);
        _visualFolderDisplayThresholdMb = Math.Max(1, settings.VisualFolderDisplayThresholdMb <= 0 ? 512 : settings.VisualFolderDisplayThresholdMb);
        _selectedProviderKind = settings.ProviderKind;
        _selectedAiConnectionId = AiConnectionOption.ResolveProfile(settings);
        _aiEndpoint = settings.AiEndpoint;
        _aiModel = settings.AiModel;
        _aiReasoningEffort = settings.AiReasoningEffort;
        if (settings.ProviderKind == AiProviderKind.Ollama)
        {
            _legacyOllamaConfiguration = (_aiEndpoint, _aiModel, _aiReasoningEffort);
        }
        _apiKey = initialApiKey;
        _maxAiBatchSize = AppSettings.NormalizeAiBatchSize(settings.MaxAiBatchSize);
        _isAiTokenSavingModeEnabled = settings.IsAiTokenSavingModeEnabled;
        _maxAiAnalysisTokens = Math.Clamp(settings.MaxAiAnalysisTokens <= 0 ? 100_000 : settings.MaxAiAnalysisTokens, MinAiAnalysisTokens, MaxAiAnalysisTokensLimit);
        _selectedThemeId = ThemeCatalog.Normalize(settings.ThemeId);
        _hasCompletedExplorerGuide = settings.HasCompletedExplorerGuide;
        _isDontShowExplorerGuideChecked = settings.HasCompletedExplorerGuide;
        _isReduceMotionEnabled = settings.IsReduceMotionEnabled ?? !SystemParameters.ClientAreaAnimation;
        _isExplorerGuideOpen = !_hasCompletedExplorerGuide;
        _hideAiDisclaimer = settings.HideAiDisclaimer;

        AnalysisItems = new ObservableCollection<AnalysisListItem>();
        AvailableDrives = new ObservableCollection<DriveScanOption>(LoadAvailableDrives());
        ScanCommand = new AsyncRelayCommand(StartScanAsync, () => CanStartNewScan && !IsScanLocationPickerOpen && !IsSettingsOpen && !IsHelpOpen && !IsAiSetupOpen && !IsScanNavigationNoticeOpen && !IsScanReportOpen);
        SelectScanRootCommand = new AsyncRelayCommand(SelectScanRootAsync, parameter => parameter is string && CanSelectScanRoot);
        BrowseScanFolderCommand = new AsyncRelayCommand(parameter => BrowseScanFolderAsync(parameter), _ => CanBrowseForLocation);
        BrowseFolderFromHomeCommand = new AsyncRelayCommand(BrowseFolderFromHomeAsync, () => CanBrowseFromHome);
        CloseScanLocationPickerCommand = new RelayCommand(CloseScanLocationPicker);
        CancelScanCommand = new RelayCommand(CancelCurrentWork, () => IsScanning || IsAnalyzing || IsScanQueued || IsRefreshing);
        ShowScanNavigationNoticeCommand = new RelayCommand(() => IsScanNavigationNoticeOpen = true, () => IsScanning);
        CloseScanNavigationNoticeCommand = new RelayCommand(() => IsScanNavigationNoticeOpen = false);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeOrConfigureAsync, () => ScanRoot is not null && !IsScanning && !IsAnalyzing && !IsRefreshing && !IsAiSetupOpen && !IsScanReportOpen && !IsReanalysisSelectionMode);
        OpenAiSetupCommand = new RelayCommand(OpenAiSetup, () => ScanRoot is not null && !IsScanning && !IsAnalyzing && !IsRefreshing && !IsAiSetupOpen && !IsScanReportOpen);
        ConfigureAiConnectionCommand = new RelayCommand(OpenAiSetupFromSettings, () => !IsScanning && !IsAnalyzing && !IsAiSetupOpen);
        CloseAiSetupCommand = new RelayCommand(CloseAiSetup, () => !IsAiSetupSaving);
        ChooseDeepSeekCommand = new RelayCommand(ChooseDeepSeekSetup, () => IsAiSetupOpen && ShowAiProviderChoices);
        ChooseCustomAiCommand = new RelayCommand(ChooseCustomAiSetup, () => IsAiSetupOpen && ShowAiProviderChoices);
        BackAiSetupCommand = new RelayCommand(BackAiSetup, () => IsAiSetupOpen && !ShowAiProviderChoices && !IsAiSetupSaving);
        SaveAiSetupAndAnalyzeCommand = new AsyncRelayCommand(SaveAiSetupAndAnalyzeAsync,
            () => IsAiSetupOpen && !IsAiSetupSaving && AiSetupCanSave);
        OpenLocationCommand = new AsyncRelayCommand(OpenLocationAsync, _ => CanOpenSelectedLocation());
        RecycleCommand = new AsyncRelayCommand(RecycleAsync, _ => CanRecycleSelectedNode());
        OpenAnalysisItemLocationCommand = new AsyncRelayCommand(OpenLocationAsync, item => ResolveTargetNode(item) is not null && CanOpenNodeLocation(ResolveTargetNode(item)!));
        RecycleAnalysisItemCommand = new AsyncRelayCommand(RecycleAsync, item => ResolveTargetNode(item) is not null && CanRecycleNode(ResolveTargetNode(item)!));
        ZoomIntoNodeCommand = new RelayCommand(
            parameter => ToggleVisualItem(parameter as FileSystemNode),
            parameter => parameter is FileSystemNode { IsDirectory: true } && CanNavigateVisualTree);
        ZoomOutCommand = new RelayCommand(ZoomOut, () => CanNavigateVisualTree && CanZoomOut);
        ResetZoomCommand = new RelayCommand(ResetZoom, () => CanNavigateVisualTree && ScanRoot is not null && !string.IsNullOrWhiteSpace(_expandedVisualPath));
        SelectThemeCommand = new RelayCommand(parameter =>
        {
            if (parameter is string themeId)
            {
                SelectedThemeId = themeId;
            }
        });
        OpenSettingsCommand = new RelayCommand(() =>
        {
            IsSettingsOpen = true;
        });
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        ResetExplorerGuideCommand = new RelayCommand(ResetExplorerGuide, () => !IsScanning && !IsAnalyzing);
        HelpCommand = new RelayCommand(() => IsHelpOpen = true);
        CloseHelpCommand = new RelayCommand(() => IsHelpOpen = false);
        ShowExplorerGuideCommand = new RelayCommand(() =>
        {
            IsHelpOpen = false;
            SetExplorerGuideStep(ScanRoot is null ? 0 : 2);
            IsExplorerGuideOpen = true;
        });
        DismissExplorerGuideCommand = new RelayCommand(DismissExplorerGuide);
        InitializeScanReportCommands();
        InitializeRefreshCommands();
        InitializeSharedKnowledgeCommands();
        InitializeSettingsNavigation();
    }

    public ObservableCollection<AnalysisListItem> AnalysisItems { get; }

    public ObservableCollection<DriveScanOption> AvailableDrives { get; }

    public bool NoAvailableDrives => AvailableDrives.Count == 0;

    public string NoDriveMessageText => NoAvailableDrives ? L.T("没有检测到已就绪的磁盘。可以改选文件夹。") : string.Empty;

    public string ScanLocationPickerMessageText => !string.IsNullOrWhiteSpace(_scanLocationErrorText)
        ? _scanLocationErrorText
        : NoDriveMessageText;

    public IReadOnlyList<AiConnectionOption> AiConnectionOptions => _legacyOllamaConfiguration is not null
        ? AiConnectionOption.WithLegacyOllama
        : AiConnectionOption.Standard;

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand SelectScanRootCommand { get; }

    public AsyncRelayCommand BrowseScanFolderCommand { get; }

    public AsyncRelayCommand BrowseFolderFromHomeCommand { get; }

    public RelayCommand CloseScanLocationPickerCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand ShowScanNavigationNoticeCommand { get; }

    public RelayCommand CloseScanNavigationNoticeCommand { get; }

    public AsyncRelayCommand AnalyzeCommand { get; }

    public RelayCommand OpenAiSetupCommand { get; }

    public RelayCommand ConfigureAiConnectionCommand { get; }

    public RelayCommand CloseAiSetupCommand { get; }

    public RelayCommand ChooseDeepSeekCommand { get; }

    public RelayCommand ChooseCustomAiCommand { get; }

    public RelayCommand BackAiSetupCommand { get; }

    public AsyncRelayCommand SaveAiSetupAndAnalyzeCommand { get; }

    public AsyncRelayCommand OpenLocationCommand { get; }

    public AsyncRelayCommand RecycleCommand { get; }

    public AsyncRelayCommand OpenAnalysisItemLocationCommand { get; }

    public AsyncRelayCommand RecycleAnalysisItemCommand { get; }

    public RelayCommand ZoomIntoNodeCommand { get; }

    public RelayCommand ZoomOutCommand { get; }

    public RelayCommand ResetZoomCommand { get; }

    public RelayCommand SelectThemeCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand CloseSettingsCommand { get; }

    public RelayCommand ResetExplorerGuideCommand { get; }

    public RelayCommand HelpCommand { get; }

    public RelayCommand CloseHelpCommand { get; }

    public RelayCommand ShowExplorerGuideCommand { get; }

    public RelayCommand DismissExplorerGuideCommand { get; }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        private set
        {
            if (SetProperty(ref _isHelpOpen, value))
            {
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
            }
        }
    }

    public bool HasCompletedExplorerGuide => _hasCompletedExplorerGuide;

    public bool IsExplorerGuideOpen
    {
        get => _isExplorerGuideOpen;
        set
        {
            if (SetProperty(ref _isExplorerGuideOpen, value))
            {
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                OnPropertyChanged(nameof(ShowScanProgressOverlay));
                OnPropertyChanged(nameof(IsTreemapContextMenuEnabled));
                RaiseCommandStateChanged();
            }
        }
    }

    public bool IsFocusModeToggleVisible =>
        (HasScanResult || IsScanning || IsFocusMode)
        && !IsScanLocationPickerOpen
        && !IsSettingsOpen
        && !IsHelpOpen
        && !IsAiSetupOpen
        && !IsScanNavigationNoticeOpen
        && !IsScanReportOpen
        && (!IsExplorerGuideOpen || ExplorerGuideStep == 2);

    public bool IsFocusModeNavigationVisible => IsFocusMode && IsFocusModeToggleVisible && !IsExplorerGuideOpen;

    // AI works on a captured scan snapshot; browsing does not replace that snapshot.
    private bool CanNavigateVisualTree => !IsScanning && !IsRefreshing && !IsExplorerGuideOpen
        && !IsSettingsOpen && !IsHelpOpen && !IsScanLocationPickerOpen && !IsAiSetupOpen && !IsScanNavigationNoticeOpen && !IsScanReportOpen && !IsScanQueued;

    public bool IsFocusModeToggleInteractive => IsFocusModeToggleVisible && !IsRefreshing;

    public bool ShowScanProgressOverlay => IsScanning && !IsFocusMode;

    public string ScanCurrentPathText => _scanCurrentPathText;

    public string ScanFilesCountText => _filesScanned.ToString("N0");

    public string ScanDirectoriesCountText => _directoriesScanned.ToString("N0");

    public string ScanDiscoveredSizeText => FormatBytes(GetDisplayedDiscoveredBytes());

    public bool IsTreemapContextMenuEnabled => !IsExplorerGuideOpen;

    public bool IsScanLocationPickerOpen
    {
        get => _isScanLocationPickerOpen;
        private set
        {
            if (SetProperty(ref _isScanLocationPickerOpen, value))
            {
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                RaiseCommandStateChanged();
            }
        }
    }

    private bool CanBrowseForLocation => IsScanLocationPickerOpen
                                         && !_isExternalLocationPickerOpen
                                         && CanStartNewScan;

    private bool CanBrowseFromHome => !_isExternalLocationPickerOpen
                                     && !IsScanLocationPickerOpen
                                     && !IsScanning
                                     && !IsAnalyzing
                                     && !IsExplorerGuideOpen
                                     && !IsSettingsOpen
                                     && !IsHelpOpen;

    private bool CanSelectScanRoot => IsScanLocationPickerOpen
                                      && !_isExternalLocationPickerOpen
                                      && CanStartNewScan;

    public int ExplorerGuideStep => _explorerGuideStep;

    public string ExplorerGuideStepText => ExplorerGuideStep switch { 0 => "01 / 03", 1 => "02 / 03", _ => "03 / 03" };

    public string ExplorerGuideTitleText => ExplorerGuideStep switch
    {
        0 => L.T("从新扫描开始"),
        1 => L.T("选择整盘，或指定文件夹"),
        _ => L.T("扫描完成，试试全屏浏览"),
    };

    public string ExplorerGuideBodyText => ExplorerGuideStep switch
    {
        0 => NoAvailableDrives
            ? L.T("启动时没有检测到已就绪磁盘。仍可点击这里，再选择文件夹进行扫描。")
            : L.T("每次扫描都从这里开始。点击高亮按钮后，先挑选一个磁盘。"),
        1 => NoAvailableDrives
            ? L.T("当前没有已就绪的磁盘。请插入并连接磁盘后重试，或选择文件夹。")
            : L.T("点磁盘卡片扫描整个盘；也可选择文件夹进行扫描。"),
        _ => L.T("扫描结果已就绪。点击文件块工具栏右侧的全屏按钮，进入全屏浏览；再点一次即可返回工作台。"),
    };

    public bool IsReduceMotionEnabled
    {
        get => _isReduceMotionEnabled;
        set
        {
            if (SetProperty(ref _isReduceMotionEnabled, value))
            {
                OnPropertyChanged(nameof(IsViewportMotionEnabled));
                OnPropertyChanged(nameof(IsScanRunnerActive));
                OnPropertyChanged(nameof(IsAiActivityAnimated));
                SaveSettingsDeferred();
            }
        }
    }

    public bool IsViewportMotionEnabled => !IsReduceMotionEnabled;

    public bool IsScanRunnerActive => IsScanning && !IsFocusMode && IsViewportMotionEnabled;

    public string DisplaySortMode
    {
        get => _displaySortMode;
        set
        {
            if (SetProperty(ref _displaySortMode, string.IsNullOrWhiteSpace(value) ? VisualExplorerPresentation.DefaultSortMode : value))
            {
                RebuildVisibleVisualExplorerItems();
            }
        }
    }

    public IReadOnlyList<string> DisplaySortModes { get; } = ["大小", "名称", "项目数"];

    public string DisplayFilterMode
    {
        get => _displayFilterMode;
        set
        {
            if (SetProperty(ref _displayFilterMode, string.IsNullOrWhiteSpace(value) ? VisualExplorerPresentation.DefaultFilterMode : value))
            {
                RebuildVisibleVisualExplorerItems();
            }
        }
    }

    public IReadOnlyList<string> DisplayFilterModes { get; } = ["全部项目", "文件夹", "文件", "更多项目"];

    public IReadOnlyList<ThemeOption> ThemeOptions => ThemeCatalog.Options;

    public string SelectedThemeId
    {
        get => _selectedThemeId;
        set
        {
            var normalizedThemeId = ThemeCatalog.Normalize(value);
            if (SetProperty(ref _selectedThemeId, normalizedThemeId))
            {
                SaveSettingsDeferred();
                OnPropertyChanged(nameof(IsNightThemeSelected));
                OnPropertyChanged(nameof(IsWarmThemeSelected));
                OnPropertyChanged(nameof(IsDayThemeSelected));
            }
        }
    }

    public bool IsNightThemeSelected => SelectedThemeId == "01";

    public bool IsWarmThemeSelected => SelectedThemeId == "02";

    public bool IsDayThemeSelected => SelectedThemeId == "03";

    public string RootPath
    {
        get => _rootPath;
        set
        {
            if (SetProperty(ref _rootPath, value))
            {
                RefreshVolumeCapacity(value);
                SaveSettingsDeferred();
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(CurrentLocationText));
                OnPropertyChanged(nameof(ScanStatusDetailText));
                OnPropertyChanged(nameof(VolumeUsagePercent));
            }
        }
    }

    public long AnalysisThresholdMb
    {
        get => _analysisThresholdMb;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref _analysisThresholdMb, normalized))
            {
                NotifySelectedAiCard();
                SaveSettingsDeferred();
            }
        }
    }

    public long VisualFolderDisplayThresholdMb
    {
        get => _visualFolderDisplayThresholdMb;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref _visualFolderDisplayThresholdMb, normalized))
            {
                SaveSettingsDeferred();
                RebuildVisualExplorerItems();
                OnPropertyChanged(nameof(VisualFolderDisplayThresholdSummaryText));
            }
        }
    }

    public string SelectedAiConnectionId
    {
        get => _selectedAiConnectionId;
        set
        {
            if (_selectedAiConnectionId == value || !AiConnectionOptions.Any(option => option.Id == value))
            {
                return;
            }

            // Apply the connection and its defaults as one persisted change.
            // The API key remains available when changing or disabling a connection.
            var wasSuspended = _suspendSettingsSave;
            _suspendSettingsSave = true;
            try
            {
                if (IsLegacyOllamaConnection)
                {
                    _legacyOllamaConfiguration = (AiEndpoint, AiModel, AiReasoningEffort);
                }

                SetProperty(ref _selectedAiConnectionId, value);
                var provider = value switch
                {
                    AiConnectionOption.DisabledId => AiProviderKind.Disabled,
                    AiConnectionOption.LegacyOllamaId => AiProviderKind.Ollama,
                    _ => AiProviderKind.OpenAiCompatible,
                };
                SetProperty(ref _selectedProviderKind, provider, nameof(SelectedProviderKind));
                if (value == AiConnectionOption.DeepSeekId)
                {
                    AiEndpoint = AiConnectionOption.DeepSeekEndpoint;
                    AiModel = AiConnectionOption.DeepSeekModel;
                    AiReasoningEffort = AiConnectionOption.DeepSeekReasoningEffort;
                }
                else if (value == AiConnectionOption.LegacyOllamaId && _legacyOllamaConfiguration is { } legacy)
                {
                    AiEndpoint = legacy.Endpoint;
                    AiModel = legacy.Model;
                    AiReasoningEffort = legacy.ReasoningEffort;
                }
            }
            finally
            {
                _suspendSettingsSave = wasSuspended;
            }

            NotifyAiConnectionChanged();
            SaveSettingsDeferred();
        }
    }

    public bool IsDeepSeekConnection => SelectedAiConnectionId == AiConnectionOption.DeepSeekId;

    public bool IsCustomAiConnection => SelectedAiConnectionId == AiConnectionOption.CustomId;

    public bool IsLegacyOllamaConnection => SelectedAiConnectionId == AiConnectionOption.LegacyOllamaId;

    public bool IsAiConnectionEnabled => SelectedAiConnectionId != AiConnectionOption.DisabledId;

    public bool HasAiConnectionConfiguration => SelectedProviderKind != AiProviderKind.Disabled
        || !string.IsNullOrWhiteSpace(AiEndpoint)
        || !string.IsNullOrWhiteSpace(AiModel);

    public bool ShowAiConnectionSetupAction => !HasAiConnectionConfiguration;

    private void NotifyAiConfigurationChanged()
    {
        OnPropertyChanged(nameof(HasAiConnectionConfiguration));
        OnPropertyChanged(nameof(ShowAiConnectionSetupAction));
    }

    private void NotifyAiConnectionChanged()
    {
        OnPropertyChanged(nameof(SelectedAiConnectionId));
        OnPropertyChanged(nameof(IsDeepSeekConnection));
        OnPropertyChanged(nameof(IsCustomAiConnection));
        OnPropertyChanged(nameof(IsLegacyOllamaConnection));
        OnPropertyChanged(nameof(IsAiConnectionEnabled));
        OnPropertyChanged(nameof(IsAiProviderConfigured));
        OnPropertyChanged(nameof(AnalysisDescriptionText));
        OnPropertyChanged(nameof(ShowAnalyzeAction));
        OnPropertyChanged(nameof(AiResultsEmptyText));
        NotifyAiConfigurationChanged();
        RaiseCommandStateChanged();
    }

    public AiProviderKind SelectedProviderKind
    {
        get => _selectedProviderKind;
        set
        {
            if (SetProperty(ref _selectedProviderKind, value))
            {
                _selectedAiConnectionId = AiConnectionOption.ResolveProfile(new AppSettings(
                    RootPath, AnalysisThresholdMb, VisualFolderDisplayThresholdMb, value, AiEndpoint, AiModel));
                NotifyAiConnectionChanged();
                SaveSettingsDeferred();
            }
        }
    }

    public string AiEndpoint
    {
        get => _aiEndpoint;
        set
        {
            if (SetProperty(ref _aiEndpoint, value))
            {
                NotifyAiConfigurationChanged();
                SaveSettingsDeferred();
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsAiProviderConfigured));
                OnPropertyChanged(nameof(AiResultsEmptyText));
            }
        }
    }

    public string AiModel
    {
        get => _aiModel;
        set
        {
            if (SetProperty(ref _aiModel, value))
            {
                NotifyAiConfigurationChanged();
                SaveSettingsDeferred();
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsAiProviderConfigured));
                OnPropertyChanged(nameof(AiResultsEmptyText));
            }
        }
    }

    public string AiReasoningEffort
    {
        get => _aiReasoningEffort;
        set
        {
            if (SetProperty(ref _aiReasoningEffort, value ?? string.Empty))
            {
                SaveSettingsDeferred();
            }
        }
    }

    public IReadOnlyList<AiReasoningEffortOption> AiReasoningEffortOptions => AiReasoningEffortOption.All;

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                SaveSettingsDeferred();
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsAiProviderConfigured));
            }
        }
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (SetProperty(ref _isSettingsOpen, value))
            {
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
            }
        }
    }

    public bool IsAiSetupOpen
    {
        get => _isAiSetupOpen;
        private set
        {
            if (SetProperty(ref _isAiSetupOpen, value))
            {
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
            }
        }
    }

    public bool IsAiSetupSaving
    {
        get => _isAiSetupSaving;
        private set
        {
            if (SetProperty(ref _isAiSetupSaving, value))
            {
                SaveAiSetupAndAnalyzeCommand.RaiseCanExecuteChanged();
                BackAiSetupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowAiProviderChoices => _aiSetupPage == "Channels";

    public bool ShowDeepSeekSetup => _aiSetupPage == "DeepSeek";

    public bool ShowCustomAiSetup => _aiSetupPage == "Custom";

    public string AiSetupTitle => ShowAiProviderChoices ? L.T("配置 AI 分析") : ShowDeepSeekSetup ? L.T("连接 DeepSeek") : L.T("自定义兼容接口");

    public string AiSetupSaveButtonText => _aiSetupReturnsToSettings || _aiSetupForReanalysis ? L.T("保存配置") : L.T("保存并分析");

    public string AiSetupApiKey
    {
        get => _aiSetupApiKey;
        set
        {
            if (SetProperty(ref _aiSetupApiKey, value ?? string.Empty))
            {
                ClearAiSetupError();
                OnPropertyChanged(nameof(AiSetupCanSave));
                SaveAiSetupAndAnalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AiSetupEndpoint
    {
        get => _aiSetupEndpoint;
        set
        {
            if (SetProperty(ref _aiSetupEndpoint, value ?? string.Empty))
            {
                ClearAiSetupError();
                OnPropertyChanged(nameof(AiSetupCanSave));
                SaveAiSetupAndAnalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AiSetupModel
    {
        get => _aiSetupModel;
        set
        {
            if (SetProperty(ref _aiSetupModel, value ?? string.Empty))
            {
                ClearAiSetupError();
                OnPropertyChanged(nameof(AiSetupCanSave));
                SaveAiSetupAndAnalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string AiSetupReasoningEffort
    {
        get => _aiSetupReasoningEffort;
        set
        {
            if (SetProperty(ref _aiSetupReasoningEffort, value ?? string.Empty))
            {
                ClearAiSetupError();
            }
        }
    }

    public string AiSetupErrorText
    {
        get => _aiSetupErrorText;
        private set
        {
            if (SetProperty(ref _aiSetupErrorText, value))
            {
                OnPropertyChanged(nameof(HasAiSetupError));
            }
        }
    }

    public bool HasAiSetupError => !string.IsNullOrWhiteSpace(AiSetupErrorText);

    public bool AiSetupCanSave =>
        !string.IsNullOrWhiteSpace(AiSetupApiKey)
        && (ShowDeepSeekSetup || ShowCustomAiSetup
            && !string.IsNullOrWhiteSpace(AiSetupEndpoint)
            && !string.IsNullOrWhiteSpace(AiSetupModel));

    public bool IsDontShowExplorerGuideChecked
    {
        get => _isDontShowExplorerGuideChecked;
        set => SetProperty(ref _isDontShowExplorerGuideChecked, value);
    }

    public int MaxAiBatchSize
    {
        get => _maxAiBatchSize;
        set
        {
            var normalized = Math.Clamp(value, 1, AiBatchPlanner.MaximumBatchSize);
            if (SetProperty(ref _maxAiBatchSize, normalized))
            {
                SaveSettingsDeferred();
                OnPropertyChanged(nameof(AiBatchSizeSummaryText));
            }
        }
    }

    public int MinAiAnalysisTokens => 10_000;

    public int MaxAiAnalysisTokensLimit => 1_000_000;

    public int AiAnalysisTokenStep => 10_000;

    public int MaxAiAnalysisTokens
    {
        get => _maxAiAnalysisTokens;
        set
        {
            var normalized = Math.Clamp(value, MinAiAnalysisTokens, MaxAiAnalysisTokensLimit);
            if (SetProperty(ref _maxAiAnalysisTokens, normalized))
            {
                SaveSettingsDeferred();
                OnPropertyChanged(nameof(AiTokenBudgetSummaryText));
            }
        }
    }

    public string AiTokenBudgetSummaryText => L.F($"每次点击分析最多预算 {MaxAiAnalysisTokens:N0} Token（输入 + 输出）");

    public string AiTokenBudgetHelpText => L.T("本次分析的所有批次及重试共享预算；优先按接口 usage 累计，无 usage 或请求状态不明时保守预估。本地分析结果缓存复用无需发送新请求，不消耗新 Token；服务商输入缓存命中仍会计入用量并按命中价收费。\n请求前预留完整提示词及输出额度，剩余不足会停止并保留部分结果。\n这是客户端预算，实际分词、推理用量及服务端限制可能不同，不保证服务商账单绝对上限。");

    public string AiCostReferenceDateText => L.T("DeepSeek 官方价格核实于 2026-11-06；价格可能变动，请以官网为准。");

    public string AiPricingReferenceUrl => "https://api-docs.deepseek.com/zh-cn/quick_start/pricing";

    public string AiTokenReferenceUrl => "https://api-docs.deepseek.com/zh-cn/quick_start/token_usage";

    public string AiTokenEstimateText => L.T("三盘实测参考（2026-11-06）：官方 deepseek-flash、中等思考、节省模式、1 GB 阈值，433 个有效分析项目共消耗 88,831 Token，其中输入 19,658、输出 69,173（包含思考用量）。这是一组样本，不是所有磁盘的平均值。文件内容不会发送；实际用量取决于候选项目数、路径、返回内容、思考和重试，并不与磁盘容量成正比。客户端预算使用更保守的 UTF-8 字节估算，可能提前停止。");

    public string AiCostEstimateText => L.T("以上 433 项按接口真实 usage 与当日官方单价计算，空闲时段约 ¥0.2964，高峰时段约 ¥0.5927；折合每 100 项约 ¥0.0684–0.1369。该金额为价格估算，不是账户扣费凭证，不保证其他磁盘同样低价。deepseek-flash 每百万 Token 输入缓存命中 0.02/0.04 元、未命中 1/2 元、输出 4/8 元（空闲/高峰）；思考 Token 已包含在输出中。北京时间工作日（不含法定节假日）9–12、14–18 时为高峰，其他时段为空闲。每次分析后的用量与费用会单独显示；本地结果缓存复用不发新请求。");

    public bool IsAiTokenSavingModeEnabled
    {
        get => _isAiTokenSavingModeEnabled;
        set
        {
            if (SetProperty(ref _isAiTokenSavingModeEnabled, value))
            {
                SaveSettingsDeferred();
            }
        }
    }

    public string StatusText
    {
        get => L.T(_statusText);
        private set => SetProperty(ref _statusText, value);
    }

    public string SettingsSaveErrorText
    {
        get => _settingsSaveErrorText;
        private set
        {
            if (SetProperty(ref _settingsSaveErrorText, value))
            {
                OnPropertyChanged(nameof(HasSettingsSaveError));
            }
        }
    }

    public bool HasSettingsSaveError => !string.IsNullOrWhiteSpace(SettingsSaveErrorText);

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                if (!value)
                {
                    IsScanNavigationNoticeOpen = false;
                }
                ShowScanNavigationNoticeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsScanProgressIndeterminate));
                OnPropertyChanged(nameof(HasScanWorkspace));
                OnPropertyChanged(nameof(CurrentLocationText));
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                OnPropertyChanged(nameof(ShowScanProgressOverlay));
                OnPropertyChanged(nameof(IsScanRunnerActive));
                OnPropertyChanged(nameof(CanZoomOut));
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(FooterText));
                OnPropertyChanged(nameof(ScanStatusHeadlineText));
                OnPropertyChanged(nameof(ScanStatusDetailText));
                OnPropertyChanged(nameof(ShowCancelAction));
                OnPropertyChanged(nameof(ShowAnalyzeAction));
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                RaiseCommandStateChanged();
                OnPropertyChanged(nameof(FooterText));
                OnPropertyChanged(nameof(ScanStatusHeadlineText));
                OnPropertyChanged(nameof(ScanStatusDetailText));
                OnPropertyChanged(nameof(ShowCancelAction));
                OnPropertyChanged(nameof(ShowAnalyzeAction));
            }
        }
    }

    public FileSystemNode? ScanRoot
    {
        get => _scanRoot;
        private set
        {
            if (SetProperty(ref _scanRoot, value))
            {
                ClearSharedMissingState();
                ResetReanalysisSelection();
                _snapshotGeneration = Interlocked.Increment(ref _snapshotCounter);
                RefreshVisibleAnalysisItems();
                RebuildVisualExplorerItems();
                OnPropertyChanged(nameof(HasScanResult));
                OnPropertyChanged(nameof(HasScanWorkspace));
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                OnPropertyChanged(nameof(ShowAnalyzeAction));
                OnPropertyChanged(nameof(IsOverviewMode));
                OnPropertyChanged(nameof(VisibleAnalysisItems));
                OnPropertyChanged(nameof(HasVisibleAnalysisItems));
                OnPropertyChanged(nameof(VisualExplorerItems));
                OnPropertyChanged(nameof(VisibleVisualExplorerItems));
                OnPropertyChanged(nameof(HasVisualExplorerItems));
                OnPropertyChanged(nameof(AiResultsEmptyText));
                OnPropertyChanged(nameof(ScanStatusDetailText));
                OnPropertyChanged(nameof(VisualExplorerEmptyText));
                NotifyAnalysisScopeChanged();
                RaiseCommandStateChanged();
            }
        }
    }

    public FileSystemNode? CurrentRoot
    {
        get => _currentRoot;
        private set
        {
            if (SetProperty(ref _currentRoot, value))
            {
                RefreshVisibleAnalysisItems();
                OnPropertyChanged(nameof(CurrentLocationText));
                OnPropertyChanged(nameof(VisualExplorerEmptyText));
                OnPropertyChanged(nameof(TotalSizeText));
                NotifyAnalysisScopeChanged();
                OnPropertyChanged(nameof(CanZoomOut));
                OnPropertyChanged(nameof(IsOverviewMode));
                OnPropertyChanged(nameof(VisibleAnalysisItems));
                OnPropertyChanged(nameof(HasVisibleAnalysisItems));
                OnPropertyChanged(nameof(AiResultsEmptyText));
                OnPropertyChanged(nameof(ScanStatusDetailText));
                RaiseCommandStateChanged();
            }
        }
    }

    public FileSystemNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            var resolvedValue = ResolveDisplayNodeToScanNode(value) ?? value;
            // Snapshot nodes are immutable records. Equal cached analysis values can still
            // belong to a newly mapped tree; selection must retain that authoritative instance.
            if (ReferenceEquals(_selectedNode, resolvedValue))
            {
                return;
            }
            _selectedNode = resolvedValue;
            OnPropertyChanged(nameof(SelectedNode));

            if (!_isSynchronizingSelection)
            {
                _isSynchronizingSelection = true;
                try
                {
                    var match = FindSelectedAnalysisItem(resolvedValue);

                    if (!ReferenceEquals(_selectedAnalysisItem, match))
                    {
                        _selectedAnalysisItem = match;
                        OnPropertyChanged(nameof(SelectedAnalysisItem));
                    }
                }
                finally
                {
                    _isSynchronizingSelection = false;
                }
            }

            OnPropertyChanged(nameof(SelectedNameText));
            OnPropertyChanged(nameof(SelectedPathText));
            OnPropertyChanged(nameof(SelectedSizeText));
            OnPropertyChanged(nameof(SelectedStorageText));
            OnPropertyChanged(nameof(SelectedRiskText));
            OnPropertyChanged(nameof(SelectedDecision));
            OnPropertyChanged(nameof(AnalysisDescriptionText));
            OnPropertyChanged(nameof(SelectedSummaryText));
            OnPropertyChanged(nameof(SelectedMetaText));
            OnPropertyChanged(nameof(CurrentLocationText));
            NotifySelectedAiCard();
            RebuildVisibleVisualExplorerItems(updateTreemapRoot: false);
            RaiseCommandStateChanged();

            if (IsExplorerGuideOpen && ExplorerGuideStep == 2 && ScanRoot is not null)
            {
                DismissExplorerGuide();
            }
        }
    }

    public AnalysisListItem? SelectedAnalysisItem
    {
        get => _selectedAnalysisItem;
        set
        {
            if (!SetProperty(ref _selectedAnalysisItem, value))
            {
                return;
            }

            if (!_isSynchronizingSelection && value is not null)
            {
                _isSynchronizingSelection = true;
                try
                {
                    var resolvedNode = _scanNodeIndex.FindNode(value.Path) ?? value.Node;
                    if (!ReferenceEquals(_selectedNode, resolvedNode))
                    {
                        _selectedNode = resolvedNode;
                        OnPropertyChanged(nameof(SelectedNode));
                    }
                }
                finally
                {
                    _isSynchronizingSelection = false;
                }
            }

            OnPropertyChanged(nameof(SelectedNameText));
            OnPropertyChanged(nameof(SelectedPathText));
            OnPropertyChanged(nameof(SelectedSizeText));
            OnPropertyChanged(nameof(SelectedStorageText));
            OnPropertyChanged(nameof(SelectedRiskText));
            OnPropertyChanged(nameof(SelectedDecision));
            OnPropertyChanged(nameof(AnalysisDescriptionText));
            OnPropertyChanged(nameof(SelectedSummaryText));
            OnPropertyChanged(nameof(SelectedMetaText));
            NotifySelectedAiCard();
            RaiseCommandStateChanged();
        }
    }

    public bool HasScanResult => ScanRoot is not null;

    public bool HasScanWorkspace => HasScanResult || IsScanning || IsScanQueued || (Workspace is not null && !IsHomeWorkspace);

    public bool IsFocusMode
    {
        get => _isFocusMode;
        private set
        {
            if (SetProperty(ref _isFocusMode, value))
            {
                OnPropertyChanged(nameof(IsFocusModeToggleVisible));
                OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
                OnPropertyChanged(nameof(ShowScanProgressOverlay));
                OnPropertyChanged(nameof(IsScanRunnerActive));
            }
        }
    }

    public void ToggleFocusMode()
    {
        var isGuideAction = IsExplorerGuideOpen && ExplorerGuideStep == 2;
        if (IsFocusModeToggleInteractive)
        {
            if (isGuideAction)
            {
                DismissExplorerGuide();
            }

            IsFocusMode = !IsFocusMode;
        }
    }

    public void ExitFocusMode() => IsFocusMode = false;

    public bool IsAiProviderConfigured =>
        SelectedProviderKind switch
        {
            AiProviderKind.Disabled => false,
            AiProviderKind.OpenAiCompatible =>
                !string.IsNullOrWhiteSpace(AiEndpoint)
                && !string.IsNullOrWhiteSpace(AiModel)
                && !string.IsNullOrWhiteSpace(ApiKey),
            AiProviderKind.Ollama =>
                !string.IsNullOrWhiteSpace(AiEndpoint)
                && !string.IsNullOrWhiteSpace(AiModel),
            _ => false,
        };

    public IReadOnlyList<AnalysisListItem> VisibleAnalysisItems => _visibleAnalysisItems;

    public bool HasVisibleAnalysisItems => _visibleAnalysisItems.Count > 0;

    public IReadOnlyList<VisualExplorerItem> VisualExplorerItems => _visualExplorerItems;

    public IReadOnlyList<VisualExplorerItem> VisibleVisualExplorerItems => _visibleVisualExplorerItems;

    public FileSystemNode? TreemapDisplayRoot => _treemapDisplayRoot;

    public bool HasVisualExplorerItems => _visualExplorerItems.Count > 0;

    public bool HasVisibleVisualExplorerItems => _treemapDisplayRoot?.Children.Count > 0;

    public string VisualExplorerEmptyText => IsScanQueued
        ? L.T("扫描任务已排队，空出名额后会自动开始。")
        : ScanRoot is null
        ? L.T("扫描后即可浏览文件块；每层展示最大的 50 项。")
        : IsScanning
            ? L.T("正在读取文件，文件块会逐步出现…")
            : IsAnalysisConclusionFilterActive
                ? L.T("当前目录及子目录中没有符合此 AI 分析结论的项目。")
            : TreemapDisplayRoot is { IsDirectory: true, Children.Count: 0 } && CurrentRoot?.Children.Count == 0
                ? L.T("此文件夹为空，返回上一级继续浏览。")
                : L.T("当前筛选条件下没有项目，请调整类型筛选。");

    public string AiResultsEmptyText
    {
        get
        {
            if (ScanRoot is null)
            {
                return L.T("请先完成一次扫描。");
            }

            if (!IsAiProviderConfigured)
            {
                return L.T("选择分析范围后点击“开始 AI 分析”，连接 DeepSeek 或自定义兼容接口。AI 帮你理解文件用途与清理风险。");
            }

            if (!string.IsNullOrWhiteSpace(_lastAnalysisSummaryText))
            {
                return L.T(_lastAnalysisSummaryText);
            }

            if (AnalysisItems.Count == 0)
            {
                return L.T("选择扫描根目录或当前文件夹，点击“开始 AI 分析”。这里只显示需要你复核的项目；文件详情中可查看其他结论。");
            }

            return L.T("本次扫描范围内暂无已分析项目。");
        }
    }

    public bool CanZoomOut =>
        !IsScanning
        && !string.IsNullOrWhiteSpace(_expandedVisualPath)
        && !PathsMatch(_expandedVisualPath, ScanRoot?.Path);

    public bool IsOverviewMode =>
        string.IsNullOrWhiteSpace(_expandedVisualPath);

    public int TreemapMaxDepth => 1;

    public string TotalSizeText => CurrentRoot is null ? "-"
        : FormatBytes(IsAnalysisConclusionFilterActive ? _filteredOccupancyBytes : GetDisplaySizeBytes(CurrentRoot));

    public string VolumeUsedText => _volumeUsedBytes is long used ? FormatBytes(used) : "—";

    public string VolumeCapacityText => _volumeCapacityBytes is long capacity ? FormatBytes(capacity) : "—";

    public string VolumeUsagePercent =>
        _volumeUsedBytes is long used && _volumeCapacityBytes is long capacity && capacity > 0
            ? $"{Math.Clamp((used / (double)capacity) * 100d, 0d, 100d):0}%"
            : "—";

    public double VolumeUsageValue =>
        _volumeUsedBytes is long used && _volumeCapacityBytes is long capacity && capacity > 0
            ? Math.Clamp((used / (double)capacity) * 100d, 0d, 100d)
            : 0d;

    public string VolumeStorageSummaryText => L.F($"{VolumeUsedText} 已用 · {VolumeCapacityText}");

    public string ScanDurationText =>
        _lastScanDuration > TimeSpan.Zero ? FormatDuration(_lastScanDuration) : L.T("耗时待统计");

    public string SelectedNameText => L.NodeName(SelectedNode);

    public string CurrentLocationText =>
        !HasScanWorkspace ? L.T("尚未选择扫描位置")
        : CurrentRoot is not null && DisplayNodeMetadata.IsOverflow(CurrentRoot)
            ? $"{DisplayNodeMetadata.GetOverflowParentPath(CurrentRoot)}  ›  {CurrentRoot.Name}"
            : _expandedVisualPath ?? ScanRoot?.Path ?? RootPath;

    public string SelectedPathText => IsAggregateNode(SelectedNode) ? L.NodeName(SelectedNode) : SelectedNode?.Path ?? L.T("尚未选择项目");

    public string SelectedSizeText => SelectedNode is null ? "-" : FormatBytes(GetDisplaySizeBytes(SelectedNode));

    public string SelectedStorageText
    {
        get
        {
            if (SelectedNode?.LogicalSizeBytes is not { } logicalBytes)
            {
                return string.Empty;
            }

            var text = L.F($"逻辑大小：{FormatBytes(logicalBytes)}；块内数字为已去重的磁盘占用。");
            if (SelectedNode.StorageInfo is { LinkCount: > 1 } storage)
            {
                text += L.F($" 扫描时检测到 {storage.LinkCount} 个硬链接，共享 {FormatBytes(storage.AllocatedBytes)}；同一份数据在本次扫描只计一次，删除单个链接不代表释放这些空间。");
            }

            if (SelectedNode.StorageInfo is { IsEstimated: true })
            {
                text += L.T(" 未能完整读取分配信息，此项占用为估算。");
            }

            return text;
        }
    }

    public string SelectedRiskText
    {
        get
        {
            if (SelectedNode is null)
            {
                return L.T("未选择");
            }

            if (SelectedVisibleAnalysis is { } analysis)
            {
                return L.F($"AI 风险：{ToRiskText(analysis.RiskLevel)}");
            }

            if (SelectedNode.IsProtectedLocation)
            {
                return L.T("受保护位置");
            }

            return L.T("未分析");
        }
    }

    public string SelectedDecision
    {
        get
        {
            if (SelectedNode is null)
            {
                return L.T("请先在树图或分析列表中选择项目。");
            }

            if (SelectedVisibleAnalysis is { } analysis)
            {
                return L.Reason(analysis);
            }

            return L.T(_deletionPolicyService.Evaluate(SelectedNode.Path, SelectedNode.IsDirectory).Reason);
        }
    }

    public string AnalysisDescriptionText
    {
        get
        {
            if (SelectedNode is null)
            {
                return L.T("扫描完成后，这里会显示当前项目的 AI 分析结论、本地保护策略和风险标签。");
            }

            var lines = new List<string>
            {
                L.F($"路径：{SelectedNode.Path}"),
                L.F($"本次扫描归属的磁盘占用：{FormatBytes(SelectedNode.SizeBytes)}"),
            };
            if (SelectedNode.LogicalSizeBytes is not null)
            {
                lines.Add(SelectedStorageText);
            }

            if (SelectedVisibleAnalysis is { } analysis)
            {
                lines.Add(L.F($"风险：{ToRiskText(analysis.RiskLevel)}"));
                lines.Add(L.F($"建议：{ToActionText(analysis.SuggestedAction)}"));
                lines.Add(L.F($"原因：{L.Reason(analysis)}"));
                lines.Add(SelectedAiConfidenceText);

            }
            else
            {
                lines.Add(L.T("AI：当前项目尚未获得分析结果。"));
                lines.Add(L.F($"本地策略：{L.T(_deletionPolicyService.Evaluate(SelectedNode.Path, SelectedNode.IsDirectory).Reason)}"));
            }

            if (SelectedNode.RiskTags.Count > 0)
            {
                lines.Add(L.F($"标签：{string.Join("，", SelectedNode.RiskTags)}"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string FooterText
    {
        get
        {
            var prefix = IsScanQueued ? L.T("等待扫描") : IsScanning
                ? L.T("扫描中")
                : IsAnalyzing
                    ? L.T("分析中")
                    : (_scanTerminalHeadlineText is null ? null : L.T(_scanTerminalHeadlineText)) ?? (ScanRoot is null
                        ? L.T("尚未开始扫描")
                        : L.T("扫描完成"));

            var estimateNote = _estimatedStorageItemCount > 0 ? L.F($"（含 {_estimatedStorageItemCount} 项估算）") : string.Empty;
            return L.F($"{prefix}：{_filesScanned} 个文件 / {_directoriesScanned} 个目录 / 磁盘占用 {FormatBytes(_bytesDiscovered)}{estimateNote}");
        }
    }

    public string ScanStatusHeadlineText =>
        IsScanQueued ? L.T("等待扫描") :
        IsScanning ? L.T("正在扫描") :
        IsPreparingAiSubmission ? L.T("正在准备分析") :
        IsAnalyzing ? L.T("正在分析") :
        _scanTerminalHeadlineText ?? (ScanRoot is null ? L.T("准备开始扫描") : L.T("扫描完成"));

    public bool ShowCancelAction => IsScanning || IsAnalyzing || IsScanQueued || IsRefreshing;

    public bool ShowAnalyzeAction =>
        ScanRoot is not null
        && !IsScanning
        && !IsAnalyzing
        && !IsRefreshing;

    public string AiBatchSizeSummaryText => L.F($"每次请求最多发送 {MaxAiBatchSize} 项，超出会自动拆批");

    public string VisualFolderDisplayThresholdSummaryText => L.T("每次展示最大的 50 项，较小项目也保留可见空间。块的面积经过自适应调整，实际占用以标注大小为准。单击区块查看说明，双击文件夹进入下一层；点击「其余项」继续查看剩余项目中最大的 50 项，可逐组展开。");

    private string StorageEstimateNote => _estimatedStorageItemCount > 0
        ? L.F($"有 {_estimatedStorageItemCount} 项无法完整读取分配量或文件身份，占用包含估算；详情中标出了对应条目。")
        : string.Empty;

    public string ScanStatusDetailText
    {
        get
        {
            var detail = IsScanQueued
                ? L.T("正在等待扫描名额；最多同时扫描 3 个位置，空出名额后会按顺序开始。")
                : IsScanning
                ? L.F($"已发现磁盘占用 {FormatBytes(GetDisplayedDiscoveredBytes())}，共扫描 {_filesScanned} 个文件")
                : IsAnalyzing
                    ? $"{AiPhaseText} · {AiCandidateProgressText} · {AiElapsedText}"
                    : IsRefreshing ? RefreshStatusText
                    : _scanTerminalDetailText ?? (CurrentRootMatchesLastAnalyzedPath() && !string.IsNullOrWhiteSpace(_lastAnalysisSummaryText)
                    ? _lastAnalysisSummaryText
                : ScanRoot is null
                    ? L.T("点击“新扫描”并选择一个位置开始扫描。")
                    : _lastScanDuration > TimeSpan.Zero
                        ? L.F($"{FormatBytes(GetDisplayedDiscoveredBytes())}，耗时 {FormatDuration(_lastScanDuration)}")
                        : L.F($"{FormatBytes(GetDisplayedDiscoveredBytes())} 已完成分析"));
            return string.IsNullOrEmpty(StorageEstimateNote) ? detail : detail + " " + StorageEstimateNote;
        }
    }

    public string SelectedSummaryText
    {
        get
        {
            if (SelectedNode is null)
            {
                return L.T("当前未选择项目");
            }

            if (IsAggregateNode(SelectedNode))
            {
                return L.F($"已选：{L.NodeName(SelectedNode)} · {FormatBytes(GetDisplaySizeBytes(SelectedNode))}");
            }

            return L.F($"已选：{SelectedNode.Path} · {FormatBytes(GetDisplaySizeBytes(SelectedNode))}");
        }
    }

    public string SelectedMetaText
    {
        get
        {
            if (SelectedNode is null)
            {
                return L.T("点击左侧树图中的区块即可查看详情。");
            }

            if (DisplayNodeMetadata.IsOverflow(SelectedNode))
            {
                return L.F($"其余 {SelectedNode.Children.Count:N0} 项；单击继续展示其中最大的 {Math.Min(VisualExplorerBuilder.MaximumVisibleItemsPerFolder, SelectedNode.Children.Count):N0} 项，剩余项目可继续展开。");
            }

            if (IsAggregateNode(SelectedNode))
            {
                return L.T("聚合显示块。单击该块可继续展开查看其中项目。");
            }

            if (SelectedNode.RiskTags.Contains("scan-error"))
            {
                return L.T("目录读取不完整；可打开“扫描报告”查看问题路径与原因。");
            }

            if (SelectedNode.RiskTags.Contains("error"))
            {
                return SelectedNode.IsDirectory
                    ? L.T("目录读取失败；可打开“扫描报告”查看路径与原因。")
                    : L.T("文件读取失败；可打开“扫描报告”查看路径与原因。");
            }

            if (SelectedNode.RiskTags.Contains("skipped"))
            {
                return L.T("此重解析点按扫描策略跳过，内容未读取。");
            }

            if (!SelectedNode.IsDirectory)
            {
                return L.T("包含 1 个文件");
            }

            var fileCount = SelectedNode.DescendantFileCount;
            return L.F($"包含 {fileCount} 个文件");
        }
    }

    private Task StartScanAsync()
    {
        RefreshAvailableDrives();
        _scanLocationErrorText = string.Empty;
        OnPropertyChanged(nameof(ScanLocationPickerMessageText));
        IsScanLocationPickerOpen = true;
        if (IsExplorerGuideOpen && ExplorerGuideStep == 0)
        {
            SetExplorerGuideStep(1);
        }

        return Task.CompletedTask;
    }

    private async Task SelectScanRootAsync(object? parameter)
    {
        if (parameter is not string selectedRootPath)
        {
            return;
        }

        if (!Directory.Exists(selectedRootPath))
        {
            _scanLocationErrorText = L.T("所选磁盘已不可用，请重新选择位置。");
            OnPropertyChanged(nameof(ScanLocationPickerMessageText));
            StatusText = _scanLocationErrorText;
            RefreshAvailableDrives();
            IsScanLocationPickerOpen = true;
            return;
        }

        await StartScanAtPathAsync(selectedRootPath).ConfigureAwait(true);
    }

    private async Task BrowseScanFolderAsync(object? parameter)
    {
        if (!TryBeginExternalLocationPicker())
        {
            return;
        }

        try
        {
            var selectedDrivePath = parameter as string;
            var initialDirectory = !string.IsNullOrWhiteSpace(selectedDrivePath)
                                    && Directory.Exists(selectedDrivePath)
                ? selectedDrivePath
                : RootPath;
            var selectedRootPath = _folderPickerService.PickFolder(initialDirectory);
            if (!string.IsNullOrWhiteSpace(selectedRootPath))
            {
                await StartScanAtPathAsync(selectedRootPath).ConfigureAwait(true);
            }
            else
            {
                _scanLocationErrorText = L.T("已取消文件夹选择，可继续选择磁盘或其他位置。");
                OnPropertyChanged(nameof(ScanLocationPickerMessageText));
                StatusText = _scanLocationErrorText;
            }
        }
        catch (Exception ex)
        {
            ReportLocationPickerError("文件夹", ex);
        }
        finally
        {
            EndExternalLocationPicker();
        }
    }

    private async Task BrowseFolderFromHomeAsync()
    {
        if (!TryBeginExternalLocationPicker(fromHome: true))
        {
            return;
        }

        try
        {
            var initialDirectory = Directory.Exists(RootPath)
                ? RootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var selectedRootPath = _folderPickerService.PickFolder(initialDirectory);
            if (!string.IsNullOrWhiteSpace(selectedRootPath))
            {
                await StartScanAtPathAsync(selectedRootPath).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ReportLocationPickerError("文件夹", ex);
        }
        finally
        {
            EndExternalLocationPicker();
        }
    }

    private bool TryBeginExternalLocationPicker(bool fromHome = false)
    {
        if (fromHome ? !CanBrowseFromHome : !CanBrowseForLocation)
        {
            return false;
        }

        _isExternalLocationPickerOpen = true;
        RaiseCommandStateChanged();
        return true;
    }

    private void EndExternalLocationPicker()
    {
        _isExternalLocationPickerOpen = false;
        RaiseCommandStateChanged();
    }

    private void ReportLocationPickerError(string locationKind, Exception exception)
    {
        _scanLocationErrorText = L.F($"无法打开{locationKind}选择器：{exception.Message}");
        OnPropertyChanged(nameof(ScanLocationPickerMessageText));
        StatusText = _scanLocationErrorText;
    }

    private async Task StartScanAtPathAsync(string selectedRootPath)
    {
        if (Workspace is not null)
        {
            if (!Directory.Exists(selectedRootPath))
            {
                ReportUnavailableScanLocation();
                return;
            }

            IsScanLocationPickerOpen = false;
            _scanLocationErrorText = string.Empty;
            OnPropertyChanged(nameof(ScanLocationPickerMessageText));
            await Workspace.StartScanAsync(selectedRootPath, this).ConfigureAwait(true);
            return;
        }

        await RunScanAtPathAsync(selectedRootPath).ConfigureAwait(true);
    }

    private async Task RunScanAtPathAsync(string selectedRootPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(selectedRootPath))
        {
            _scanLocationErrorText = L.T("所选位置已不可用，请重新选择位置。");
            OnPropertyChanged(nameof(ScanLocationPickerMessageText));
            StatusText = _scanLocationErrorText;
            RefreshAvailableDrives();
            IsScanLocationPickerOpen = true;
            return;
        }

        IsScanLocationPickerOpen = false;
        _scanLocationErrorText = string.Empty;
        OnPropertyChanged(nameof(ScanLocationPickerMessageText));
        RootPath = selectedRootPath;
        RefreshVolumeCapacity(selectedRootPath);
        _isAwaitingGuideScan |= IsExplorerGuideOpen && ExplorerGuideStep == 1;
        if (_isAwaitingGuideScan)
        {
            IsExplorerGuideOpen = false;
        }

        SetScanTerminalStatus(null, null);
        IsFocusMode = false;
        ClearScanReport();

        TreemapPurposeReveals.Clear();

        CancelSharedLookup();
        _scanCancellationTokenSource?.Cancel();
        var scanCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCancellationTokenSource = scanCancellationTokenSource;

        ResetScanProgress(_volumeUsedBytes);
        _filesScanned = 0;
        _directoriesScanned = 0;
        _bytesDiscovered = 0;
        _estimatedStorageItemCount = 0;
        _scanCurrentPathText = RootPath;
        _lastScanDuration = TimeSpan.Zero;
        ClearAnalysisFeedback();
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(ScanStatusHeadlineText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
        OnPropertyChanged(nameof(ScanFilesCountText));
        OnPropertyChanged(nameof(ScanDirectoriesCountText));
        OnPropertyChanged(nameof(ScanDiscoveredSizeText));
        OnPropertyChanged(nameof(ScanCurrentPathText));

        var progress = new Progress<ScanProgressSnapshot>(snapshot =>
        {
            if (!_acceptIntermediateSnapshots
                || scanCancellationTokenSource.IsCancellationRequested
                || !ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
            {
                return;
            }

            _filesScanned = snapshot.FilesScanned;
            ApplyScanStorageProgress(snapshot.BytesDiscovered);
            _directoriesScanned = snapshot.DirectoriesScanned;
            _bytesDiscovered = snapshot.BytesDiscovered;
            _estimatedStorageItemCount = snapshot.EstimatedStorageItemCount;
            OnPropertyChanged(nameof(ScanFilesCountText));
            OnPropertyChanged(nameof(ScanDirectoriesCountText));
            OnPropertyChanged(nameof(ScanDiscoveredSizeText));
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(ScanStatusDetailText));

            if (!string.IsNullOrWhiteSpace(snapshot.CurrentPath))
            {
                _scanCurrentPathText = snapshot.CurrentPath;
                OnPropertyChanged(nameof(ScanCurrentPathText));
            }

            if (snapshot.Root is not null)
            {
                ApplyIntermediateSnapshot(snapshot, scanCancellationTokenSource);
            }
        });

        _acceptIntermediateSnapshots = true;
        IsScanning = true;
        StatusText = L.F($"开始扫描：{RootPath}");

        try
        {
            var result = await _scanner.ScanAsync(CreateScanOptions(), progress, scanCancellationTokenSource.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
            {
                return;
            }

            scanCancellationTokenSource.Token.ThrowIfCancellationRequested();

            _filesScanned = result.FilesScanned;
            _directoriesScanned = result.DirectoriesScanned;
            _bytesDiscovered = result.BytesDiscovered;
            ApplyScanStorageProgress(result.BytesDiscovered);
            _estimatedStorageItemCount = result.EstimatedStorageItemCount;
            OnPropertyChanged(nameof(ScanFilesCountText));
            OnPropertyChanged(nameof(ScanDirectoriesCountText));
            OnPropertyChanged(nameof(ScanDiscoveredSizeText));
            _lastScanDuration = result.Duration;
            _navigationHistory.Clear();
            _acceptIntermediateSnapshots = false;
            var snapshotApplied = await ApplySnapshotAsync(
                result.Root,
                result.Root.Path,
                result.Root.Path,
                () => ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource)
                    && !scanCancellationTokenSource.IsCancellationRequested).ConfigureAwait(true);
            scanCancellationTokenSource.Token.ThrowIfCancellationRequested();
            if (!snapshotApplied)
            {
                return;
            }

            if (!ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
            {
                return;
            }

            RebuildAnalysisItems(result.Root);
            if (result.IsPartial)
            {
                LastScanOutcome = ScanTaskState.Partial;
            }
            else
            {
                LastScanOutcome = ScanTaskState.Completed;
            }
            CaptureCompletedScanFeedback(result);

            RefreshVolumeCapacity(RootPath);
            CaptureScanReport(result);
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(ScanStatusHeadlineText));
            OnPropertyChanged(nameof(ScanStatusDetailText));
            if (!result.IsPartial && Workspace is null)
            {
                CompleteScanProgress();
            }
            IsScanning = false;
            CompleteGuideScan();
            // Do not keep a disk scan slot occupied by optional network work.
            _ = RefreshSharedExperienceAfterScanAsync();

        }
        catch (OperationCanceledException)
        {
            LastScanOutcome = ScanTaskState.Canceled;
            if (ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
            {
                StatusText = L.T("扫描已取消。");
                SetScanTerminalStatus(
                    L.T("扫描已取消"),
                    ScanRoot is null
                        ? L.T("本次扫描已取消，没有生成可浏览结果。")
                        : L.T("本次扫描已取消，仍保留上一次成功扫描的文件块。可以调整位置后重新扫描。"));
                RestoreGuideAfterUnsuccessfulScan();
            }
        }
        catch (Exception ex)
        {
            LastScanOutcome = ScanTaskState.Failed;
            if (ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
            {
                StatusText = L.F($"扫描失败：{ex.Message}");
                var errorDetail = string.IsNullOrWhiteSpace(ex.Message) ? L.T("未提供具体错误信息。") : ex.Message;
                SetScanTerminalStatus(
                    L.T("扫描失败"),
                    ScanRoot is null
                        ? L.F($"本次扫描未能完成：{errorDetail} 没有生成可浏览结果。")
                        : L.F($"本次扫描未能完成：{errorDetail} 仍保留上一次成功扫描的文件块。可以调整位置后重新扫描。"));
                RestoreGuideAfterUnsuccessfulScan();
            }
        }
        finally
        {
            _acceptIntermediateSnapshots = false;
            if (ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
            {
                IsScanning = false;
                _scanCancellationTokenSource = null;
            }

            scanCancellationTokenSource.Dispose();
        }
    }

    private async Task AnalyzeAsync() => await AnalyzeWithOptionsAsync(false);

    private async Task AnalyzeWithOptionsAsync(bool bypassMemory, IReadOnlySet<string>? selectedPaths = null)
    {
        if (ScanRoot is null)
        {
            StatusText = L.T("请先完成一次扫描。");
            return;
        }

        await RefreshSharedConsentAsync();
        var sharingOfflineBeforeLookup = SharedRuntime is { } initialRuntime && _sharedConsent is { CanShare: true }
            && NetworkFailureClassifier.IsNetworkUnavailable(initialRuntime.Endpoint.AbsoluteUri, initialRuntime.NetworkAvailability);
        if (ScanRoot is null || _isDisposed) return;

        var scanSnapshot = ScanRoot;
        var analysisRoot = selectedPaths is null ? ResolveAiAnalysisRoot() ?? scanSnapshot : scanSnapshot;

        var providerOptions = CreateProviderOptions();
        if (!providerOptions.IsConfigured && SharedRuntime is null)
        {
            StatusText = L.T("AI 提供者未正确配置，跳过分析。");
            return;
        }

        await RefreshSharedConsentAsync();
        var providerOffline = providerOptions.IsConfigured
            && NetworkFailureClassifier.IsNetworkUnavailable(providerOptions.Endpoint, _aiAnalysisService.NetworkAvailability);
        var sharingOffline = SharedRuntime is { } runtime && _sharedConsent is { CanShare: true }
            && NetworkFailureClassifier.IsNetworkUnavailable(runtime.Endpoint.AbsoluteUri, runtime.NetworkAvailability);
        var networkUnavailable = providerOffline || sharingOffline || sharingOfflineBeforeLookup;
        if (networkUnavailable)
        {
            await HandleOfflineAsync(analysisRoot.Path);
            // Local model endpoints remain usable without an external network.
            if (providerOffline || !providerOptions.IsConfigured) return;
        }

        CancelSharedLookup();
        _analysisCancellationTokenSource?.Cancel();
        _analysisCancellationTokenSource = new CancellationTokenSource();

        IsAnalyzing = true;
        var progress = BeginAiProgress(analysisRoot.Path);
        var analysisOptions = CreateScanOptions() with { RootPath = analysisRoot.Path, AnalysisMemoryRootPath = scanSnapshot.Path,
            BypassAnalysisMemory = bypassMemory, IncludedAnalysisPaths = selectedPaths,
            SharedLookupReuse = bypassMemory ? null : CaptureSharedAnalysisReuse(scanSnapshot) };
        StatusText = L.T("正在准备分析清单，尚未调用模型。");
        SetAnalysisFeedback(analysisRoot.Path, StatusText);
        var configureAfterAnalysis = false;
        IReadOnlySet<string>? budgetContinuationPaths = null;
        FileSystemNode? budgetContinuationSnapshot = null;
        var continueBudgetAnalysis = false;

        async Task<bool> ConfirmPreparedSubmission(AiAnalysisSubmissionPreview preview, CancellationToken token)
        {
            await RunOnAiUiAsync(() => { _isAwaitingAiConfirmation = true; NotifyAiPreparationChanged(); });
            var accepted = ConfirmAiSubmissionAsync is null || await ConfirmAiSubmissionAsync(preview, token).ConfigureAwait(false);
            if (accepted && !preview.ProviderConfigured && preview.Items.Count > 0)
            {
                configureAfterAnalysis = true;
                return false;
            }
            if (accepted && !token.IsCancellationRequested)
                await RunOnAiUiAsync(() => StartConfirmedAiProgress(preview.Items.Count > 0));
            return accepted;
        }

        try
        {
            var analysisCancellationToken = _analysisCancellationTokenSource.Token;
            await RefreshSharedConsentAsync();
            var sharedConsentAtStart = _sharedConsent;
            // Candidate preparation and cache IO must not freeze the WPF dispatcher.
            var report = await Task.Run(() => _aiAnalysisService.AnalyzeWithReportAsync(
                analysisRoot,
                analysisOptions,
                providerOptions,
                analysisCancellationToken,
                progress,
                provenance: SharedKnowledgeRuntime.CreateProvenance(providerOptions, analysisOptions.UseTokenSavingMode),
                confirmSubmission: ConfirmPreparedSubmission), analysisCancellationToken).ConfigureAwait(true);

            if (report.NetworkUnavailable)
            {
                networkUnavailable = true;
                await HandleOfflineAsync(analysisRoot.Path);
            }

            await ApplyAnalysisResultsAndReportAsync(
                analysisRoot,
                report.Results,
                analysisCancellationToken.IsCancellationRequested,
                report.TokenBudget, scanSnapshot, providerOptions, report.SharedMissingPaths, sharedConsentAtStart,
                report.SharedKnowledgeWarning).ConfigureAwait(true);
            SetSharedAnalysisSummary(report);
            if (report.TokenBudget.StoppedForBudget)
            {
                budgetContinuationPaths = report.Results.Where(result => result.LooksLikeFallback())
                    .Where(result => _scanNodeIndex.FindNode(result.Path) is { } node
                        && (node.Analysis is null || node.Analysis.LooksLikeFallback()))
                    .Select(result => result.Path).ToHashSet(StringComparer.Ordinal);
                budgetContinuationSnapshot = ScanRoot;
                if (budgetContinuationPaths.Count > 0 && ConfirmBudgetContinuationAsync is { } confirmContinuation
                    && MaxAiAnalysisTokens >= report.TokenBudget.NextRequestRequiredTokens
                    && !_isDisposed && !analysisCancellationToken.IsCancellationRequested)
                    continueBudgetAnalysis = await confirmContinuation(MaxAiAnalysisTokens,
                        budgetContinuationPaths.Count, analysisCancellationToken).ConfigureAwait(true);
            }
        }
        catch (AiAnalysisCanceledException ex)
        {
            await ApplyAnalysisResultsAndReportAsync(analysisRoot, ex.PartialResults, wasCanceled: true,
                tokenBudget: ex.TokenBudget, scanSnapshot: scanSnapshot, providerOptions: providerOptions).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            var summary = configureAfterAnalysis ? L.T("还有项目需要 AI 分析，请先配置分析服务。未提交模型请求。")
                : L.T("本次扫描分析已取消。已完成的结果已保存，可在再次分析时复用；未完成的项目可重试。");
            SetAnalysisFeedback(analysisRoot.Path, summary);
            StatusText = summary;
        }
        catch (Exception ex)
        {
            if (NetworkFailureClassifier.IsOffline(ex, _aiAnalysisService.NetworkAvailability))
            {
                networkUnavailable = true;
                await HandleOfflineAsync(analysisRoot.Path);
            }
            else
            {
                var summary = L.F($"本次扫描分析失败：{ex.Message}");
                SetAnalysisFeedback(analysisRoot.Path, summary);
                StatusText = summary;
            }
        }
        finally
        {
            EndAiProgress();
            // Keep the analysis gate through the async handoff so a pending file
            // refresh cannot replace the snapshot after the user approved another round.
            await RefreshSharedOutboxStatusAsync().ConfigureAwait(true);
            if (_analysisCancellationTokenSource?.IsCancellationRequested == true)
                continueBudgetAnalysis = false;
            _analysisCancellationTokenSource?.Dispose();
            _analysisCancellationTokenSource = null;
            if (!continueBudgetAnalysis) IsAnalyzing = false;
            if (networkUnavailable && !_isDisposed)
            {
                StatusText = NetworkFailureClassifier.OfflineMessage;
                SetAnalysisFeedback(analysisRoot.Path, StatusText);
            }
        }
        if (configureAfterAnalysis) OpenAiSetup();
        if (continueBudgetAnalysis)
        {
            try
            {
                if (budgetContinuationPaths is { Count: > 0 } unfinishedPaths
                    && !_isDisposed && !IsScanning && !IsRefreshing
                    && !IsAiSetupOpen && !IsScanReportOpen
                    && (!IsReanalysisSelectionMode || bypassMemory)
                    && ReferenceEquals(ScanRoot, budgetContinuationSnapshot))
                    await AnalyzeWithOptionsAsync(bypassMemory, unfinishedPaths).ConfigureAwait(true);
            }
            finally { IsAnalyzing = false; }
        }
    }

    private Task AnalyzeOrConfigureAsync() => AnalyzeOrConfigureWithOptionsAsync(false);

    private async Task AnalyzeOrConfigureWithOptionsAsync(bool bypassMemory)
    {
        if (!IsAiProviderConfigured)
        {
            var sharing = SharedRuntime is not null && (await SharedRuntime.Store.GetSnapshotAsync()).CanShare;
            if (!sharing || bypassMemory) { OpenAiSetup(); return; }
        }

        await AnalyzeWithOptionsAsync(bypassMemory);
    }

    private void OpenAiSetup()
    {
        if (ScanRoot is null || IsScanning || IsAnalyzing)
        {
            return;
        }

        OpenAiSetupCore(returnsToSettings: false);
    }

    private void OpenAiSetupFromSettings()
    {
        if (IsScanning || IsAnalyzing || IsAiSetupOpen)
        {
            return;
        }

        OpenAiSetupCore(returnsToSettings: true);
    }

    private void OpenAiSetupCore(bool returnsToSettings)
    {
        _aiSetupForReanalysis = false;
        _aiSetupReturnsToSettings = returnsToSettings;
        OnPropertyChanged(nameof(AiSetupSaveButtonText));
        IsSettingsOpen = false;
        IsHelpOpen = false;
        AiSetupErrorText = string.Empty;
        AiSetupApiKey = string.Empty;
        SetAiSetupPage("Channels");
        IsAiSetupOpen = true;
    }

    private void CloseAiSetup()
    {
        if (IsAiSetupSaving)
        {
            return;
        }

        IsAiSetupOpen = false;
        if (_aiSetupReturnsToSettings)
        {
            IsSettingsOpen = true;
        }
        AiSetupApiKey = string.Empty;
        AiSetupErrorText = string.Empty;
    }

    private void ChooseDeepSeekSetup()
    {
        AiSetupEndpoint = "https://api.deepseek.com";
        AiSetupModel = "deepseek-flash";
        AiSetupReasoningEffort = "medium";
        SetAiSetupPage("DeepSeek");
    }

    private void ChooseCustomAiSetup()
    {
        AiSetupEndpoint = SelectedProviderKind == AiProviderKind.OpenAiCompatible ? AiEndpoint : string.Empty;
        AiSetupModel = SelectedProviderKind == AiProviderKind.OpenAiCompatible ? AiModel : string.Empty;
        AiSetupReasoningEffort = SelectedProviderKind == AiProviderKind.OpenAiCompatible ? AiReasoningEffort : string.Empty;
        SetAiSetupPage("Custom");
    }

    private void BackAiSetup()
    {
        AiSetupErrorText = string.Empty;
        SetAiSetupPage("Channels");
    }

    private void SetAiSetupPage(string page)
    {
        _aiSetupPage = page;
        OnPropertyChanged(nameof(ShowAiProviderChoices));
        OnPropertyChanged(nameof(ShowDeepSeekSetup));
        OnPropertyChanged(nameof(ShowCustomAiSetup));
        OnPropertyChanged(nameof(AiSetupTitle));
        OnPropertyChanged(nameof(AiSetupCanSave));
        ChooseDeepSeekCommand.RaiseCanExecuteChanged();
        ChooseCustomAiCommand.RaiseCanExecuteChanged();
        BackAiSetupCommand.RaiseCanExecuteChanged();
        SaveAiSetupAndAnalyzeCommand.RaiseCanExecuteChanged();
    }

    private void ClearAiSetupError()
    {
        if (!string.IsNullOrWhiteSpace(AiSetupErrorText))
        {
            AiSetupErrorText = string.Empty;
        }
    }

    private async Task SaveAiSetupAndAnalyzeAsync()
    {
        if (!AiSetupCanSave)
        {
            AiSetupErrorText = L.T("请完整填写必需信息。");
            return;
        }

        var isDeepSeek = ShowDeepSeekSetup;
        var endpoint = isDeepSeek ? "https://api.deepseek.com" : AiSetupEndpoint.Trim();
        var model = isDeepSeek ? "deepseek-flash" : AiSetupModel.Trim();
        if (!isDeepSeek && !IsValidOpenAiCompatibleEndpoint(endpoint, out var endpointError))
        {
            AiSetupErrorText = endpointError;
            return;
        }

        IsAiSetupSaving = true;
        try
        {
            _suspendSettingsSave = true;
            SelectedAiConnectionId = isDeepSeek ? AiConnectionOption.DeepSeekId : AiConnectionOption.CustomId;
            AiEndpoint = endpoint.TrimEnd('/');
            AiModel = model;
            ApiKey = AiSetupApiKey.Trim();
            AiReasoningEffort = isDeepSeek ? "medium" : AiSetupReasoningEffort;
            _suspendSettingsSave = false;

            if (!await SaveSettingsAsync().ConfigureAwait(true))
            {
                AiSetupErrorText = string.IsNullOrWhiteSpace(SettingsSaveErrorText)
                    ? L.T("配置保存失败，请检查设置目录权限后重试。")
                    : SettingsSaveErrorText;
                return;
            }

            if (!IsAiSetupOpen)
            {
                return;
            }

            AiSetupApiKey = string.Empty;
            IsAiSetupOpen = false;
            if (_aiSetupForReanalysis)
            {
                _aiSetupForReanalysis = false;
                OnPropertyChanged(nameof(AiSetupSaveButtonText));
                StatusText = L.T("分析服务配置已保存。请再次点击“重新分析”提交已勾选项目，API 调用可能产生费用。");
                return;
            }
            if (_aiSetupReturnsToSettings)
            {
                IsSettingsOpen = true;
                StatusText = L.T("AI 配置已保存。");
                return;
            }

            StatusText = L.T("AI 配置已保存，正在分析本次扫描。");
            await AnalyzeAsync().ConfigureAwait(true);
        }
        finally
        {
            _suspendSettingsSave = false;
            IsAiSetupSaving = false;
        }
    }

    private static bool IsValidOpenAiCompatibleEndpoint(string endpoint, out string error)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            error = L.T("请输入有效的 HTTPS 接口基础地址；仅本机服务允许 HTTP。");
            return false;
        }

        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            error = L.T("请填写接口基础地址，不要附加 /chat/completions。");
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task ApplyAnalysisResultsAndReportAsync(
        FileSystemNode analysisRoot,
        IReadOnlyList<AiAnalysisResult> results,
        bool wasCanceled,
        AiTokenBudgetReport? tokenBudget = null,
        FileSystemNode? scanSnapshot = null,
        AiProviderOptions? providerOptions = null,
        IReadOnlyList<string>? sharedMissingPaths = null,
        SharedConsentSnapshot? sharedConsent = null,
        string? sharedWarning = null)
    {
        scanSnapshot ??= analysisRoot;
        // A scan can be replaced while an asynchronous analysis is in flight.
        // Never map the old request's paths onto a newer scan tree.
        if (_isDisposed || !ReferenceEquals(ScanRoot, scanSnapshot))
        {
            return;
        }

        var mappedRoot = await Task.Run(
            () => ApplyAnalysisResults(scanSnapshot, results),
            CancellationToken.None).ConfigureAwait(true);

        if (_isDisposed || !ReferenceEquals(ScanRoot, scanSnapshot))
        {
            return;
        }

        if (!ReferenceEquals(mappedRoot, scanSnapshot))
        {
            if (!await ApplySnapshotAsync(mappedRoot,
                canApplySnapshot: () => !_isDisposed && ReferenceEquals(ScanRoot, scanSnapshot),
                beforeApplySnapshot: () => QueueNewAiPurposeReveals(results)).ConfigureAwait(true)) return;
            RebuildAnalysisItems(mappedRoot);
        }
        if (sharedMissingPaths is not null)
            await BindSharedMissingPathsAsync(mappedRoot, sharedConsent, sharedMissingPaths);
        if (_isDisposed || !ReferenceEquals(ScanRoot, mappedRoot)) return;

        var noCandidates = results.Count == 0 && !wasCanceled;
        var analyzedThreshold = AnalysisThresholdMb;
        var analysisSummary = AiAnalysisSummary.FromResults(results);
        _formatAnalysisFeedback = () =>
        {
            var parts = new List<string>
            {
                noCandidates ? L.F($"本次扫描分析完成，没有达到 {analyzedThreshold} MB 阈值的项目。")
                    : L.T(analysisSummary.FormatStatusMessage(wasCanceled)),
            };
            if (tokenBudget is not null) parts.Add(L.T(tokenBudget.FormatStatusMessage()));
            if (!string.IsNullOrWhiteSpace(sharedWarning)) parts.Add(string.Join(Environment.NewLine,
                sharedWarning.Split(["\r\n", "\n"], StringSplitOptions.None).Select(L.T)));
            return $"{analysisRoot.Path} · {string.Join(" ", parts)}";
        };
        var summary = _lastRenderedAnalysisFeedback = _formatAnalysisFeedback();
        if (tokenBudget is not null) SetAiUsageFeedback(tokenBudget, providerOptions ?? CreateProviderOptions());

        SetAnalysisFeedback(analysisRoot.Path, summary);
        StatusText = summary;
    }

    private async Task OpenLocationAsync(object? parameter)
    {
        var node = ResolveTargetNode(parameter);
        if (node is null)
        {
            return;
        }

        var decision = _deletionPolicyService.Evaluate(node.Path, node.IsDirectory);
        if (!decision.CanOpenLocation)
        {
            StatusText = decision.Reason;
            return;
        }

        try
        {
            await _fileLocationService.OpenLocationAsync(node.Path, node.IsDirectory).ConfigureAwait(true);
            StatusText = L.F($"已打开位置：{node.Path}");
        }
        catch (Exception ex)
        {
            StatusText = L.F($"打开位置失败：{ex.Message}");
        }
    }

    private async Task RecycleAsync(object? parameter)
    {
        var node = ResolveTargetNode(parameter);
        if (node is null)
        {
            return;
        }

        var decision = _deletionPolicyService.Evaluate(node.Path, node.IsDirectory);
        if (!decision.CanRecycle)
        {
            StatusText = decision.Reason;
            return;
        }

        var confirmation = LocalizedMessageBox.Show(
            L.F($"最后确认：是否将以下项目送入回收站？{Environment.NewLine}{Environment.NewLine}{node.Path}{Environment.NewLine}{Environment.NewLine}此操作不会由 AI 自动执行，仍需你本人确认。"),
            L.T("确认送入回收站"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            StatusText = L.T("已取消送入回收站。");
            return;
        }

        try
        {
            await _recycleBinService.RecycleAsync(node.Path, node.IsDirectory).ConfigureAwait(true);
            if (await ApplyLocalRemovalAsync(node).ConfigureAwait(true))
            {
                StatusText = L.F($"已送入回收站，并已从当前视图移除：{node.Name}");
            }
            else
            {
                StatusText = L.F($"已送入回收站，但未能在当前视图中定位该节点：{node.Name}");
            }
        }
        catch (Exception ex)
        {
            StatusText = L.F($"送入回收站失败：{ex.Message}");
        }
    }

    private void CancelCurrentWork()
    {
        CancelRefresh();
        Workspace?.CancelTask(this);
        _scanCancellationTokenSource?.Cancel();
        _analysisCancellationTokenSource?.Cancel();
    }

    private async Task<bool> ApplySnapshotAsync(
        FileSystemNode root,
        string? currentPathOverride = null,
        string? selectedPathOverride = null,
        Func<bool>? canApplySnapshot = null,
        Action? beforeApplySnapshot = null)
    {
        var nodeIndex = await Task.Run(() => FileSystemNodeIndex.Build(root)).ConfigureAwait(true);
        if (_isDisposed || (canApplySnapshot is not null && !canApplySnapshot()))
        {
            return false;
        }

        beforeApplySnapshot?.Invoke();
        ApplySnapshot(root, nodeIndex, currentPathOverride, selectedPathOverride);
        return true;
    }

    private void ApplySnapshot(
        FileSystemNode root,
        FileSystemNodeIndex nodeIndex,
        string? currentPathOverride = null,
        string? selectedPathOverride = null)
    {
        var nextSelectedPath = selectedPathOverride ?? SelectedNode?.Path;
        _scanNodeIndex = nodeIndex;
        var nextCurrentPath = currentPathOverride ?? _expandedVisualPath ?? root.Path;
        var nextCurrentRoot = ResolveCurrentRootFromSnapshot(root, nextCurrentPath) ?? root;
        _expandedAggregateRoot = IsAggregateNode(nextCurrentRoot) ? nextCurrentRoot : null;
        _expandedVisualPath = PathsMatch(nextCurrentRoot.Path, root.Path)
            ? null
            : nextCurrentRoot.Path;
        ScanRoot = root;
        CurrentRoot = nextCurrentRoot;

        SelectedNode = string.IsNullOrWhiteSpace(nextSelectedPath)
            ? CurrentRoot
            : ResolveSnapshotNode(nextSelectedPath, SelectedNode) ?? CurrentRoot;
        RefreshNavigationHistory();
    }

    private FileSystemNode? ResolveCurrentRootFromSnapshot(FileSystemNode root, string path)
    {
        var resolved = ResolveSnapshotNode(path, CurrentRoot);
        while (resolved is null && TryGetAggregateParentPath(path) is string parentPath)
        {
            path = parentPath;
            resolved = ResolveSnapshotNode(path, FindPreviousDisplayNode(path));
        }

        return resolved ?? root;
    }

    private void ZoomIntoNode(FileSystemNode? node)
    {
        if (node is null)
        {
            return;
        }

        ToggleVisualItem(node);
    }

    private void ZoomOut()
    {
        if (!CanNavigateVisualTree || ScanRoot is null || CurrentRoot is null)
        {
            return;
        }

        var returnedFromNode = CurrentRoot;
        var parent = ResolveDisplayParentNode(CurrentRoot);
        if (parent is null)
        {
            return;
        }

        if (TryPopNavigationHistory(CurrentRoot, out var historyParent)
            && historyParent is not null
            && PathsMatch(historyParent.Path, parent.Path))
        {
            parent = historyParent;
        }
        else
        {
            RebuildNavigationHistory(parent);
        }

        _expandedVisualPath = PathsMatch(parent.Path, ScanRoot.Path)
            ? null
            : parent.Path;
        _expandedAggregateRoot = IsAggregateNode(parent) ? parent : null;
        CurrentRoot = parent;
        SelectedNode = returnedFromNode;
        RebuildVisualExplorerItems();
        StatusText = L.F($"已返回上一级：{CurrentLocationText}");
    }

    private void ResetZoom()
    {
        if (!CanNavigateVisualTree || ScanRoot is null)
        {
            return;
        }

        _expandedVisualPath = null;
        _expandedAggregateRoot = null;
        _navigationHistory.Clear();
        CurrentRoot = ScanRoot;
        SelectedNode = ScanRoot;
        RebuildVisualExplorerItems();
        StatusText = L.F($"已回到根目录：{ScanRoot.Path}");
        return;
    }

    private void ApplySnapshot(FileSystemNode root, string? currentPathOverride = null, string? selectedPathOverride = null)
    {
        ApplySnapshot(
            root,
            FileSystemNodeIndex.Build(root),
            currentPathOverride,
            selectedPathOverride);
    }

    private async Task<bool> ApplyLocalRemovalAsync(FileSystemNode removedNode)
    {
        if (ScanRoot is null)
        {
            return false;
        }

        var originalRoot = ScanRoot;
        var fallbackParentPath = _scanNodeIndex.FindParentNode(removedNode.Path)?.Path ?? originalRoot.Path;
        var nextCurrentPath = IsWithinRemovedSubtree(CurrentRoot?.Path, removedNode.Path)
            ? fallbackParentPath
            : CurrentRoot?.Path;
        var nextSelectedPath = IsWithinRemovedSubtree(SelectedNode?.Path, removedNode.Path)
            ? fallbackParentPath
            : SelectedNode?.Path;

        var removedFileCount = removedNode.IsDirectory
            ? removedNode.DescendantFileCount
            : 1;
        var removedDirectoryCount = removedNode.IsDirectory
            ? removedNode.DescendantDirectoryCount
            : 0;

        var removalResult = await Task.Run(() =>
        {
            if (!FileSystemTreeEditor.TryRemovePath(originalRoot, removedNode.Path, out var updatedRoot))
            {
                return null;
            }

            var nodeIndex = FileSystemNodeIndex.Build(updatedRoot);
            return new LocalRemovalResult(updatedRoot, nodeIndex);
        }).ConfigureAwait(true);

        if (removalResult is null)
        {
            return false;
        }

        _filesScanned = Math.Max(0, _filesScanned - removedFileCount);
        _directoriesScanned = Math.Max(0, _directoriesScanned - removedDirectoryCount);
        _bytesDiscovered = removalResult.UpdatedRoot.SizeBytes;
        _estimatedStorageItemCount = removalResult.UpdatedRoot.EnumerateFiles().Count(node => node.StorageInfo?.IsEstimated == true);
        RefreshVolumeCapacity(RootPath);
        ApplySnapshot(removalResult.UpdatedRoot, removalResult.NodeIndex, nextCurrentPath, nextSelectedPath);
        RebuildAnalysisItems(removalResult.UpdatedRoot);
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
        return true;
    }

    private void RebuildAnalysisItems(FileSystemNode root)
    {
        AnalysisItems.Clear();

        foreach (var node in root
                     .DescendantsAndSelf()
                     .Where(item => item.Analysis is not null)
                     .OrderByDescending(item => item.SizeBytes)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Name, StringComparer.Ordinal)
                     .ThenBy(item => item.Path, StringComparer.Ordinal))
        {
            AnalysisItems.Add(new AnalysisListItem(
                node.Path,
                node.Name,
                FormatBytes(node.SizeBytes),
                node.Analysis!.RiskLevel,
                node.Analysis.SuggestedAction,
                L.Reason(node.Analysis),
                node));
        }

        RebuildAnalysisSelectionIndex();
        RefreshVisibleAnalysisItems();
        OnPropertyChanged(nameof(VisibleAnalysisItems));
        OnPropertyChanged(nameof(HasVisibleAnalysisItems));
        OnPropertyChanged(nameof(AiResultsEmptyText));
    }

    private void RebuildAnalysisItems(IReadOnlyList<AiAnalysisResult> results)
    {
        AnalysisItems.Clear();

        var items = results
            .GroupBy(result => result.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(result => new
            {
                Result = result,
                Node = _scanNodeIndex.FindNode(result.Path),
            })
            .Where(item => item.Node is not null)
            .OrderByDescending(item => item.Node!.SizeBytes)
            .ThenBy(item => item.Node!.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Node!.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Node!.Path, StringComparer.Ordinal);

        foreach (var item in items)
        {
            var node = item.Node!;
            AnalysisItems.Add(new AnalysisListItem(
                node.Path,
                node.Name,
                FormatBytes(node.SizeBytes),
                item.Result.RiskLevel,
                item.Result.SuggestedAction,
                L.Reason(item.Result),
                node));
        }

        RebuildAnalysisSelectionIndex();
        RefreshVisibleAnalysisItems();
        OnPropertyChanged(nameof(VisibleAnalysisItems));
        OnPropertyChanged(nameof(HasVisibleAnalysisItems));
        OnPropertyChanged(nameof(AiResultsEmptyText));
    }

    private void RebuildVisualExplorerItems()
    {
        if (ScanRoot is null)
        {
            _visualExplorerItems = [];
        }
        else
        {
            var builtItems = _visualExplorerBuilder.Build(
                ScanRoot,
                new VisualExplorerOptions(ExpandedPath: _expandedVisualPath));

            if (!string.IsNullOrWhiteSpace(_expandedVisualPath)
                && _scanNodeIndex.FindNode(_expandedVisualPath) is null
                && !string.Equals(_expandedAggregateRoot?.Path, _expandedVisualPath, StringComparison.Ordinal)
                && !ContainsVisualItemPath(builtItems, _expandedVisualPath))
            {
                _expandedVisualPath = null;
                builtItems = _visualExplorerBuilder.Build(
                    ScanRoot,
                    new VisualExplorerOptions());
            }

            _visualExplorerItems = builtItems;
        }

        OnPropertyChanged(nameof(VisualExplorerItems));
        OnPropertyChanged(nameof(HasVisualExplorerItems));
        RebuildVisibleVisualExplorerItems();
        OnPropertyChanged(nameof(CanZoomOut));
        OnPropertyChanged(nameof(IsOverviewMode));
        OnPropertyChanged(nameof(TreemapMaxDepth));
        OnPropertyChanged(nameof(CurrentLocationText));
    }

    private void RebuildVisibleVisualExplorerItems(bool updateTreemapRoot = true)
    {
        var sourceItems = _visualExplorerItems;
        if (IsAnalysisConclusionFilterActive)
        {
            sourceItems = GetConclusionFilterItems(GetTreemapNavigationRoot());
        }
        else
        {
            _conclusionFilterRoot = null;
            _conclusionFilterItems = [];
        }
        _visibleVisualExplorerItems = VisualExplorerPresentation.Apply(
            sourceItems,
            IsAnalysisConclusionFilterActive ? "大小" : DisplaySortMode,
            DisplayFilterMode,
            SelectedNode?.Path);
        if (updateTreemapRoot)
        {
            _treemapDisplayRoot = BuildTreemapDisplayRoot(_visibleVisualExplorerItems);
            RefreshConclusionOccupancy();
        }
        OnPropertyChanged(nameof(VisibleVisualExplorerItems));
        OnPropertyChanged(nameof(HasVisibleVisualExplorerItems));
        OnPropertyChanged(nameof(VisualExplorerEmptyText));
        if (updateTreemapRoot)
        {
            OnPropertyChanged(nameof(TreemapDisplayRoot));
        }
    }

    private FileSystemNode? BuildTreemapDisplayRoot(IReadOnlyList<VisualExplorerItem> projectedItems)
    {
        var displayRoot = GetTreemapNavigationRoot();
        if (displayRoot is null)
        {
            return null;
        }

        var hasNavigationRoot = !string.IsNullOrWhiteSpace(_expandedVisualPath);
        var displayItems = projectedItems;
        if (hasNavigationRoot && !IsAnalysisConclusionFilterActive)
        {
            var items = _visualExplorerBuilder.Build(
                displayRoot,
                new VisualExplorerOptions());
            displayItems = VisualExplorerPresentation.Apply(
                items,
                DisplaySortMode,
                DisplayFilterMode,
                SelectedNode?.Path);
        }

        return displayRoot with
        {
            Children = displayItems.Select(ToTreemapNode).ToArray(),
        };
    }

    private FileSystemNode? GetTreemapNavigationRoot() => string.IsNullOrWhiteSpace(_expandedVisualPath)
        ? ScanRoot
        : _expandedAggregateRoot is not null
          && string.Equals(_expandedAggregateRoot.Path, _expandedVisualPath, StringComparison.Ordinal)
            ? _expandedAggregateRoot
            : _scanNodeIndex.FindNode(_expandedVisualPath)
              ?? FindVisualDisplayNode(_visualExplorerItems, _expandedVisualPath)
              ?? ScanRoot;

    private static FileSystemNode ToTreemapNode(VisualExplorerItem item)
    {
        return item.DisplayNode with
        {
            Children = DisplayNodeMetadata.IsOverflow(item.DisplayNode)
                ? item.DisplayNode.Children
                : item.Children.Select(ToTreemapNode).ToArray(),
        };
    }

    private static FileSystemNode? FindVisualDisplayNode(IEnumerable<VisualExplorerItem> items, string path)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.DisplayNode.Path, path, StringComparison.Ordinal))
            {
                return item.DisplayNode;
            }

            var nested = FindVisualDisplayNode(item.Children, path);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void RefreshVolumeCapacity(string? path)
    {
        // Query the full directory so mounted folders and cross-volume junctions
        // use the disk which actually stores the scanned files.
        var space = VolumeSpaceReader.Read(path);
        _volumeSpaceInfo = space;
        _volumeUsedBytes = space?.UsedBytes;
        _volumeCapacityBytes = space?.CapacityBytes;

        OnPropertyChanged(nameof(VolumeUsedText));
        OnPropertyChanged(nameof(VolumeCapacityText));
        OnPropertyChanged(nameof(VolumeUsagePercent));
        OnPropertyChanged(nameof(VolumeUsageValue));
        OnPropertyChanged(nameof(VolumeStorageSummaryText));
    }

    private void ToggleVisualItem(FileSystemNode? node)
    {
        if (node is null || (!node.IsDirectory && !IsAggregateNode(node)))
        {
            return;
        }

        var resolvedNode = ResolveDisplayNodeToScanNode(node);
        if (resolvedNode is null)
        {
            return;
        }

        if (PathsMatch(_expandedVisualPath, node.Path))
        {
            SelectedNode = resolvedNode;
            return;
        }

        PushNavigationHistory(CurrentRoot);
        // CurrentRoot and SelectedNode notify command bindings synchronously.
        // Publish the destination first so Back/Reset see the new navigation state.
        _expandedAggregateRoot = IsAggregateNode(node) ? resolvedNode : null;
        _expandedVisualPath = node.Path;
        CurrentRoot = resolvedNode;
        SelectedNode = resolvedNode;

        RebuildVisualExplorerItems();
        StatusText = L.F($"已进入：{resolvedNode.Name}");
    }

    private string? GetCollapseTargetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ScanRoot is null)
        {
            return null;
        }

        var parentPath = TryGetAggregateParentPath(path) ?? _scanNodeIndex.FindParentPath(path) ?? Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parentPath)
            || string.Equals(parentPath, ScanRoot.Path, StringComparison.Ordinal))
        {
            return null;
        }

        return parentPath;
    }

    private string? NormalizeExpandedVisualPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ScanRoot is null)
        {
            return null;
        }

        if (TryGetAggregateParentPath(path) is not null)
        {
            return path;
        }

        return _scanNodeIndex.FindNode(path) is null
            ? null
            : path;
    }

    private ScanSessionOptions CreateScanOptions()
    {
        return new ScanSessionOptions(
            RootPath: RootPath,
            AnalysisThresholdBytes: Math.Max(1, AnalysisThresholdMb) * 1024L * 1024L,
            UseTokenSavingMode: IsAiTokenSavingModeEnabled,
            MaxAiBatchSize: Math.Clamp(MaxAiBatchSize, 1, AiBatchPlanner.MaximumBatchSize),
            MaxAiAnalysisTokens: MaxAiAnalysisTokens,
            IncludeIntermediateTreeSnapshots: true,
            ProgressReportInterval: 96,
            TreeSnapshotIntervalMilliseconds: 250,
            AnalysisSnapshotGeneration: _snapshotGeneration);
    }

    private void ApplyIntermediateSnapshot(
        ScanProgressSnapshot snapshot,
        CancellationTokenSource scanCancellationTokenSource)
    {
        if (snapshot.Root is null
            || !_acceptIntermediateSnapshots
            || !IsScanning
            || scanCancellationTokenSource.IsCancellationRequested
            || !ReferenceEquals(_scanCancellationTokenSource, scanCancellationTokenSource))
        {
            return;
        }

        var root = snapshot.Root;
        // Scan navigation is disabled. The live overview needs the root and its visible
        // children, not two new dictionaries of every path on the disk for each update.
        // Apply this bounded preview synchronously so no obsolete indexing tasks pile up.
        ApplySnapshot(root, FileSystemNodeIndex.CreateRootOnly(root), root.Path, root.Path);
        StatusText = L.F($"扫描中：已发现 {FormatBytes(snapshot.BytesDiscovered)}，正在整理可视化块");
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(ScanStatusHeadlineText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
    }

    private long GetDisplaySizeBytes(FileSystemNode node)
    {
        return Math.Max(0, node.SizeBytes);
    }

    private long GetDisplayedDiscoveredBytes()
    {
        return Math.Max(0, _bytesDiscovered);
    }

    private AiProviderOptions CreateProviderOptions()
    {
        return new AiProviderOptions(
            SelectedProviderKind,
            AiEndpoint,
            AiModel,
            string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            ReasoningEffort: string.IsNullOrWhiteSpace(AiReasoningEffort) ? null : AiReasoningEffort);
    }

    private bool CurrentRootMatchesLastAnalyzedPath()
    {
        return CurrentRoot is not null
               && !string.IsNullOrWhiteSpace(_lastAnalyzedRootPath)
               && string.Equals(CurrentRoot.Path, _lastAnalyzedRootPath, StringComparison.Ordinal);
    }

    private void SetAnalysisFeedback(string analyzedRootPath, string summary)
    {
        _lastAnalyzedRootPath = analyzedRootPath;
        _lastAnalysisSummaryText = summary;
        OnPropertyChanged(nameof(AiResultsEmptyText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
    }

    private void ClearAnalysisFeedback()
    {
        _formatAnalysisFeedback = null;
        _lastRenderedAnalysisFeedback = "";
        _lastAnalyzedRootPath = string.Empty;
        _lastAnalysisSummaryText = string.Empty;
        OnPropertyChanged(nameof(AiResultsEmptyText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
    }

    private void DismissExplorerGuide()
    {
        _isAwaitingGuideScan = false;
        IsScanLocationPickerOpen = false;
        IsExplorerGuideOpen = false;
        var shouldRememberDismissal = IsDontShowExplorerGuideChecked;
        IsDontShowExplorerGuideChecked = false;
        if (_hasCompletedExplorerGuide == shouldRememberDismissal)
        {
            return;
        }

        _hasCompletedExplorerGuide = shouldRememberDismissal;
        OnPropertyChanged(nameof(HasCompletedExplorerGuide));
        SaveSettingsDeferred();
    }

    private void ResetExplorerGuide()
    {
        _hasCompletedExplorerGuide = false;
        IsDontShowExplorerGuideChecked = false;
        OnPropertyChanged(nameof(HasCompletedExplorerGuide));
        SaveSettingsDeferred();
        IsSettingsOpen = false;
        IsHelpOpen = false;
        _isAwaitingGuideScan = false;
        SetExplorerGuideStep(ScanRoot is null ? 0 : 2);
        IsExplorerGuideOpen = true;
    }

    private void CloseScanLocationPicker()
    {
        IsScanLocationPickerOpen = false;
        if (IsExplorerGuideOpen && ExplorerGuideStep == 1)
        {
            SetExplorerGuideStep(0);
        }
    }

    private void CompleteGuideScan()
    {
        if (!_isAwaitingGuideScan)
        {
            return;
        }

        _isAwaitingGuideScan = false;
        if (IsFocusMode)
        {
            // The user already used the control this final guide step would explain.
            DismissExplorerGuide();
            return;
        }

        SetExplorerGuideStep(2);
        IsExplorerGuideOpen = true;
    }

    private void RestoreGuideAfterUnsuccessfulScan()
    {
        if (!_isAwaitingGuideScan)
        {
            return;
        }

        _isAwaitingGuideScan = false;
        IsFocusMode = false;
        SetExplorerGuideStep(0);
        IsExplorerGuideOpen = true;
    }

    private void SetScanTerminalStatus(string? headline, string? detail)
    {
        if (string.Equals(_scanTerminalHeadlineText, headline, StringComparison.Ordinal)
            && string.Equals(_scanTerminalDetailText, detail, StringComparison.Ordinal))
        {
            return;
        }

        _scanTerminalHeadlineText = headline;
        _scanTerminalDetailText = detail;
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(ScanStatusHeadlineText));
        OnPropertyChanged(nameof(ScanStatusDetailText));
    }

    private void SetExplorerGuideStep(int step)
    {
        var normalizedStep = Math.Clamp(step, 0, 2);
        if (_explorerGuideStep == normalizedStep)
        {
            return;
        }

        _explorerGuideStep = normalizedStep;
        OnPropertyChanged(nameof(ExplorerGuideStep));
        OnPropertyChanged(nameof(ExplorerGuideStepText));
        OnPropertyChanged(nameof(ExplorerGuideTitleText));
        OnPropertyChanged(nameof(ExplorerGuideBodyText));
        OnPropertyChanged(nameof(IsFocusModeToggleVisible));
        OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
        OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
    }

    private void RefreshAvailableDrives()
    {
        var hadNoDrives = NoAvailableDrives;
        var refreshedDrives = LoadAvailableDrives();
        if (!AvailableDrives.SequenceEqual(refreshedDrives))
        {
            AvailableDrives.Clear();
            foreach (var drive in refreshedDrives)
            {
                AvailableDrives.Add(drive);
            }
        }

        if (hadNoDrives != NoAvailableDrives)
        {
            OnPropertyChanged(nameof(NoAvailableDrives));
            OnPropertyChanged(nameof(NoDriveMessageText));
            OnPropertyChanged(nameof(ExplorerGuideBodyText));
        }

        OnPropertyChanged(nameof(ScanLocationPickerMessageText));
    }

    private void SaveSettingsDeferred()
    {
        if (!_suspendSettingsSave)
        {
            _ = SaveSettingsAsync();
        }
    }

    private async Task<bool> SaveSettingsAsync()
    {
        if (_isDisposed)
        {
            return true;
        }

        if (Workspace is not null)
        {
            return await Workspace.SaveSharedSettingsAsync(this).ConfigureAwait(true);
        }

        try
        {
            await _settingsStore.SaveAsync(new AppSettings(
                RootPath,
                AnalysisThresholdMb,
                VisualFolderDisplayThresholdMb,
                SelectedProviderKind,
                AiEndpoint,
                AiModel,
                MaxAiBatchSize,
                IsAiTokenSavingModeEnabled,
                SelectedThemeId,
                HasCompletedExplorerGuide,
                IsReduceMotionEnabled,
                MaxAiAnalysisTokens,
                AiReasoningEffort,
                SelectedAiConnectionId,
                HideAiDisclaimer, SelectedLanguage)).ConfigureAwait(false);
            await _secretStore.SaveApiKeyAsync(ApiKey).ConfigureAwait(false);
            await ReportSettingsSaveErrorAsync(null).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await ReportSettingsSaveErrorAsync(L.F($"设置保存失败：{ex.Message}")).ConfigureAwait(false);
            return false;
        }
    }

    private Task ReportSettingsSaveErrorAsync(string? message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => ReportSettingsSaveError(message)).Task;
        }

        ReportSettingsSaveError(message);
        return Task.CompletedTask;
    }

    private void ReportSettingsSaveError(string? message)
    {
        SettingsSaveErrorText = message ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText = message;
        }
    }

    private bool CanOpenSelectedLocation()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return false;
        }

        if (IsAggregateNode(node))
        {
            return false;
        }

        return CanOpenNodeLocation(node);
    }

    private bool CanRecycleSelectedNode()
    {
        var node = SelectedNode;
        if (node is null || IsScanning || IsAnalyzing)
        {
            return false;
        }

        if (IsAggregateNode(node))
        {
            return false;
        }

        return CanRecycleNode(node);
    }

    private FileSystemNode? ResolveTargetNode(object? parameter)
    {
        return parameter switch
        {
            AnalysisListItem item => item.Node,
            FileSystemNode node => node,
            _ => SelectedNode,
        };
    }

    private void RaiseCommandStateChanged()
    {
        ReanalyzeWithoutMemoryCommand?.RaiseCanExecuteChanged();
        ToggleReanalysisSelectionCommand?.RaiseCanExecuteChanged();
        CancelReanalysisSelectionCommand?.RaiseCanExecuteChanged();
        ReportSharedExperienceCommand?.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        SelectScanRootCommand.RaiseCanExecuteChanged();
        BrowseScanFolderCommand.RaiseCanExecuteChanged();
        BrowseFolderFromHomeCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        AnalyzeCommand.RaiseCanExecuteChanged();
        OpenAiSetupCommand.RaiseCanExecuteChanged();
        ConfigureAiConnectionCommand.RaiseCanExecuteChanged();
        ChooseDeepSeekCommand.RaiseCanExecuteChanged();
        ChooseCustomAiCommand.RaiseCanExecuteChanged();
        BackAiSetupCommand.RaiseCanExecuteChanged();
        SaveAiSetupAndAnalyzeCommand.RaiseCanExecuteChanged();
        OpenLocationCommand.RaiseCanExecuteChanged();
        RecycleCommand.RaiseCanExecuteChanged();
        OpenAnalysisItemLocationCommand.RaiseCanExecuteChanged();
        RecycleAnalysisItemCommand.RaiseCanExecuteChanged();
        ZoomIntoNodeCommand.RaiseCanExecuteChanged();
        ZoomOutCommand.RaiseCanExecuteChanged();
        ResetZoomCommand.RaiseCanExecuteChanged();
        RaiseRefreshCommandStateChanged();
        OpenScanReportCommand.RaiseCanExecuteChanged();
        NotifySharedRematchState();
    }

    private bool CanOpenNodeLocation(FileSystemNode node)
    {
        return !IsAggregateNode(node)
               && _deletionPolicyService.Evaluate(node.Path, node.IsDirectory).CanOpenLocation;
    }

    private bool CanRecycleNode(FileSystemNode node)
    {
        return !IsScanning
               && !IsAnalyzing
               && !IsRefreshing
               && !IsAggregateNode(node)
               && _deletionPolicyService.Evaluate(node.Path, node.IsDirectory).CanRecycle;
    }

    private void RefreshVisibleAnalysisItems()
    {
        _visibleAnalysisItems = ScanRoot is null || CurrentRoot is null
            ? []
            : AnalysisItems.Where(item => VisibleAnalysisFor(item.Node) is not null).ToArray();
    }

    private FileSystemNode? ResolveSnapshotNode(string? path, FileSystemNode? previousNode)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (TryGetAggregateParentPath(path) is string parentPath)
        {
            // Keep the clicked page's membership (including its file/folder filter),
            // but refresh its contents from the authoritative scan index.
            if (previousNode is not null
                && IsAggregateNode(previousNode)
                && string.Equals(previousNode.Path, path, StringComparison.Ordinal))
            {
                var physicalMarker = path.IndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
                var physicalPath = path[..physicalMarker];
                if (physicalPath.Length == 2 && physicalPath[1] == ':')
                {
                    physicalPath += Path.DirectorySeparatorChar;
                }

                if (_scanNodeIndex.FindNode(physicalPath) is null)
                {
                    return null;
                }

                var refreshed = ResolveAggregateDisplayNode(previousNode);
                return refreshed.Children.Count > 0 ? refreshed : null;
            }

            // A page is built from the previous page, not from the scan root.
            // Root-wide MaxDepth cannot discover nested or deep-directory pages.
            var parent = ResolveSnapshotNode(parentPath, FindPreviousDisplayNode(parentPath));
            return parent is null ? null : _visualExplorerBuilder
                .Build(parent, new VisualExplorerOptions())
                .FirstOrDefault(item => string.Equals(item.DisplayNode.Path, path, StringComparison.Ordinal))
                ?.DisplayNode;
        }

        return _scanNodeIndex.FindNode(path);
    }

    private FileSystemNode? FindPreviousDisplayNode(string path)
    {
        if (string.Equals(CurrentRoot?.Path, path, StringComparison.Ordinal))
        {
            return CurrentRoot;
        }

        return _navigationHistory.FirstOrDefault(node => string.Equals(node.Path, path, StringComparison.Ordinal));
    }

    private FileSystemNode? ResolveDisplayNodeToScanNode(FileSystemNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (IsAggregateNode(node))
        {
            return ResolveAggregateDisplayNode(node);
        }

        return _scanNodeIndex.FindNode(node.Path) ?? node;
    }

    private void PushNavigationHistory(FileSystemNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (_navigationHistory.TryPeek(out var previousNode)
            && PathsMatch(previousNode.Path, node.Path))
        {
            return;
        }

        _navigationHistory.Push(node);
    }

    private bool TryPopNavigationHistory(FileSystemNode currentRoot, out FileSystemNode? previousRoot)
    {
        while (_navigationHistory.Count > 0)
        {
            var candidate = _navigationHistory.Pop();
            var resolvedCandidate = ResolveSnapshotNode(candidate.Path, candidate);
            if (resolvedCandidate is null)
            {
                continue;
            }

            if (PathsMatch(resolvedCandidate.Path, currentRoot.Path))
            {
                continue;
            }

            previousRoot = resolvedCandidate;
            return true;
        }

        previousRoot = null;
        return false;
    }

    private void RefreshNavigationHistory()
    {
        if (_navigationHistory.Count == 0)
        {
            return;
        }

        var previousNodes = _navigationHistory.Reverse().ToArray();
        _navigationHistory.Clear();
        foreach (var previousNode in previousNodes)
        {
            var resolvedNode = ResolveSnapshotNode(previousNode.Path, previousNode);
            if (resolvedNode is null)
            {
                continue;
            }

            PushNavigationHistory(resolvedNode);
        }
    }

    private void RebuildNavigationHistory(FileSystemNode currentRoot)
    {
        _navigationHistory.Clear();
        if (ScanRoot is null || PathsMatch(currentRoot.Path, ScanRoot.Path))
        {
            return;
        }

        var ancestors = new List<FileSystemNode>();
        var cursor = currentRoot;
        while (ancestors.Count < 256 && !PathsMatch(cursor.Path, ScanRoot.Path))
        {
            var parent = ResolveDisplayParentNode(cursor);
            if (parent is null || PathsMatch(parent.Path, cursor.Path))
            {
                break;
            }

            ancestors.Add(parent);
            cursor = parent;
        }

        for (var index = ancestors.Count - 1; index >= 0; index--)
        {
            PushNavigationHistory(ancestors[index]);
        }
    }

    private static bool PathsMatch(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private FileSystemNode? ResolveDisplayParentNode(FileSystemNode currentRoot)
    {
        if (IsAggregateNode(currentRoot))
        {
            var aggregateParentPath = TryGetAggregateParentPath(currentRoot.Path);
            if (!string.IsNullOrWhiteSpace(aggregateParentPath))
            {
                return ResolveSnapshotNode(aggregateParentPath, FindPreviousDisplayNode(aggregateParentPath))
                       ?? _scanNodeIndex.FindParentNode(aggregateParentPath)
                       ?? FindClosestIndexedAncestorNode(aggregateParentPath);
            }
        }

        return _scanNodeIndex.FindParentNode(currentRoot.Path)
               ?? FindClosestIndexedAncestorNode(currentRoot.Path);
    }

    private FileSystemNode? FindClosestIndexedAncestorNode(string path)
    {
        var ancestorPath = GetParentPath(path);
        while (!string.IsNullOrWhiteSpace(ancestorPath))
        {
            var candidate = _scanNodeIndex.FindNode(ancestorPath);
            if (candidate is not null)
            {
                return candidate;
            }

            ancestorPath = GetParentPath(ancestorPath);
        }

        return null;
    }

    private static string? GetParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (TryGetAggregateParentPath(path) is string aggregateParentPath)
        {
            return aggregateParentPath;
        }

        var parentPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            return parentPath;
        }

        var rootPath = Path.GetPathRoot(path);
        return string.Equals(rootPath, path, StringComparison.OrdinalIgnoreCase)
            ? null
            : rootPath;
    }

    private FileSystemNode ResolveAggregateDisplayNode(FileSystemNode aggregateNode)
    {
        var resolvedChildren = aggregateNode.Children
            .Select(child => IsAggregateNode(child)
                ? ResolveAggregateDisplayNode(child)
                : _scanNodeIndex.FindNode(child.Path))
            .OfType<FileSystemNode>()
            .ToArray();

        return aggregateNode with
        {
            Name = DisplayNodeMetadata.IsOverflow(aggregateNode)
                ? L.F($"其余 {resolvedChildren.Length:N0} 项")
                : aggregateNode.Name,
            Children = resolvedChildren,
            SizeBytes = resolvedChildren.Sum(child => child.SizeBytes),
            LogicalSizeBytes = aggregateNode.LogicalSizeBytes is null ? null
                : resolvedChildren.Sum(child => child.LogicalSizeBytes ?? child.SizeBytes),
            DescendantFileCount = resolvedChildren.Sum(child => child.IsDirectory ? child.DescendantFileCount : 1),
            DescendantDirectoryCount = resolvedChildren.Sum(child => child.IsDirectory ? child.DescendantDirectoryCount + 1 : 0),
        };
    }

    private static bool IsWithinRemovedSubtree(string? candidatePath, string removedPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(removedPath))
        {
            return false;
        }

        var markerIndex = candidatePath.IndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
        var physicalCandidatePath = markerIndex > 0 ? candidatePath[..markerIndex] : candidatePath;
        var normalizedCandidate = physicalCandidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRemoved = removedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedCandidate, normalizedRemoved, StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(normalizedRemoved + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || normalizedCandidate.StartsWith(normalizedRemoved + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsAggregateNode(FileSystemNode? node)
    {
        return node is not null && node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag);
    }

    private static bool ContainsVisualItemPath(IEnumerable<VisualExplorerItem> items, string path)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.DisplayNode.Path, path, StringComparison.Ordinal))
            {
                return true;
            }

            if (ContainsVisualItemPath(item.Children, path))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetAggregateParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var markerIndex = path.LastIndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return null;
        }

        var parentPath = path[..markerIndex];
        return parentPath.Length == 2 && parentPath[1] == ':'
            ? parentPath + Path.DirectorySeparatorChar
            : parentPath;
    }

    private static FileSystemNode ApplyAnalysisResults(FileSystemNode node, IReadOnlyList<AiAnalysisResult> results)
    {
        var map = results
            .GroupBy(result => result.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return ApplyAnalysisResults(node, map);
    }

    private static FileSystemNode ApplyAnalysisResults(FileSystemNode node, IReadOnlyDictionary<string, AiAnalysisResult> results)
    {
        FileSystemNode[]? changedChildren = null;
        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = ApplyAnalysisResults(node.Children[index], results);
            if (ReferenceEquals(child, node.Children[index])) continue;
            changedChildren ??= node.Children.ToArray();
            changedChildren[index] = child;
        }
        var analysis = results.TryGetValue(node.Path, out var result) ? result : node.Analysis;
        // A failed retry is not evidence that an already verified cloud answer vanished.
        if (analysis?.LooksLikeFallback() == true && node.Analysis is { } previous
            && IsUsableKnownCloudResult(previous)) analysis = previous;
        if (changedChildren is null && Equals(analysis, node.Analysis)) return node;
        return node with
        {
            Analysis = analysis,
            Children = changedChildren ?? node.Children,
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    private static IReadOnlyList<DriveScanOption> LoadAvailableDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return [];
        }

        var availableDrives = new List<DriveScanOption>(drives.Length);
        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var capacity = Math.Max(0, drive.TotalSize);
                var freeSpace = Math.Clamp(drive.AvailableFreeSpace, 0, capacity);
                var volumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? L.T("本地磁盘") : drive.VolumeLabel;
                availableDrives.Add(new DriveScanOption(
                    RootPath: drive.RootDirectory.FullName,
                    DisplayName: drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    VolumeLabel: volumeLabel,
                    CapacityText: FormatBytes(capacity),
                    FreeSpaceText: L.F($"可用 {FormatBytes(freeSpace)}"),
                    UsedPercentage: capacity <= 0 ? 0 : Math.Clamp((capacity - freeSpace) * 100.0 / capacity, 0, 100),
                    CapacityBytes: capacity,
                    FreeBytes: freeSpace,
                    IsDefaultVolumeLabel: string.IsNullOrWhiteSpace(drive.VolumeLabel)));
            }
            catch
            {
                // A disconnected removable volume should not hide other ready drives.
            }
        }

        return availableDrives
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Max(1, Math.Round(duration.TotalMinutes))} min";
        }

        return $"{Math.Max(1, Math.Round(duration.TotalSeconds))} sec";
    }

    private static string ToRiskText(RiskLevel riskLevel)
    {
        return riskLevel switch
        {
            RiskLevel.Low => L.T("低风险"),
            RiskLevel.Review => L.T("建议复核"),
            RiskLevel.High => L.T("高风险"),
            RiskLevel.Protected => L.T("受保护"),
            _ => L.T("未知"),
        };
    }

    private static string ToActionText(SuggestedAction action)
    {
        return action switch
        {
            SuggestedAction.Keep => L.T("保留"),
            SuggestedAction.RecycleCandidate => L.T("可考虑送入回收站"),
            SuggestedAction.DoNotDelete => L.T("不要删除"),
            _ => L.T("建议复核"),
        };
    }

    private sealed record LocalRemovalResult(FileSystemNode UpdatedRoot, FileSystemNodeIndex NodeIndex);
}

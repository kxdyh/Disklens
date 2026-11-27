using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;
using DiskLens.Core.Networking;
using Microsoft.Win32;

namespace DiskLens.App;

public partial class SharedKnowledgeWindow : Window
{
    private readonly SharedKnowledgeRuntime _runtime;
    private readonly Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? _preview;
    private readonly Func<Task<SharedContributionReceipt>>? _history;
    private readonly CancellationTokenSource _lifetime = new();
    private SharedPreferences _settings = new();
    private bool _busy;
    private bool _closeRequested;
    private FeedbackKind _feedbackKind;
    private Func<Task>? _retryAction;
    private bool _retryInProgress;
    private enum FeedbackKind { Info, Busy, Error, Success }
    private bool _agreementRead;
    private TimeProvider _agreementClock = TimeProvider.System;
    private readonly DispatcherTimer _agreementTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(100) };
    private TimeSpan _agreementRemaining = TimeSpan.FromSeconds(5);
    private long? _agreementStartedAt;
    private bool _initialized;
    private bool _scopeReviewed;
    private bool _enableCompleted;
    private Task? _backgroundHistoryTask;
    private int _page;
    private bool _rendering;
    private int _managementReturnPage;
    private Func<string>? _formatPreviewText;

    public SharedKnowledgeWindow(SharedKnowledgeRuntime runtime, string themeId,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? preview = null,
        Func<Task<SharedContributionReceipt>>? history = null, bool openManagement = false)
    {
        _runtime = runtime; _preview = preview; _history = history;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/{ThemeCatalog.GetDictionaryPath(themeId)}") });
        var dark = ThemeCatalog.Normalize(themeId) == "01";
        Resources["MainSurfaceBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#151515" : "#F5F5F3"));
        Resources["CardBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#1A1A19" : "#FCFCFB"));
        Resources["CardSubtleBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#20201E" : "#EDEDE9"));
        Resources["DialogHeadingWeight"] = dark ? FontWeights.Medium : FontWeights.SemiBold;
        Resources["DialogStrongWeight"] = dark ? FontWeights.Normal : FontWeights.Medium;
        Resources["DialogWarningWeight"] = dark ? FontWeights.SemiBold : FontWeights.Bold;
        Resources["DialogBodyWeight"] = FontWeights.Normal;
        Resources["FeedbackErrorBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#382422" : "#FFF0EC"));
        Resources["FeedbackErrorBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#C87568" : "#CC6554"));
        Resources["FeedbackBusyBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#203637" : "#E1F1F2"));
        Resources["FeedbackBusyBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#65AAAF" : "#47878C"));
        Resources["FeedbackInfoBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#28332A" : "#EAF2E7"));
        Resources["FeedbackInfoBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#739A72" : "#7C9C65"));
        var disclosureTime = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 180 : 0);
        Resources["DisclosureDuration"] = new Duration(disclosureTime);
        Resources["DisclosureEndTime"] = System.Windows.Media.Animation.KeyTime.FromTimeSpan(disclosureTime);
        Resources["DialogIllustrationBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#8BC6A6" : "#527A64"));
        InitializeComponent();
        _agreementTimer.Tick += AgreementCountdown_OnTick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ResumeAgreementCountdown();
            else PauseAgreementCountdown();
        };
        WindowLanguage.Attach(this);
        WindowLanguage.Register(this, () => OnLanguageChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(L.Language))));
        FitWindowToWorkArea();
        SetPage(0);
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            _settings = await _runtime.Settings.LoadAsync(_lifetime.Token);
            Render();
            _initialized = true;
            SetPage(openManagement ? 3 : 0);
        });
        Closing += (_, _) => { _closeRequested = true; PauseAgreementCountdown(); _lifetime.Cancel(); };
        Closed += (_, _) => { if (!_busy) _lifetime.Dispose(); };
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        CloseButton.Content = _settings.Enabled ? L.T("返回") : L.T("不启用，返回");
        SaveButton.Content = _settings.Enabled ? L.T("保存共享设置") : L.T("启用文件用途经验共享");
        NextButton.Content = _page == 0 ? L.T("下一页") : L.T("阅读协议");
        ManagementContinueButton.Content = _managementReturnPage == 2 ? L.T("返回启用流程") : L.T("了解并启用");
        DialogHeadingText.Text = _page == 3 ? L.T("文件用途经验共享管理") : L.T("文件用途经验共享");
        if (!string.IsNullOrWhiteSpace(FeedbackText.Text)) SetFeedback(L.T(FeedbackText.Text), _feedbackKind);
        if (_formatPreviewText is not null) PreviewText.Text = _formatPreviewText();
        UpdateAgreementCountdownText();
    }

    private void Render(bool preserveConsent = false)
    {
        if (_closeRequested) return;
        _rendering = true;
        try
        {
        CloseButton.Content = _settings.Enabled ? L.T("返回") : L.T("不启用，返回");
        SaveButton.Content = _settings.Enabled ? L.T("保存共享设置") : L.T("启用文件用途经验共享");
        DisableButton.IsEnabled = _settings.Enabled;
        if (!preserveConsent)
        {
        IncludeHistory.IsChecked = _settings.ShareHistory;
        AcceptFuture.IsChecked = _settings.Enabled;
        // Stored consent does not preselect a new enable/save decision.
        AcceptAgreement.IsChecked = false;
        AcceptAgreement.IsEnabled = false;
        _agreementRead = false;
        PauseAgreementCountdown();
        _agreementRemaining = TimeSpan.FromSeconds(5);
        UpdateAgreementCountdownText();
        AgreementScroll.ScrollToHome();
        }
        ExclusionList.ItemsSource = (_settings.ExcludedPaths ?? []).ToList();
        ServiceEndpointText.Text = _runtime.Endpoint.AbsoluteUri;
        ImportHistoryButton.IsEnabled = _history is not null && _settings.Enabled && _settings.ShareHistory;
        EnabledNotice.Visibility = _settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        DisableButton.Visibility = _settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _rendering = false; ResumeAgreementCountdown(); UpdateControls(); }
    }

    private void FitWindowToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MaxWidth = Math.Max(320, area.Width - 24);
        MaxHeight = Math.Max(320, area.Height - 24);
        MinWidth = Math.Min(900, MaxWidth);
        MinHeight = Math.Min(680, MaxHeight);
        Width = Math.Min(1080, MaxWidth);
        Height = Math.Min(820, MaxHeight);
    }

    private void SetPage(int page)
    {
        if (IntroPage is null || _closeRequested) return;
        if (page != 2) PauseAgreementCountdown();
        _page = Math.Clamp(page, 0, 3);
        IntroPage.Visibility = _page == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScopePage.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        ManagementPage.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = _page != 2 ? Visibility.Visible : Visibility.Collapsed;
        AgreementPage.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        ScopeCloudConnectionHelp.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        AgreementCloudConnectionHint.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = _page is > 0 and < 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _page < 2 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _page == 0 ? L.T("下一页") : L.T("阅读协议");
        SaveButton.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        ManagementContinueButton.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        ManagementContinueButton.Content = _managementReturnPage == 2 ? L.T("返回启用流程") : L.T("了解并启用");
        StepProgress.Visibility = PageNumberText.Visibility = _page == 3 ? Visibility.Collapsed : Visibility.Visible;
        DialogHeadingText.Text = _page == 3 ? L.T("文件用途经验共享管理") : L.T("文件用途经验共享");
        PageNumberText.Text = $"{_page + 1} / 3";
        var steps = new[] { StepOne, StepTwo, StepThree };
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i].SetResourceReference(TextBlock.ForegroundProperty, i == _page ? "PrimaryTextBrush" : "MutedTextBrush");
            steps[i].SetResourceReference(TextBlock.FontWeightProperty, i == _page ? "DialogStrongWeight" : "DialogBodyWeight");
        }
        PageScroll.ScrollToHome();
        if (_page == 2) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AgreementScroll.Focus()));
        ResumeAgreementCountdown();
        UpdateControls();
    }

    private async void Next_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_page == 0)
        {
            SetPage(1);
            return;
        }
        if (_page == 1 && !_scopeReviewed)
        {
            await RunAsync(PreviewScopeAsync);
            if (!_scopeReviewed) return;
        }
        SetPage(_page + 1);
    }
    private void Previous_OnClick(object sender, RoutedEventArgs e) => SetPage(_page - 1);

    private void ManageScope_OnClick(object sender, RoutedEventArgs e)
    {
        _managementReturnPage = 2;
        SetPage(3);
    }

    private async void ManagementContinue_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_managementReturnPage == 2 && !_scopeReviewed) await RunAsync(PreviewScopeAsync);
        if (_managementReturnPage == 2 && !_scopeReviewed) return;
        SetPage(_managementReturnPage);
    }

    private async Task RunAsync(Func<Task> action, Func<Task>? retry = null)
    {
        if (_busy || _closeRequested) return;
        _retryAction = retry;
        _busy = true;
        SetFeedback(null);
        ActivityBar.Visibility = Visibility.Visible;
        UpdateControls();
        try { await action(); }
        catch (Exception ex) when (NetworkFailureClassifier.IsOffline(ex, _runtime.NetworkAvailability))
        {
            if (_closeRequested) return;
            _settings = await _runtime.Settings.LoadAsync();
            Render(preserveConsent: true);
            SetFeedback(L.T(NetworkFailureClassifier.OfflineMessage), FeedbackKind.Error);
        }
        catch (SharedRequestTimeoutException)
        {
            if (_closeRequested) return;
            _settings = await _runtime.Settings.LoadAsync();
            Render(preserveConsent: true);
            SetFeedback(L.T("连接云端经验库超时，请稍后重试。磁盘扫描可继续使用。"), FeedbackKind.Error);
        }
        catch (OperationCanceledException)
        {
            if (_closeRequested) return;
            _settings = await _runtime.Settings.LoadAsync();
            Render(preserveConsent: true);
            SetFeedback(L.T("云端连接已中断，请稍后重试。磁盘扫描可继续使用。"), FeedbackKind.Error);
        }
        catch (Exception ex)
        {
            if (_closeRequested) return;
            _settings = await _runtime.Settings.LoadAsync();
            Render(preserveConsent: true);
            SetFeedback(DescribeFailure(ex), FeedbackKind.Error);
        }
        finally
        {
            _busy = false;
            if (_feedbackKind != FeedbackKind.Error) _retryAction = null;
            if (_closeRequested) _lifetime.Dispose();
            else
            {
                ActivityBar.Visibility = Visibility.Collapsed;
                UpdateControls();
            }
        }
    }

    private string DescribeFailure(Exception exception) => exception switch
    {
        SharedNewContributorsPausedException =>
            L.T("贡献计划暂时暂停接纳新用户，请稍后再试。磁盘扫描可继续使用。"),
        System.Net.Http.HttpRequestException { StatusCode: not null } =>
            L.T("云端服务暂时无法完成本次操作，请稍后重试。磁盘扫描可继续使用。"),
        System.Net.Http.HttpRequestException =>
            L.T("无法建立云端连接，请检查网络或稍后重试。磁盘扫描可继续使用。"),
        InvalidDataException or JsonException =>
            L.T("云端响应格式异常，尚未完成本次操作。请稍后重试。"),
        _ => _runtime.Settings.LastError ?? L.T("暂时无法连接云端经验库，请稍后重试。磁盘扫描可继续使用。")
    };

    private void SetFeedback(string? message, FeedbackKind kind = FeedbackKind.Info)
    {
        if (_closeRequested) return;
        _feedbackKind = kind;
        FeedbackText.Text = message ?? "";
        FeedbackPanel.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (FeedbackPanel.Visibility != Visibility.Visible) return;
        var (background, border, icon, title) = kind switch
        {
            FeedbackKind.Error => ("FeedbackErrorBackground", "FeedbackErrorBorder", "!",
                _page == 2 && !_settings.Enabled ? "云端连接失败，共享未启用" : "云端操作失败"),
            FeedbackKind.Busy => ("FeedbackBusyBackground", "FeedbackBusyBorder", "…", "正在处理云端共享"),
            FeedbackKind.Success => ("FeedbackInfoBackground", "FeedbackInfoBorder", "✓", "操作已完成"),
            _ => ("FeedbackInfoBackground", "FeedbackInfoBorder", "i", "请注意")
        };
        FeedbackPanel.SetResourceReference(Border.BackgroundProperty, background);
        FeedbackPanel.SetResourceReference(Border.BorderBrushProperty, border);
        FeedbackIcon.SetResourceReference(TextBlock.ForegroundProperty, kind == FeedbackKind.Error ? "StatusDangerBrush" : "PrimaryTextBrush");
        FeedbackTitle.SetResourceReference(TextBlock.ForegroundProperty, kind == FeedbackKind.Error ? "StatusDangerBrush" : "PrimaryTextBrush");
        FeedbackIcon.Text = icon;
        FeedbackTitle.Text = L.T(title);
        UpdateFeedbackAction();
    }

    private void UpdateFeedbackAction()
    {
        FeedbackActionButton.Content = L.T(_feedbackKind switch
        {
            FeedbackKind.Error => "重试",
            FeedbackKind.Busy => "停止连接并关闭",
            _ => "关闭并继续本地使用"
        });
        FeedbackActionButton.Visibility = _feedbackKind == FeedbackKind.Error && _retryAction is null
            ? Visibility.Collapsed : Visibility.Visible;
        FeedbackActionButton.IsEnabled = !_retryInProgress && (!_busy || _feedbackKind == FeedbackKind.Busy);
    }

    private async void FeedbackAction_OnClick(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || _retryInProgress) return;
        if (_feedbackKind == FeedbackKind.Error)
        {
            if (!_busy && _retryAction is { } retry)
            {
                _retryInProgress = true;
                UpdateFeedbackAction();
                try { await retry(); }
                finally
                {
                    _retryInProgress = false;
                    if (!_closeRequested) UpdateFeedbackAction();
                }
            }
            return;
        }
        Close();
    }

    private void UpdateControls()
    {
        if (SaveButton is null) return;
        UpdateFeedbackAction();
        SaveButton.IsEnabled = _page == 2 && _initialized && !_busy && _agreementRead && _scopeReviewed
            && AcceptAgreement.IsChecked == true && AcceptFuture.IsChecked == true;
        NextButton.IsEnabled = PreviousButton.IsEnabled = _initialized && !_busy;
        DisableButton.IsEnabled = _initialized && !_busy && _settings.Enabled;
        AcceptAgreement.IsEnabled = _page == 2 && _agreementRead && !_busy;
        AcceptFuture.IsEnabled = IncludeHistory.IsEnabled = !_busy;
        PreviewButton.IsEnabled = !_busy;
        ImportHistoryButton.IsEnabled = !_busy && _history is not null && _settings.Enabled && _settings.ShareHistory;
    }

    private void Consent_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_rendering) UpdateControls();
    }

    // Expanding a description never grants consent; retained for callers that open the reading surface.
    private void Agreement_OnExpanded(object sender, RoutedEventArgs e)
    { UpdateControls(); }

    private TimeSpan AgreementTimeRemaining => _agreementStartedAt is { } started
        ? TimeSpan.FromTicks(Math.Max(0, (_agreementRemaining - _agreementClock.GetElapsedTime(started)).Ticks))
        : _agreementRemaining;

    private void ResumeAgreementCountdown()
    {
        if (_rendering || _closeRequested || _page != 2 || !IsVisible || _agreementRead) return;
        _agreementStartedAt ??= _agreementClock.GetTimestamp();
        _agreementTimer.Start();
        AgreementCountdown_OnTick(this, EventArgs.Empty);
    }

    private void PauseAgreementCountdown()
    {
        _agreementRemaining = AgreementTimeRemaining;
        _agreementStartedAt = null;
        _agreementTimer.Stop();
        UpdateAgreementCountdownText();
    }

    private void AgreementCountdown_OnTick(object? sender, EventArgs e)
    {
        if (_closeRequested || _page != 2 || !IsVisible)
        {
            PauseAgreementCountdown();
            return;
        }
        if (AgreementTimeRemaining <= TimeSpan.Zero)
        {
            _agreementRemaining = TimeSpan.Zero;
            _agreementStartedAt = null;
            _agreementTimer.Stop();
            _agreementRead = true;
            UpdateControls();
        }
        UpdateAgreementCountdownText();
    }

    private void UpdateAgreementCountdownText()
    {
        if (AgreementCountdownText is null) return;
        AgreementCountdownText.Text = L.T("请仔细阅读协议内容再确定是否参与此测试功能")
            + (!_agreementRead ? L.F($" - {Math.Ceiling(AgreementTimeRemaining.TotalSeconds):0} s") : string.Empty);
    }

    private async void AddExclusion_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new OpenFolderDialog { Title = L.T("选择不向经验服务器共享的目录") };
        if (picker.ShowDialog(this) != true) return;
        var paths = ExclusionPaths().ToList();
        if (!paths.Contains(picker.FolderName, StringComparer.OrdinalIgnoreCase)) paths.Add(picker.FolderName);
        ExclusionList.ItemsSource = paths;
        await SuspendForScopeChangeAsync(paths.ToArray());
    }

    private async void RemoveExclusion_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || ExclusionList.SelectedItem is not string path) return;
        var paths = ExclusionPaths().Where(item => item != path).ToArray();
        ExclusionList.ItemsSource = paths;
        await SuspendForScopeChangeAsync(paths);
    }

    private async Task SuspendForScopeChangeAsync(string[] paths) => await RunAsync(async () =>
    {
        var wasEnabled = _settings.Enabled;
        _scopeReviewed = false;
        _settings = _settings with { Enabled = false, ExcludedPaths = paths, PolicyRevision = Guid.NewGuid().ToString("N") };
        await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
        Render();
        _formatPreviewText = () => L.T("范围已变更，请重新预览。确认新范围并阅读协议后，可以重新开启。");
        PreviewText.Text = _formatPreviewText();
        SetFeedback(L.T("新的隐私保护清单已在本机生效，共享保持暂停。"), FeedbackKind.Success);
        if (wasEnabled && _settings.Installation is { } install)
        {
            var state = await _runtime.Client.GetConsentAsync(install, _lifetime.Token);
            var receipt = await _runtime.Client.UpdateConsentAsync(install,
                new(state.Epoch, false, SharedConsentSnapshot.CurrentAgreementVersion, _settings.ShareHistory, _settings.PolicyRevision), _lifetime.Token);
            _settings = _settings with { Epoch = receipt.Epoch, Installation = install with { Epoch = receipt.Epoch } };
            await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
        }
    });

    private string[] ExclusionPaths() => ExclusionList.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();

    private async void Preview_OnClick(object sender, RoutedEventArgs e) => await RunAsync(PreviewScopeAsync);

    private async Task PreviewScopeAsync()
    {
        if (_preview is null)
        {
            _formatPreviewText = () => L.T("面向普通文件与文件夹，不限定几个软件。上传成功的中、高置信度经验进入公共库，所有用户可按相同归一化路径匹配；不再设仅本人恢复。\n路径用途匹配不依赖子目录是否读取完整；需要结构验证的经验仍须通过结构检查。原始路径不以明文上传；短用途与详细用途继续经过隐私检查。\n当前没有扫描结果；不共享清单中的内容均不参与。");
            PreviewText.Text = _formatPreviewText();
            _scopeReviewed = true;
            return;
        }
        var rows = await _preview(ExclusionPaths());
        if (_closeRequested) return;
        _formatPreviewText = () => rows.Count == 0 ? L.T("当前范围没有可安全归一化的项目；不会上传。仍可发起文件用途分析。")
            : L.F($"可贡献 {rows.Count} 个匿名项目。符合条件的短用途和详细用途经隐私检查后原文共享；完整私人路径不会上传：\n") + string.Join("\n", rows.Take(30).Select(L.T));
        PreviewText.Text = _formatPreviewText();
        _scopeReviewed = true;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_busy || _enableCompleted) return;
        if (_page != 2 || !_agreementRead || AcceptAgreement.IsChecked != true || AcceptFuture.IsChecked != true)
        { SetPage(2); SetFeedback(L.T("请先等待阅读倒计时结束，并明确同意协议与今后贡献。保持关闭也可继续使用应用。")); return; }
        if (!_scopeReviewed)
        { SetFeedback(L.T("请先预览共享范围，确认不共享清单后再开启。预览不会连接经验服务器。")); return; }
        var exclusions = ExclusionPaths();
        var shareHistory = IncludeHistory.IsChecked == true;
        var completed = false;
        await RunAsync(async () =>
        {
            if (NetworkFailureClassifier.IsNetworkUnavailable(_runtime.Endpoint.AbsoluteUri, _runtime.NetworkAvailability))
            {
                await _runtime.Settings.SuspendAsync();
                _settings = await _runtime.Settings.LoadAsync();
                Render(preserveConsent: true);
                SetFeedback(L.T(NetworkFailureClassifier.OfflineMessage), FeedbackKind.Error);
                return;
            }
            // Suspend before any network call: stale scans must stop using the previous scope.
            await _runtime.Settings.SuspendAsync(_lifetime.Token);
            SetFeedback(L.T("正在连接云端经验库…"), FeedbackKind.Busy);
            var installation = _settings.Installation ?? await _runtime.Client.RegisterAsync(_lifetime.Token);
            _settings = _settings with { Enabled = false, Installation = installation };
            await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
            var current = await _runtime.Client.GetConsentAsync(installation, _lifetime.Token);
            var revision = Guid.NewGuid().ToString("N");
            var state = await _runtime.Client.UpdateConsentAsync(installation,
                new(current.Epoch, true, SharedConsentSnapshot.CurrentAgreementVersion, shareHistory, revision), _lifetime.Token);
            if (!state.Enabled || state.AgreementVersion != SharedConsentSnapshot.CurrentAgreementVersion
                || state.ShareHistory != shareHistory || state.PolicyRevision != revision || state.Epoch < current.Epoch)
                throw new InvalidDataException(L.T("云端未确认本次共享授权。"));
            _settings = _settings with
            {
                Enabled = true, AgreementVersion = SharedConsentSnapshot.CurrentAgreementVersion,
                ShareHistory = shareHistory, Epoch = state.Epoch, PolicyRevision = revision,
                EnabledAt = DateTimeOffset.UtcNow,
                ExcludedPaths = exclusions, Installation = installation with { Epoch = state.Epoch }
            };
            await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
            // The consent is committed only after the successful atomic local save.
            // Optional history uploads must not change this completed enable operation.
            _enableCompleted = completed = true;
        }, retry: SaveAsync);
        if (completed && !_closeRequested)
        {
            // ShowDialog closes when the result is assigned. Tests may host this as a non-modal Window.
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
            if (shareHistory && _history is not null)
                _backgroundHistoryTask = ImportHistoryAfterEnableAsync();
        }
    }

    private async Task ImportHistoryAfterEnableAsync()
    {
        // Let the modal return first. This work belongs to the application/runtime,
        // not the closed dialog's cancellation token; the outbox retains pending uploads.
        await Task.Yield();
        try
        {
            await _history!();
        }
        catch (Exception)
        {
            // A transient failure does not revoke saved consent; the durable
            // outbox retains the original contribution IDs for recovery.
        }
    }

    private async void Disable_OnClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await _runtime.Settings.SuspendAsync(_lifetime.Token);
        _settings = await _runtime.Settings.LoadAsync(_lifetime.Token);
        Render();
        SetFeedback(L.T("本机共享已停用。已上传内容可另行撤回。"), FeedbackKind.Success);
        if (_settings.Installation is not { } install) return;
        var state = await _runtime.Client.GetConsentAsync(install, _lifetime.Token);
        var updated = await _runtime.Client.UpdateConsentAsync(install,
            new(state.Epoch, false, SharedConsentSnapshot.CurrentAgreementVersion, _settings.ShareHistory, Guid.NewGuid().ToString("N")), _lifetime.Token);
        _settings = _settings with { Epoch = updated.Epoch, Installation = install with { Epoch = updated.Epoch } };
        await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
    });

    private async void ImportHistory_OnClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_history is null || !_settings.Enabled || !_settings.ShareHistory) return;
        var receipt = await _history();
        if (receipt.NetworkUnavailable)
        {
            _settings = await _runtime.Settings.LoadAsync();
            Render(preserveConsent: true);
            SetFeedback(L.T(NetworkFailureClassifier.OfflineMessage), FeedbackKind.Error);
            return;
        }
        SetFeedback(L.F($"历史校验完成：贡献 {receipt.Accepted} 项，跳过 {receipt.Skipped} 项；重复记录不会增加票数。"), FeedbackKind.Success);
    });

    private sealed record ContributionRow(SharedContributionItem Item)
    { public string DisplayText => $"{Item.Key}    {Item.Purpose}    {Item.Action}"; }

    private async void ListContributions_OnClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_settings.Installation is not { } install) { SetFeedback(L.T("尚无管理凭据，也没有由本次安装提交的贡献。")); return; }
        var list = await _runtime.Client.ListContributionsAsync(install, _lifetime.Token);
        ContributionList.ItemsSource = list.Items.Select(item => new ContributionRow(item)).ToList();
        SetFeedback(L.F($"已读取 {list.Items.Count} 条贡献。"), FeedbackKind.Success);
    });

    private async void WithdrawOne_OnClick(object sender, RoutedEventArgs e)
    {
        if (ContributionList.SelectedItem is ContributionRow row) await WithdrawAsync(row.Item.Id);
        else SetFeedback(L.T("请先选中一条贡献。"));
    }
    private async void WithdrawAll_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocalizedMessageBox.Show(this, L.T("撤回当前安装提交的全部贡献？共享会同时暂停，可稍后重新开启。"), L.T("撤回贡献"), MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            await WithdrawAsync(null);
    }
    private async Task WithdrawAsync(string? id) => await RunAsync(async () =>
    {
        await _runtime.Settings.SuspendAsync(_lifetime.Token);
        _settings = await _runtime.Settings.LoadAsync(_lifetime.Token);
        Render();
        if (_settings.Installation is not { } install) return;
        var state = await _runtime.Client.GetConsentAsync(install, _lifetime.Token);
        var receipt = await _runtime.Client.WithdrawAsync(install, state.Epoch, id, _lifetime.Token);
        _settings = _settings with { Epoch = receipt.Epoch, Installation = install with { Epoch = receipt.Epoch } };
        await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
        ContributionList.ItemsSource = null;
        SetFeedback(receipt.CleanupPending
            ? L.T("撤回已生效；部分记录尚待云端清理，可稍后再次撤回以继续。")
            : L.T("已撤回，服务器将重新计算受影响的经验结论。旧上传不能恢复已撤回内容。"), FeedbackKind.Success);
    });

    private async void Export_OnClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_settings.Installation is not { } install) { SetFeedback(L.T("尚无可导出的恢复凭据。")); return; }
        if (LocalizedMessageBox.Show(this, L.T("恢复文件包含可管理和撤回贡献的密钥，将以明文导出。请存到安全位置，不要发送给他人。"), L.T("导出恢复凭据"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var picker = new SaveFileDialog { FileName = L.T("DiskLens-文件用途经验共享恢复凭据.json"), Filter = L.T("JSON 文件|*.json") };
        if (picker.ShowDialog(this) != true) return;
        await File.WriteAllBytesAsync(picker.FileName, JsonSerializer.SerializeToUtf8Bytes(new RecoveryDocument(1, _runtime.Endpoint.AbsoluteUri, install)), _lifetime.Token);
        SetFeedback(L.T("恢复凭据已导出，请妥善保管。"), FeedbackKind.Success);
    });

    private sealed record RecoveryDocument(int Version, string Endpoint, SharedInstallation Installation);
    private async void Import_OnClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var picker = new OpenFileDialog { Filter = L.T("JSON 文件|*.json") };
        if (picker.ShowDialog(this) != true) return;
        if (new FileInfo(picker.FileName).Length > 16_384) throw new InvalidDataException();
        var restored = JsonSerializer.Deserialize<RecoveryDocument>(await File.ReadAllBytesAsync(picker.FileName, _lifetime.Token));
        if (restored?.Version != 1 || !SharedServiceIdentity.CanRestoreFrom(restored.Endpoint, _runtime.Endpoint) || restored.Installation is null)
        { SetFeedback(L.T("恢复文件无效或不属于当前服务；没有导入。")); return; }
        await _runtime.Settings.SuspendAsync(_lifetime.Token);
        _settings = await _runtime.Settings.LoadAsync(_lifetime.Token);
        Render();
        var state = await _runtime.Client.GetConsentAsync(restored.Installation, _lifetime.Token);
        _settings = _settings with { Enabled = false, Epoch = state.Epoch, Installation = restored.Installation with { Epoch = state.Epoch } };
        await _runtime.Settings.SaveAsync(_settings, _lifetime.Token);
        Render();
        SetFeedback(L.T("管理凭据已恢复。共享保持关闭，可查看或撤回贡献。"), FeedbackKind.Success);
    });

    private void CloudIssue_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/kxdyh/Disklens/issues")
            { UseShellExecute = true });
        }
        catch (Exception) { SetFeedback(L.T("暂时无法打开浏览器，请访问 github.com/kxdyh/Disklens。")); }
    }

    private void Repository_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/kxdyh/Disklens")
            { UseShellExecute = true });
        }
        catch (Exception)
        {
            SetFeedback(L.T("暂时无法打开浏览器，请访问 github.com/kxdyh/Disklens。"));
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
}

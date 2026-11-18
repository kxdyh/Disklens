using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Stopwatch _analysisClock = new();
    private bool _hasConfirmedAiSubmission;
    private bool _isAwaitingAiConfirmation;
    public bool IsPreparingAiSubmission => IsAnalyzing && !_hasConfirmedAiSubmission;
    private DispatcherTimer? _analysisClockTimer;
    private AiAnalysisProgress? _analysisProgress;
    private int _analysisProgressVersion;
    private bool _useCurrentDirectoryForAi;
    private string _activeAiScopePath = string.Empty;
    private string _aiUsageSummaryText = string.Empty;
    private string _aiUsageDetailText = string.Empty;
    private AiAnalysisReport? _aiOutcomeReport;
    private AiAnalysisStage? _aiOutcomeStage;

    public bool IsCurrentDirectoryAiScope
    {
        get => _useCurrentDirectoryForAi;
        set
        {
            if (IsAnalyzing || !SetProperty(ref _useCurrentDirectoryForAi, value)) return;
            NotifyAnalysisScopeChanged();
        }
    }

    public bool IsRootDirectoryAiScope
    {
        get => !_useCurrentDirectoryForAi;
        set { if (value) IsCurrentDirectoryAiScope = false; }
    }

    public string AiScopePathText => IsAnalyzing ? _activeAiScopePath : ResolveAiAnalysisRoot()?.Path ?? string.Empty;
    public string AiScopeDescriptionText => L.F($"分析范围含子目录 · ≥ {AnalysisThresholdMb:N0} MB");
    public string AiPhaseText => IsPreparingAiSubmission
        ? L.T(_isAwaitingAiConfirmation ? "等待确认分析" : "正在准备分析清单")
        : _analysisProgress?.Stage switch
    {
        AiAnalysisStage.LoadingCache => L.T("正在检查已有分析"),
        AiAnalysisStage.Analyzing => L.T("AI 正在分析"),
        AiAnalysisStage.Retrying => L.T("正在补充分析未完成项目"),
        AiAnalysisStage.Saving => L.T("正在保存分析结果"),
        AiAnalysisStage.Completed => L.T("分析完成"),
        AiAnalysisStage.Canceled => L.T("分析已取消"),
        AiAnalysisStage.StoppedForBudget => L.T("剩余预算不足，分析已停止"),
        AiAnalysisStage.Failed => L.T("分析未完成"),
        AiAnalysisStage.Uploading => L.T("正在上传云端经验"),
        _ => L.T("正在准备分析"),
    };
    public string AiProgressDetailText
    {
        get
        {
            var inFlight = new List<string>();
            if (_analysisProgress is { FreshActiveBatchSize: > 0 } fresh)
                inFlight.Add(L.F($"新项目通道正在分析 {fresh.FreshActiveBatchSize:N0} 项。"));
            if (_analysisProgress is { RetryActiveBatchSize: > 0 } retry)
                inFlight.Add(L.F($"重试通道正在补充 {retry.RetryActiveBatchSize:N0} 项。"));
            var message = inFlight.Count > 0 ? string.Join(" ", inFlight)
                : L.T(_analysisProgress?.Message) is { Length: > 0 } text ? text : L.T("正在整理候选项目…");
            return _analysisProgress is { } p && (p.RequestCount > 0 || p.QueuedRetryCount > 0)
                ? message + " · " + L.F($"新项目请求 {Math.Max(0, p.RequestCount - p.RetryRequestCount):N0} 次，补充分析 {p.RetryRequestCount:N0} 次，进行中 {p.ActiveRequestCount:N0} 个，待补充 {p.QueuedRetryCount:N0} 项，已补回 {p.RecoveredCandidateCount:N0} 项。")
                : message;
        }
    }
    public double AiCandidateProgressValue => (_analysisProgress?.Fraction ?? 0) * 100;
    public bool HasAiCandidateProgress => _analysisProgress?.TotalCandidates > 0;
    public string AiCandidateProgressText => _analysisProgress is { } p
        ? L.F($"已处理 {p.ProcessedCandidates:N0} / {p.TotalCandidates:N0} 项")
        : L.T("正在统计候选项目");
    public string AiElapsedText => IsPreparingAiSubmission ? L.T("尚未提交模型") : L.F($"已用时 {_analysisClock.Elapsed:mm\\:ss}");
    public string AiActivityText => IsPreparingAiSubmission ? L.T("正在整理本次提交范围，尚未调用模型，可以取消。")
        : _analysisProgress?.Stage == AiAnalysisStage.Uploading
        ? L.T("正在等待云端经验上传回执。")
        : _analysisProgress?.IsWaitingForResponse == true
        ? L.T("正在等待模型返回，请求仍在进行，可随时取消。")
        : L.T("正在处理本次分析，可随时取消。");
    public bool IsAiActivityAnimated => IsAnalyzing && !IsReduceMotionEnabled;
    public bool HasAiUsageSummary => !string.IsNullOrWhiteSpace(_aiUsageSummaryText);
    public string AiUsageSummaryText => _aiUsageSummaryText;
    public string AiUsageDetailText => _aiUsageDetailText;

    private FileSystemNode? ResolveAiAnalysisRoot() => IsCurrentDirectoryAiScope ? GetRefreshTarget() : ScanRoot;

    private void NotifyAnalysisScopeChanged()
    {
        OnPropertyChanged(nameof(IsRootDirectoryAiScope));
        OnPropertyChanged(nameof(IsCurrentDirectoryAiScope));
        OnPropertyChanged(nameof(AiScopePathText));
        OnPropertyChanged(nameof(AiScopeDescriptionText));
    }

    private IProgress<AiAnalysisProgress> BeginAiProgress(string path)
    {
        var version = ++_analysisProgressVersion;
        _activeAiScopePath = path;
        _analysisProgress = null;
        _hasConfirmedAiSubmission = false;
        _isAwaitingAiConfirmation = false;
        ClearAiOutcome();
        _aiUsageSummaryText = _aiUsageDetailText = string.Empty;
        _usageReport = null;
        _usageProvider = null;
        _analysisClock.Reset();
        _analysisClockTimer?.Stop();
        _analysisClockTimer = new DispatcherTimer(DispatcherPriority.Background,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _analysisClockTimer.Tick += AnalysisClock_OnTick;
        NotifyAiPreparationChanged();
        NotifyAiProgressChanged();
        NotifyAiUsageChanged();
        NotifyAnalysisScopeChanged();
        return new Progress<AiAnalysisProgress>(progress =>
        {
            if (_isDisposed || version != _analysisProgressVersion || !IsAnalyzing) return;
            _analysisProgress = progress;
            NotifyAiProgressChanged();
        });
    }

    private static Task RunOnAiUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) return dispatcher.InvokeAsync(action).Task;
        action();
        return Task.CompletedTask;
    }

    private void StartConfirmedAiProgress(bool hasModelCandidates)
    {
        _isAwaitingAiConfirmation = false;
        _hasConfirmedAiSubmission = true;
        if (hasModelCandidates)
        {
            _analysisClock.Restart();
            _analysisClockTimer?.Start();
        }
        NotifyAiPreparationChanged();
    }

    private void NotifyAiPreparationChanged()
    {
        OnPropertyChanged(nameof(IsPreparingAiSubmission));
        OnPropertyChanged(nameof(ScanStatusHeadlineText));
        NotifyAiProgressChanged();
    }

    private void AnalysisClock_OnTick(object? sender, EventArgs e)
    {
        if (!_isDisposed) OnPropertyChanged(nameof(AiElapsedText));
    }

    private void EndAiProgress()
    {
        if (_analysisClock.IsRunning && _aiOutcomeReport is null && !IsScanning && !IsScanQueued)
            _aiOutcomeStage = _usageReport?.StoppedForBudget == true ? AiAnalysisStage.StoppedForBudget
                : _analysisCancellationTokenSource?.IsCancellationRequested == true ? AiAnalysisStage.Canceled
                : _analysisProgress is { IsTerminal: true } progress ? progress.Stage : AiAnalysisStage.Failed;
        ++_analysisProgressVersion; // Ignore queued progress callbacks after cancel/close/finish.
        _analysisClock.Stop();
        if (_analysisClockTimer is not null)
        {
            _analysisClockTimer.Stop();
            _analysisClockTimer.Tick -= AnalysisClock_OnTick;
            _analysisClockTimer = null;
        }
        NotifyAiProgressChanged();
        NotifyAnalysisScopeChanged();
    }

    private void ClearAiOutcome()
    {
        _aiOutcomeReport = null;
        _aiOutcomeStage = null;
        NotifySharedUploadFooter();
    }

    private void CaptureAiOutcome(AiAnalysisReport report)
    {
        _aiOutcomeReport = report;
        _aiOutcomeStage = report.TokenBudget.StoppedForBudget ? AiAnalysisStage.StoppedForBudget
            : report.UnfinishedCandidateCount > 0 || report.UnsubmittedCandidateCount > 0
                ? AiAnalysisStage.Failed : AiAnalysisStage.Completed;
        NotifySharedUploadFooter();
    }

    private static string RenderAiAnalysisOutcome(AiAnalysisReport report, bool includeDiagnostics)
    {
        var parts = new List<string>
        {
            L.F($"AI 成功 {report.SuccessfulAiCount:N0} 项，未完成 {report.UnfinishedCandidateCount:N0} 项，尚未提交 {report.UnsubmittedCandidateCount:N0} 项。"),
        };
        if (report.UnsubmittedCandidateCount > 0) parts.Add(L.T("未完成包含尚未提交的项目。"));
        if (report.TokenBudget.StoppedForBudget) parts.Add(RenderBudgetStopReason(report.TokenBudget));
        else if (report.UnfinishedCandidateCount > 0)
            parts.Add(L.T("部分项目未获得有效结果，可再次分析。"));
        if (report.RetryRequestCount > 0 || report.QueuedRetryCount > 0)
            parts.Add(L.F($"补充分析 {report.RetryRequestCount:N0} 次请求，涉及 {report.RetriedCandidateCount:N0} 项，已补回 {report.RecoveredCandidateCount:N0} 项，结束时待补充 {report.QueuedRetryCount:N0} 项，进行中 {report.ActiveRequestCount:N0} 个请求。"));
        if (!string.IsNullOrWhiteSpace(report.PersistenceWarning)) parts.Add(L.T(report.PersistenceWarning));
        if (includeDiagnostics && FormatResponseDiagnostics(report.TokenBudget.ResponseDiagnostics) is { Length: > 0 } diagnostics)
            parts.Add(diagnostics);
        return string.Join("\n", parts);
    }

    private static string RenderBudgetStopReason(AiTokenBudgetReport report) => report.NextRequestRequiredTokens > report.Limit
        ? L.F($"当前最大预算 {report.Limit:N0} Token 无法容纳下一请求所需的 {report.NextRequestRequiredTokens:N0} Token。请调高最大 Token 预算或缩小每批条目数后再继续。")
        : report.NextRequestRequiredTokens > 0
        ? L.F($"预算剩余 {report.RemainingTokens:N0} Token，下一请求至少需要 {report.NextRequestRequiredTokens:N0} Token，已停止后续请求。")
        : L.T("已达预算或剩余预算不足，已停止后续请求。已完成结果已保留。");

    private void NotifyAiProgressChanged()
    {
        foreach (var property in new[] { nameof(AiPhaseText), nameof(AiProgressDetailText),
            nameof(AiCandidateProgressValue), nameof(HasAiCandidateProgress), nameof(AiCandidateProgressText),
            nameof(AiElapsedText), nameof(AiActivityText), nameof(IsAiActivityAnimated), nameof(ScanStatusDetailText) })
            OnPropertyChanged(property);
    }

    private void NotifyAiUsageChanged()
    {
        OnPropertyChanged(nameof(HasAiUsageSummary));
        OnPropertyChanged(nameof(AiUsageSummaryText));
        OnPropertyChanged(nameof(AiUsageDetailText));
    }

    private void SetAiUsageFeedback(AiTokenBudgetReport report, AiProviderOptions provider)
    {
        _usageReport = report;
        _usageProvider = provider;
        var cost = AiUsageCostEstimator.Estimate(provider, report);
        _aiUsageSummaryText = report.RequestCount == 0
            ? L.T("本次无新请求 · 0 Token")
            : L.F($"本次用量 {report.AccountedTokens:N0} Token · {report.RequestCount:N0} 次请求");
        if (report.ResponseDiagnostics.HttpRequestCount > 0 && report.ResponseDiagnostics.HttpRequestCount != report.RequestCount)
            _aiUsageSummaryText += L.F($" · 实际 HTTP {report.ResponseDiagnostics.HttpRequestCount:N0} 次");
        if (cost is not null && report.RequestCount > 0)
            _aiUsageSummaryText += L.F($" · 约 ¥{cost.MinimumCny:0.0000}–{cost.MaximumCny:0.0000}");
        _aiUsageDetailText = report.HasCompleteUsageBreakdown
            ? L.F($"接口返回：输入 {report.ProviderInputTokens:N0}，输出 {report.ProviderOutputTokens:N0} Token。")
            : L.T("服务商未返回完整用量，显示的 Token 可能包含保守估算，不能据此核算扣费。");
        if (report.HasCompleteUsageBreakdown)
        {
            _aiUsageDetailText += report.ProviderInputTokensWithoutCacheBreakdown > 0
                ? L.F($"已知缓存命中 {report.ProviderCacheHitTokens:N0} Token；另有 {report.ProviderInputTokensWithoutCacheBreakdown:N0} 输入 Token 未返回缓存明细。")
                : L.F($"缓存命中输入 {report.ProviderCacheHitTokens:N0} Token。");
        }
        if (cost is not null)
            _aiUsageDetailText += L.F($"\n{L.T(cost.FormatStatusMessage())}\n单价核验于 {AiUsageCostEstimate.SourceDate:yyyy-MM-dd}，实际扣费以服务商账单为准。");
        else if (report.RequestCount > 0)
            _aiUsageDetailText += L.T("\n当前渠道或模型没有已核验的价格，请查看服务商账单。");
        _aiUsageDetailText += "\n" + L.F($"预算剩余 {report.RemainingTokens:N0} Token。");
        if (report.StoppedForBudget) _aiUsageDetailText += "\n" + RenderBudgetStopReason(report);
        var responseDiagnostics = FormatResponseDiagnostics(report.ResponseDiagnostics);
        if (responseDiagnostics.Length > 0) _aiUsageDetailText += "\n" + responseDiagnostics;
        NotifyAiUsageChanged();
    }

    private static string FormatResponseDiagnostics(AiResponseDiagnostics diagnostics)
    {
        var parts = new List<string>();
        if (diagnostics.RequestedItems > 0 || diagnostics.ReturnedItems > 0 || diagnostics.AcceptedItems > 0)
            parts.Add(L.F($"结构化响应累计（含补充分析）：请求 {diagnostics.RequestedItems:N0} 项，返回 {diagnostics.ReturnedItems:N0} 项，接纳 {diagnostics.AcceptedItems:N0} 项。"));
        foreach (var issue in diagnostics.Issues.Where(item => item.Count > 0 && Enum.IsDefined(item.Issue)))
            parts.Add(L.F($"{L.T(GetResponseIssueLabel(issue.Issue))}：{issue.Count:N0}"));
        if (diagnostics.Issues.Any(item => item.Count > 0 && Enum.IsDefined(item.Issue)))
            parts.Add(L.T("问题计数可能重叠，不代表剩余项目数。"));
        return parts.Count == 0 ? string.Empty : L.F($"响应诊断：{string.Join(L.T("；"), parts)}");
    }

    private static string GetResponseIssueLabel(AiResponseIssue issue) => issue switch
    {
        AiResponseIssue.FinishLength => "输出达到长度上限",
        AiResponseIssue.EmptyContent => "返回内容为空",
        AiResponseIssue.ContentFilter => "服务商内容过滤",
        AiResponseIssue.ModelRefusal => "模型拒绝回答",
        AiResponseIssue.MalformedJson => "JSON 格式不完整",
        AiResponseIssue.MissingItemsArray => "缺少结果数组",
        AiResponseIssue.NonObjectItem => "结果项格式不正确",
        AiResponseIssue.MissingPath => "结果缺少路径",
        AiResponseIssue.MissingPurposeField => "用途字段缺失",
        AiResponseIssue.EmptyPurposeField => "用途字段为空",
        AiResponseIssue.PurposeTextTooLong => "用途说明超长",
        AiResponseIssue.InvalidPurposeText => "用途说明含控制字符",
        AiResponseIssue.InvalidEnglishText => "英文用途字符不合规",
        AiResponseIssue.MissingResult => "缺少候选结果",
        AiResponseIssue.UnexpectedPath => "返回了未请求路径",
        AiResponseIssue.DuplicatePath => "返回了重复路径",
        AiResponseIssue.FormatFallback => "结构化格式兼容回退",
        _ => "其他响应问题",
    };
    private AiTokenBudgetReport? _usageReport;
    private AiProviderOptions? _usageProvider;
}

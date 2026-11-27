using System.ComponentModel;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Ai;
using DiskLens.Core.Networking;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private SharedUploadStatus _sharedUploadStatus = new(SharedUploadStage.Idle, 0);
    private bool CanPresentSharedUpload => !IsScanning && !IsScanQueued && !IsRefreshing
        && (!IsAnalyzing || _analysisProgress?.Stage == AiAnalysisStage.Uploading);
    private bool HasFooterSharedUpload => _sharedUploadStatus.Stage != SharedUploadStage.Idle
        && !HasFooterOfflineStatus;
    private bool HasFooterOfflineStatus => IsOfflineAlertVisible || StatusText == L.T(NetworkFailureClassifier.OfflineMessage);
    private bool HasFooterAiOutcome => !IsScanning && !IsScanQueued && !IsRefreshing && !IsAnalyzing
        && _aiOutcomeStage is not null;

    // Only explicit cloud matching uses the large progress panel. Uploads stay
    // in the existing footer text, including while other work is active.
    public bool IsFooterCloudActivityVisible => IsLoadingSharedExperience || _sharedLookupIncomplete;
    public bool IsFooterCloudActivityWarning => !IsLoadingSharedExperience && _sharedLookupIncomplete;
    public string FooterCloudActivityIcon => IsFooterCloudActivityWarning ? "!" : "↓";
    public string FooterCloudActivityHeadlineText => IsLoadingSharedExperience ? L.T("正在加载云端经验")
        : _sharedLookupStopped ? L.T("云端经验匹配已停止")
        : _sharedLookupIncomplete ? L.T("云端经验加载未完成")
        : L.T("正在加载云端经验");
    public string FooterCloudActivityDetailText => IsLoadingSharedExperience || _sharedLookupIncomplete
        ? (IsLoadingSharedExperience ? SharedLookupStageText + Environment.NewLine : string.Empty)
            + SharedLookupProgressDetailText
            + (_sharedLookupIncomplete && !IsLoadingSharedExperience
                ? Environment.NewLine + L.T("点击「重试匹配」重新匹配，已显示的结果会保留。") : string.Empty)
        : string.Empty;
    public double FooterCloudActivityProgressValue => SharedLookupProgressValue;
    public string FooterCloudActivityProgressText => _sharedLookupProgress.Total > 0
            ? _sharedLookupIncomplete || _sharedLookupProgress.Failed > 0
                ? L.F($"已处理 {Math.Floor(SharedLookupProgressValue):0}% · 尚未完整读取")
                : L.F($"加载进度 {Math.Floor(SharedLookupProgressValue):0}%")
            : L.T("加载进度：总量尚未确定");

    public string FooterStatusHeadlineText => HasFooterOfflineStatus ? ScanStatusHeadlineText
        : HasFooterAiOutcome ? _aiOutcomeStage switch
        {
            AiAnalysisStage.StoppedForBudget => L.T("AI 分析未完成（预算不足）"),
            AiAnalysisStage.Canceled => L.T("AI 分析已取消"),
            AiAnalysisStage.Failed => _aiOutcomeReport is not null ? L.T("AI 分析部分完成") : L.T("AI 分析未完成"),
            _ => L.T("AI 分析完成"),
        }
        : HasFooterSharedUpload && CanPresentSharedUpload ? _sharedUploadStatus.Stage switch
    {
        SharedUploadStage.Pending => L.T("云端经验待上传"),
        SharedUploadStage.Uploading => L.T("正在上传云端经验"),
        SharedUploadStage.Completed => L.T("云端经验上传完成"),
        SharedUploadStage.Failed => L.T("云端经验上传失败"),
        SharedUploadStage.Paused => L.T("云端经验上传已暂停"),
        _ => ScanStatusHeadlineText
    } : ScanStatusHeadlineText;

    public string FooterStatusDetailText
    {
        get
        {
            if (HasFooterOfflineStatus) return StatusText;
            var upload = HasFooterSharedUpload ? RenderSharedUploadNotice(_sharedUploadStatus) : string.Empty;
            var activity = HasFooterAiOutcome ? RenderFooterAiOutcome()
                : IsAnalyzing ? $"{AiPhaseText} · {AiCandidateProgressText} · {AiElapsedText} · {AiProgressDetailText}"
                : StatusText;
            if (upload.Length == 0) return activity;
            return !HasFooterAiOutcome && CanPresentSharedUpload ? upload : activity + " · " + upload;
        }
    }

    private string RenderFooterAiOutcome()
    {
        var outcome = _aiOutcomeReport is { } report ? RenderAiAnalysisOutcome(report, includeDiagnostics: false)
            : _aiOutcomeStage == AiAnalysisStage.Canceled
                ? L.T("分析已取消。已完成结果已保留，未完成项目可再次分析。")
                : _aiOutcomeStage == AiAnalysisStage.StoppedForBudget && _usageReport is { } budget
                    ? RenderBudgetStopReason(budget)
                    : L.T("分析未完成。已完成结果已保留，未完成项目可再次分析。");
        outcome = outcome.Replace('\n', ' ');
        return outcome;
    }

    private static string RenderSharedUploadNotice(SharedUploadStatus status) => status switch
    {
        { Stage: SharedUploadStage.Failed, AutomaticRetriesExhausted: true } => L.F($"云端经验上传已重试 3 次，仍有 {status.Pending} 项待上传，自动重试已停止。分析结果和待上传内容已保存在本机。"),
        { Stage: SharedUploadStage.Failed, NextRetryAt: not null } => L.F($"云端经验上传失败，待上传 {status.Pending} 项已存本机，等待后台重试（第 {Math.Clamp(status.AutomaticRetriesUsed + 1, 1, 3)}/3 次，间隔 1 分钟）。"),
        { Stage: SharedUploadStage.Uploading, AutomaticRetriesUsed: > 0 } => L.F($"正在后台重试上传云端经验（第 {status.AutomaticRetriesUsed}/3 次）：已确认 {status.Confirmed} / {status.Total} 项，剩余 {status.Pending} 项。"),
        { Stage: SharedUploadStage.Pending } => L.F($"云端经验待上传 {status.Pending} 项。待上传内容已存本机队列。"),
        { Stage: SharedUploadStage.Uploading } => L.F($"正在上传云端经验：已确认 {status.Confirmed} / {status.Total} 项，剩余 {status.Pending} 项。"),
        { Stage: SharedUploadStage.Completed } => L.F($"云端经验上传完成：服务器已接收 {status.Accepted} 项，跳过 {status.Skipped} 项。"),
        { Stage: SharedUploadStage.Failed } => string.IsNullOrWhiteSpace(status.Error)
            ? L.F($"云端经验上传未完成，待上传 {status.Pending} 项已保存在本机。")
            : L.T(status.Error) + (status.Pending > 0 ? L.F($" 待上传 {status.Pending} 项。") : string.Empty),
        { Stage: SharedUploadStage.Paused } => L.T("云端经验上传已暂停。已完成的分析结果仍可查看。")
            + (status.Pending > 0 ? L.F($" 待上传 {status.Pending} 项。") : string.Empty),
        _ => string.Empty
    };

    private void SharedUploadFooter_OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsScanning) && IsScanning)
            ClearAiOutcome();
        if (e.PropertyName is null or nameof(StatusText) or nameof(ScanStatusHeadlineText)
            or nameof(AiPhaseText) or nameof(AiProgressDetailText) or nameof(AiCandidateProgressText)
            or nameof(AiElapsedText) or nameof(IsOfflineAlertVisible) or nameof(IsScanQueued)
            or nameof(IsScanning) or nameof(IsAnalyzing) or nameof(IsRefreshing)
            or nameof(IsLoadingSharedExperience))
            NotifySharedUploadFooter();
    }

    private void NotifySharedUploadFooter()
    {
        OnPropertyChanged(nameof(FooterStatusHeadlineText));
        OnPropertyChanged(nameof(FooterStatusDetailText));
        OnPropertyChanged(nameof(IsFooterCloudActivityVisible));
        OnPropertyChanged(nameof(IsFooterCloudActivityWarning));
        OnPropertyChanged(nameof(FooterCloudActivityIcon));
        OnPropertyChanged(nameof(FooterCloudActivityHeadlineText));
        OnPropertyChanged(nameof(FooterCloudActivityDetailText));
        OnPropertyChanged(nameof(FooterCloudActivityProgressValue));
        OnPropertyChanged(nameof(FooterCloudActivityProgressText));
    }
}

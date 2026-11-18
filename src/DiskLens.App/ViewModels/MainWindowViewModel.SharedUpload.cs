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
    private bool HasFooterSharedUpload => CanPresentSharedUpload && _sharedUploadStatus.Stage != SharedUploadStage.Idle
        && !HasFooterOfflineStatus;
    private bool HasFooterOfflineStatus => IsOfflineAlertVisible || StatusText == L.T(NetworkFailureClassifier.OfflineMessage);
    private bool HasFooterAiOutcome => !IsScanning && !IsScanQueued && !IsRefreshing && !IsAnalyzing
        && _aiOutcomeStage is not null;

    // This reminder follows the shared durable queue, even when a different tab
    // is scanning/analyzing or the uploader is paused and waiting for a retry.
    public bool IsFooterSharedUploadReminderVisible => _sharedUploadStatus.Pending > 0;
    public string FooterSharedUploadReminderText => L.T("正在将您贡献的分析经验上传至云端共享经验库，请在文字消失前尽量不要关闭应用.......");

    // Cloud work gets its own notice so that scanning and AI outcome text cannot
    // hide an active download or a durable upload queue.
    public bool IsFooterCloudActivityVisible => IsLoadingSharedExperience || _sharedLookupIncomplete
        || IsFooterSharedUploadReminderVisible;
    public bool IsFooterCloudActivityWarning => !IsLoadingSharedExperience && (_sharedLookupIncomplete
        || IsFooterSharedUploadReminderVisible
            && _sharedUploadStatus.Stage is SharedUploadStage.Failed or SharedUploadStage.Paused);
    public string FooterCloudActivityIcon => IsFooterCloudActivityWarning ? "!"
        : IsLoadingSharedExperience ? "↓" : "↑";
    public string FooterCloudActivityHeadlineText => IsLoadingSharedExperience ? L.T("正在加载云端经验")
        : _sharedLookupStopped ? L.T("云端经验匹配已停止")
        : _sharedLookupIncomplete ? L.T("云端经验加载未完成")
        : IsFooterSharedUploadReminderVisible
        ? _sharedUploadStatus.Stage switch
        {
            SharedUploadStage.Failed => L.T("云端经验上传失败"),
            SharedUploadStage.Paused => L.T("云端经验上传已暂停"),
            SharedUploadStage.Pending => L.T("云端经验待上传"),
            _ => L.T("正在上传云端经验"),
        }
        : L.T("正在加载云端经验");
    public string FooterCloudActivityDetailText => IsLoadingSharedExperience || _sharedLookupIncomplete
        ? (IsLoadingSharedExperience ? SharedLookupStageText + Environment.NewLine : string.Empty)
            + SharedLookupProgressDetailText
            + (_sharedLookupIncomplete && !IsLoadingSharedExperience
                ? Environment.NewLine + L.T("点击「重试匹配」重新匹配，已显示的结果会保留。") : string.Empty)
            + (IsFooterSharedUploadReminderVisible ? Environment.NewLine + RenderSharedUploadNotice(_sharedUploadStatus) : string.Empty)
        : RenderSharedUploadNotice(_sharedUploadStatus);
    public double FooterCloudActivityProgressValue => IsLoadingSharedExperience || _sharedLookupIncomplete ? SharedLookupProgressValue
        : _sharedUploadStatus.Total > 0
            ? Math.Clamp(100d * _sharedUploadStatus.Confirmed / _sharedUploadStatus.Total, 0, 100)
            : 0;
    public string FooterCloudActivityProgressText => IsLoadingSharedExperience || _sharedLookupIncomplete
        ? _sharedLookupProgress.Total > 0
            ? _sharedLookupIncomplete || _sharedLookupProgress.Failed > 0
                ? L.F($"已处理 {Math.Floor(SharedLookupProgressValue):0}% · 尚未完整读取")
                : L.F($"加载进度 {Math.Floor(SharedLookupProgressValue):0}%")
            : L.T("加载进度：总量尚未确定")
        : _sharedUploadStatus.Total > 0
            ? L.F($"上传进度 {Math.Floor(FooterCloudActivityProgressValue):0}% · 已确认 {_sharedUploadStatus.Confirmed:N0} / {_sharedUploadStatus.Total:N0} 项")
            : L.T("上传进度：等待上传回执。");

    public string FooterStatusHeadlineText => HasFooterOfflineStatus ? ScanStatusHeadlineText
        : HasFooterAiOutcome ? _aiOutcomeStage switch
        {
            AiAnalysisStage.StoppedForBudget => L.T("AI 分析未完成（预算不足）"),
            AiAnalysisStage.Canceled => L.T("AI 分析已取消"),
            AiAnalysisStage.Failed => _aiOutcomeReport is not null ? L.T("AI 分析部分完成") : L.T("AI 分析未完成"),
            _ => L.T("AI 分析完成"),
        }
        : HasFooterSharedUpload ? _sharedUploadStatus.Stage switch
    {
        SharedUploadStage.Pending => L.T("云端经验待上传"),
        SharedUploadStage.Uploading => L.T("正在上传云端经验"),
        SharedUploadStage.Completed => L.T("云端经验上传完成"),
        SharedUploadStage.Failed => L.T("云端经验上传失败"),
        SharedUploadStage.Paused => L.T("云端经验上传已暂停"),
        _ => ScanStatusHeadlineText
    } : ScanStatusHeadlineText;

    public string FooterStatusDetailText => HasFooterOfflineStatus ? StatusText
        : HasFooterAiOutcome ? RenderFooterAiOutcome()
        : HasFooterSharedUpload ? RenderSharedUploadNotice(_sharedUploadStatus)
        : IsAnalyzing ? $"{AiPhaseText} · {AiCandidateProgressText} · {AiElapsedText} · {AiProgressDetailText}"
        : StatusText;

    private string RenderFooterAiOutcome()
    {
        var outcome = _aiOutcomeReport is { } report ? RenderAiAnalysisOutcome(report, includeDiagnostics: false)
            : _aiOutcomeStage == AiAnalysisStage.Canceled
                ? L.T("分析已取消。已完成结果已保留，未完成项目可再次分析。")
                : _aiOutcomeStage == AiAnalysisStage.StoppedForBudget && _usageReport is { } budget
                    ? RenderBudgetStopReason(budget)
                    : L.T("分析未完成。已完成结果已保留，未完成项目可再次分析。");
        outcome = outcome.Replace('\n', ' ');
        return HasFooterSharedUpload ? outcome + " · " + RenderSharedUploadNotice(_sharedUploadStatus) : outcome;
    }

    private static string RenderSharedUploadNotice(SharedUploadStatus status) => status.Stage switch
    {
        SharedUploadStage.Pending => L.F($"云端经验待上传 {status.Pending} 项。待上传内容已存本机队列。"),
        SharedUploadStage.Uploading => L.F($"正在上传云端经验：已确认 {status.Confirmed} / {status.Total} 项，剩余 {status.Pending} 项。"),
        SharedUploadStage.Completed => L.F($"云端经验上传完成：服务器已接收 {status.Accepted} 项，跳过 {status.Skipped} 项。"),
        SharedUploadStage.Failed => string.IsNullOrWhiteSpace(status.Error)
            ? L.F($"上传至云端经验库失败，待上传 {status.Pending} 项，将稍后重试。已完成的分析结果仍可查看。")
            : L.T(status.Error) + (status.Pending > 0 ? L.F($" 待上传 {status.Pending} 项。") : string.Empty),
        SharedUploadStage.Paused => L.T("云端经验上传已暂停。已完成的分析结果仍可查看。")
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
        OnPropertyChanged(nameof(IsFooterSharedUploadReminderVisible));
        OnPropertyChanged(nameof(FooterSharedUploadReminderText));
        OnPropertyChanged(nameof(IsFooterCloudActivityVisible));
        OnPropertyChanged(nameof(IsFooterCloudActivityWarning));
        OnPropertyChanged(nameof(FooterCloudActivityIcon));
        OnPropertyChanged(nameof(FooterCloudActivityHeadlineText));
        OnPropertyChanged(nameof(FooterCloudActivityDetailText));
        OnPropertyChanged(nameof(FooterCloudActivityProgressValue));
        OnPropertyChanged(nameof(FooterCloudActivityProgressText));
    }
}

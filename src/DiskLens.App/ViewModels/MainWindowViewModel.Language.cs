using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Func<string>? _formatAnalysisFeedback;
    private string _lastRenderedAnalysisFeedback = "";
    private ScanResult? _completedScanResult;
    private string _lastRenderedCompletedScanStatus = "";

    private void OnPresentationLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RunOnLanguageThread(RefreshLocalizedPresentation);

    private void RefreshLocalizedPresentation()
    {
        if (_isDisposed) return;
        RefreshCompletedScanFeedback();
        RefreshSharedSummaryLanguage();
        if (_usageReport is not null && _usageProvider is not null) SetAiUsageFeedback(_usageReport, _usageProvider);
        if (_formatAnalysisFeedback is not null)
        {
            var previous = _lastRenderedAnalysisFeedback;
            var updated = _formatAnalysisFeedback();
            if (_statusText == previous) _statusText = updated;
            if (_lastAnalysisSummaryText == previous) _lastAnalysisSummaryText = updated;
            _lastRenderedAnalysisFeedback = updated;
        }
        if (_scanReport is { } report) RebuildScanReadIssueRows(report);
        RebuildVisibleVisualExplorerItems(updateTreemapRoot: false);
        OnPropertyChanged(null);
    }

    private void CaptureCompletedScanFeedback(ScanResult result)
    {
        _completedScanResult = result;
        RefreshCompletedScanFeedback(forceStatus: true);
    }

    private void RefreshCompletedScanFeedback(bool forceStatus = false)
    {
        if (_completedScanResult is not { } result) return;
        var skipped = result.SkippedReparsePointCount;
        string status;
        string? headline;
        string? detail;
        if (result.IsPartial)
        {
            status = L.F($"部分扫描：共读取 {result.FilesScanned} 个文件 / {result.DirectoriesScanned} 个目录；{result.ScanIssueCount} 处路径遇到读取问题。{(skipped > 0 ? L.F($" 按扫描策略跳过了 {skipped} 个重解析点（如符号链接或挂载点）。") : string.Empty)}");
            headline = L.T("部分扫描");
            detail = L.F($"有 {result.ScanIssueCount} 处路径遇到读取问题，点击“扫描报告”查看路径与原因。无法据此确定未读内容的大小。按扫描策略跳过的重解析点：{skipped} 个。已扫描到的文件仍可正常查看和分析。");
        }
        else
        {
            var skippedSummary = skipped > 0 ? L.F($"（跳过 {skipped} 个重解析点）") : string.Empty;
            status = L.F($"扫描完成{skippedSummary}，共 {result.FilesScanned} 个文件 / {result.DirectoriesScanned} 个目录。");
            headline = skipped > 0 ? L.F($"扫描完成{skippedSummary}") : null;
            detail = skipped > 0 ? L.T("这些重解析点按当前扫描策略排除，未纳入文件树和 AI 候选。") : null;
        }
        if (result.EstimatedStorageItemCount > 0)
            status += " " + L.F($"有 {result.EstimatedStorageItemCount} 项无法完整读取分配量或文件身份，占用包含估算；详情中标出了对应条目。");
        if (forceStatus || _statusText == _lastRenderedCompletedScanStatus) StatusText = status;
        _lastRenderedCompletedScanStatus = status;
        SetScanTerminalStatus(headline, detail);
    }
    public string SelectedLanguage
    {
        get => L.Current.Language;
        set
        {
            var language = L.Normalize(value);
            if (language == L.Current.Language) return;
            L.SetLanguage(language);
            OnPropertyChanged(nameof(SelectedLanguage));
            SaveSettingsDeferred();
        }
    }
    public bool IsChineseLanguage
    {
        get => !L.IsEnglish;
        set { if (value) SelectedLanguage = "zh"; }
    }
    public bool IsEnglishLanguage
    {
        get => L.IsEnglish;
        set { if (value) SelectedLanguage = "en"; }
    }
}

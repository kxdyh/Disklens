using DiskLens.App.Models;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isAiSidebarSelected = true;
    private readonly Dictionary<string, AnalysisListItem> _analysisItemIndex = new(StringComparer.OrdinalIgnoreCase);

    public string SelectedAiStatusText
    {
        get
        {
            if (SelectedVisibleAnalysis is { } analysis && !analysis.LooksLikeFallback())
            {
                return analysis.Origin switch
                {
                    AiAnalysisResultOrigin.SharedExperience => L.T("匹配到的云端分析经验"),
                    AiAnalysisResultOrigin.AiAnalyzed => L.T("您的AI分析结论"),
                    AiAnalysisResultOrigin.CacheReused => L.T("本地保存的经验"),
                    _ => string.Empty,
                };
            }

            return HasSelectedSharedMissing ? L.T("未匹配到云端分析经验") : string.Empty;
        }
    }

    public bool HasSelectedAiAction => SelectedVisibleAnalysis is { } analysis && !analysis.LooksLikeFallback();

    public string SelectedAiActionText => !HasSelectedAiAction
        ? string.Empty
        : SelectedVisibleAnalysis!.SuggestedAction == SuggestedAction.RecycleCandidate ? L.T("可以考虑删") : L.T("不建议删");

    public bool SelectedAiActionIsConsiderDelete => HasSelectedAiAction
        && SelectedVisibleAnalysis!.SuggestedAction == SuggestedAction.RecycleCandidate;

    public bool HasSelectedAiReason => !string.IsNullOrWhiteSpace(SelectedAiReasonText);

    public bool IsSelectedBelowAnalysisThreshold => SelectedNode is { } node
        && VisibleAnalysisFor(node) is null && !string.Equals(node.Path, ScanRoot?.Path, StringComparison.OrdinalIgnoreCase)
        && node.SizeBytes < Math.Max(1, AnalysisThresholdMb) * 1024L * 1024L;

    public string SelectedAiReasonText
    {
        get
        {
            if (SelectedVisibleAnalysis is { } analysis)
                return analysis.LooksLikeFallback() ? L.T("分析未完成，请重试") : L.Reason(analysis);
            if (HasSelectedSharedMissing) return string.Empty;
            if (SelectedNode is null) return L.T("单击文件块查看用途与建议。");
            if (IsSelectedBelowAnalysisThreshold) return L.T("本项未达到分析阈值");
            if (IsAnalyzing) return L.T("正在分析");
            return L.T("尚无分析结论");
        }
    }

    private AnalysisListItem? FindSelectedAnalysisItem(FileSystemNode? node)
    {
        // A refreshed tree invalidates old conclusions before the result list is rebuilt.
        // Matching the snapshot instance also prevents deleted/replaced paths showing stale text.
        return node is not null && VisibleAnalysisFor(node) is not null
            && _analysisItemIndex.TryGetValue(node.Path, out var item)
            && ReferenceEquals(item.Node, node) ? item : null;
    }

    private void RebuildAnalysisSelectionIndex()
    {
        _analysisItemIndex.Clear();
        foreach (var item in AnalysisItems) _analysisItemIndex[item.Path] = item;
        var selected = FindSelectedAnalysisItem(SelectedNode);
        if (!ReferenceEquals(_selectedAnalysisItem, selected))
        {
            _selectedAnalysisItem = selected;
            OnPropertyChanged(nameof(SelectedAnalysisItem));
        }
        NotifySelectedAiCard();
    }

    private void NotifySelectedAiCard()
    {
        OnPropertyChanged(nameof(SelectedAiStatusText));
        OnPropertyChanged(nameof(HasSelectedAiAction));
        OnPropertyChanged(nameof(SelectedAiActionText));
        OnPropertyChanged(nameof(SelectedAiActionIsConsiderDelete));
        OnPropertyChanged(nameof(SelectedAiReasonText));
        OnPropertyChanged(nameof(HasSelectedAiReason));
        OnPropertyChanged(nameof(SelectedAiOriginText));
        OnPropertyChanged(nameof(SelectedAiOriginIcon));
        OnPropertyChanged(nameof(HasSelectedAiOriginBadge));
        OnPropertyChanged(nameof(SelectedAiConfidenceText));
        OnPropertyChanged(nameof(IsSelectedSharedExperience));
        OnPropertyChanged(nameof(SelectedSharedExplanation));
        OnPropertyChanged(nameof(HasSelectedSharedMissing));
        OnPropertyChanged(nameof(SelectedSharedMissingText));
        ReportSharedExperienceCommand?.RaiseCanExecuteChanged();
    }

    public bool IsAiSidebarSelected
    {
        get => _isAiSidebarSelected;
        set
        {
            if (SetProperty(ref _isAiSidebarSelected, value))
                OnPropertyChanged(nameof(IsDetailsSidebarSelected));
        }
    }

    public bool IsDetailsSidebarSelected
    {
        get => !_isAiSidebarSelected;
        set { if (value) IsAiSidebarSelected = false; }
    }
}

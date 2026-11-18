using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // A display preference only: toggling sharing must not destroy conclusions in the scan.
    // Query/application guards still validate expiry, snapshot and consent before accepting results.
    public bool ShowSharedExperienceAnalysis => _sharedConsent is { CanShare: true };

    private AiAnalysisResult? VisibleAnalysisFor(FileSystemNode? node)
        => node?.Analysis is { } analysis
            && (analysis.Origin != AiAnalysisResultOrigin.SharedExperience || ShowSharedExperienceAnalysis)
                ? analysis : null;

    private AiAnalysisResult? SelectedVisibleAnalysis => VisibleAnalysisFor(SelectedNode);

    private void NotifyAnalysisVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowSharedExperienceAnalysis));
        RefreshVisibleAnalysisItems();
        RebuildAnalysisSelectionIndex();
        if (IsAnalysisConclusionFilterActive) RebuildVisibleVisualExplorerItems();
        OnPropertyChanged(nameof(VisibleAnalysisItems));
        OnPropertyChanged(nameof(HasVisibleAnalysisItems));
        OnPropertyChanged(nameof(AiResultsEmptyText));
        OnPropertyChanged(nameof(SelectedRiskText));
        OnPropertyChanged(nameof(SelectedDecision));
        OnPropertyChanged(nameof(AnalysisDescriptionText));
    }
}

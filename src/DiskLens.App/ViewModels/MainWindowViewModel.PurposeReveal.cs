using DiskLens.App.Models;
using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public TreemapPurposeRevealBatch TreemapPurposeReveals { get; } = new();

    private void QueueNewAiPurposeReveals(IReadOnlyList<AiAnalysisResult> results)
    {
        foreach (var result in results)
        {
            if (result.Origin is not (AiAnalysisResultOrigin.AiAnalyzed or AiAnalysisResultOrigin.CacheReused)
                || result.LooksLikeFallback() || string.IsNullOrWhiteSpace(result.ShortPurpose)) continue;
            var previous = VisibleAnalysisFor(_scanNodeIndex.FindNode(result.Path));
            if (previous is not null && !previous.LooksLikeFallback()
                && !string.IsNullOrWhiteSpace(previous.ShortPurpose)) continue;
            TreemapPurposeReveals.Enqueue(result.Path, L.Purpose(result));
        }
        if (!IsViewportMotionEnabled) TreemapPurposeReveals.CompleteAll();
    }
}

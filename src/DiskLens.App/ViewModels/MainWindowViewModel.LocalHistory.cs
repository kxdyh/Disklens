namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private long _localHistoryRestoreRevision;

    private async Task RestoreLocalHistoryAfterScanAsync()
    {
        if (_isDisposed || ScanRoot is not { } snapshot || IsScanning || IsAnalyzing || IsRefreshing || IsReanalysisSelectionMode) return;
        var revision = Interlocked.Increment(ref _localHistoryRestoreRevision);
        var provider = CreateProviderOptions();
        var options = CreateScanOptions() with { RootPath = snapshot.Path, AnalysisMemoryRootPath = snapshot.Path };
        bool CanApply() => !_isDisposed && revision == Interlocked.Read(ref _localHistoryRestoreRevision)
            && ReferenceEquals(ScanRoot, snapshot) && !IsScanning && !IsAnalyzing && !IsRefreshing && !IsReanalysisSelectionMode
            && provider == CreateProviderOptions()
            && options.UseTokenSavingMode == IsAiTokenSavingModeEnabled
            && options.AnalysisThresholdBytes == Math.Max(1, AnalysisThresholdMb) * 1024L * 1024L;
        try
        {
            var restored = await Task.Run(() => _aiAnalysisService.LoadReusableLocalResultsAsync(snapshot, options, provider));
            if (!CanApply()) return;
            var applicable = restored.Where(result => _scanNodeIndex.FindNode(result.Path) is { } node
                && (node.Analysis is null || node.Analysis.LooksLikeFallback()
                    || node.Analysis.Origin == DiskLens.Core.Models.AiAnalysisResultOrigin.SharedExperience
                        && !IsUsableKnownCloudResult(node.Analysis))).ToArray();
            if (applicable.Length == 0) return;
            var mappedRoot = await Task.Run(() => ApplyAnalysisResults(snapshot, applicable));
            if (!CanApply()) return;
            if (!await ApplySnapshotAsync(mappedRoot, canApplySnapshot: CanApply)) return;
            RebuildAnalysisItems(mappedRoot);
        }
        catch (Exception)
        {
            // History is optional. A read failure must leave the fresh scan usable
            // and must never start AI analysis, write the cache, or renew consent.
        }
    }
}

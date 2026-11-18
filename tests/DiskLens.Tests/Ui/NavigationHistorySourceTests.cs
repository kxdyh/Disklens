using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class NavigationHistorySourceTests
{
    [Fact]
    public void ViewModel_should_track_navigation_history_to_restore_previous_aggregate_views()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("_navigationHistory", source, StringComparison.Ordinal);
        Assert.Contains("PushNavigationHistory", source, StringComparison.Ordinal);
        Assert.Contains("TryPopNavigationHistory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Zoom_out_should_follow_the_real_parent_when_history_is_stale()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);
        var zoomOutStart = source.IndexOf("private void ZoomOut()", StringComparison.Ordinal);
        var resetZoomStart = source.IndexOf("private void ResetZoom()", zoomOutStart, StringComparison.Ordinal);
        Assert.True(zoomOutStart >= 0 && resetZoomStart > zoomOutStart);

        var zoomOut = source[zoomOutStart..resetZoomStart];
        Assert.Contains("ResolveDisplayParentNode(CurrentRoot)", zoomOut, StringComparison.Ordinal);
        Assert.Contains("PathsMatch(historyParent.Path, parent.Path)", zoomOut, StringComparison.Ordinal);
        Assert.Contains("RebuildNavigationHistory(parent)", zoomOut, StringComparison.Ordinal);
    }
}

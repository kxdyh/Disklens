using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class AnalysisResultsScrollSourceTests
{
    [Fact]
    public void Analysis_results_list_should_have_a_stable_name_for_scroll_reset()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("x:Name=\"AnalysisResultsListBox\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Analysis_result_cards_should_bound_long_reason_text_without_hard_clipping()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("x:Name=\"AnalysisReasonScrollViewer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"112\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeight=\"36\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_details_panel_should_scroll_internally_for_long_text()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("x:Name=\"SelectedDetailsScrollViewer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ElasticTextBlockStyle", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Treemap_labels_should_allow_multiple_lines_when_region_has_room()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.Contains("layout.MaxLineCount", source, StringComparison.Ordinal);
        Assert.Contains("MaxLineCount = Math.Max(1, maxLineCount)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxLineCount = 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_reset_analysis_results_scroll_when_current_root_changes()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var codeBehindPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs");
        var source = File.ReadAllText(codeBehindPath);

        Assert.Contains("PropertyChanged", source, StringComparison.Ordinal);
        Assert.Contains("nameof(MainWindowViewModel.CurrentRoot)", source, StringComparison.Ordinal);
        Assert.Contains("ScrollAnalysisResultsToTop", source, StringComparison.Ordinal);
    }
}

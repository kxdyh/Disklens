using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class UiResponsivenessSourceTests
{
    [Fact]
    public void ViewModel_should_not_recount_selected_directory_files_on_the_ui_thread()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("SelectedNode.EnumerateFiles().Count()", source, StringComparison.Ordinal);
        Assert.Contains("DescendantFileCount", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_not_resolve_aggregate_nodes_by_repeated_full_tree_scans()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("FindNodeByPath(scanRoot, child.Path)", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemNodeIndex", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_prepare_layout_off_the_ui_thread()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.DoesNotContain("var plan = _displayPlanner.Plan(", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.InvokeAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_keep_previous_layout_visible_while_background_layout_is_pending()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.DoesNotContain("StopAnimation();\r\n        _hoveredNode = null;\r\n        _rectangles = [];", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_build_risk_summaries_from_the_display_root_instead_of_the_full_scan_subtree()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.Matches(@"BuildRiskSummaries\(\s*displayPlan\.DisplayRoot\s*[,)]", source);
        Assert.DoesNotMatch(@"BuildRiskSummaries\(\s*request\.RootNode\b", source);
    }

    [Fact]
    public void Modal_overlays_should_release_input_immediately_and_keep_close_transition_visuals_alive()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));
        var visibilityBindings = new[]
        {
            "Visibility=\"{Binding IsSettingsOpen",
            "Visibility=\"{Binding IsHelpOpen",
            "Visibility=\"{Binding IsScanLocationPickerOpen",
            "Visibility=\"{Binding IsExplorerGuideOpen",
        };

        foreach (var binding in visibilityBindings)
        {
            Assert.DoesNotContain(binding, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("SetOverlayOpenState", codeBehind, StringComparison.Ordinal);
        Assert.Contains("overlay.IsHitTestVisible = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OverlayExitDuration", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_not_build_full_scan_index_inside_scan_root_setter()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("_scanNodeIndex = FileSystemNodeIndex.Build(value);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_and_analysis_flows_should_prepare_scan_index_off_the_ui_thread_before_applying_snapshots()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("ApplySnapshotAsync", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => FileSystemNodeIndex.Build(root))", source, StringComparison.Ordinal);
        Assert.Contains("await ApplySnapshotAsync(", source, StringComparison.Ordinal);
        Assert.Contains("result.Root,", source, StringComparison.Ordinal);
        Assert.Contains("await ApplySnapshotAsync(mappedRoot,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Analysis_flow_should_not_map_full_scan_tree_on_the_ui_thread()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("var mappedRoot = ApplyAnalysisResults(ScanRoot, results);", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", source, StringComparison.Ordinal);
        Assert.Contains("ApplyAnalysisResults(scanSnapshot, results)", source, StringComparison.Ordinal);
        Assert.Contains("RebuildAnalysisItems(mappedRoot)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_flow_should_prepare_updated_tree_and_index_off_the_ui_thread()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("ApplyLocalRemovalAsync", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() =>", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemTreeEditor.TryRemovePath", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemNodeIndex.Build(updatedRoot)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySnapshot(updatedRoot, nextCurrentPath, nextSelectedPath);", source, StringComparison.Ordinal);
    }
}

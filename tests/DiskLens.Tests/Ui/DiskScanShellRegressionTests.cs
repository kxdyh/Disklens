using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class DiskScanShellRegressionTests
{
    [Fact]
    public void MainWindow_should_render_disk_scan_workspace_instead_of_chat_shell()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("AI 辅助分析", xaml, StringComparison.Ordinal);
        Assert.Contains("选中项详情", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"设置\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"新扫描\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ScanCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScanStatusHeadlineText", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowCancelAction", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowAnalyzeAction", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:TreemapControl", xaml, StringComparison.Ordinal);
        Assert.Contains("RootNode=\"{Binding TreemapDisplayRoot}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"开始扫描\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AI Analysis Results", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Selection Details", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Start Scan", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_render_ai_analysis_results_list_instead_of_empty_placeholder()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("ItemsSource=\"{Binding VisibleAnalysisItems}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAnalysisItem, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenAnalysisItemLocationCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RecycleAnalysisItemCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<WrapPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Open Location\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Move to Recycle Bin\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AppStartup_should_wire_disk_scan_services()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var startupPath = Path.Combine(repoRoot, "src", "DiskLens.App", "App.xaml.cs");
        var startupCode = LocalizedXamlSource.ReadAllText(startupPath);

        Assert.Contains("DirectoryScanner", startupCode, StringComparison.Ordinal);
        Assert.Contains("DeletionPolicyService", startupCode, StringComparison.Ordinal);
        Assert.Contains("AiAnalysisService", startupCode, StringComparison.Ordinal);
        Assert.Contains("AnalysisMemoryStore", startupCode, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(10)", startupCode, StringComparison.Ordinal);
        Assert.Contains("ExplorerFileLocationService", startupCode, StringComparison.Ordinal);
        Assert.Contains("RecycleBinService", startupCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_fit_the_desktop_work_area_on_startup()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var codeBehindPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);
        var codeBehind = LocalizedXamlSource.ReadAllText(codeBehindPath);

        Assert.Contains("WindowStartupLocation=\"CenterScreen\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.WorkArea", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Width = Math.Clamp(Width,", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Height = Math.Clamp(Height,", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*(?:this\.)?Max(?:Width|Height)\s*=", codeBehind);
    }

    [Fact]
    public void Recycle_flow_should_not_trigger_full_rescan_after_success()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.DoesNotContain("await StartScanAsync().ConfigureAwait(true);", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemTreeEditor.TryRemovePath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_flow_should_keep_last_successful_visualization_until_new_scan_completes()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.DoesNotContain("ScanRoot = null;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentRoot = null;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedNode = null;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySnapshot(snapshot.Root);", source, StringComparison.Ordinal);
        Assert.Contains("await ApplySnapshotAsync(", source, StringComparison.Ordinal);
        Assert.Contains("result.Root,", source, StringComparison.Ordinal);
        Assert.Contains("result.Root.Path,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_flow_should_not_auto_trigger_ai_analysis_after_scan_completes()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        var scanStart = source.IndexOf("private Task StartScanAsync()", StringComparison.Ordinal);
        var analysisStart = source.IndexOf("private async Task AnalyzeAsync()", scanStart, StringComparison.Ordinal);
        Assert.True(scanStart >= 0 && analysisStart > scanStart);
        Assert.DoesNotContain("AnalyzeAsync", source[scanStart..analysisStart], StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_flow_should_not_auto_start_ai_analysis_after_scan_completion()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        var scanStart = source.IndexOf("private Task StartScanAsync()", StringComparison.Ordinal);
        var analysisStart = source.IndexOf("private async Task AnalyzeAsync()", scanStart, StringComparison.Ordinal);
        Assert.True(scanStart >= 0 && analysisStart > scanStart);
        Assert.DoesNotContain("AnalyzeAsync", source[scanStart..analysisStart], StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_flow_should_target_full_scan_root_for_total_disk_analysis()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.DoesNotContain("CurrentRoot ?? ScanRoot", source, StringComparison.Ordinal);
        Assert.Contains("_aiAnalysisService.AnalyzeWithReportAsync(", source, StringComparison.Ordinal);
        Assert.Contains("ScanRoot,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Analysis_results_list_should_not_be_limited_to_files_only()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.DoesNotContain("foreach (var node in root\r\n                     .EnumerateFiles()", source, StringComparison.Ordinal);
        Assert.Contains(".DescendantsAndSelf()", source, StringComparison.Ordinal);
        Assert.Contains(".Where(item => item.Analysis is not null)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_overlay_should_not_use_transparent_button_as_fullscreen_backdrop()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.DoesNotContain("<Button Background=\"Transparent\" BorderThickness=\"0\" Command=\"{Binding CloseSettingsCommand}\" />", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Buttons_should_keep_surface_steady_on_hover_and_use_pressed_state_feedback()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        var buttonStyleStart = xaml.IndexOf("<Style x:Key=\"HandDrawnButtonBaseStyle\"", StringComparison.Ordinal);
        var buttonStyleEnd = xaml.IndexOf("</Style>", buttonStyleStart, StringComparison.Ordinal);
        Assert.True(buttonStyleStart >= 0 && buttonStyleEnd > buttonStyleStart);
        var buttonStyle = xaml[buttonStyleStart..buttonStyleEnd];
        Assert.DoesNotContain("Property=\"IsMouseOver\"", buttonStyle, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsPressed\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomCursor", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_display_nodes_should_be_guarded_from_open_and_recycle_actions()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.Contains("DisplayNodeMetadata.AggregateRiskTag", source, StringComparison.Ordinal);
        Assert.Contains("private static bool IsAggregateNode", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Drilldown_should_resolve_display_nodes_back_to_scan_tree_nodes_before_navigation()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.Contains("ResolveDisplayNodeToScanNode", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAggregateDisplayNode", source, StringComparison.Ordinal);
        Assert.Contains("var resolvedNode = ResolveDisplayNodeToScanNode(node);", source, StringComparison.Ordinal);
        Assert.Contains("SelectedNode = resolvedNode;", source, StringComparison.Ordinal);
        Assert.Contains("_expandedVisualPath = node.Path;", source, StringComparison.Ordinal);
        Assert.Contains("RebuildVisualExplorerItems();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_should_resolve_non_aggregate_display_nodes_back_to_scan_tree_nodes()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.Contains("var resolvedValue = ResolveDisplayNodeToScanNode(value) ?? value;", source, StringComparison.Ordinal);
        // The resolved snapshot instance must be retained even when record values are equal.
        // Repeated-cache selection is exercised by the WPF behavior suite.
        Assert.Contains("_selectedNode = resolvedValue;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_drilldown_should_keep_analysis_filtering_and_allow_zoom_out()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        var navigationRootStart = source.IndexOf("var hasNavigationRoot =", StringComparison.Ordinal);
        Assert.True(navigationRootStart >= 0);
        var navigationRootEnd = source.IndexOf("return displayRoot with", navigationRootStart, StringComparison.Ordinal);
        Assert.True(navigationRootStart >= 0 && navigationRootEnd > navigationRootStart);
        var navigationRootBlock = source[navigationRootStart..navigationRootEnd];
        Assert.Contains("if (hasNavigationRoot)", navigationRootBlock, StringComparison.Ordinal);
        Assert.Contains("VisualExplorerPresentation.Apply(", navigationRootBlock, StringComparison.Ordinal);
        Assert.Contains("DisplayFilterMode", navigationRootBlock, StringComparison.Ordinal);

        var collapseTargetStart = source.IndexOf("private string? GetCollapseTargetPath", StringComparison.Ordinal);
        Assert.True(collapseTargetStart >= 0);
        var collapseTargetEnd = source.IndexOf("private string? NormalizeExpandedVisualPath", collapseTargetStart, StringComparison.Ordinal);
        Assert.True(collapseTargetStart >= 0 && collapseTargetEnd > collapseTargetStart);
        Assert.Contains("TryGetAggregateParentPath(path)", source[collapseTargetStart..collapseTargetEnd], StringComparison.Ordinal);

        var zoomOutStart = source.IndexOf("private void ZoomOut()", StringComparison.Ordinal);
        Assert.True(zoomOutStart >= 0);
        var zoomOutEnd = source.IndexOf("private void ResetZoom()", zoomOutStart, StringComparison.Ordinal);
        Assert.True(zoomOutStart >= 0 && zoomOutEnd > zoomOutStart);
        var zoomOutBlock = source[zoomOutStart..zoomOutEnd];
        Assert.Contains("TryPopNavigationHistory", zoomOutBlock, StringComparison.Ordinal);
        Assert.Contains("ResolveDisplayParentNode", zoomOutBlock, StringComparison.Ordinal);
        Assert.Contains("RebuildVisualExplorerItems();", zoomOutBlock, StringComparison.Ordinal);
    }
}

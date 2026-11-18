using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class VisualExplorerMarkupTests
{
    [Fact]
    public void MainWindow_should_bind_the_treemap_instead_of_single_page_visual_explorer_items()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("controls:TreemapControl", xaml, StringComparison.Ordinal);
        Assert.Contains("RootNode=\"{Binding TreemapDisplayRoot}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedNode=\"{Binding SelectedNode, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ItemsControl ItemsSource=\"{Binding VisibleVisualExplorerItems}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_render_the_03_treemap_and_bind_live_navigation_and_filters()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));

        Assert.Contains("controls:TreemapControl", xaml, StringComparison.Ordinal);
        Assert.Contains("RootNode=\"{Binding TreemapDisplayRoot}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedNode=\"{Binding SelectedNode, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ZoomIntoCommand=\"{Binding ZoomIntoNodeCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TileUserSpaceBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ItemsControl ItemsSource=\"{Binding VisibleVisualExplorerItems}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_project_current_filter_and_sort_into_the_treemap_source()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("public FileSystemNode? TreemapDisplayRoot", source, StringComparison.Ordinal);
        Assert.Contains("BuildTreemapDisplayRoot", source, StringComparison.Ordinal);
        Assert.Contains("TreemapDisplayRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_volume_usage_binding_should_not_write_back_during_window_startup()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));

        Assert.Contains("Value=\"{Binding VolumeUsageValue, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_define_visual_explorer_state_and_toggle_logic()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.Contains("VisualExplorerBuilder", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<VisualExplorerItem> _visualExplorerItems", source, StringComparison.Ordinal);
        Assert.Contains("VisualFolderDisplayThresholdMb", source, StringComparison.Ordinal);
        Assert.Contains("RebuildVisualExplorerItems", source, StringComparison.Ordinal);
        Assert.Contains("ToggleVisualItem", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_keep_navigation_commands_bound_to_treemap()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("ZoomIntoCommand=\"{Binding ZoomIntoNodeCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ZoomOutCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ResetZoomCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Visual_explorer_nested_cards_should_stretch_inside_expanded_parent()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("<Setter Property=\"Width\" Value=\"Auto\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<Condition Binding=\"{Binding Depth}\" Value=\"0\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<StackPanel />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<DataTrigger Binding=\"{Binding Weight}\" Value=\"{x:Static visual:VisualExplorerItemWeight.Hero}\">", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Visual_tiles_should_expose_full_name_size_and_path_in_theme_aware_tooltip()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("<Border.ToolTip>", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{loc:Bind DisplayNode, NodeName=True}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SizeText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TooltipPathText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{loc:Bind SubtitleText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource CardBackgroundBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource PrimaryTextBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"12\"", xaml, StringComparison.Ordinal);

        var templateStart = xaml.IndexOf("<DataTemplate x:Key=\"VisualExplorerTileTemplate\"", StringComparison.Ordinal);
        var templateEnd = xaml.IndexOf("</DataTemplate>", templateStart, StringComparison.Ordinal);
        var template = xaml[templateStart..templateEnd];
        var tooltipStart = template.IndexOf("<Border.ToolTip>", StringComparison.Ordinal);
        var showDelayStart = template.IndexOf("ToolTipService.InitialShowDelay=\"450\"", StringComparison.Ordinal);
        var showDurationStart = template.IndexOf("ToolTipService.ShowDuration=\"30000\"", StringComparison.Ordinal);
        Assert.InRange(showDelayStart, 0, tooltipStart - 1);
        Assert.InRange(showDurationStart, 0, tooltipStart - 1);
    }

    [Fact]
    public void Clicking_visual_tiles_selects_them_and_directory_navigation_requires_double_click()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var codeBehindPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs");
        var source = LocalizedXamlSource.ReadAllText(codeBehindPath);

        Assert.Contains("viewModel.SelectedNode = item.DisplayNode;", source, StringComparison.Ordinal);
        Assert.Contains("var isDirectoryDoubleClick", source, StringComparison.Ordinal);
        Assert.Contains("&& e.ClickCount == 2;", source, StringComparison.Ordinal);
        Assert.Contains("var isPagingClick", source, StringComparison.Ordinal);
        Assert.Contains("viewModel.ZoomIntoNodeCommand.Execute(item.DisplayNode);", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }
}

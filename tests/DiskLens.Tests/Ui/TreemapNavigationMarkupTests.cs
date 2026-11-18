using System.IO;
using System.Xml.Linq;

namespace DiskLens.Tests.Ui;

public sealed class TreemapNavigationMarkupTests
{
    [Fact]
    public void Workbench_should_give_treemap_priority_while_preserving_a_responsive_details_column()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));

        Assert.Contains("new GridLength(hasWorkspace ? (width < 980 ? 300 : 340) : 0)", source, StringComparison.Ordinal);
        Assert.Contains("new GridLength(1, GridUnitType.Star)", source, StringComparison.Ordinal);
        Assert.Contains("var width = widthOverride ?? WorkbenchGrid.ActualWidth;", source, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"2.05*\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"1*\" />", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_expose_navigation_cues_for_treemap()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("controls:TreemapControl", xaml, StringComparison.Ordinal);
        Assert.Contains("RootNode=\"{Binding TreemapDisplayRoot}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ZoomIntoCommand=\"{Binding ZoomIntoNodeCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ZoomOutCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ResetZoomCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_mode_toggle_should_have_a_toolbar_slot_and_use_the_root_only_while_focused()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var toggle = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "FocusModeToggleButton");
        var host = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "WorkbenchFocusToggleHost");
        var codeBehind = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));

        Assert.Equal("RootLayout", (string?)toggle.Parent?.Attribute(x + "Name"));
        Assert.Null(toggle.Attribute("Grid.Column"));
        Assert.Null(toggle.Attribute("Grid.ColumnSpan"));
        Assert.Equal("Center", (string?)toggle.Attribute("HorizontalAlignment"));
        Assert.Contains(host.Ancestors(), element => (string?)element.Attribute(x + "Name") == "ExplorerNavigationBar");
        Assert.Equal("40", (string?)host.Attribute("Width"));
        Assert.Contains("var host = isFocusMode ? RootLayout : WorkbenchFocusToggleHost;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("previousHost.Children.Remove(FocusModeToggleButton);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("host.Children.Add(FocusModeToggleButton);", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationColumn", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_mode_toggle_should_use_toolbar_layout_instead_of_a_fixed_workbench_offset()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));
        var buttonStart = xaml.IndexOf("x:Name=\"FocusModeToggleButton\"", StringComparison.Ordinal);
        var buttonEnd = xaml.IndexOf("</Button>", buttonStart, StringComparison.Ordinal);
        var button = xaml[buttonStart..buttonEnd];

        Assert.Contains("VerticalAlignment=\"Top\"", button, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusToggleWorkbenchTopMargin", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FocusToggleTopMargin = 8", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ThicknessAnimation", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FocusModeToggleButton.BeginAnimation(MarginProperty, animation);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_fullscreenController.SetFullscreen(isFocusMode);", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_mode_toggle_should_allow_live_scans_and_hide_the_scan_sidebar_while_focused()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModel = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("public bool IsFocusModeToggleVisible =>", viewModel, StringComparison.Ordinal);
        Assert.Contains("(HasScanResult || IsScanning || IsFocusMode)", viewModel, StringComparison.Ordinal);
        Assert.Contains("public bool ShowScanProgressOverlay => IsScanning && !IsFocusMode;", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (IsFocusModeToggleInteractive)", viewModel, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsFocusModeToggleVisible, Converter={StaticResource BoolToVisibilityConverter}}\"", File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml")), StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowScanProgressOverlay, Converter={StaticResource BoolToVisibilityConverter}}\"", File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml")), StringComparison.Ordinal);
        Assert.Contains("if (SetProperty(ref _isAnalyzing, value))\n            {\n                OnPropertyChanged(nameof(IsFocusModeToggleInteractive));", viewModel.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_tiles_select_on_single_click_enter_on_double_click_and_return_button_tracks_drilldown_state()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var control = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs"));
        var viewModel = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("Key=\"Escape\" Command=\"{Binding ZoomOutCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Back\" Command=\"{Binding ZoomOutCommand}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ReturnToParentButton\"", xaml, StringComparison.Ordinal);

        var selectionUpdate = control.IndexOf("SelectNode(hitNode);", StringComparison.Ordinal);
        var directoryClick = control.IndexOf("DisplayNodeMetadata.IsOverflow(hitNode) ? e.ClickCount == 1 : e.ClickCount == 2", StringComparison.Ordinal);
        Assert.True(selectionUpdate >= 0 && directoryClick > selectionUpdate);
        Assert.Contains("SetCurrentValue(SelectedNodeProperty, hitNode);", control, StringComparison.Ordinal);
        Assert.Contains("_expandedVisualPath = node.Path;", viewModel, StringComparison.Ordinal);

        var backAction = viewModel.IndexOf("var returnedFromNode = CurrentRoot;", StringComparison.Ordinal);
        Assert.True(backAction >= 0);
        Assert.Contains("SelectedNode = returnedFromNode;", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Treemap_hover_should_keep_theme_fill_and_click_feedback_should_be_temporary()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs"));

        Assert.DoesNotContain("HoverFillBrush", source, StringComparison.Ordinal);
        Assert.Contains("ClickFeedbackDurationMilliseconds = 320", source, StringComparison.Ordinal);
        Assert.Contains("private void StartClickFeedback(FileSystemNode node)", source, StringComparison.Ordinal);
        Assert.Contains("StopRenderingSubscriptionIfIdle();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_use_the_system_cursor_without_a_cursor_overlay()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));

        Assert.DoesNotContain("CustomCursorOverlay", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomCursor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.OverrideCursor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Cursors.None", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_not_track_or_animate_a_custom_cursor()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("Window_OnPreviewMouseMove", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Window_OnActivated", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Window_OnDeactivated", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RootLayout_OnMouseLeave", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateCustomCursor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimateCursorRingStroke", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_expose_keyboard_shortcuts_for_visual_branch_collapse()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("Key=\"Escape\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Back\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Home\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Modifiers=\"Control\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_describe_adaptive_display_without_legacy_threshold_editor()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("Text=\"{Binding VisualFolderDisplayThresholdMb", xaml, StringComparison.Ordinal);
        Assert.Contains("VisualFolderDisplayThresholdSummaryText", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenSettingsCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RootNode=\"{Binding TreemapDisplayRoot}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_enable_layout_rounding_and_text_rendering_for_crisper_ui()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("UseLayoutRounding=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SnapsToDevicePixels=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextOptions.TextRenderingMode=\"Auto\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_show_scan_waiting_overlay_with_jump_game()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("controls:ScanRunnerControl", xaml, StringComparison.Ordinal);
        Assert.Contains("IsScanning, Converter={StaticResource BoolToVisibilityConverter}", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowScanProgressOverlay", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("实时扫描 · 文件空间映射", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_should_stabilize_waiting_overlay_text_regions_with_fixed_slots()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var scanPanel = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ScanExperienceOverlay");
        var currentPath = scanPanel.Descendants().Single(element => (string?)element.Attribute("Text") == "{Binding ScanCurrentPathText}");
        Assert.Equal("22", (string?)currentPath.Attribute("Height"));
        Assert.Equal("CharacterEllipsis", (string?)currentPath.Attribute("TextTrimming"));
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
    }
}

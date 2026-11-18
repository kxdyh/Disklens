using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class SettingsPersistenceSourceTests
{
    [Fact]
    public void AppSettings_should_include_current_visual_and_ai_defaults_without_legacy_treemap_settings()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var settingsPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Models", "AppSettings.cs");
        var source = LocalizedXamlSource.ReadAllText(settingsPath);

        Assert.Contains("long VisualFolderDisplayThresholdMb", source, StringComparison.Ordinal);
        Assert.Contains("VisualFolderDisplayThresholdMb: 512", source, StringComparison.Ordinal);
        Assert.Contains("ProviderKind: AiProviderKind.Disabled", source, StringComparison.Ordinal);
        Assert.Contains("AiEndpoint: string.Empty", source, StringComparison.Ordinal);
        Assert.Contains("AiModel: string.Empty", source, StringComparison.Ordinal);
        Assert.Contains("int MaxAiBatchSize = 20", source, StringComparison.Ordinal);
        Assert.Contains("bool IsAiTokenSavingModeEnabled = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxVisibleChildrenPerRegion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TreemapCompactionLevel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_should_persist_first_run_guide_and_reduce_motion_preferences()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "Models", "AppSettings.cs"));
        var viewModel = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("HasCompletedExplorerGuide", source, StringComparison.Ordinal);
        Assert.Contains("IsReduceMotionEnabled", source, StringComparison.Ordinal);
        Assert.Contains("settings.HasCompletedExplorerGuide", viewModel, StringComparison.Ordinal);
        Assert.Contains("settings.IsReduceMotionEnabled", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsReduceMotionEnabled", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_should_show_first_run_guide_and_keep_a_help_entry_point()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var viewModel = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("Command=\"{Binding HelpCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HelpCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("HasCompletedExplorerGuide", viewModel, StringComparison.Ordinal);
        Assert.Contains("FileSystemGuideButton", xaml, StringComparison.Ordinal);
        Assert.Contains("减少动态效果", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_manage_settings_panel_state_without_legacy_treemap_settings()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.Contains("bool _isSettingsOpen", source, StringComparison.Ordinal);
        Assert.Contains("OpenSettingsCommand", source, StringComparison.Ordinal);
        Assert.Contains("CloseSettingsCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_treemapMaxDepth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxVisibleChildrenPerRegion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TreemapCompactionLevel", source, StringComparison.Ordinal);
        Assert.Contains("IsOverviewMode", source, StringComparison.Ordinal);
        Assert.Contains("ShowCancelAction", source, StringComparison.Ordinal);
        Assert.Contains("ShowAnalyzeAction", source, StringComparison.Ordinal);
        Assert.Contains("VolumeSpaceReader.Read(path)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Min(sizeBytes, _rootVolumeUsedBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Min(discoveredBytes, _rootVolumeUsedBytes", source, StringComparison.Ordinal);
        Assert.Contains("IsAiProviderConfigured", source, StringComparison.Ordinal);
        Assert.Contains("VisibleAnalysisItems", source, StringComparison.Ordinal);
        Assert.Contains("VisualExplorerItems", source, StringComparison.Ordinal);
        Assert.Contains("VisualFolderDisplayThresholdMb", source, StringComparison.Ordinal);
        Assert.Contains("RebuildVisualExplorerItems", source, StringComparison.Ordinal);
        Assert.Contains("AiResultsEmptyText", source, StringComparison.Ordinal);
        Assert.Contains("_lastAnalyzedRootPath", source, StringComparison.Ordinal);
        Assert.Contains("_lastAnalysisSummaryText", source, StringComparison.Ordinal);
        Assert.Contains("AppSettings.NormalizeAiBatchSize(settings.MaxAiBatchSize)", source, StringComparison.Ordinal);
        Assert.Contains("settings.VisualFolderDisplayThresholdMb <= 0 ? 512 : settings.VisualFolderDisplayThresholdMb", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(value, 1, AiBatchPlanner.MaximumBatchSize)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(MaxAiBatchSize, 1, AiBatchPlanner.MaximumBatchSize)", source, StringComparison.Ordinal);
        Assert.Contains("_isAiTokenSavingModeEnabled", source, StringComparison.Ordinal);
        Assert.Contains("IsAiTokenSavingModeEnabled", source, StringComparison.Ordinal);
        Assert.Contains("OpenAnalysisItemLocationCommand", source, StringComparison.Ordinal);
        Assert.Contains("RecycleAnalysisItemCommand", source, StringComparison.Ordinal);
        Assert.Contains("AiProviderKind.OpenAiCompatible =>", source, StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(ApiKey)", source, StringComparison.Ordinal);
        Assert.Contains("UseTokenSavingMode: IsAiTokenSavingModeEnabled", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_panel_should_not_render_legacy_treemap_controls()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.DoesNotContain("最大可见层级", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("每个区域最多显示子项", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("显示压缩强度", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_panel_should_render_ai_token_saving_mode_toggle()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("节省模式", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsAiTokenSavingModeEnabled, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("节省模式：仅发送路径和大小", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_save_failures_should_surface_visible_feedback()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);
        var xaml = LocalizedXamlSource.ReadAllText(xamlPath);

        Assert.Contains("SettingsSaveErrorText", source, StringComparison.Ordinal);
        Assert.Contains("HasSettingsSaveError", source, StringComparison.Ordinal);
        Assert.Contains("设置保存失败", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSaveErrorText", xaml, StringComparison.Ordinal);
        Assert.Contains("HasSettingsSaveError", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_surface_persistent_feedback_when_full_scan_analysis_has_no_candidates()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = LocalizedXamlSource.ReadAllText(viewModelPath);

        Assert.Contains("CurrentRootMatchesLastAnalyzedPath()", source, StringComparison.Ordinal);
        var noCandidatesSummaryStart = source.IndexOf("var noCandidates = results.Count == 0 && !wasCanceled;", StringComparison.Ordinal);
        Assert.True(noCandidatesSummaryStart >= 0);
        var noCandidatesSummaryEnd = source.IndexOf("SetAnalysisFeedback(analysisRoot.Path, summary);", noCandidatesSummaryStart, StringComparison.Ordinal);
        Assert.True(noCandidatesSummaryStart >= 0 && noCandidatesSummaryEnd > noCandidatesSummaryStart);
        var noCandidatesSummaryBlock = source[noCandidatesSummaryStart..noCandidatesSummaryEnd];
        Assert.Contains("var analyzedThreshold = AnalysisThresholdMb;", noCandidatesSummaryBlock, StringComparison.Ordinal);
        Assert.Contains("_formatAnalysisFeedback = () =>", noCandidatesSummaryBlock, StringComparison.Ordinal);
        Assert.Contains("noCandidates ? L.F($\"本次扫描分析完成，没有达到 {analyzedThreshold} MB 阈值的项目。\")",
            noCandidatesSummaryBlock, StringComparison.Ordinal);
        Assert.Contains("var summary = _lastRenderedAnalysisFeedback = _formatAnalysisFeedback();",
            noCandidatesSummaryBlock, StringComparison.Ordinal);
        Assert.Contains("_lastAnalysisSummaryText = summary;", source, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(AiResultsEmptyText));", source, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(ScanStatusDetailText));", source, StringComparison.Ordinal);

        var languageSource = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App",
            "ViewModels", "MainWindowViewModel.Language.cs"));
        Assert.Contains("var updated = _formatAnalysisFeedback();", languageSource, StringComparison.Ordinal);
        Assert.Contains("if (_lastAnalysisSummaryText == previous) _lastAnalysisSummaryText = updated;",
            languageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_expose_max_depth_dependency_property()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = LocalizedXamlSource.ReadAllText(controlPath);

        Assert.Contains("nameof(MaxDepth)", source, StringComparison.Ordinal);
        Assert.Contains("public int MaxDepth", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(MaxDepth", source, StringComparison.Ordinal);
        Assert.Contains("nameof(IsScanning)", source, StringComparison.Ordinal);
        Assert.Contains("EnsureLayout()", source, StringComparison.Ordinal);
        Assert.Contains("DrawLabel(", source, StringComparison.Ordinal);
        Assert.Contains("DrawNodeLabel(", source, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering", source, StringComparison.Ordinal);
        Assert.Contains("InterpolateRectangle", source, StringComparison.Ordinal);
        Assert.Contains("TreemapDisplayPlanner", source, StringComparison.Ordinal);
        Assert.Contains("TreemapAnimationSourceResolver", source, StringComparison.Ordinal);
        Assert.Contains("nameof(CompactionLevel)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(UseOverviewChrome)", source, StringComparison.Ordinal);
        Assert.Contains("ZoomAnimationDurationMilliseconds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsScanning || rootChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_render_directory_hierarchy_chrome()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = LocalizedXamlSource.ReadAllText(controlPath);

        Assert.Contains("DrawDirectoryChrome", source, StringComparison.Ordinal);
        Assert.Contains("CreateDirectoryHeaderBrush", source, StringComparison.Ordinal);
        Assert.Contains("CreateDirectoryContentPen", source, StringComparison.Ordinal);
        Assert.Contains("bounds.Bottom - Math.Min(Math.Max(0, bounds.Height - 2), DirectoryHeaderBandHeight)", source, StringComparison.Ordinal);
        Assert.Contains("bounds.Bottom - DirectoryHeaderBandHeight", source, StringComparison.Ordinal);
        Assert.Contains("node.IsDirectory ? bounds.Bottom - height : bounds.Top", source, StringComparison.Ordinal);
        Assert.Contains("Color.FromRgb(24, 24, 27)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_use_lightweight_rendering_during_viewport_animation()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = LocalizedXamlSource.ReadAllText(controlPath);

        Assert.Contains("IsViewportAnimationActive", source, StringComparison.Ordinal);
        Assert.Contains("ShouldRenderLabel", source, StringComparison.Ordinal);
        Assert.Contains("ShouldRenderDirectoryChrome", source, StringComparison.Ordinal);
        Assert.Contains("RenderExitingRectangles", source, StringComparison.Ordinal);
        Assert.Contains("var animatedRectangles = new TreemapRectangle[_rectangles.Count];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("renderRectangles.Where", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Treemap_labels_and_directory_chrome_should_fade_in_before_viewport_transition_finishes()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = LocalizedXamlSource.ReadAllText(controlPath);

        Assert.Contains("TreemapTransitionPresentation.GetOpacity", source, StringComparison.Ordinal);
        Assert.Contains("TreemapTransitionPresentation.HasExactSourceRectangle", source, StringComparison.Ordinal);
        Assert.Contains("ShouldRenderLabel(bounds, labelOpacity)", source, StringComparison.Ordinal);
        Assert.Contains("drawingContext.PushOpacity(labelOpacity)", source, StringComparison.Ordinal);
        Assert.Contains("drawingContext.PushOpacity(chromeOpacity)", source, StringComparison.Ordinal);
        Assert.Contains("return labelOpacity > 0 && TreemapLabelPlanner.Plan(bounds.Width, bounds.Height) is not null", source, StringComparison.Ordinal);
        Assert.Contains("return TreemapTransitionPresentation.ShouldRenderExitingRectangles(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_bind_viewport_transitions_to_reduce_motion_setting()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = LocalizedXamlSource.ReadAllText(controlPath);
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));

        Assert.Contains("new FrameworkPropertyMetadata(true, OnEnableViewportTransitionsChanged)", source, StringComparison.Ordinal);
        Assert.Contains("EnableViewportTransitions=\"{Binding IsViewportMotionEnabled}\"", xaml, StringComparison.Ordinal);
        var requestStart = source.IndexOf("private TreemapLayoutRequest? CreateLayoutRequest()", StringComparison.Ordinal);
        Assert.True(requestStart >= 0);
        var requestEnd = source.IndexOf("private void QueueLayoutRequest(", requestStart, StringComparison.Ordinal);
        Assert.True(requestEnd > requestStart);
        var request = source[requestStart..requestEnd];
        Assert.Contains("new TreemapLayoutRequest(", request, StringComparison.Ordinal);
        Assert.Contains("EnableViewportTransitions", request, StringComparison.Ordinal);
        Assert.Contains("EnableViewportTransitions = enableViewportTransitions;", source, StringComparison.Ordinal);
        Assert.Contains("if (EnableViewportTransitions", source, StringComparison.Ordinal);
        Assert.Contains("&& preparedLayout.Request.EnableViewportTransitions", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TreemapControl_should_render_collapsed_directories_as_summary_blocks()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = LocalizedXamlSource.ReadAllText(controlPath);

        Assert.Contains("_visibleContainerPaths", source, StringComparison.Ordinal);
        Assert.Contains("BuildVisibleContainerPaths", source, StringComparison.Ordinal);
        Assert.Contains("HasVisibleChildren", source, StringComparison.Ordinal);
        Assert.Contains("DrawDirectoryRiskIndicator", source, StringComparison.Ordinal);
        Assert.Contains("BuildRiskSummaries", source, StringComparison.Ordinal);
        Assert.Contains("CreateRiskFillBrush", source, StringComparison.Ordinal);
    }
}

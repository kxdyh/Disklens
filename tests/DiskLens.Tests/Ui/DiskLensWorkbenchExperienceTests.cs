using System.IO;
using System.Reflection;
using System.Text.Json;
using DiskLens.App.Models;
using DiskLens.Core.Models;
using DiskLens.Core.Visualization;

namespace DiskLens.Tests.Ui;

public sealed class DiskLensWorkbenchExperienceTests
{
    [Fact]
    public void Workbench_should_expose_path_filter_sort_capacity_and_persistent_status_regions()
    {
        var repoRoot = GetRepoRoot();
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var viewModel = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("SelectedItem=\"{Binding DisplaySortMode, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding DisplayFilterMode, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{loc:Bind VolumeUsagePercent}", xaml, StringComparison.Ordinal);
        Assert.Contains("{loc:Bind FooterText}", xaml, StringComparison.Ordinal);
        Assert.Contains("VisibleVisualExplorerItems", xaml, StringComparison.Ordinal);
        Assert.Contains("RebuildVisibleVisualExplorerItems", viewModel, StringComparison.Ordinal);
        var presentation = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "Models", "VisualExplorerPresentation.cs"));
        Assert.Contains("OrderByDescending(item => item.DisplayNode.SizeBytes)", presentation, StringComparison.Ordinal);
        Assert.Contains("\"文件夹\" => item.Kind == VisualExplorerItemKind.Directory", presentation, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_should_default_to_day_mode_and_be_switchable_from_main_footer()
    {
        var repoRoot = GetRepoRoot();
        var settings = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "Models", "AppSettings.cs"));
        var viewModel = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var codeBehind = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));

        Assert.Equal("03", ThemeCatalog.DefaultThemeId);
        Assert.Contains("string ThemeId = ThemeCatalog.DefaultThemeId", settings, StringComparison.Ordinal);
        Assert.Contains("SelectedThemeId", viewModel, StringComparison.Ordinal);
        Assert.Contains("ThemeId: ThemeCatalog.DefaultThemeId", settings, StringComparison.Ordinal);
        Assert.Contains("_settingsStore.SaveAsync(new AppSettings(", viewModel, StringComparison.Ordinal);
        Assert.Contains("SelectedThemeId,", viewModel, StringComparison.Ordinal);
        Assert.Contains("SelectThemeCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("FooterThemeChoiceStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyTheme", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ThemeCatalog.GetDictionaryPath", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Theme{Normalize(themeId)}.xaml", LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "Models", "ThemeCatalog.cs")), StringComparison.Ordinal);
        Assert.Contains("IsNightThemeSelected", xaml, StringComparison.Ordinal);
        Assert.Contains("IsWarmThemeSelected", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDayThemeSelected", xaml, StringComparison.Ordinal);
        Assert.Contains("设置", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void App_settings_should_round_trip_theme_and_default_missing_legacy_theme_to_glacier()
    {
        var defaultSettings = AppSettings.Default();
        var themeProperty = typeof(AppSettings).GetProperty("ThemeId", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(themeProperty);
        Assert.Equal("03", themeProperty!.GetValue(defaultSettings));

        var oldJson = """
            { "RootPath": "E:\\\\Sample", "AnalysisThresholdMb": 128, "VisualFolderDisplayThresholdMb": 64, "ProviderKind": 0, "AiEndpoint": "", "AiModel": "" }
            """;
        var legacySettings = JsonSerializer.Deserialize<AppSettings>(oldJson);
        Assert.NotNull(legacySettings);
        Assert.Equal("03", themeProperty.GetValue(legacySettings));

        themeProperty.SetValue(defaultSettings, "01");
        var customSettings = defaultSettings;
        var roundTripped = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(customSettings));
        Assert.NotNull(roundTripped);
        Assert.Equal("01", themeProperty.GetValue(roundTripped));
    }

    [Fact]
    public void Theme_dictionaries_should_define_the_same_workbench_tokens()
    {
        var repoRoot = GetRepoRoot();
        var themeDirectory = Path.Combine(repoRoot, "src", "DiskLens.App", "Resources", "Themes");
        var files = new[] { "Theme01.xaml", "Theme02.xaml", "Theme03.xaml" };
        var requiredKeys = new[]
        {
            "WindowBackgroundBrush",
            "CardBackgroundBrush",
            "PrimaryTextBrush",
            "MutedTextBrush",
            "AccentBrush",
            "NavigationBackgroundBrush",
            "TileUserSpaceBrush",
            "TileSystemBrush",
            "ModalBackdropBrush",
            "WarningSurfaceBrush",
            "WarningBorderBrush",
            "WarningTextBrush",
        };

        foreach (var file in files)
        {
            var dictionary = LocalizedXamlSource.ReadAllText(Path.Combine(themeDirectory, file));
            foreach (var key in requiredKeys)
            {
                Assert.Contains($"x:Key=\"{key}\"", dictionary, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Visual_explorer_presentation_should_filter_and_sort_without_mutating_scan_items()
    {
        var largeDirectory = CreateItem("E:\\Sample\\Large", "Large", true, 900, 12, 4);
        var smallFile = CreateItem("E:\\Sample\\note.txt", "note.txt", false, 12, 0, 0);
        var aggregate = CreateItem("E:\\Sample\\Other", "Other folders", true, 240, 4, 2, VisualExplorerItemKind.Aggregate);
        var input = new[] { largeDirectory, smallFile, aggregate };

        var files = VisualExplorerPresentation.Apply(input, "大小", "文件");
        Assert.Single(files);
        Assert.Same(smallFile.DisplayNode, files[0].DisplayNode);

        var directoriesByName = VisualExplorerPresentation.Apply(input, "名称", "文件夹");
        Assert.Equal(["Large"], directoriesByName.Select(item => item.DisplayNode.Name));

        var aggregateOnly = VisualExplorerPresentation.Apply(input, "大小", "聚合块");
        Assert.Single(aggregateOnly);
        Assert.Same(aggregate.DisplayNode, aggregateOnly[0].DisplayNode);

        var byCount = VisualExplorerPresentation.Apply(input, "项目数", "全部项目");
        Assert.Equal([largeDirectory.DisplayNode, aggregate.DisplayNode, smallFile.DisplayNode], byCount.Select(item => item.DisplayNode));
        Assert.Equal([largeDirectory, smallFile, aggregate], input);
    }

    [Fact]
    public void Theme_catalog_should_resolve_only_known_theme_ids_and_keep_glacier_as_fallback()
    {
        Assert.Equal("03", ThemeCatalog.Normalize(null));
        Assert.Equal("03", ThemeCatalog.Normalize("unsupported"));
        Assert.Equal("Resources/Themes/Theme01.xaml", ThemeCatalog.GetDictionaryPath("01"));
        Assert.Equal("Resources/Themes/Theme02.xaml", ThemeCatalog.GetDictionaryPath("02"));
        Assert.Equal("Resources/Themes/Theme03.xaml", ThemeCatalog.GetDictionaryPath("03"));
    }

    private static VisualExplorerItem CreateItem(
        string path,
        string name,
        bool isDirectory,
        long size,
        int fileCount,
        int directoryCount,
        VisualExplorerItemKind? kind = null)
    {
        var node = new FileSystemNode(
            path,
            name,
            Path.GetExtension(name),
            size,
            FileAttributes.Normal,
            null,
            null,
            isDirectory,
            false,
            [],
            [],
            fileCount,
            directoryCount);

        return new VisualExplorerItem(node, kind ?? (isDirectory ? VisualExplorerItemKind.Directory : VisualExplorerItemKind.File),
            VisualExplorerItemWeight.Standard, 0, $"{size} B", string.Empty, path, false, []);
    }

    private static string GetRepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}

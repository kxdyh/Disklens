using System.Xml.Linq;

namespace DiskLens.Tests.Ui;

public sealed class VisualAuditRegressionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Scan_status_should_occupy_the_details_column_without_covering_the_map()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var scanPanel = Named(document, "ScanExperienceOverlay");
        Assert.Equal("WorkbenchGrid", (string?)scanPanel.Parent?.Attribute(X + "Name"));
        Assert.Equal("1", (string?)scanPanel.Attribute("Grid.Column"));
        Assert.Null(scanPanel.Attribute("Grid.ColumnSpan"));
        Assert.DoesNotContain("实时扫描 · 文件空间映射", scanPanel.ToString());
    }

    [Fact]
    public void Fullscreen_back_button_should_use_parent_navigation_and_modal_visibility()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var button = Named(document, "FocusModeBackButton");
        Assert.Equal("RootLayout", (string?)button.Parent?.Attribute(X + "Name"));
        Assert.Equal("{Binding ZoomOutCommand}", (string?)button.Attribute("Command"));
        Assert.Contains("IsFocusModeNavigationVisible", (string?)button.Attribute("Visibility"));
    }

    [Fact]
    public void Workbench_back_button_should_stay_in_the_path_bar_and_share_fullscreen_navigation()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var button = Named(document, "WorkbenchBackButton");
        Assert.Contains(button.Ancestors(), element =>
            (string?)element.Attribute(X + "Name") == "ExplorerNavigationBar");
        Assert.Equal((string?)Named(document, "FocusModeBackButton").Attribute("Command"),
            (string?)button.Attribute("Command"));
        Assert.Null(button.Attribute("Visibility"));
        Assert.Contains("返回上一级目录", (string?)button.Attribute("ToolTip"));
        Assert.Equal("40", (string?)button.Attribute("Width"));
    }

    [Fact]
    public void Settings_should_display_connection_names_instead_of_protocol_enum_values()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var selector = Named(document, "AiConnectionSelector");
        Assert.Equal("{Binding AiConnectionOptions}", (string?)selector.Attribute("ItemsSource"));
        Assert.Equal("Label", (string?)selector.Attribute("DisplayMemberPath"));
        Assert.Equal("Id", (string?)selector.Attribute("SelectedValuePath"));
        Assert.Equal("{Binding SelectedAiConnectionId, Mode=TwoWay}", (string?)selector.Attribute("SelectedValue"));
        Assert.DoesNotContain("ProviderKinds", Named(document, "SettingsPanel").ToString());
        Assert.Contains("HasAiConnectionConfiguration", (string?)Named(document, "AiConnectionFields").Attribute("Visibility"));
        var configure = Named(document, "ConfigureAiConnectionButton");
        Assert.Equal("{Binding ConfigureAiConnectionCommand}", (string?)configure.Attribute("Command"));
        Assert.Contains("ShowAiConnectionSetupAction", (string?)configure.Attribute("Visibility"));
    }

    [Fact]
    public void Theme_controls_should_render_selection_and_checkbox_labels_with_theme_colors()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var comboStyle = document.Descendants().Single(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "ComboBox");
        Assert.Contains(comboStyle.Descendants(), element => element.Name.LocalName == "ControlTemplate");
        Assert.Contains(comboStyle.Descendants(), element =>
            (string?)element.Attribute("Content") == "{TemplateBinding SelectionBoxItem}");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "CheckBox"
            && element.Descendants().Any(setter => (string?)setter.Attribute("Property") == "Foreground"));
    }

    [Fact]
    public void Guide_should_allow_keyboard_access_to_the_dont_show_again_option()
    {
        var code = LocalizedXamlSource.ReadAllText(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));
        Assert.Contains("candidates.Add(GuideDoNotShowAgainCheckBox);", code);
        Assert.Contains("IsDescendantOf(focused, GuideDoNotShowAgainCheckBox)", code);
        Assert.Contains("LocationPickerTarget.Margin = isLocationStep ? new Thickness(24, 16, 24, 180)", code);
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    [Fact]
    public void Shell_should_use_the_full_width_and_keep_one_entry_per_action()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var root = Named(document, "RootLayout");
        Assert.DoesNotContain(root.Elements(), element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(X + "Name") is "NavigationRail" or "NavigationColumn");
        Assert.Null(Named(document, "MainContentGrid").Attribute("Grid.Column"));
        foreach (var command in new[] { "AnalyzeCommand", "OpenSettingsCommand", "HelpCommand" })
        {
            Assert.Single(document.Descendants(), element => element.Name.LocalName == "Button"
                && (string?)element.Attribute("Command") == $"{{Binding {command}}}");
        }

        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "Button"
            && (string?)element.Attribute("Command") == "{Binding ToggleTreemapDisplaySettingsCommand}");
        var rootButtons = document.Descendants().Where(element => element.Name.LocalName == "Button"
            && (string?)element.Attribute("Command") == "{Binding ResetZoomCommand}").ToArray();
        Assert.Equal(2, rootButtons.Length);
        Assert.Contains(Named(document, "WorkbenchRootButton"), rootButtons);
        Assert.Contains(Named(document, "FocusModeRootButton"), rootButtons);

        Assert.Contains(Named(document, "HelpButton").Ancestors(), element =>
            (string?)element.Attribute(X + "Name") == "AppHeader");
    }

    [Fact]
    public void Settings_should_keep_close_action_outside_the_scrolling_fields_and_expose_budget_help()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var settings = Named(document, "SettingsPanel");
        var close = settings.Descendants().Single(element => (string?)element.Attribute("Command") == "{Binding CloseSettingsCommand}");
        Assert.DoesNotContain(close.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("Cycle", (string?)settings.Attribute("KeyboardNavigation.TabNavigation"));
        var slider = settings.Descendants().Single(element => element.Name.LocalName == "Slider"
            && ((string?)element.Attribute("Value"))?.Contains("MaxAiAnalysisTokens") == true);
        Assert.Equal("True", (string?)slider.Attribute("IsSnapToTickEnabled"));
        var help = Named(document, "AiTokenBudgetHelp");
        Assert.Contains("AiCostEstimateText", help.ToString());
        Assert.Contains("AiTokenEstimateText", help.ToString());
        Assert.Contains("AiCostReferenceDateText", help.ToString());
        Assert.Equal(2, help.Descendants().Count(element => element.Name.LocalName == "Hyperlink"));
    }

    [Fact]
    public void Footer_should_show_operation_feedback_and_details_should_show_real_size()
    {
        var document = LocalizedXamlSource.Load(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var footer = Named(document, "AppFooter");
        var status = Named(document, "FooterStatusText");
        Assert.Contains(status, footer.Descendants());
        // Keep the normal feedback in the style so the loading trigger can replace it.
        Assert.Null(status.Attribute("Text"));
        var style = status.Descendants().Single(element => element.Name.LocalName == "Style");
        Assert.Contains(style.Elements(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Text"
            && (string?)element.Attribute("Value") == "{Binding FooterStatusDetailText}");
        Assert.Contains(style.Elements(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "ToolTip"
            && (string?)element.Attribute("Value") == "{Binding FooterStatusDetailText}");
        Assert.Contains("FooterStatusHeadlineText", footer.ToString());
        var upload = File.ReadAllText(Path.Combine(RepoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.SharedUpload.cs"));
        Assert.Contains(": ScanStatusHeadlineText", upload); // Scans retain the existing headline.
        Assert.Contains(": StatusText;", upload); // Ordinary operation feedback remains the fallback.
        Assert.Contains("HasFooterOfflineStatus ? StatusText", upload);
        var loading = style.Descendants().Single(element => element.Name.LocalName == "DataTrigger"
            && (string?)element.Attribute("Binding") == "{Binding IsLoadingSharedExperience}");
        Assert.Equal("True", (string?)loading.Attribute("Value"));
        Assert.Contains(loading.Elements(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Text"
            && (string?)element.Attribute("Value") == "正在加载云端经验");
        var details = Named(document, "SelectedDetailsScrollViewer");
        Assert.Contains(details.Descendants(), element => (string?)element.Attribute("Text") == "{loc:Bind SelectedSizeText}");
    }

    [Fact]
    public void Escape_should_close_the_modal_before_applying_directory_navigation()
    {
        var code = LocalizedXamlSource.ReadAllText(Path.Combine(RepoRoot, "src", "DiskLens.App", "MainWindow.xaml.cs"));
        var model = LocalizedXamlSource.ReadAllText(Path.Combine(RepoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));
        var start = code.IndexOf("private void Window_OnPreviewKeyDown", StringComparison.Ordinal);
        var end = code.IndexOf("private void WorkbenchGrid_OnSizeChanged", start, StringComparison.Ordinal);
        var handler = code[start..end];
        Assert.Contains("viewModel.CloseSettingsCommand.Execute(null);", handler);
        Assert.Contains("viewModel.CloseHelpCommand.Execute(null);", handler);
        Assert.Contains("viewModel.CloseScanLocationPickerCommand.Execute(null);", handler);
        Assert.Contains("() => CanNavigateVisualTree && CanZoomOut", model);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskLens.App.Localization;
using DiskLens.App.Models;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Internationalization_completed_scan_reformats_nested_skip_and_estimate_notes_in_both_directions(bool partial) => OnSta(() =>
    {
        L.SetLanguage("zh");
        try
        {
            using var scene = new Scene(AppSettings.Default());
            scene.AddCompletedTask("scan-language");
            var vm = scene.Workspace.ActiveViewModel;
            var fixture = Tree(@"E:\扫描\名称");
            ApplySnapshot(vm, fixture);
            var report = new ScanResult(fixture, 123, 4, fixture.SizeBytes, TimeSpan.FromSeconds(1))
            {
                SkippedReparsePointCount = 2,
                EstimatedStorageItemCount = 1,
                ScanIssueCount = partial ? 1 : 0,
            };
            Invoke(vm, "CaptureCompletedScanFeedback", report);
            Assert.Contains("重解析点", vm.StatusText);
            L.SetLanguage("en");
            Assert.Contains("reparse", vm.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("include estimates", vm.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("重解析点", vm.StatusText);
            Assert.DoesNotContain("估算", vm.StatusText);
            Assert.DoesNotContain("重解析点", vm.FooterText);
            L.SetLanguage("zh");
            Assert.Contains("重解析点", vm.StatusText);
            Assert.Contains("估算", vm.StatusText);
            Assert.DoesNotContain("reparse", vm.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(fixture.Path, vm.ScanRoot!.Path);
        }
        finally { L.SetLanguage("zh"); }
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Internationalization_maximum_English_analysis_fields_fit_the_small_sidebar_and_keep_full_detail(string theme) => OnSta(() =>
    {
        L.SetLanguage("en");
        try
        {
            using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
            scene.AddCompletedTask("english-analysis-card");
            var vm = scene.Workspace.ActiveViewModel;
            var fixture = Tree(@"E:\英文分析\设置");
            var purpose = "Application settings, local cache, logs, and recoverable generated service data.".PadRight(80, '.');
            var detail = string.Join(" ", Enumerable.Repeat("Local cache and generated data may be recreated after confirming no unique files.", 4))[..240];
            var selected = fixture.Children[0] with
            {
                Name = "设置",
                Analysis = new AiAnalysisResult(fixture.Children[0].Path, RiskLevel.Low, SuggestedAction.RecycleCandidate,
                    "应用缓存与可重新生成的数据", .9)
                {
                    ShortPurpose = "应用缓存", ShortPurposeEn = purpose, ReasonEn = detail,
                },
            };
            fixture = fixture with { Children = [selected] };
            ApplySnapshot(vm, fixture);
            vm.SelectedNode = selected;
            foreach (var size in new[] { new Size(1000, 640), new Size(1100, 760) })
            {
                var root = Layout(scene.Window, (int)size.Width, (int)size.Height);
                var card = Named<Border>(scene.Window, "SelectedAiAnalysisCard");
                var reason = Named<TextBlock>(scene.Window, "SelectedAiReasonText");
                var action = Named<TextBlock>(scene.Window, "SelectedAiActionText");
                var tooltip = Assert.IsType<ToolTip>(reason.ToolTip);
                tooltip.PlacementTarget = reason;
                System.Windows.Data.BindingOperations.GetBindingExpression(tooltip, FrameworkElement.DataContextProperty)!.UpdateTarget();
                var fullDetail = Assert.IsType<TextBlock>(tooltip.Content);
                System.Windows.Data.BindingOperations.GetMultiBindingExpression(fullDetail, TextBlock.TextProperty)!.UpdateTarget();
                Assert.Equal(80, L.Purpose(selected.Analysis!).Length);
                Assert.Equal(240, reason.Text.Length);
                Assert.Equal(detail, reason.Text);
                Assert.Equal(detail, fullDetail.Text);
                Assert.Equal(TextWrapping.Wrap, fullDetail.TextWrapping);
                Assert.Equal("设置", Named<TextBlock>(scene.Window, "SelectedAiNameText").Text);
                Assert.True(Fits(card, root));
                Assert.True(Fits(Named<Button>(scene.Window, "AiStartAnalysisButton"), root));
                Assert.True(Bounds(card, root).Contains(Bounds(reason, root)));
                Assert.True(Bounds(card, root).Contains(Bounds(action, root)));
                Assert.InRange(reason.ActualHeight, 1, 54);
                Save(Render(root), $"i18n-english-theme{theme}-maximum-analysis-{size.Width}x{size.Height}.png");
            }
            vm.SelectedNode = fixture with
            {
                Name = "其余 1 项",
                RiskTags = [DisplayNodeMetadata.AggregateRiskTag, DisplayNodeMetadata.OverflowRiskTag],
            };
            Assert.DoesNotContain("其余", vm.SelectedSummaryText);
            Assert.Contains(L.NodeName(vm.SelectedNode), vm.SelectedSummaryText);
            vm.SelectedNode = selected;
            L.SetLanguage("zh");
            Layout(scene.Window, 1000, 640);
            Assert.Equal(selected.Analysis!.Reason, Named<TextBlock>(scene.Window, "SelectedAiReasonText").Text);
        }
        finally { L.SetLanguage("zh"); }
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Internationalization_existing_window_switches_language_globally_and_English_settings_fit(string theme) => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        L.SetLanguage("zh");
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("internationalization");
        var vm = scene.Workspace.ActiveViewModel;
        try
        {
            vm.SelectedLanguage = "en";
            vm.OpenSettingsCommand.Execute(null);
            vm.SettingsSection = "appearance";
            var root = Layout(scene.Window, 1100, 700);
            Assert.Equal("DiskLens", scene.Window.Title);
            Assert.Contains(Descendants<TextBlock>(root), text => text.Text == "Appearance and interaction");
            Assert.True(vm.IsEnglishLanguage);
            Assert.Equal("en", vm.SelectedLanguage);
            Assert.True(Fits(Named<Border>(scene.Window, "SettingsPanel"), root));
            foreach (var choice in Named<StackPanel>(scene.Window, "SettingsNavigation").Children.OfType<Button>())
            {
                var label = Assert.Single(Descendants<TextBlock>(choice));
                Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
                Assert.True(Bounds(choice, root).Contains(Bounds(label, root)), "The full English navigation label must fit inside its button.");
            }
            Save(Render(root), $"i18n-english-theme{theme}-appearance.png");
            vm.SettingsSection = "analysis";
            root = Layout(scene.Window, 1100, 700);
            Save(Render(root), $"i18n-english-theme{theme}-analysis.png");
            vm.SelectedLanguage = "zh";
            Layout(scene.Window, 1100, 700);
            Assert.Equal("DiskLens 磁盘透镜", scene.Window.Title);
            Assert.True(vm.IsChineseLanguage);
        }
        finally { vm.SelectedLanguage = "zh"; }
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Internationalization_English_confirmation_preserves_paths_and_actions_inside_small_layouts(string theme) => OnSta(() =>
    {
        L.SetLanguage("en");
        var preview = ConfirmationPreview();
        var window = new AiAnalysisConfirmationWindow(preview, theme);
        try
        {
            var root = LayoutConfirmation(window, 700, 600);
            Assert.Equal(preview.RootPath, ((TextBlock)window.FindName("ScopePathText")).Text);
            Assert.Equal("Confirm and start", ((Button)window.FindName("ConfirmButton")).Content);
            Assert.StartsWith("13 candidates · 8 need AI", window.SummaryText);
            foreach (var name in new[] { "ScopePathText", "SearchBox", "TypeFilter", "FilterCountText", "CandidateList", "ConfirmButton" })
                Assert.True(Fits((FrameworkElement)window.FindName(name), root), name);
            Save(RenderConfirmation(window, root, 700, 600), $"i18n-english-theme{theme}-confirmation.png");
        }
        finally { window.Close(); L.SetLanguage("zh"); }
    });
}

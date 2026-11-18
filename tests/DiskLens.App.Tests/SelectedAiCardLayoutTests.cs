using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskLens.App.Models;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Fact]
    public void Missing_conclusion_distinguishes_threshold_from_unsubmitted_large_items_and_updates_when_threshold_changes() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        scene.AddCompletedTask("threshold-card");
        var vm = scene.Workspace.ActiveViewModel;
        var root = Tree(@"E:\AI-threshold");
        var folder = root.Children[0] with { SizeBytes = 512L * 1024 * 1024, Analysis = null };
        root = root with { Children = [folder] };
        ApplySnapshot(vm, root);
        vm.AnalysisThresholdMb = 1024;
        vm.SelectedNode = folder;
        Assert.Equal("本项未达到分析阈值", vm.SelectedAiReasonText);
        Assert.Equal(string.Empty, vm.SelectedAiStatusText);
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? "");
        vm.AnalysisThresholdMb = 256;
        Assert.Contains(nameof(vm.SelectedAiReasonText), changed);
        Assert.False(vm.IsSelectedBelowAnalysisThreshold);
        Assert.Equal("尚无分析结论", vm.SelectedAiReasonText);
        Assert.DoesNotContain("未达到分析阈值", vm.SelectedAiReasonText);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Selected_ai_card_shows_compact_conclusion_and_hides_review_queue_in_small_windows(string theme) => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("ai-card");
        var vm = scene.Workspace.ActiveViewModel;
        var fixture = Tree(@"E:\AI-card-layout");
        var selected = fixture.Children[0] with
        {
            Name = "selected-folder-with-a-very-long-english-name",
            Analysis = new AiAnalysisResult(fixture.Children[0].Path, RiskLevel.Review, SuggestedAction.Review,
                "存放用户配置、文档与应用数据，删除前请确认用途。", .9)
                { ShortPurpose = "用户资料与应用数据" },
        };
        fixture = fixture with { Children = [selected] };
        ApplySnapshot(vm, fixture);
        typeof(MainWindowViewModel).GetMethod("RebuildAnalysisItems", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode)], null)!.Invoke(vm, [fixture]);
        vm.SelectedNode = selected;
        typeof(MainWindowViewModel).GetField("_aiUsageSummaryText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, "本次用量 12,345 Token · 约 ¥0.0123–0.0321");
        typeof(MainWindowViewModel).GetField("_aiUsageDetailText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, "离屏布局用量占位，用于验证费用与范围入口保持可见，并非真实账单。");
        Invoke(vm, "NotifyAiUsageChanged");

        foreach (var size in new[] { new Size(1000, 640), new Size(1024, 640), new Size(1000, 720), new Size(1100, 760), new Size(1440, 860) })
        {
            Named<Expander>(scene.Window, "AiScopeExpander").IsExpanded = true;
            var root = Layout(scene.Window, (int)size.Width, (int)size.Height);
            var resultDisclosure = Named<Expander>(scene.Window, "AiResultsExpander");
            var card = Named<Border>(scene.Window, "SelectedAiAnalysisCard");
            var reason = Named<TextBlock>(scene.Window, "SelectedAiReasonText");
            var action = Named<TextBlock>(scene.Window, "SelectedAiActionText");
            var start = Named<Button>(scene.Window, "AiStartAnalysisButton");
            var usage = Named<Button>(scene.Window, "AiUsageInfoButton");
            var shortcut = Named<Button>(scene.Window, "AiAnalysisSettingsShortcut");
            var scope = Named<Expander>(scene.Window, "AiScopeExpander");
            var disclaimer = Named<Border>(scene.Window, "AiDisclaimerAlert");
            Save(Render(root), $"selected-ai-card-theme{theme}-{size.Width}x{size.Height}.png");
            File.WriteAllText(Path.Combine(OutputDirectory, $"selected-ai-bounds-{theme}.txt"),
                $"size={size}, card={Bounds(card, root)}, reason={Bounds(reason, root)}, usage={Bounds(usage, root)}, scope={Bounds(scope, root)}, start={Bounds(start, root)}");
            Assert.True(Fits(card, root));
            Assert.True(Fits(start, root));
            Assert.True(Fits(usage, root));
            Assert.True(Fits(scope, root));
            Assert.True(Fits(shortcut, root));
            Assert.True(Fits(disclaimer, root));
            Assert.Equal(((SolidColorBrush)scene.Window.FindResource("SecondaryTextBrush")).Color,
                ((SolidColorBrush)usage.Foreground).Color);
            Assert.DoesNotContain(disclaimer, Descendants<Border>(card));
            Assert.True(Bounds(disclaimer, root).Bottom <= Bounds(Named<Grid>(scene.Window, "WorkbenchGrid"), root).Top);
            foreach (var text in Descendants<TextBlock>(disclaimer))
                Assert.True(Bounds(disclaimer, root).Contains(Bounds(text, root)), "The top disclaimer text must stay inside its alert.");
            Assert.Equal(Visibility.Collapsed, resultDisclosure.Visibility);
            Assert.True(Bounds(card, root).Bottom < Bounds(usage, root).Top);
            Assert.True(Bounds(shortcut, root).Bottom <= Bounds(start, root).Top);
            Assert.Same(vm.OpenAiAnalysisSettingsCommand, shortcut.Command);
            var tooltip = Assert.IsType<ToolTip>(usage.ToolTip);
            Assert.False(tooltip.IsOpen, "This regression test must not create a desktop popup.");
            Assert.Equal(((SolidColorBrush)scene.Window.FindResource("CardBackgroundBrush")).Color,
                Assert.IsType<SolidColorBrush>(tooltip.Background).Color);
            Assert.Equal(((SolidColorBrush)scene.Window.FindResource("PrimaryTextBrush")).Color,
                Assert.IsType<SolidColorBrush>(tooltip.Foreground).Color);
            Assert.Equal(((SolidColorBrush)scene.Window.FindResource("BorderBrush")).Color,
                Assert.IsType<SolidColorBrush>(tooltip.BorderBrush).Color);
            var tooltipContent = Assert.IsType<StackPanel>(tooltip.Content);
            Assert.Equal(new[] { "AiUsageSummaryText", "SharedExperienceSummaryText", "AiUsageDetailText" },
                tooltipContent.Children.OfType<TextBlock>().Select(text =>
                {
                    var binding = Assert.IsType<System.Windows.Data.MultiBinding>(System.Windows.Data.BindingOperations.GetBindingBase(text, TextBlock.TextProperty));
                    Assert.Equal(2, binding.Bindings.Count);
                    Assert.IsType<System.Windows.Data.Binding>(binding.Bindings[1]);
                    return Assert.IsType<System.Windows.Data.Binding>(binding.Bindings[0]).Path.Path;
                }));
            Assert.True(reason.ActualHeight > 0, "The compact purpose must remain visible.");
            Assert.Equal(selected.Analysis!.Reason, reason.Text);
            Assert.InRange(reason.Text.Length, 1, 30);
            Assert.Equal("您的AI分析结论", vm.SelectedAiStatusText);
            Assert.Equal("不建议删", action.Text);
            Assert.True(action.ActualHeight > 0);
            Assert.True(Bounds(card, root).Contains(Bounds(reason, root)), "The conclusion must not draw beyond the card.");
            Assert.True(Bounds(card, root).Contains(Bounds(action, root)), "The advice must not draw beyond the card.");
            Assert.InRange(Bounds(reason, root).Top - Bounds(action, root).Bottom, 0, 8);
            Save(Render(root), $"selected-ai-card-theme{theme}-{size.Width}x{size.Height}.png");

            // Expanding the scope must not hide the analysis/cancel action or settings shortcut.
            scope.IsExpanded = true;
            root = Layout(scene.Window, (int)size.Width, (int)size.Height);
            Assert.True(Fits(start, root));
            Assert.True(Fits(card, root));
            Assert.True(Fits(shortcut, root));
            Assert.True(Bounds(card, root).Bottom <= Bounds(start, root).Top);
            Assert.True(reason.ActualHeight > 0, "The compact conclusion stays visible with the footer disclosures open.");
            var options = Named<ScrollViewer>(scene.Window, "AiFooterOptionsScrollViewer");
            Assert.True(scope.ActualHeight <= options.ViewportHeight,
                "The complete scope selector must fit without a 60px clipped strip.");
            foreach (var radio in Descendants<RadioButton>(scope))
                Assert.Equal(((SolidColorBrush)scene.Window.FindResource("PrimaryTextBrush")).Color,
                    ((SolidColorBrush)radio.Foreground).Color);
            scope.IsExpanded = false;
        }

        // A real tile selection must update the visible card, without navigating the queue.
        Layout(scene.Window, 1024, 640);
        vm.SelectedNode = fixture;
        Layout(scene.Window, 1024, 640);
        Assert.Equal(string.Empty, vm.SelectedAiStatusText);
        Assert.Equal("尚无分析结论", Named<TextBlock>(scene.Window, "SelectedAiReasonText").Text);
        Assert.Null(vm.SelectedAnalysisItem);
    });
}

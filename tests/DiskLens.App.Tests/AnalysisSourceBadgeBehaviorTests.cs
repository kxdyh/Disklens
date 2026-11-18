using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DiskLens.App.Models;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Theory]
    [InlineData(AiAnalysisResultOrigin.SharedExperience, "云端经验", "☁")]
    [InlineData(AiAnalysisResultOrigin.AiAnalyzed, "AI 分析", "✦")]
    [InlineData(AiAnalysisResultOrigin.CacheReused, "AI 分析 · 本地缓存", "↺")]
    [InlineData(AiAnalysisResultOrigin.ProtectedByRule, "本地保护规则", "◆")]
    [InlineData(AiAnalysisResultOrigin.Incomplete, "尚未完成", "…")]
    public void Result_source_labels_and_symbols_agree_in_the_list_and_selected_card(
        AiAnalysisResultOrigin origin, string expectedLabel, string expectedIcon) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        scene.AddCompletedTask("source-label");
        var vm = scene.Workspace.ActiveViewModel;
        if (origin == AiAnalysisResultOrigin.SharedExperience) EnableSharedSourcePresentation(vm);
        var root = SourceTree(origin);
        ApplySnapshot(vm, root);
        typeof(MainWindowViewModel).GetMethod("RebuildAnalysisItems", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode)], null)!.Invoke(vm, [root]);
        vm.SelectedNode = root.Children[0];

        var item = Assert.Single(vm.AnalysisItems);
        Assert.Equal(expectedLabel, item.SourceLabel);
        Assert.Equal(expectedIcon, item.SourceIcon);
        Assert.Equal(item.SourceLabel, vm.SelectedAiOriginText);
        Assert.Equal(item.SourceIcon, vm.SelectedAiOriginIcon);
        Assert.True(vm.HasSelectedAiOriginBadge);
    });

    [Theory]
    [InlineData(AiAnalysisResultOrigin.SharedExperience, "匹配到的云端分析经验")]
    [InlineData(AiAnalysisResultOrigin.AiAnalyzed, "您的AI分析结论")]
    [InlineData(AiAnalysisResultOrigin.CacheReused, "您的AI分析结论")]
    [InlineData(AiAnalysisResultOrigin.ProtectedByRule, "")]
    public void Production_card_uses_only_the_three_allowed_source_statuses_and_hides_review_queue(
        AiAnalysisResultOrigin origin, string expectedStatus) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        scene.AddCompletedTask("source-badge");
        var vm = scene.Workspace.ActiveViewModel;
        if (origin == AiAnalysisResultOrigin.SharedExperience) EnableSharedSourcePresentation(vm);
        var root = SourceTree(origin);
        ApplySnapshot(vm, root);
        typeof(MainWindowViewModel).GetMethod("RebuildAnalysisItems", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode)], null)!.Invoke(vm, [root]);
        vm.SelectedNode = root.Children[0];
        Layout(scene.Window, 1440, 900);

        Assert.Equal(expectedStatus, vm.SelectedAiStatusText);
        Assert.Equal(expectedStatus, Named<TextBlock>(scene.Window, "SelectedAiStatusText").Text);
        Assert.Equal(Visibility.Collapsed, Named<Expander>(scene.Window, "AiResultsExpander").Visibility);
        Assert.DoesNotContain("待复核", Named<Expander>(scene.Window, "AiResultsExpander").Header?.ToString() ?? string.Empty);
    });

    [Fact]
    public void Selected_source_badge_is_hidden_without_an_analysis_result_and_updates_on_selection() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        scene.AddCompletedTask("source-badge-empty");
        var vm = scene.Workspace.ActiveViewModel;
        var root = SourceTree(AiAnalysisResultOrigin.SharedExperience);
        var unanalysed = root.Children[0] with { Analysis = null };
        root = root with { Children = [unanalysed] };
        ApplySnapshot(vm, root);
        vm.SelectedNode = unanalysed;
        Layout(scene.Window, 1440, 900);

        Assert.False(vm.HasSelectedAiOriginBadge);
        Assert.Equal(string.Empty, Named<TextBlock>(scene.Window, "SelectedAiStatusText").Text);

        root = SourceTree(AiAnalysisResultOrigin.AiAnalyzed);
        ApplySnapshot(vm, root);
        vm.SelectedNode = root.Children[0];
        Layout(scene.Window, 1440, 900);
        Assert.True(vm.HasSelectedAiOriginBadge);
        Assert.Equal("您的AI分析结论", vm.SelectedAiStatusText);
        Assert.Equal("您的AI分析结论", Named<TextBlock>(scene.Window, "SelectedAiStatusText").Text);

        vm.SelectedNode = null;
        Layout(scene.Window, 1440, 900);
        Assert.False(vm.HasSelectedAiOriginBadge);
        Assert.Equal(string.Empty, Named<TextBlock>(scene.Window, "SelectedAiStatusText").Text);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Widest_source_badges_stay_compact_and_readable_in_every_theme(string theme) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("source-badge-theme-" + theme);
        var vm = scene.Workspace.ActiveViewModel;
        var root = SourceTree(AiAnalysisResultOrigin.CacheReused);
        ApplySnapshot(vm, root);
        typeof(MainWindowViewModel).GetMethod("RebuildAnalysisItems", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode)], null)!.Invoke(vm, [root]);
        vm.SelectedNode = root.Children[0];
        Layout(scene.Window, 1440, 900);

        var status = Named<TextBlock>(scene.Window, "SelectedAiStatusText");
        var action = Named<TextBlock>(scene.Window, "SelectedAiActionText");
        Assert.Equal("您的AI分析结论", status.Text);
        Assert.Equal("不建议删", action.Text);
        Assert.Equal(((SolidColorBrush)scene.Window.FindResource("StatusDangerBrush")).Color,
            ((SolidColorBrush)action.Foreground).Color);
        Assert.Equal(Visibility.Collapsed, Named<Expander>(scene.Window, "AiResultsExpander").Visibility);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Consider_delete_action_is_blue_and_bold_in_every_theme(string theme) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("consider-delete-theme-" + theme);
        var vm = scene.Workspace.ActiveViewModel;
        EnableSharedSourcePresentation(vm);
        var root = SourceTree(AiAnalysisResultOrigin.SharedExperience);
        var node = root.Children[0];
        node = node with { Analysis = node.Analysis! with { SuggestedAction = SuggestedAction.RecycleCandidate } };
        root = root with { Children = [node] };
        ApplySnapshot(vm, root);
        vm.SelectedNode = node;
        Layout(scene.Window, 1440, 900);

        var action = Named<TextBlock>(scene.Window, "SelectedAiActionText");
        Assert.Equal("可以考虑删", action.Text);
        Assert.Equal(FontWeights.Bold, action.FontWeight);
        Assert.Equal(((SolidColorBrush)scene.Window.FindResource("LinkBrush")).Color,
            ((SolidColorBrush)action.Foreground).Color);
    });

    private static void EnableSharedSourcePresentation(MainWindowViewModel vm)
    {
        // This offscreen layout harness has no cloud transport. Its synthetic cloud
        // records need enabled consent; real settings event transitions are tested separately.
        var consent = new SharedConsentSnapshot(true, SharedConsentSnapshot.CurrentAgreementVersion, false,
            DateTimeOffset.UtcNow, 1, "layout-fixture", [], new SharedInstallation("layout-fixture", "synthetic", 1));
        typeof(MainWindowViewModel).GetField("_sharedConsent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, consent);
        typeof(MainWindowViewModel).GetMethod("NotifyAnalysisVisibilityChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);
    }

    private static void AssertCompactReadableBadge(Border badge, string labelBinding, string theme)
    {
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.InRange(badge.ActualHeight, 1, 24);
        var label = Assert.Single(Descendants<TextBlock>(badge), text =>
            BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == labelBinding);
        Assert.Equal("AI 分析 · 本地缓存", label.Text);
        Assert.True(label.ActualWidth > 0 && label.ActualHeight > 0);
        var foreground = Assert.IsType<SolidColorBrush>(label.Foreground).Color;
        var background = Assert.IsType<SolidColorBrush>(badge.Background).Color;
        Assert.True(SourceBadgeContrast(foreground, background) >= 4.5,
            $"The widest source label needs at least 4.5:1 contrast in theme {theme}.");
    }

    private static double SourceBadgeContrast(Color first, Color second)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static FileSystemNode SourceTree(AiAnalysisResultOrigin origin)
    {
        const string rootPath = @"E:\DiskLens-source-badges";
        const string path = rootPath + @"\cache";
        var result = new AiAnalysisResult(path, RiskLevel.Review, SuggestedAction.Keep,
            "Synthetic purpose conclusion for source badge verification.", .9)
        { Origin = origin, ConfidenceLevel = AiConfidenceLevel.High };
        var folder = new FileSystemNode(path, "cache", "", 2L * 1024 * 1024 * 1024,
            FileAttributes.Directory, null, null, true, false, [], [], 0, 1, result);
        return new(rootPath, "DiskLens-source-badges", "", folder.SizeBytes, FileAttributes.Directory,
            null, null, true, false, [], [folder], 0, 2);
    }

    private static void AssertVisibleSourceText(DependencyObject root, string bindingPath, string expectedText)
    {
        var element = Assert.Single(Descendants<TextBlock>(root), text =>
            BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == bindingPath);
        Assert.Equal(expectedText, element.Text);
        Assert.Equal(Visibility.Visible, element.Visibility);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0,
            $"The {bindingPath} badge must render text or a symbol, even when all colors are identical.");
    }
}

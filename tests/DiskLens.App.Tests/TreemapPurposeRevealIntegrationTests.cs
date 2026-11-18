using System.Reflection;
using System.Windows;
using System.Windows.Media;
using DiskLens.App.Controls;
using DiskLens.App.Models;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Fact]
    public void Real_analysis_completion_in_a_background_task_defers_only_new_ai_and_cache_purposes() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        scene.AddCompletedTask("deferred-ai-purposes");
        var tab = scene.Workspace.ActiveTab;
        var vm = tab.ViewModel;
        vm.IsReduceMotionEnabled = false;
        scene.Workspace.HomeViewModel.IsReduceMotionEnabled = false;
        var nodes = new[] { "fresh", "cached", "existing-cloud", "new-cloud", "failed", "protected" }
            .Select(name => PurposeNode(name, "短用途", "详细用途")).ToArray();
        var before = nodes.Select((node, index) => index == 2
            ? node with { Analysis = node.Analysis! with { Origin = AiAnalysisResultOrigin.SharedExperience } }
            : node with { Analysis = null }).ToArray();
        var root = new FileSystemNode(@"E:\", "E:\\", string.Empty, before.Sum(node => node.SizeBytes),
            System.IO.FileAttributes.Directory, null, null, true, false, [], before);
        ApplySnapshot(vm, root);
        scene.Workspace.SelectTab(scene.Workspace.TaskTabs[0]);
        Pump();
        var results = nodes.Select(node => node.Analysis!).ToArray();
        results[1] = results[1] with { Origin = AiAnalysisResultOrigin.CacheReused };
        results[3] = results[3] with { Origin = AiAnalysisResultOrigin.SharedExperience };
        results[4] = AiAnalysisResult.CreateFallback(nodes[4].Path, "失败") with { ShortPurpose = "失败文本" };
        results[5] = results[5] with { Origin = AiAnalysisResultOrigin.ProtectedByRule };
        var completion = (Task)typeof(MainWindowViewModel)
            .GetMethod("ApplyAnalysisResultsAndReportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [root, results, false, null, root, null, null, null, null])!;
        Complete(completion);
        var control = Named<TreemapControl>(scene.Window, "TreemapView");
        Assert.Same(scene.Workspace.HomeViewModel.TreemapPurposeReveals, control.PurposeReveals);
        // Sharing is off: a hidden cloud purpose must not suppress a newly obtained AI purpose.
        foreach (var node in nodes.Take(3))
            Assert.Equal(0, vm.TreemapPurposeReveals.GetProgress(node.Path, "短用途"));
        foreach (var node in nodes.Skip(3))
            Assert.Equal(1, vm.TreemapPurposeReveals.GetProgress(node.Path, node.Analysis!.ShortPurpose));

        scene.Workspace.SelectTab(tab);
        Pump();
        Assert.Same(vm.TreemapPurposeReveals, control.PurposeReveals);
        Assert.Equal(0, control.PurposeReveals!.GetProgress(nodes[0].Path, "短用途"));
        Assert.Equal("短用途", vm.ScanRoot!.Children[0].Analysis!.ShortPurpose);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Revealing_a_purpose_never_moves_the_name_or_capacity_into_the_badge(string theme) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("purpose-reveal-layout");
        var control = Named<TreemapControl>(scene.Window, "TreemapView");
        control.EnableViewportTransitions = true;
        var node = PurposeNode("Users", "用户资料与应用数据", "右侧详细用途");
        var batch = new TreemapPurposeRevealBatch();
        control.PurposeReveals = batch;
        batch.Enqueue(node.Path, node.Analysis!.ShortPurpose);
        batch.AdvanceTo(0);
        batch.RevealVisible([new(node.Path, node.Analysis.ShortPurpose)]);
        batch.AdvanceTo(80);
        var drawing = DrawPurposeTile(control, node, new Size(220, 130));
        var ink = (Brush)control.FindResource("AccentTextBrush");
        var glyphs = AllDrawings(drawing).OfType<GlyphRunDrawing>().ToArray();
        var purposeTop = glyphs.Where(glyph => ReferenceEquals(glyph.ForegroundBrush, ink)).Min(glyph => glyph.Bounds.Top);
        Assert.All(glyphs.Where(glyph => !ReferenceEquals(glyph.ForegroundBrush, ink)),
            glyph => Assert.True(glyph.Bounds.Bottom < purposeTop));
        var animated = Assert.Single(AllDrawings(drawing).OfType<DrawingGroup>(),
            group => group.Opacity > 0 && group.Opacity < 1);
        Assert.InRange(animated.Opacity, .5, .6);
        Assert.True(new Rect(0, 0, 220, 130).Contains(drawing.Bounds));
        control.EnableViewportTransitions = false;
        Assert.Equal(1, batch.GetProgress(node.Path, node.Analysis.ShortPurpose));
    });

    [Fact]
    public void Binding_another_task_pauses_the_previous_task_reveal_clock() => OnSta(() =>
    {
        var first = new TreemapPurposeRevealBatch();
        var control = new TreemapControl { PurposeReveals = first };
        first.Enqueue(@"C:\Users", "用户资料");
        first.AdvanceTo(0);
        first.RevealVisible([new(@"C:\Users", "用户资料")]);
        first.AdvanceTo(80);
        var progress = first.GetProgress(@"C:\Users", "用户资料");
        control.PurposeReveals = new TreemapPurposeRevealBatch();
        first.AdvanceTo(90_000);
        Assert.Equal(progress, first.GetProgress(@"C:\Users", "用户资料"));
        control.PurposeReveals = first;
        first.AdvanceTo(180_000);
        Assert.Equal(progress, first.GetProgress(@"C:\Users", "用户资料"));
        first.AdvanceTo(180_240);
        Assert.Equal(1, first.GetProgress(@"C:\Users", "用户资料"));
    });
}

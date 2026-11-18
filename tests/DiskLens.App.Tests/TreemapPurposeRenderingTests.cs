using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskLens.App.Controls;
using DiskLens.App.Models;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Purpose_bands_use_real_short_results_and_leave_name_size_and_selection_disjoint(string theme) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("purpose-layout");
        var control = Named<TreemapControl>(scene.Window, "TreemapView");
        Layout(scene.Window, 1440, 860);
        var node = PurposeNode("Users", "用户资料与应用数据", "这是右侧的详细用途，不应出现在悬停框中");

        foreach (var selection in new[] { false, true })
        foreach (var size in new[] { new Size(260, 180), new Size(110, 100), new Size(72, 92), new Size(160, 56), new Size(38, 22), new Size(40, 38), new Size(35, 60), new Size(38, 42) })
        {
            control.IsReanalysisSelectionMode = selection;
            var drawing = DrawPurposeTile(control, node, size);
            Assert.True(drawing.Bounds.IsEmpty || new Rect(-1, -1, size.Width + 2, size.Height + 2).Contains(drawing.Bounds),
                $"All glyphs and badge geometry must stay inside {size}.");
            var glyphs = AllDrawings(drawing).OfType<GlyphRunDrawing>().ToArray();
            var purposeInk = (Brush)control.FindResource("AccentTextBrush");
            var purpose = glyphs.Where(glyph => ReferenceEquals(glyph.ForegroundBrush, purposeInk)).ToArray();
            var checkDrawing = DrawPurposeCheck(control, node, size);
            var checks = AllDrawings(checkDrawing).OfType<GeometryDrawing>().ToArray();
            if (!selection || size.Width < 38 || size.Height < 42) Assert.Empty(checks);
            else
            {
                Assert.NotEmpty(checks);
                Assert.All(checks, check => Assert.All(glyphs,
                    glyph => Assert.False(check.Bounds.IntersectsWith(glyph.Bounds),
                        $"Selection checkbox and text cannot overlap at {size}.")));
            }
            if (size.Height >= 90)
            {
                Assert.NotEmpty(purpose);
                Assert.All(purpose, glyph => Assert.True(glyph.GlyphRun.FontRenderingEmSize >= 12));
                var top = purpose.Min(glyph => glyph.Bounds.Top);
                Assert.All(glyphs.Where(glyph => !ReferenceEquals(glyph.ForegroundBrush, purposeInk)),
                    glyph => Assert.True(glyph.Bounds.Bottom < top, "Name and capacity cannot enter the purpose row."));
                if (selection) Assert.True(glyphs.Min(glyph => glyph.Bounds.Top) >= 25);
            }
            else if (size.Height <= 22) Assert.Empty(purpose);
        }

        foreach (var absent in new[]
        {
            node with { Analysis = node.Analysis! with { ShortPurpose = string.Empty } },
            node with { Analysis = AiAnalysisResult.CreateFallback(node.Path, "失败诊断不可充当用途") with { ShortPurpose = "失败诊断" } },
        })
        {
            var absentDrawing = DrawPurposeTile(control, absent, new Size(260, 180));
            Assert.DoesNotContain(AllDrawings(absentDrawing).OfType<GlyphRunDrawing>(),
                glyph => ReferenceEquals(glyph.ForegroundBrush, control.FindResource("AccentTextBrush")));
        }

        // A parent with nested visible rectangles only owns its 34px title band.
        control.IsReanalysisSelectionMode = false;
        typeof(TreemapControl).GetField("_visibleContainerPaths", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(control, new HashSet<string>([node.Path], StringComparer.Ordinal));
        var nested = DrawPurposeTile(control, node, new Size(300, 180));
        Assert.All(AllDrawings(nested).OfType<GlyphRunDrawing>(), glyph => Assert.True(glyph.Bounds.Top >= 146));
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Hover_presents_complete_short_purpose_in_theme_without_the_detailed_reason(string theme) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("purpose-tooltip");
        var control = Named<TreemapControl>(scene.Window, "TreemapView");
        Layout(scene.Window, 1024, 640);
        var node = PurposeNode("a-very-long-filename.ext", "完整用途短句不得按小块宽度提前截断", "右侧专用详细用途");
        var tooltip = CreatePurposeTooltip(node);
        var card = Assert.IsType<Border>(tooltip.Content);
        var content = Assert.IsType<StackPanel>(card.Child);
        var strings = content.Children.OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains(node.Analysis!.ShortPurpose, strings);
        Assert.DoesNotContain(node.Analysis.Reason, strings);
        Assert.Contains(node.Name, strings);
        Assert.Equal(0, tooltip.BorderThickness.Left);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(tooltip.Background).Color);

        // A ToolTip is a separate popup. Give this offscreen copy the same theme
        // dictionary to verify the production dynamic resource bindings and wrap.
        card.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/Resources/Themes/Theme{theme}.xaml") });
        card.Measure(new Size(280, 500));
        card.Arrange(new Rect(0, 0, 280, card.DesiredSize.Height));
        Assert.Equal(((SolidColorBrush)scene.Window.FindResource("CardBackgroundBrush")).Color,
            Assert.IsType<SolidColorBrush>(card.Background).Color);
        Assert.All(content.Children.OfType<TextBlock>(), text => Assert.Equal(TextTrimming.None, text.TextTrimming));
        Directory.CreateDirectory(OutputDirectory);
        Save(Render(card), $"treemap-purpose-tooltip-theme{theme}.png");

        var failed = node with { Analysis = AiAnalysisResult.CreateFallback(node.Path, "网络失败诊断，不是用途") };
        var failedCard = Assert.IsType<Border>(CreatePurposeTooltip(failed).Content);
        Assert.DoesNotContain(Assert.IsType<StackPanel>(failedCard.Child).Children.OfType<TextBlock>(),
            block => block.Text.Contains("网络失败", StringComparison.Ordinal));
        var legacy = node with { Analysis = node.Analysis with { ShortPurpose = string.Empty } };
        var legacyCard = Assert.IsType<Border>(CreatePurposeTooltip(legacy).Content);
        Assert.DoesNotContain(Assert.IsType<StackPanel>(legacyCard.Child).Children.OfType<TextBlock>(),
            block => block.Text == node.Analysis.Reason);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Production_workspace_purpose_labels_are_rendered_in_every_theme(string theme) => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("purpose-workspace");
        var vm = scene.Workspace.ActiveViewModel;
        var nodes = new[]
        {
            PurposeNode("Users", "用户资料与应用数据", "保存各账户的个人文件与应用数据", 47_200),
            PurposeNode("Windows", "系统运行文件", "Windows 操作系统的运行文件", 15_600),
            PurposeNode("Program Files", "已安装程序及组件", "保存已安装的应用程序及运行组件", 19_000),
            PurposeNode("ProgramData", "应用共享数据", "供不同账户共用的应用配置与数据", 6_100),
            PurposeNode("emulator", "安卓模拟器", "Android 模拟器程序与运行依赖", 1_030),
            PurposeNode("package-cache-with-long-name", "软件安装缓存", "软件安装过程中保留的下载包缓存", 1_220),
            PurposeNode("Temp", "临时文件", "应用与系统运行时生成的临时文件", 1_230),
            PurposeNode("configuration", "应用配置", "应用保存的配置与偏好设置", 1_100),
        };
        var root = new FileSystemNode(@"E:\", "E:\\", string.Empty, nodes.Sum(node => node.SizeBytes),
            FileAttributes.Directory, null, null, true, false, [], nodes);
        ApplySnapshot(vm, root);
        vm.SelectedNode = nodes[0];
        for (var pass = 0; pass < 8; pass++)
        {
            Layout(scene.Window, 1440, 860);
            Pump();
        }
        Save(Render(Layout(scene.Window, 1440, 860)), $"treemap-purpose-workspace-theme{theme}.png");
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Sharing_consent_hides_cloud_tile_purpose_and_open_tooltip_without_erasing_results(string theme) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("purpose-consent");
        Layout(scene.Window, 1024, 640);
        var control = Named<TreemapControl>(scene.Window, "TreemapView");
        var personal = PurposeNode("local-cache", "本地分析用途", "本地详细说明");
        var cloudSource = PurposeNode("cloud-cache", "云端分析用途", "云端详细说明");
        var cloud = cloudSource with
        {
            Analysis = cloudSource.Analysis! with { Origin = AiAnalysisResultOrigin.SharedExperience },
        };
        var retained = cloud.Analysis;

        // Use the production drawing and tooltip builders through repeated consent changes.
        foreach (var enabled in new[] { true, false, true, false })
        {
            var previousTooltip = CreatePurposeTooltip(control, cloud);
            control.ToolTip = previousTooltip;
            typeof(TreemapControl).GetField("_hoveredNode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(control, cloud);
            control.ShowSharedExperienceAnalysis = enabled;
            var purposeInk = (Brush)control.FindResource("AccentTextBrush");
            bool HasPurpose(FileSystemNode node) => AllDrawings(DrawPurposeTile(control, node, new Size(260, 180)))
                .OfType<GlyphRunDrawing>().Any(glyph => ReferenceEquals(glyph.ForegroundBrush, purposeInk));
            Assert.Equal(enabled, HasPurpose(cloud));
            Assert.True(HasPurpose(personal));
            Assert.Same(retained, cloud.Analysis);
            Assert.NotSame(previousTooltip, control.ToolTip);
            var card = Assert.IsType<Border>(Assert.IsType<ToolTip>(control.ToolTip).Content);
            var texts = Assert.IsType<StackPanel>(card.Child).Children.OfType<TextBlock>().Select(text => text.Text);
            Assert.Equal(enabled, texts.Contains(retained!.ShortPurpose));
            Assert.DoesNotContain(retained.Reason, texts);
            Assert.Contains(cloud.Name, texts);
        }
    });

    private static FileSystemNode PurposeNode(string name, string shortPurpose, string reason, long sizeMb = 4000)
    {
        var path = @"E:\" + name;
        return new FileSystemNode(path, name, string.Empty, sizeMb * 1024 * 1024,
            FileAttributes.Directory, null, null, true, false, [], [],
            Analysis: new AiAnalysisResult(path, RiskLevel.High, SuggestedAction.DoNotDelete, reason, .9)
            { ShortPurpose = shortPurpose });
    }

    private static ToolTip CreatePurposeTooltip(FileSystemNode node) =>
        CreatePurposeTooltip(new TreemapControl(), node);

    private static ToolTip CreatePurposeTooltip(TreemapControl control, FileSystemNode node) =>
        (ToolTip)typeof(TreemapControl).GetMethod("CreateNodeToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [node])!;

    private static DrawingGroup DrawPurposeTile(TreemapControl control, FileSystemNode node, Size size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            typeof(TreemapControl).GetMethod("DrawLabel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(control, [context, new TreemapRectangle(node, 0, 0, size.Width, size.Height, 1),
                    new Rect(size), 3d, false, 1d, false]);
        return visual.Drawing;
    }

    private static DrawingGroup DrawPurposeCheck(TreemapControl control, FileSystemNode node, Size size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            typeof(TreemapControl).GetMethod("DrawReanalysisCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(control, [context, new TreemapRectangle(node, 0, 0, size.Width, size.Height, 1), new Rect(size)]);
        return visual.Drawing;
    }

    private static IEnumerable<Drawing> AllDrawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in AllDrawings(child)) yield return nested;
    }
}

using DiskLens.Core.Layout;
using DiskLens.Core.Models;
using DiskLens.Core.Visualization;

namespace DiskLens.Tests.Layout;

public sealed class AdaptiveTreemapTests
{
    [Theory]
    [InlineData(1200, 700)]
    [InlineData(640, 360)]
    [InlineData(360, 640)]
    public void Extreme_size_differences_should_keep_all_fifty_items_visible(double width, double height)
    {
        var nodes = Enumerable.Range(0, 50).Select(index => File(index, index == 0 ? 1_000_000_000_000 : index)).ToArray();
        var rectangles = new SquarifiedTreemapLayoutEngine().Layout(Root(nodes), 0, 0, width, height, 1);

        Assert.Equal(50, rectangles.Count);
        Assert.All(rectangles, rectangle =>
        {
            Assert.True(double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height));
            Assert.True(rectangle.Width >= 18 && rectangle.Height >= 18);
            Assert.InRange(rectangle.X, 0, width);
            Assert.InRange(rectangle.Y, 0, height);
            Assert.True(rectangle.X + rectangle.Width <= width);
            Assert.True(rectangle.Y + rectangle.Height <= height);
            Assert.True(Area(rectangle) > width * height / 150);
        });
        for (var left = 0; left < rectangles.Count; left++)
        for (var right = left + 1; right < rectangles.Count; right++)
        {
            var a = rectangles[left];
            var b = rectangles[right];
            Assert.False(a.X < b.X + b.Width && a.X + a.Width > b.X
                && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y);
        }

        Assert.True(Area(rectangles[0]) > Area(rectangles[1]));
        Assert.Equal(1_000_000_000_000, rectangles[0].Node.SizeBytes);
        Assert.Empty(nodes[0].Children);
    }

    [Fact]
    public void All_zero_byte_files_should_have_equal_nonzero_area_and_repeatable_layout()
    {
        var root = Root(Enumerable.Range(0, 50).Select(index => File(index, 0)).ToArray());
        var engine = new SquarifiedTreemapLayoutEngine();
        var first = engine.Layout(root, 0, 0, 1000, 600, 1);
        var second = engine.Layout(root, 0, 0, 1000, 600, 1);

        Assert.Equal(50, first.Count);
        Assert.Equal(first, second);
        Assert.All(first, item => Assert.True(Area(item) > 9000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Planner_should_keep_fifty_plus_overflow_without_second_stage_compaction(bool scanning)
    {
        var source = Root(Enumerable.Range(0, 150).Select(index => File(index, 150 - index)).ToArray());
        var prepared = source with
        {
            Children = new VisualExplorerBuilder().Build(source, new VisualExplorerOptions())
                .Select(item => item.DisplayNode).ToArray(),
        };
        var plan = new TreemapDisplayPlanner().Plan(prepared, new TreemapDisplayPlannerOptions(
            960, 540, MaxDepth: 1, MaxVisibleChildrenPerRegion: 8, CompactionLevel: 100,
            IsScanning: scanning, IsOverviewMode: true));

        Assert.Equal(51, plan.Rectangles.Count);
        Assert.Equal(prepared.Children.Select(node => node.Path), plan.Rectangles.Select(item => item.Node.Path));
        Assert.Single(plan.Rectangles, item => DisplayNodeMetadata.IsOverflow(item.Node));
        Assert.Equal(150, source.Children.Count);
        Assert.Equal(source.SizeBytes, plan.DisplayRoot.SizeBytes);
    }

    [Fact]
    public void Empty_viewport_should_keep_the_display_tree_without_rendering_rectangles()
    {
        var root = Root([File(0, 0)]);
        var plan = new TreemapDisplayPlanner().Plan(root, new TreemapDisplayPlannerOptions(0, 400, 1, 50, 0, false, true));
        Assert.Same(root, plan.DisplayRoot);
        Assert.Empty(plan.Rectangles);
    }

    private static double Area(TreemapRectangle item) => item.Width * item.Height;

    private static FileSystemNode Root(IReadOnlyList<FileSystemNode> children) => new(
        @"E:\Data", "Data", "", children.Sum(node => node.SizeBytes), FileAttributes.Directory,
        null, null, true, false, [], children);

    private static FileSystemNode File(int index, long size) => new(
        $@"E:\Data\file-{index:000}.bin", $"file-{index:000}.bin", ".bin", size, FileAttributes.Normal,
        null, null, false, false, [], []);
}

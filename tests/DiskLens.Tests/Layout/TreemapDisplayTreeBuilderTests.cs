using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Layout;

public sealed class TreemapDisplayTreeBuilderTests
{
    [Fact]
    public void Build_should_aggregate_overflowing_children_into_a_single_directory_node()
    {
        var builder = new TreemapDisplayTreeBuilder();
        var root = CreateDirectory(
            @"C:\Data",
            "Data",
            100,
            [
                CreateFile(@"C:\Data\a.bin", "a.bin", 30),
                CreateFile(@"C:\Data\b.bin", "b.bin", 20),
                CreateFile(@"C:\Data\c.bin", "c.bin", 15),
                CreateFile(@"C:\Data\d.bin", "d.bin", 14),
                CreateFile(@"C:\Data\e.bin", "e.bin", 11),
                CreateFile(@"C:\Data\f.bin", "f.bin", 10),
            ]);

        var displayRoot = builder.Build(root, maxVisibleChildrenPerRegion: 4);

        Assert.Equal(4, displayRoot.Children.Count);
        var aggregateNode = Assert.Single(displayRoot.Children, child => child.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag));
        Assert.Equal("\u5176\u4f59 3 \u9879", aggregateNode.Name);
        Assert.Equal(35, aggregateNode.SizeBytes);
        Assert.Equal(3, aggregateNode.Children.Count);
    }

    [Fact]
    public void Build_should_not_mutate_original_tree()
    {
        var builder = new TreemapDisplayTreeBuilder();
        var root = CreateDirectory(
            @"C:\Data",
            "Data",
            100,
            [
                CreateFile(@"C:\Data\a.bin", "a.bin", 30),
                CreateFile(@"C:\Data\b.bin", "b.bin", 20),
                CreateFile(@"C:\Data\c.bin", "c.bin", 15),
                CreateFile(@"C:\Data\d.bin", "d.bin", 14),
                CreateFile(@"C:\Data\e.bin", "e.bin", 11),
                CreateFile(@"C:\Data\f.bin", "f.bin", 10),
            ]);

        _ = builder.Build(root, maxVisibleChildrenPerRegion: 4);

        Assert.Equal(6, root.Children.Count);
        Assert.DoesNotContain(root.Children, child => child.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag));
    }

    [Fact]
    public void Build_should_aggregate_small_share_children_even_when_child_count_is_within_limit()
    {
        var builder = new TreemapDisplayTreeBuilder();
        var root = CreateDirectory(
            @"C:\Users",
            "Users",
            1000,
            [
                CreateFile(@"C:\Users\AppData", "AppData", 610),
                CreateFile(@"C:\Users\Projects", "Projects", 210),
                CreateFile(@"C:\Users\Videos", "Videos", 70),
                CreateFile(@"C:\Users\Pictures", "Pictures", 45),
                CreateFile(@"C:\Users\Music", "Music", 35),
                CreateFile(@"C:\Users\Temp", "Temp", 30),
            ]);

        var displayRoot = builder.Build(
            root,
            maxVisibleChildrenPerRegion: 8,
            minVisibleChildShareOfParent: 0.1);

        Assert.Equal(3, displayRoot.Children.Count);
        Assert.Contains(displayRoot.Children, child => child.Name == "AppData");
        Assert.Contains(displayRoot.Children, child => child.Name == "Projects");

        var aggregateNode = Assert.Single(displayRoot.Children, child => child.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag));
        Assert.Equal(180, aggregateNode.SizeBytes);
        Assert.Equal(4, aggregateNode.Children.Count);
    }

    [Fact]
    public void Build_should_collapse_non_root_directory_when_readable_child_ratio_is_too_low()
    {
        var builder = new TreemapDisplayTreeBuilder();
        var nested = CreateDirectory(
            @"C:\Program Files\WindowsApps",
            "WindowsApps",
            1000,
            [
                CreateFile(@"C:\Program Files\WindowsApps\a.bin", "a.bin", 310),
                CreateFile(@"C:\Program Files\WindowsApps\b.bin", "b.bin", 250),
                CreateFile(@"C:\Program Files\WindowsApps\c.bin", "c.bin", 70),
                CreateFile(@"C:\Program Files\WindowsApps\d.bin", "d.bin", 68),
                CreateFile(@"C:\Program Files\WindowsApps\e.bin", "e.bin", 66),
                CreateFile(@"C:\Program Files\WindowsApps\f.bin", "f.bin", 64),
                CreateFile(@"C:\Program Files\WindowsApps\g.bin", "g.bin", 60),
                CreateFile(@"C:\Program Files\WindowsApps\h.bin", "h.bin", 56),
                CreateFile(@"C:\Program Files\WindowsApps\i.bin", "i.bin", 56),
            ]);

        var root = CreateDirectory(
            @"C:\Program Files",
            "Program Files",
            1000,
            [nested]);

        var displayRoot = builder.Build(
            root,
            maxVisibleChildrenPerRegion: 24,
            minVisibleChildShareOfParent: 0.15,
            minReadableChildrenRatioForExpansion: 0.45);

        var collapsedDirectory = Assert.Single(displayRoot.Children);
        Assert.Empty(collapsedDirectory.Children);
    }

    [Fact]
    public void Build_should_not_collapse_the_current_root_even_when_readable_child_ratio_is_low()
    {
        var builder = new TreemapDisplayTreeBuilder();
        var root = CreateDirectory(
            @"C:\Users\DemoUser",
            "DemoUser",
            1000,
            [
                CreateFile(@"C:\Users\DemoUser\a.bin", "a.bin", 310),
                CreateFile(@"C:\Users\DemoUser\b.bin", "b.bin", 250),
                CreateFile(@"C:\Users\DemoUser\c.bin", "c.bin", 70),
                CreateFile(@"C:\Users\DemoUser\d.bin", "d.bin", 68),
                CreateFile(@"C:\Users\DemoUser\e.bin", "e.bin", 66),
                CreateFile(@"C:\Users\DemoUser\f.bin", "f.bin", 64),
                CreateFile(@"C:\Users\DemoUser\g.bin", "g.bin", 60),
                CreateFile(@"C:\Users\DemoUser\h.bin", "h.bin", 56),
                CreateFile(@"C:\Users\DemoUser\i.bin", "i.bin", 56),
            ]);

        var displayRoot = builder.Build(
            root,
            maxVisibleChildrenPerRegion: 24,
            minVisibleChildShareOfParent: 0.15,
            minReadableChildrenRatioForExpansion: 0.45);

        Assert.NotEmpty(displayRoot.Children);
    }

    [Fact]
    public void Build_should_prune_descendants_beyond_the_requested_build_depth()
    {
        var builder = new TreemapDisplayTreeBuilder();
        var greatGrandChild = CreateDirectory(
            @"C:\Data\Top\Child\Leaf",
            "Leaf",
            40,
            [
                CreateFile(@"C:\Data\Top\Child\Leaf\a.bin", "a.bin", 40),
            ]);

        var grandChild = CreateDirectory(
            @"C:\Data\Top\Child",
            "Child",
            40,
            [greatGrandChild]);

        var child = CreateDirectory(
            @"C:\Data\Top",
            "Top",
            40,
            [grandChild]);

        var root = CreateDirectory(
            @"C:\Data",
            "Data",
            40,
            [child]);

        var displayRoot = builder.Build(
            root,
            maxVisibleChildrenPerRegion: 24,
            maxBuildDepth: 2);

        var top = Assert.Single(displayRoot.Children);
        var childNode = Assert.Single(top.Children);
        Assert.Empty(childNode.Children);
    }

    private static FileSystemNode CreateDirectory(string path, string name, long sizeBytes, IReadOnlyList<FileSystemNode> children)
    {
        return new FileSystemNode(
            Path: path,
            Name: name,
            Extension: string.Empty,
            SizeBytes: sizeBytes,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: children);
    }

    private static FileSystemNode CreateFile(string path, string name, long sizeBytes)
    {
        return new FileSystemNode(
            Path: path,
            Name: name,
            Extension: Path.GetExtension(name),
            SizeBytes: sizeBytes,
            Attributes: FileAttributes.Normal,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: false,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);
    }
}

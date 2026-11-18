using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Layout;

public sealed class TreemapDisplayReducerTests
{
    [Fact]
    public void Reduce_should_hide_descendants_of_dense_directories()
    {
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 1000,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateDenseDirectory("WindowsApps", 30, 600),
                CreateLeaf("pagefile.sys", 400, @"C:\"),
            ]);

        var engine = new SquarifiedTreemapLayoutEngine();
        var reducer = new TreemapDisplayReducer();
        var rectangles = engine.Layout(root, 0, 0, 1200, 800, maxDepth: 4);

        var reduced = reducer.Reduce(
            rectangles,
            new TreemapDisplayOptions(
                MaxVisibleChildrenPerRegion: 12,
                MinVisibleEdge: 0,
                MinVisibleArea: 0));

        Assert.Contains(reduced, rectangle => rectangle.Node.Name == "WindowsApps");
        Assert.DoesNotContain(reduced, rectangle => rectangle.Node.Path.StartsWith(@"C:\WindowsApps\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reduce_should_keep_descendants_when_directory_is_not_dense()
    {
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 1000,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateDenseDirectory("Projects", 4, 600),
                CreateLeaf("pagefile.sys", 400, @"C:\"),
            ]);

        var engine = new SquarifiedTreemapLayoutEngine();
        var reducer = new TreemapDisplayReducer();
        var rectangles = engine.Layout(root, 0, 0, 1200, 800, maxDepth: 4);

        var reduced = reducer.Reduce(
            rectangles,
            new TreemapDisplayOptions(
                MaxVisibleChildrenPerRegion: 12,
                MinVisibleEdge: 0,
                MinVisibleArea: 0));

        Assert.Contains(reduced, rectangle => rectangle.Node.Path.StartsWith(@"C:\Projects\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reduce_should_hide_descendants_when_parent_rectangle_is_too_small()
    {
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 1000,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateDenseDirectory("TinyBucket", 5, 20),
                CreateLeaf("big.bin", 980, @"C:\"),
            ]);

        var engine = new SquarifiedTreemapLayoutEngine();
        var reducer = new TreemapDisplayReducer();
        var rectangles = engine.Layout(root, 0, 0, 900, 600, maxDepth: 4)
            .Select(rectangle => rectangle.Node.Name == "TinyBucket"
                ? rectangle with { Width = 20 }
                : rectangle).ToArray();

        var reduced = reducer.Reduce(
            rectangles,
            new TreemapDisplayOptions(
                MaxVisibleChildrenPerRegion: 50,
                MinVisibleEdge: 24,
                MinVisibleArea: 1800));

        Assert.Contains(reduced, rectangle => rectangle.Node.Name == "TinyBucket");
        Assert.DoesNotContain(reduced, rectangle => rectangle.Node.Path.StartsWith(@"C:\TinyBucket\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reduce_should_hide_children_of_aggregate_nodes_when_aggregate_rectangle_is_collapsed()
    {
        var aggregateChildren = Enumerable.Range(1, 5)
            .Select(index => CreateLeaf($"child-{index}.bin", 20, @"C:\Users\DemoUser"))
            .ToArray();

        var aggregateNode = new FileSystemNode(
            Path: $@"C:\Users\DemoUser{DisplayNodeMetadata.AggregatePathMarker}5",
            Name: "其余 5 项",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [DisplayNodeMetadata.AggregateRiskTag],
            Children: aggregateChildren);

        var rectangles = new[]
        {
            new TreemapRectangle(CreateDirectory(@"C:\Users", "Users", 1000, [aggregateNode]), 0, 0, 1000, 800, 0),
            new TreemapRectangle(aggregateNode, 0, 0, 320, 260, 1),
            new TreemapRectangle(aggregateChildren[0], 10, 10, 40, 18, 2),
        };

        var reducer = new TreemapDisplayReducer();
        var reduced = reducer.Reduce(
            rectangles,
            new TreemapDisplayOptions(
                MaxVisibleChildrenPerRegion: 2,
                MinVisibleEdge: 0,
                MinVisibleArea: 0));

        Assert.Contains(reduced, rectangle => rectangle.Node.Path == aggregateNode.Path);
        Assert.DoesNotContain(reduced, rectangle => rectangle.Node.Path == aggregateChildren[0].Path);
    }

    private static FileSystemNode CreateDenseDirectory(string name, int childCount, long totalSize)
    {
        var childSize = Math.Max(1, totalSize / childCount);
        var children = Enumerable.Range(0, childCount)
            .Select(index => CreateLeaf($"child-{index}.bin", childSize, $@"C:\{name}"))
            .ToArray();

        return new FileSystemNode(
            Path: $@"C:\{name}",
            Name: name,
            Extension: string.Empty,
            SizeBytes: children.Sum(child => child.SizeBytes),
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: children);
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

    private static FileSystemNode CreateLeaf(string name, long size, string parentPath)
    {
        return new FileSystemNode(
            Path: $@"{parentPath}\{name}",
            Name: name,
            Extension: ".bin",
            SizeBytes: size,
            Attributes: FileAttributes.Normal,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: false,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);
    }
}

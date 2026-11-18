using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Layout;

public sealed class SquarifiedTreemapLayoutEngineTests
{
    [Fact]
    public void Layout_should_preserve_the_prepared_child_order_for_active_sort_mode()
    {
        var engine = new SquarifiedTreemapLayoutEngine();
        var first = CreateLeaf("z-first.bin", 30);
        var second = CreateLeaf("a-second.bin", 70);
        var root = new FileSystemNode(
            Path: @"C:\Data",
            Name: "Data",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [first, second]);

        var rectangles = engine.Layout(root, 0, 0, 100, 100, maxDepth: 1);

        Assert.Equal([first.Name, second.Name], rectangles.Select(rectangle => rectangle.Node.Name));
    }

    [Fact]
    public void Layout_should_cover_parent_area_without_overlapping_children()
    {
        var engine = new SquarifiedTreemapLayoutEngine();
        var root = new FileSystemNode(
            Path: @"C:\Data",
            Name: "Data",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateLeaf("one.bin", 70),
                CreateLeaf("two.bin", 30),
            ]);

        var rectangles = engine.Layout(root, 0, 0, 100, 100, maxDepth: 1);

        Assert.Equal(2, rectangles.Count);
        Assert.All(rectangles, rectangle => Assert.True(rectangle.Width > 0 && rectangle.Height > 0));

        var first = rectangles[0];
        var second = rectangles[1];

        Assert.False(Intersects(first, second));

        var totalArea = rectangles.Sum(rectangle => rectangle.Width * rectangle.Height);
        Assert.InRange(totalArea, 8500, 9999.9);
    }

    [Fact]
    public void Layout_should_inset_nested_children_inside_their_directory_container()
    {
        var engine = new SquarifiedTreemapLayoutEngine();
        var nestedDirectory = new FileSystemNode(
            Path: @"C:\Data\folder",
            Name: "folder",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateLeaf("one.bin", 60, @"C:\Data\folder"),
                CreateLeaf("two.bin", 40, @"C:\Data\folder"),
            ]);

        var root = new FileSystemNode(
            Path: @"C:\Data",
            Name: "Data",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [nestedDirectory]);

        var rectangles = engine.Layout(root, 0, 0, 100, 100, maxDepth: 2);

        var directoryRectangle = Assert.Single(rectangles, rectangle => rectangle.Node.Path == nestedDirectory.Path);
        var childRectangles = rectangles.Where(rectangle => rectangle.Node.Path != nestedDirectory.Path).ToList();

        Assert.Equal(2, childRectangles.Count);
        Assert.All(childRectangles, rectangle =>
        {
            Assert.True(rectangle.X > directoryRectangle.X);
            Assert.True(rectangle.Y > directoryRectangle.Y);
            Assert.True(rectangle.X + rectangle.Width < directoryRectangle.X + directoryRectangle.Width);
            Assert.True(rectangle.Y + rectangle.Height < directoryRectangle.Y + directoryRectangle.Height);
        });
    }

    [Fact]
    public void Layout_should_overlay_directory_headers_without_pushing_children_down_by_a_large_fixed_band()
    {
        var engine = new SquarifiedTreemapLayoutEngine();
        var nestedDirectory = new FileSystemNode(
            Path: @"C:\Data\Users",
            Name: "Users",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateLeaf("DemoUser.bin", 60, @"C:\Data\Users"),
                CreateLeaf("Other.bin", 40, @"C:\Data\Users"),
            ]);

        var root = new FileSystemNode(
            Path: @"C:\Data",
            Name: "Data",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [nestedDirectory]);

        var rectangles = engine.Layout(root, 0, 0, 240, 200, maxDepth: 2);
        var directoryRectangle = Assert.Single(rectangles, rectangle => rectangle.Node.Path == nestedDirectory.Path);
        var firstChildRectangle = rectangles
            .Where(rectangle => rectangle.Node.Path != nestedDirectory.Path)
            .OrderBy(rectangle => rectangle.Y)
            .First();

        Assert.True(
            firstChildRectangle.Y - directoryRectangle.Y < 12,
            $"Expected nested children to keep almost the full parent area, but top offset was still {firstChildRectangle.Y - directoryRectangle.Y:0.##}px.");

        Assert.All(
            rectangles.Where(rectangle => rectangle.Node.Path != nestedDirectory.Path),
            rectangle => Assert.True(
                rectangle.Y + rectangle.Height < directoryRectangle.Y + directoryRectangle.Height - 10,
                "Nested children should leave the directory title strip clear at the bottom."));
    }

    [Fact]
    public void Layout_should_not_turn_the_largest_top_level_directory_into_a_full_width_stripe()
    {
        var engine = new SquarifiedTreemapLayoutEngine();
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                CreateDirectory("Users", 43),
                CreateDirectory("Windows", 28),
                CreateDirectory("Program Files", 13),
                CreateDirectory("Program Files (x86)", 7),
                CreateDirectory("ProgramData", 5),
                CreateDirectory("NVIDIA", 4),
            ]);

        var rectangles = engine.Layout(root, 0, 0, 1200, 800, maxDepth: 1);
        var usersRectangle = Assert.Single(rectangles, rectangle => rectangle.Node.Name == "Users");

        Assert.True(
            usersRectangle.Width < 1140,
            $"Expected a balanced top-level partition, but largest directory still spans almost the full width: {usersRectangle.Width}");
    }

    [Fact]
    public void Layout_should_not_consume_a_large_fixed_header_band_from_single_child_directory_area()
    {
        var engine = new SquarifiedTreemapLayoutEngine();
        var userData = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data",
            Name: "User Data",
            Extension: string.Empty,
            SizeBytes: 1010,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);

        var edge = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft\Edge",
            Name: "Edge",
            Extension: string.Empty,
            SizeBytes: 1010,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [userData]);

        var root = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft",
            Name: "Microsoft",
            Extension: string.Empty,
            SizeBytes: 1010,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [edge]);

        var rectangles = engine.Layout(root, 0, 0, 900, 500, maxDepth: 2);
        var edgeRectangle = Assert.Single(rectangles, rectangle => rectangle.Node.Path == edge.Path);
        var userDataRectangle = Assert.Single(rectangles, rectangle => rectangle.Node.Path == userData.Path);

        var retainedAreaRatio = (userDataRectangle.Width * userDataRectangle.Height) / (edgeRectangle.Width * edgeRectangle.Height);
        Assert.True(
            retainedAreaRatio >= 0.9,
            $"Expected a single child directory to retain almost all of its parent's visible area, but ratio was only {retainedAreaRatio:0.###}.");
    }

    private static FileSystemNode CreateLeaf(string name, long size, string? parentPath = null)
    {
        return new FileSystemNode(
            Path: $@"{parentPath ?? @"C:\Data"}\{name}",
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

    private static FileSystemNode CreateDirectory(string name, long size)
    {
        return new FileSystemNode(
            Path: $@"C:\{name}",
            Name: name,
            Extension: string.Empty,
            SizeBytes: size,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);
    }

    private static bool Intersects(TreemapRectangle left, TreemapRectangle right)
    {
        return left.X < right.X + right.Width
            && left.X + left.Width > right.X
            && left.Y < right.Y + right.Height
            && left.Y + left.Height > right.Y;
    }
}

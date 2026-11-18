using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Layout;

public sealed class TreemapDisplayPlannerTests
{
    [Fact]
    public void Plan_should_preserve_the_same_items_in_overview_and_detail_mode()
    {
        var planner = new TreemapDisplayPlanner();
        var root = CreateDirectory(
            @"C:\Users\DemoUser",
            "DemoUser",
            1000,
            [
                CreateDirectory(@"C:\Users\DemoUser\AppData", "AppData", 320, CreateFiles(@"C:\Users\DemoUser\AppData", 10, 32)),
                CreateDirectory(@"C:\Users\DemoUser\Projects", "Projects", 210, CreateFiles(@"C:\Users\DemoUser\Projects", 7, 30)),
                CreateDirectory(@"C:\Users\DemoUser\Games", "Games", 180, CreateFiles(@"C:\Users\DemoUser\Games", 9, 20)),
                CreateDirectory(@"C:\Users\DemoUser\Temp", "Temp", 120, CreateFiles(@"C:\Users\DemoUser\Temp", 12, 10)),
                CreateDirectory(@"C:\Users\DemoUser\Tools", "Tools", 90, CreateFiles(@"C:\Users\DemoUser\Tools", 8, 10)),
                CreateDirectory(@"C:\Users\DemoUser\Misc", "Misc", 80, CreateFiles(@"C:\Users\DemoUser\Misc", 10, 8)),
            ]);

        var overviewPlan = planner.Plan(root, new TreemapDisplayPlannerOptions(
            ViewportWidth: 1200,
            ViewportHeight: 800,
            MaxDepth: 4,
            MaxVisibleChildrenPerRegion: 24,
            CompactionLevel: 58,
            IsScanning: false,
            IsOverviewMode: true));

        var detailPlan = planner.Plan(root, new TreemapDisplayPlannerOptions(
            ViewportWidth: 1200,
            ViewportHeight: 800,
            MaxDepth: 4,
            MaxVisibleChildrenPerRegion: 24,
            CompactionLevel: 58,
            IsScanning: false,
            IsOverviewMode: false));

        Assert.Equal(overviewPlan.Rectangles, detailPlan.Rectangles);
    }

    [Fact]
    public void Plan_should_not_hide_items_while_scanning()
    {
        var planner = new TreemapDisplayPlanner();
        var root = CreateDirectory(
            @"C:\Data",
            "Data",
            1000,
            [
                CreateDirectory(@"C:\Data\A", "A", 310, CreateFiles(@"C:\Data\A", 16, 12)),
                CreateDirectory(@"C:\Data\B", "B", 280, CreateFiles(@"C:\Data\B", 14, 12)),
                CreateDirectory(@"C:\Data\C", "C", 190, CreateFiles(@"C:\Data\C", 11, 12)),
                CreateDirectory(@"C:\Data\D", "D", 120, CreateFiles(@"C:\Data\D", 8, 12)),
                CreateDirectory(@"C:\Data\E", "E", 100, CreateFiles(@"C:\Data\E", 10, 10)),
            ]);

        var idlePlan = planner.Plan(root, new TreemapDisplayPlannerOptions(
            ViewportWidth: 1280,
            ViewportHeight: 820,
            MaxDepth: 4,
            MaxVisibleChildrenPerRegion: 24,
            CompactionLevel: 58,
            IsScanning: false,
            IsOverviewMode: true));

        var scanningPlan = planner.Plan(root, new TreemapDisplayPlannerOptions(
            ViewportWidth: 1280,
            ViewportHeight: 820,
            MaxDepth: 4,
            MaxVisibleChildrenPerRegion: 24,
            CompactionLevel: 58,
            IsScanning: true,
            IsOverviewMode: true));

        Assert.Equal(idlePlan.Rectangles, scanningPlan.Rectangles);
    }

    private static IReadOnlyList<FileSystemNode> CreateFiles(string parentPath, int count, long sizeBytes)
    {
        return Enumerable.Range(1, count)
            .Select(index => CreateFile($@"{parentPath}\file-{index}.bin", $"file-{index}.bin", sizeBytes))
            .ToArray();
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

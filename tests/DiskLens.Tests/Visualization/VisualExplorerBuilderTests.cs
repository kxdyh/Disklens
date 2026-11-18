using DiskLens.Core.Models;
using DiskLens.Core.Visualization;

namespace DiskLens.Tests.Visualization;

public sealed class VisualExplorerBuilderTests
{
    [Fact]
    public void Build_should_keep_small_and_zero_byte_items_without_threshold_merging()
    {
        var children = new[]
        {
            CreateDirectory(@"E:\Data\small", "small", 1, []),
            CreateFile(@"E:\Data\empty.log", "empty.log", 0),
            CreateFile(@"E:\Data\huge.bin", "huge.bin", 1_000_000_000_000),
        };
        var root = CreateDirectory(@"E:\Data", "Data", children.Sum(node => node.SizeBytes), children);
        var items = new VisualExplorerBuilder().Build(root, new VisualExplorerOptions(long.MaxValue));

        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, item => item.Kind == VisualExplorerItemKind.Aggregate);
        Assert.Equal(children.OrderByDescending(node => node.SizeBytes).Select(node => node.Path),
            items.Select(item => item.DisplayNode.Path));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(49, 49, 0)]
    [InlineData(50, 50, 0)]
    [InlineData(51, 50, 1)]
    [InlineData(200, 50, 150)]
    public void Build_should_apply_one_limit_to_files_and_directories_together(int count, int visible, int remaining)
    {
        var children = Enumerable.Range(1, count).Select(index => index % 2 == 0
            ? CreateDirectory($@"E:\Data\folder-{index:000}", $"folder-{index:000}", index, [])
            : CreateFile($@"E:\Data\file-{index:000}.bin", $"file-{index:000}.bin", index)).ToArray();
        var root = CreateDirectory(@"E:\Data", "Data", children.Sum(node => node.SizeBytes), children);
        var items = new VisualExplorerBuilder().Build(root, new VisualExplorerOptions());
        var regular = items.Where(item => !DisplayNodeMetadata.IsOverflow(item.DisplayNode)).ToArray();

        Assert.Equal(visible, regular.Length);
        Assert.Equal(children.OrderByDescending(node => node.SizeBytes).Take(50).Select(node => node.Path),
            regular.Select(item => item.DisplayNode.Path));
        if (remaining == 0)
        {
            Assert.DoesNotContain(items, item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
            return;
        }

        var overflow = Assert.Single(items, item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
        Assert.Equal($"其余 {remaining:N0} 项", overflow.DisplayNode.Name);
        Assert.Equal(remaining, overflow.DisplayNode.Children.Count);
        Assert.Empty(overflow.Children);
        Assert.Equal(children.OrderByDescending(node => node.SizeBytes).Skip(50).Sum(node => node.SizeBytes), overflow.DisplayNode.SizeBytes);
        Assert.Equal(@"E:\Data", DisplayNodeMetadata.GetOverflowParentPath(overflow.DisplayNode));
        Assert.Equal(@"E:\Data", DisplayNodeMetadata.GetOverflowNavigationParentPath(overflow.DisplayNode));
        Assert.DoesNotContain(DisplayNodeMetadata.AggregatePathMarker, overflow.TooltipPathText);
        Assert.Contains("点击继续查看", overflow.TooltipPathText);
        Assert.Equal($"点击继续查看其中最大的 {Math.Min(50, remaining):N0} 项", overflow.SubtitleText);
        Assert.Equal(count, root.Children.Count);
    }

    [Fact]
    public void Build_should_preserve_drive_root_for_overflow_location()
    {
        var children = Enumerable.Range(1, 51).Select(index => CreateFile($@"E:\{index}.bin", $"{index}.bin", index)).ToArray();
        var root = CreateDirectory(@"E:\", "E:", 1326, children);
        var overflow = Assert.Single(new VisualExplorerBuilder().Build(root, new VisualExplorerOptions()),
            item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
        Assert.Equal(@"E:\", DisplayNodeMetadata.GetOverflowParentPath(overflow.DisplayNode));
        Assert.Equal("上级目录：E:\\\n点击继续查看其中最大的 1 项", overflow.TooltipPathText);
    }

    [Fact]
    public void Build_should_limit_each_nested_folder_independently_and_sort_ties_stably()
    {
        var files = Enumerable.Range(1, 60).Select(index => CreateFile($@"E:\Data\folder\{index:000}.bin", $"{index:000}.bin", 10)).ToArray();
        var folder = CreateDirectory(@"E:\Data\folder", "folder", 600, files.Reverse().ToArray());
        var root = CreateDirectory(@"E:\Data", "Data", 600, [folder]);
        var items = new VisualExplorerBuilder().Build(root, new VisualExplorerOptions(MaxDepth: 2));
        var children = Assert.Single(items).Children;
        Assert.Equal(51, children.Count);
        Assert.Equal("001.bin", children[0].DisplayNode.Name);
        Assert.Equal("050.bin", children[49].DisplayNode.Name);
        Assert.Equal(10, children[50].DisplayNode.Children.Count);
    }

    [Theory]
    [InlineData(51)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(150)]
    [InlineData(151)]
    [InlineData(173)]
    [InlineData(200)]
    public void Build_should_page_through_every_item_once_and_preserve_sizes(int count)
    {
        var children = Enumerable.Range(1, count).Select(index =>
        {
            var node = index % 2 == 0
                ? CreateDirectory($@"E:\Data\item-{index:000}", $"item-{index:000}", index % 13, [])
                : CreateFile($@"E:\Data\item-{index:000}.bin", $"item-{index:000}.bin", index % 13);
            return node with { LogicalSizeBytes = index % 11 == 0 ? null : index * 100L };
        }).Reverse().ToArray();
        var root = CreateDirectory(@"E:\Data", "Data", children.Sum(node => node.SizeBytes), children) with
        {
            LogicalSizeBytes = children.Sum(node => node.LogicalSizeBytes ?? node.SizeBytes),
        };
        var ordered = children.OrderByDescending(node => node.SizeBytes)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Path, StringComparer.Ordinal).ToArray();
        var builder = new VisualExplorerBuilder();
        var pageRoot = root;
        var visitedPaths = new List<string>();
        var pagePaths = new HashSet<string>(StringComparer.Ordinal) { root.Path };

        while (true)
        {
            var items = builder.Build(pageRoot, new VisualExplorerOptions(MaxDepth: 12));
            var regular = items.Where(item => !DisplayNodeMetadata.IsOverflow(item.DisplayNode)).ToArray();
            Assert.Equal(ordered.Skip(visitedPaths.Count).Take(50).Select(node => node.Path),
                regular.Select(item => item.DisplayNode.Path));
            Assert.Equal(pageRoot.SizeBytes, items.Sum(item => item.DisplayNode.SizeBytes));
            Assert.Equal(pageRoot.LogicalSizeBytes,
                items.Sum(item => item.DisplayNode.LogicalSizeBytes ?? item.DisplayNode.SizeBytes));
            visitedPaths.AddRange(regular.Select(item => item.DisplayNode.Path));

            var overflow = items.SingleOrDefault(item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
            if (overflow is null)
            {
                Assert.InRange(items.Count, 1, 50);
                break;
            }

            Assert.Equal(51, items.Count);
            Assert.Empty(overflow.Children);
            Assert.Equal($"其余 {count - visitedPaths.Count:N0} 项", overflow.DisplayNode.Name);
            Assert.Equal(ordered.Skip(visitedPaths.Count).Select(node => node.Path),
                overflow.DisplayNode.Children.Select(node => node.Path));
            Assert.Equal(pageRoot.Path, DisplayNodeMetadata.GetOverflowNavigationParentPath(overflow.DisplayNode));
            Assert.Equal(root.Path, DisplayNodeMetadata.GetOverflowParentPath(overflow.DisplayNode));
            Assert.True(pagePaths.Add(overflow.DisplayNode.Path));
            pageRoot = overflow.DisplayNode;
        }

        Assert.Equal(ordered.Select(node => node.Path), visitedPaths);
        Assert.Equal(count, visitedPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Same(children, root.Children);
    }

    [Fact]
    public void Build_should_preserve_unknown_logical_size_on_every_overflow_page()
    {
        var children = Enumerable.Range(1, 151)
            .Select(index => CreateFile($@"E:\Data\{index}.bin", $"{index}.bin", index)).ToArray();
        var pageRoot = CreateDirectory(@"E:\Data", "Data", children.Sum(node => node.SizeBytes), children);
        var builder = new VisualExplorerBuilder();

        for (var page = 0; page < 3; page++)
        {
            var overflow = Assert.Single(builder.Build(pageRoot, new VisualExplorerOptions()),
                item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
            Assert.Null(overflow.DisplayNode.LogicalSizeBytes);
            pageRoot = overflow.DisplayNode;
        }

        Assert.Single(builder.Build(pageRoot, new VisualExplorerOptions()));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"E:\", @"E:\")]
    [InlineData(@"E:\Data\", @"E:\Data")]
    [InlineData(@"\\server\share\", @"\\server\share\")]
    public void Build_should_use_stable_nested_page_paths_and_keep_physical_directory_separate(
        string rootPath, string normalizedRootPath)
    {
        var children = Enumerable.Range(1, 151)
            .Select(index => CreateFile(Path.Join(rootPath, $"{index}.bin"), $"{index}.bin", index)).ToArray();
        var root = CreateDirectory(rootPath, "root", children.Sum(node => node.SizeBytes), children);
        var builder = new VisualExplorerBuilder();
        var firstPage = Assert.Single(builder.Build(root, new VisualExplorerOptions()),
            item => DisplayNodeMetadata.IsOverflow(item.DisplayNode)).DisplayNode;
        var secondPage = Assert.Single(builder.Build(firstPage, new VisualExplorerOptions()),
            item => DisplayNodeMetadata.IsOverflow(item.DisplayNode)).DisplayNode;
        var thirdPage = Assert.Single(builder.Build(secondPage, new VisualExplorerOptions()),
            item => DisplayNodeMetadata.IsOverflow(item.DisplayNode)).DisplayNode;
        var pageSuffix = DisplayNodeMetadata.AggregatePathMarker + "overflow";

        Assert.Equal(normalizedRootPath + pageSuffix, firstPage.Path);
        Assert.Equal(firstPage.Path + pageSuffix, secondPage.Path);
        Assert.Equal(secondPage.Path + pageSuffix, thirdPage.Path);
        Assert.Equal(normalizedRootPath, DisplayNodeMetadata.GetOverflowNavigationParentPath(firstPage));
        Assert.Equal(firstPage.Path, DisplayNodeMetadata.GetOverflowNavigationParentPath(secondPage));
        Assert.Equal(secondPage.Path, DisplayNodeMetadata.GetOverflowNavigationParentPath(thirdPage));
        Assert.Equal(normalizedRootPath, DisplayNodeMetadata.GetOverflowParentPath(thirdPage));
        Assert.Equal(firstPage.Path, Assert.Single(builder.Build(root, new VisualExplorerOptions()),
            item => DisplayNodeMetadata.IsOverflow(item.DisplayNode)).DisplayNode.Path);
        Assert.Contains(normalizedRootPath, Assert.Single(builder.Build(secondPage, new VisualExplorerOptions()),
            item => DisplayNodeMetadata.IsOverflow(item.DisplayNode)).TooltipPathText);
    }

    [Fact]
    public void Overflow_parent_helpers_should_ignore_regular_nodes_and_missing_markers()
    {
        var ordinary = CreateDirectory(@"E:\Data", "Data", 0, []);
        var malformed = ordinary with { RiskTags = [DisplayNodeMetadata.OverflowRiskTag] };

        Assert.Null(DisplayNodeMetadata.GetOverflowParentPath(ordinary));
        Assert.Null(DisplayNodeMetadata.GetOverflowNavigationParentPath(ordinary));
        Assert.Null(DisplayNodeMetadata.GetOverflowParentPath(malformed));
        Assert.Null(DisplayNodeMetadata.GetOverflowNavigationParentPath(malformed));
    }

    [Fact]
    public void Build_should_expand_only_the_single_selected_branch_without_switching_the_page_root()
    {
        var office16 = CreateDirectory(
            @"C:\root\Microsoft Office\Office16",
            "Office16",
            2100,
            []);

        var vfs = CreateDirectory(
            @"C:\root\Microsoft Office\vfs",
            "vfs",
            2000,
            []);

        var root = CreateDirectory(
            @"C:\root",
            "root",
            7900,
            [
                CreateDirectory(
                    @"C:\root\Microsoft Office",
                    "Microsoft Office",
                    4300,
                    [
                        office16,
                        vfs,
                    ]),
                CreateDirectory(@"C:\root\Docker", "Docker", 3600, []),
            ]);

        var builder = new VisualExplorerBuilder();

        var items = builder.Build(root, new VisualExplorerOptions(
            FolderDisplayThresholdBytes: 512,
            MaxDepth: 3, ExpandedPath: @"C:\root\Microsoft Office\Office16"));

        var office = Assert.Single(items, item => string.Equals(item.DisplayNode.Path, @"C:\root\Microsoft Office", StringComparison.OrdinalIgnoreCase));
        var docker = Assert.Single(items, item => string.Equals(item.DisplayNode.Path, @"C:\root\Docker", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(office.Children);
        Assert.Empty(docker.Children);
        Assert.Contains(office.Children, child => string.Equals(child.DisplayNode.Path, @"C:\root\Microsoft Office\Office16", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(office.Children, child => string.Equals(child.DisplayNode.Path, @"C:\root\Microsoft Office\vfs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_should_show_direct_files_inside_the_expanded_directory()
    {
        var root = CreateDirectory(
            @"C:\root",
            "root",
            3200,
            [
                CreateDirectory(
                    @"C:\root\Cache",
                    "Cache",
                    3200,
                    [
                        CreateDirectory(@"C:\root\Cache\Logs", "Logs", 2200, []),
                        CreateFile(@"C:\root\Cache\dump.bin", "dump.bin", 64),
                    ]),
            ]);

        var builder = new VisualExplorerBuilder();

        var items = builder.Build(root, new VisualExplorerOptions(
            FolderDisplayThresholdBytes: 1024,
            MaxDepth: 2, ExpandedPath: @"C:\root\Cache"));

        var cache = Assert.Single(items);
        Assert.Contains(cache.Children, child => child.Kind == VisualExplorerItemKind.Directory && string.Equals(child.DisplayNode.Path, @"C:\root\Cache\Logs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cache.Children, child => child.Kind == VisualExplorerItemKind.File && string.Equals(child.DisplayNode.Path, @"C:\root\Cache\dump.bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_should_ignore_the_legacy_file_limit_and_show_fifty_plus_overflow()
    {
        var files = Enumerable.Range(1, 120)
            .Select(index => CreateFile($@"C:\root\Cache\file-{index:000}.bin", $"file-{index:000}.bin", index))
            .ToArray();
        var root = CreateDirectory(
            @"C:\root",
            "root",
            files.Sum(file => file.SizeBytes),
            files);

        var builder = new VisualExplorerBuilder();

        var items = builder.Build(root, new VisualExplorerOptions(FolderDisplayThresholdBytes: 1024));

        Assert.Equal(50, items.Count(item => item.Kind == VisualExplorerItemKind.File));
        var aggregate = Assert.Single(items, item => item.Kind == VisualExplorerItemKind.Aggregate);
        Assert.Equal(70, aggregate.DisplayNode.Children.Count);
        Assert.Equal(files.OrderByDescending(file => file.SizeBytes).Skip(50).Sum(file => file.SizeBytes), aggregate.DisplayNode.SizeBytes);
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

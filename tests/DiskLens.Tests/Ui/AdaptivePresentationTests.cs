using DiskLens.App.Models;
using DiskLens.Core.Models;
using DiskLens.Core.Visualization;

namespace DiskLens.Tests.Ui;

public sealed class AdaptivePresentationTests
{
    [Theory]
    [InlineData("文件", 15)]
    [InlineData("文件夹", 15)]
    public void Filtered_overflow_should_report_only_the_matching_items(string filter, int count)
    {
        var children = Enumerable.Range(0, 80).Select(index => new FileSystemNode(
            $@"E:\Data\item-{index:000}", $"item-{index:000}", "", index,
            index % 2 == 0 ? FileAttributes.Directory : FileAttributes.Normal,
            null, null, index % 2 == 0, false, [], [])).ToArray();
        var root = new FileSystemNode(@"E:\Data", "Data", "", children.Sum(node => node.SizeBytes),
            FileAttributes.Directory, null, null, true, false, [], children);
        var input = new VisualExplorerBuilder().Build(root, new VisualExplorerOptions());
        var result = VisualExplorerPresentation.Apply(input, "大小", filter);
        var overflowItem = Assert.Single(result, item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
        var overflow = overflowItem.DisplayNode;
        var expected = children.OrderByDescending(node => node.SizeBytes).Skip(50)
            .Where(node => node.IsDirectory == (filter == "文件夹")).ToArray();
        Assert.Equal(count, overflow.Children.Count);
        Assert.Equal($"其余 {count:N0} 项", overflow.Name);
        Assert.Equal(expected.Sum(node => node.SizeBytes), overflow.SizeBytes);
        Assert.Equal($"{expected.Sum(node => node.SizeBytes)} B", overflowItem.SizeText);
        Assert.Equal($"点击继续查看其中最大的 {count:N0} 项", overflowItem.SubtitleText);
        Assert.Equal($"上级目录：{root.Path}\n{overflowItem.SubtitleText}", overflowItem.TooltipPathText);
        Assert.Equal(expected.Select(node => node.Path), overflow.Children.Select(node => node.Path));
        Assert.All(overflow.Children, node => Assert.Equal(filter == "文件夹", node.IsDirectory));
        Assert.Equal(30, input[^1].DisplayNode.Children.Count);
    }

    [Theory]
    [InlineData(false, "文件")]
    [InlineData(true, "文件夹")]
    public void Type_filter_should_keep_the_overflow_shortcut_for_matching_remaining_items(bool directories, string filter)
    {
        var children = Enumerable.Range(0, 80).Select(index => new FileSystemNode(
            $@"E:\Data\item-{index:000}", $"item-{index:000}", "", index,
            directories ? FileAttributes.Directory : FileAttributes.Normal,
            null, null, directories, false, [], [])).ToArray();
        var root = new FileSystemNode(@"E:\Data", "Data", "", children.Sum(node => node.SizeBytes),
            FileAttributes.Directory, null, null, true, false, [], children);
        var input = new VisualExplorerBuilder().Build(root, new VisualExplorerOptions());

        var result = VisualExplorerPresentation.Apply(input, "名称", filter);
        Assert.Equal(51, result.Count);
        Assert.True(DisplayNodeMetadata.IsOverflow(result[^1].DisplayNode));
        Assert.Equal(30, result[^1].DisplayNode.Children.Count);
        Assert.Equal(80, root.Children.Count);
        Assert.Equal(50, input.Count(item => !DisplayNodeMetadata.IsOverflow(item.DisplayNode)));
    }

    [Theory]
    [InlineData("文件", "大小")]
    [InlineData("文件夹", "大小")]
    [InlineData("文件", "名称")]
    [InlineData("文件夹", "名称")]
    public void Filtered_overflow_should_page_through_its_remaining_collection_once(string filter, string sort)
    {
        var children = Enumerable.Range(0, 373).Select(index => new FileSystemNode(
            $@"E:\Data\item-{index:000}", $"item-{index:000}", "", index % 17,
            index % 2 == 0 ? FileAttributes.Directory : FileAttributes.Normal,
            null, null, index % 2 == 0, false, [], [])
        {
            LogicalSizeBytes = index * 2L,
        }).Reverse().ToArray();
        var root = new FileSystemNode(@"E:\Data", "Data", "", children.Sum(node => node.SizeBytes),
            FileAttributes.Directory, null, null, true, false, [], children)
        {
            LogicalSizeBytes = children.Sum(node => node.LogicalSizeBytes),
        };
        var builder = new VisualExplorerBuilder();
        var initialItems = VisualExplorerPresentation.Apply(builder.Build(root, new VisualExplorerOptions()), sort, filter);
        var firstOverflow = Assert.Single(initialItems, item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
        var expected = children.OrderByDescending(node => node.SizeBytes)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Path, StringComparer.Ordinal)
            .Skip(50).Where(node => node.IsDirectory == (filter == "文件夹")).ToArray();
        Assert.Equal(expected.Select(node => node.Path), firstOverflow.DisplayNode.Children.Select(node => node.Path));
        var pageRoot = firstOverflow.DisplayNode;
        var visitedPaths = new List<string>();
        var pagePaths = new HashSet<string>(StringComparer.Ordinal) { pageRoot.Path };

        while (true)
        {
            var items = VisualExplorerPresentation.Apply(builder.Build(pageRoot, new VisualExplorerOptions()), sort, filter);
            var regular = items.Where(item => !DisplayNodeMetadata.IsOverflow(item.DisplayNode)).ToArray();
            var expectedPage = expected.Skip(visitedPaths.Count).Take(50);
            Assert.Equal(expectedPage.Select(node => node.Path).OrderBy(path => path, StringComparer.Ordinal),
                regular.Select(item => item.DisplayNode.Path).OrderBy(path => path, StringComparer.Ordinal));
            Assert.All(regular, item => Assert.Equal(filter == "文件夹", item.DisplayNode.IsDirectory));
            visitedPaths.AddRange(regular.Select(item => item.DisplayNode.Path));
            Assert.Equal(pageRoot.SizeBytes, items.Sum(item => item.DisplayNode.SizeBytes));
            Assert.Equal(pageRoot.LogicalSizeBytes, items.Sum(item => item.DisplayNode.LogicalSizeBytes));

            var nextOverflow = items.SingleOrDefault(item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));
            if (nextOverflow is null)
            {
                Assert.InRange(items.Count, 1, 50);
                break;
            }

            Assert.Equal(51, items.Count);
            Assert.Equal(expected.Skip(visitedPaths.Count).Select(node => node.Path),
                nextOverflow.DisplayNode.Children.Select(node => node.Path));
            Assert.Equal($"其余 {expected.Length - visitedPaths.Count:N0} 项", nextOverflow.DisplayNode.Name);
            Assert.Equal($"点击继续查看其中最大的 {Math.Min(50, expected.Length - visitedPaths.Count):N0} 项",
                nextOverflow.SubtitleText);
            Assert.Equal(pageRoot.Path, DisplayNodeMetadata.GetOverflowNavigationParentPath(nextOverflow.DisplayNode));
            Assert.True(pagePaths.Add(nextOverflow.DisplayNode.Path));
            Assert.Empty(nextOverflow.Children);
            pageRoot = nextOverflow.DisplayNode;
        }

        Assert.Equal(expected.Select(node => node.Path).OrderBy(path => path, StringComparer.Ordinal),
            visitedPaths.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(expected.Length, visitedPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(373, root.Children.Count);
        Assert.Equal(expected.Length, firstOverflow.DisplayNode.Children.Count);
    }
}

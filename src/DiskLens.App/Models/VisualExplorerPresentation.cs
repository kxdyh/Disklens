using DiskLens.Core.Visualization;

namespace DiskLens.App.Models;

public static class VisualExplorerPresentation
{
    public const string DefaultSortMode = "大小";
    public const string DefaultFilterMode = "全部项目";

    public static IReadOnlyList<VisualExplorerItem> Apply(
        IEnumerable<VisualExplorerItem> items,
        string? sortMode,
        string? filterMode,
        string? selectedPath = null)
    {
        return ApplyCore(items, sortMode, filterMode, selectedPath);
    }

    private static IReadOnlyList<VisualExplorerItem> ApplyCore(
        IEnumerable<VisualExplorerItem> items,
        string? sortMode,
        string? filterMode,
        string? selectedPath)
    {
        var projected = items
            .Select(item => ProjectItem(item, sortMode, filterMode, selectedPath))
            .Where(item => MatchesFilter(item, filterMode));

        IOrderedEnumerable<VisualExplorerItem> ordered = sortMode switch
        {
            "名称" => projected
                .OrderBy(item => item.DisplayNode.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DisplayNode.Name, StringComparer.Ordinal),
            "项目数" => projected.OrderByDescending(GetItemCount).ThenByDescending(item => item.DisplayNode.SizeBytes),
            _ => projected.OrderByDescending(item => item.DisplayNode.SizeBytes),
        };

        return ordered
            .OrderBy(item => DiskLens.Core.Models.DisplayNodeMetadata.IsOverflow(item.DisplayNode))
            .Select(item => item with
            {
                IsSelected = !string.IsNullOrWhiteSpace(selectedPath)
                    && string.Equals(item.DisplayNode.Path, selectedPath, StringComparison.Ordinal),
            })
            .ToArray();
    }

    private static VisualExplorerItem ProjectItem(VisualExplorerItem item, string? sortMode, string? filterMode, string? selectedPath)
    {
        var node = FilterOverflowItems(item.DisplayNode, filterMode);
        var isOverflow = DiskLens.Core.Models.DisplayNodeMetadata.IsOverflow(node);
        var subtitle = isOverflow
            ? L.F($"点击继续查看其中最大的 {Math.Min(VisualExplorerBuilder.MaximumVisibleItemsPerFolder, node.Children.Count):N0} 项")
            : L.T(item.SubtitleText);
        return item with
        {
            Children = ApplyCore(item.Children, sortMode, filterMode, selectedPath),
            DisplayNode = node,
            SizeText = isOverflow ? FormatBytes(node.SizeBytes) : item.SizeText,
            SubtitleText = subtitle,
            TooltipPathText = isOverflow
                ? L.F($"上级目录：{DiskLens.Core.Models.DisplayNodeMetadata.GetOverflowParentPath(node)}\n{subtitle}")
                : item.TooltipPathText,
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static DiskLens.Core.Models.FileSystemNode FilterOverflowItems(DiskLens.Core.Models.FileSystemNode node, string? filter)
    {
        if (!DiskLens.Core.Models.DisplayNodeMetadata.IsOverflow(node) || filter is not ("文件" or "文件夹"))
        {
            return node;
        }
        var children = node.Children.Where(child => child.IsDirectory == (filter == "文件夹")).ToArray();
        return node with
        {
            Name = L.F($"其余 {children.Length:N0} 项"),
            Children = children,
            SizeBytes = children.Sum(child => child.SizeBytes),
            LogicalSizeBytes = node.LogicalSizeBytes is null ? null
                : children.Sum(child => child.LogicalSizeBytes ?? child.SizeBytes),
        };
    }

    private static bool MatchesFilter(VisualExplorerItem item, string? filterMode)
    {
        if (DiskLens.Core.Models.DisplayNodeMetadata.IsOverflow(item.DisplayNode))
        {
            return filterMode switch
            {
                "文件" => item.DisplayNode.Children.Any(child => !child.IsDirectory),
                "文件夹" => item.DisplayNode.Children.Any(child => child.IsDirectory),
                _ => true,
            };
        }

        return filterMode switch
        {
            "文件夹" => item.Kind == VisualExplorerItemKind.Directory || item.Children.Count > 0,
            "文件" => item.Kind == VisualExplorerItemKind.File || item.Children.Count > 0,
            "更多项目" or "聚合块" => item.Kind == VisualExplorerItemKind.Aggregate || item.Children.Count > 0,
            _ => true,
        };
    }

    private static long GetItemCount(VisualExplorerItem item)
    {
        return (long)item.DisplayNode.DescendantFileCount + item.DisplayNode.DescendantDirectoryCount;
    }
}

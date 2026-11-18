using DiskLens.Core.Models;

namespace DiskLens.Core.Visualization;

public sealed class VisualExplorerBuilder
{
    public const int MaximumVisibleItemsPerFolder = 50;

    // A conclusion search spans the full scanned subtree, not the bounded folder preview.
    // Flatten only the display copies; navigation and file actions still resolve the real path.
    public IReadOnlyList<VisualExplorerItem> BuildFlatMatches(FileSystemNode root, Func<FileSystemNode, bool> matches)
    {
        var candidates = root.IsDirectory
            ? root.Children.SelectMany(child => child.DescendantsAndSelf())
            : root.DescendantsAndSelf();
        var nodes = candidates
            .Where(node => !node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag) && matches(node))
            .DistinctBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(node => node.SizeBytes)
            .ThenBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return nodes.Select((node, index) =>
        {
            var item = BuildItem(node, expandedPath: null, maxDepth: 1, depth: 0,
                index, nodes.Length, GetRootPalette(node, index));
            return item with { DisplayNode = node with { Children = [] } };
        }).ToArray();
    }

    public IReadOnlyList<VisualExplorerItem> Build(FileSystemNode root, VisualExplorerOptions options)
    {
        var maxDepth = Math.Clamp(options.MaxDepth, 1, 12);
        if (!root.IsDirectory)
        {
            return [BuildItem(root, options.ExpandedPath, maxDepth, depth: 0, index: 0, siblingCount: 1, VisualExplorerPalette.Other)];
        }

        return BuildItems(root, options.ExpandedPath, maxDepth, depth: 0, inheritedPalette: null);
    }

    private IReadOnlyList<VisualExplorerItem> BuildItems(
        FileSystemNode parent,
        string? expandedPath,
        int maxDepth,
        int depth,
        VisualExplorerPalette? inheritedPalette)
    {
        var displayChildren = BuildDisplayChildren(parent);
        return displayChildren
            .Select((child, index) =>
            {
                var palette = inheritedPalette ?? GetRootPalette(child, index);
                return BuildItem(child, expandedPath, maxDepth, depth, index, displayChildren.Count, palette);
            })
            .ToArray();
    }

    private VisualExplorerItem BuildItem(
        FileSystemNode node,
        string? expandedPath,
        int maxDepth,
        int depth,
        int index,
        int siblingCount,
        VisualExplorerPalette palette)
    {
        var isExpanded = IsOnExpandedBranch(node, expandedPath);
        var children = node.IsDirectory
                       && !DisplayNodeMetadata.IsOverflow(node)
                       && node.Children.Count > 0
                       && depth < maxDepth - 1
            ? BuildItems(node, expandedPath, maxDepth, depth + 1, palette)
            : [];

        return new VisualExplorerItem(
            DisplayNode: node,
            Kind: GetKind(node),
            Weight: GetWeight(depth, index, siblingCount),
            Depth: depth,
            SizeText: FormatBytes(node.SizeBytes),
            SubtitleText: CreateSubtitleText(node),
            TooltipPathText: CreateTooltipPathText(node),
            IsExpanded: isExpanded,
            Children: children)
        {
            Palette = GetTilePalette(node, depth, palette),
        };
    }

    private static VisualExplorerPalette GetRootPalette(FileSystemNode node, int index)
    {
        if (!node.IsDirectory || node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            return VisualExplorerPalette.Other;
        }

        var name = node.Name.Trim();
        if (name.Equals("Users", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Documents and Settings", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Home", StringComparison.OrdinalIgnoreCase))
        {
            return VisualExplorerPalette.UserSpace;
        }

        if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
        {
            return VisualExplorerPalette.System;
        }

        if (name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Public", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Shared", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Common", StringComparison.OrdinalIgnoreCase))
        {
            return VisualExplorerPalette.Shared;
        }

        if (name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Applications", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Apps", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Games", StringComparison.OrdinalIgnoreCase))
        {
            return VisualExplorerPalette.Applications;
        }

        return index % 2 == 0 ? VisualExplorerPalette.Other : VisualExplorerPalette.Shared;
    }

    private static VisualExplorerPalette GetTilePalette(FileSystemNode node, int depth, VisualExplorerPalette regionPalette)
    {
        if (depth == 0)
        {
            return regionPalette;
        }

        if (!node.IsDirectory)
        {
            return regionPalette;
        }

        if (regionPalette is VisualExplorerPalette.UserSpace)
        {
            return node.Name.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("Games", StringComparison.OrdinalIgnoreCase)
                ? VisualExplorerPalette.UserMoss
                : node.Name.Equals("Documents", StringComparison.OrdinalIgnoreCase)
                    ? VisualExplorerPalette.UserSlate
                    : VisualExplorerPalette.UserFolder;
        }

        return regionPalette switch
        {
            VisualExplorerPalette.System => VisualExplorerPalette.SystemFolder,
            VisualExplorerPalette.Applications => VisualExplorerPalette.ApplicationsFolder,
            VisualExplorerPalette.Shared => VisualExplorerPalette.SharedFolder,
            _ => VisualExplorerPalette.OtherFolder,
        };
    }

    private static IReadOnlyList<FileSystemNode> BuildDisplayChildren(FileSystemNode parent)
    {
        if (!parent.IsDirectory || parent.Children.Count == 0)
        {
            return [];
        }

        var ordered = parent.Children
            .OrderByDescending(child => child.SizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.Path, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length <= MaximumVisibleItemsPerFolder)
        {
            return ordered;
        }

        var visible = ordered.Take(MaximumVisibleItemsPerFolder).ToList();
        var remainder = ordered.Skip(MaximumVisibleItemsPerFolder).ToArray();
        visible.Add(CreateAggregateNode(parent, $"其余 {remainder.Length:N0} 项", "overflow", remainder, remainder.Sum(child => child.SizeBytes)));
        return visible;
    }

    private static FileSystemNode CreateAggregateNode(
        FileSystemNode parent,
        string name,
        string key,
        IReadOnlyList<FileSystemNode> children,
        long sizeBytes)
    {
        var normalizedParentPath = parent.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentRoot = Path.GetPathRoot(parent.Path);
        if (!string.IsNullOrWhiteSpace(parentRoot)
            && string.Equals(
                normalizedParentPath,
                parentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            normalizedParentPath = parentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   + Path.DirectorySeparatorChar;
        }

        // Preserve a synthetic parent's full path so consecutive pages have distinct identities.
        var aggregatePath = $"{normalizedParentPath}{DisplayNodeMetadata.AggregatePathMarker}{key}";

        return new FileSystemNode(
            Path: aggregatePath,
            Name: name,
            Extension: string.Empty,
            SizeBytes: sizeBytes,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [DisplayNodeMetadata.AggregateRiskTag, DisplayNodeMetadata.OverflowRiskTag],
            Children: children)
        {
            LogicalSizeBytes = parent.LogicalSizeBytes is null ? null
                : children.Sum(child => child.LogicalSizeBytes ?? child.SizeBytes),
        };
    }

    private static VisualExplorerItemKind GetKind(FileSystemNode node)
    {
        if (node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            return VisualExplorerItemKind.Aggregate;
        }

        return node.IsDirectory
            ? VisualExplorerItemKind.Directory
            : VisualExplorerItemKind.File;
    }

    private static VisualExplorerItemWeight GetWeight(int depth, int index, int siblingCount)
    {
        if (depth == 0 && index == 0 && siblingCount > 1)
        {
            return VisualExplorerItemWeight.Hero;
        }

        return index <= 2
            ? VisualExplorerItemWeight.Standard
            : VisualExplorerItemWeight.Compact;
    }

    private static bool IsOnExpandedBranch(FileSystemNode node, string? expandedPath)
    {
        if (string.IsNullOrWhiteSpace(expandedPath))
        {
            return false;
        }

        if (string.Equals(node.Path, expandedPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            if (expandedPath.StartsWith(node.Path + DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal))
            {
                return true;
            }

            return node.Children.Any(child => ContainsPath(child, expandedPath));
        }

        return IsDescendantPath(expandedPath, node.Path);
    }

    private static bool ContainsPath(FileSystemNode node, string candidatePath)
    {
        if (string.Equals(node.Path, candidatePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag)
            && IsDescendantPath(candidatePath, node.Path))
        {
            return true;
        }

        return node.Children.Any(child => ContainsPath(child, candidatePath));
    }

    private static bool IsDescendantPath(string candidatePath, string ancestorPath)
    {
        var normalizedAncestor = ancestorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedAncestor))
        {
            return false;
        }

        if (string.Equals(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedAncestor, StringComparison.Ordinal))
        {
            return true;
        }

        return candidatePath.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || candidatePath.StartsWith(normalizedAncestor + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    private static string CreateSubtitleText(FileSystemNode node)
    {
        if (node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            return $"点击继续查看其中最大的 {Math.Min(MaximumVisibleItemsPerFolder, node.Children.Count):N0} 项";
        }

        if (node.RiskTags.Contains("scan-error"))
        {
            return "目录读取不完整，详情中可查看错误";
        }

        if (node.RiskTags.Contains("error"))
        {
            return node.IsDirectory ? "目录读取失败，详情中可查看错误" : "文件读取失败，详情中可查看错误";
        }

        if (node.RiskTags.Contains("skipped"))
        {
            return "按扫描策略跳过了重解析点内容";
        }

        if (!node.IsDirectory)
        {
            return "文件";
        }

        if (node.Children.Count == 0)
        {
            return "空目录";
        }

        var childFolderCount = node.Children.Count(child => child.IsDirectory);
        var childFileCount = node.Children.Count - childFolderCount;
        if (childFileCount == 0)
        {
            return $"包含 {childFolderCount} 个文件夹";
        }

        if (childFolderCount == 0)
        {
            return $"包含 {childFileCount} 个文件";
        }

        return $"包含 {childFolderCount} 个文件夹和 {childFileCount} 个文件";
    }

    private static string CreateTooltipPathText(FileSystemNode node)
    {
        if (!node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            return node.Path;
        }

        var markerIndex = node.Path.IndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return $"上级目录\n{CreateSubtitleText(node)}";
        }

        var parentPath = node.Path[..markerIndex];
        if (parentPath.Length == 2 && parentPath[1] == Path.VolumeSeparatorChar)
        {
            parentPath += Path.DirectorySeparatorChar;
        }

        return $"上级目录：{parentPath}\n{CreateSubtitleText(node)}";
    }
}

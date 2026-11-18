using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public sealed class TreemapDisplayTreeBuilder
{
    public FileSystemNode Build(
        FileSystemNode root,
        int maxVisibleChildrenPerRegion,
        double minVisibleChildShareOfParent = 0d,
        double minReadableChildrenRatioForExpansion = 0d,
        int maxBuildDepth = int.MaxValue)
    {
        var limit = Math.Max(2, maxVisibleChildrenPerRegion);
        var minShare = Math.Clamp(minVisibleChildShareOfParent, 0d, 0.95d);
        var minReadableRatio = Math.Clamp(minReadableChildrenRatioForExpansion, 0d, 0.95d);
        var normalizedMaxBuildDepth = Math.Max(1, maxBuildDepth);
        return BuildCore(root, limit, minShare, minReadableRatio, depth: 0, normalizedMaxBuildDepth);
    }

    private static FileSystemNode BuildCore(
        FileSystemNode node,
        int limit,
        double minShare,
        double minReadableRatio,
        int depth,
        int maxBuildDepth)
    {
        if (!node.IsDirectory || node.Children.Count == 0)
        {
            return node;
        }

        if (depth >= maxBuildDepth)
        {
            return node with
            {
                Children = [],
            };
        }

        var orderedChildren = node.Children.ToArray();

        var minimumAlwaysVisibleChildren = Math.Min(2, orderedChildren.Length);
        var visibleChildren = new List<FileSystemNode>(orderedChildren.Length);
        var aggregatedChildren = new List<FileSystemNode>();

        for (var index = 0; index < orderedChildren.Length; index++)
        {
            var child = orderedChildren[index];
            var builtChild = BuildCore(child, limit, minShare, minReadableRatio, depth + 1, maxBuildDepth);
            var shareOfParent = node.SizeBytes <= 0
                ? 1d
                : child.SizeBytes / (double)node.SizeBytes;

            if (index < minimumAlwaysVisibleChildren || shareOfParent >= minShare)
            {
                visibleChildren.Add(builtChild);
            }
            else
            {
                aggregatedChildren.Add(builtChild);
            }
        }

        var readableRatio = orderedChildren.Length == 0
            ? 1d
            : visibleChildren.Count / (double)orderedChildren.Length;

        if (depth > 0
            && orderedChildren.Length >= 6
            && minReadableRatio > 0
            && readableRatio < minReadableRatio)
        {
            return node with
            {
                Children = [],
            };
        }

        if (aggregatedChildren.Count > 0)
        {
            var maxVisibleWithoutAggregate = Math.Max(1, limit - 1);
            while (visibleChildren.Count > maxVisibleWithoutAggregate)
            {
                aggregatedChildren.Insert(0, visibleChildren[^1]);
                visibleChildren.RemoveAt(visibleChildren.Count - 1);
            }
        }
        else if (visibleChildren.Count > limit)
        {
            aggregatedChildren.AddRange(visibleChildren.Skip(limit - 1));
            visibleChildren = visibleChildren.Take(limit - 1).ToList();
        }

        if (aggregatedChildren.Count == 1 && visibleChildren.Count < limit)
        {
            visibleChildren.Add(aggregatedChildren[0]);
        }
        else if (aggregatedChildren.Count > 1)
        {
            visibleChildren.Add(CreateAggregateNode(node, aggregatedChildren));
        }

        return node with
        {
            Children = visibleChildren,
        };
    }

    private static FileSystemNode CreateAggregateNode(FileSystemNode parent, IReadOnlyList<FileSystemNode> children)
    {
        var count = children.Count;
        var sizeBytes = children.Sum(child => child.SizeBytes);
        var normalizedParentPath = parent.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var aggregatePath = $"{normalizedParentPath}{DisplayNodeMetadata.AggregatePathMarker}{count}";

        return new FileSystemNode(
            Path: aggregatePath,
            Name: $"\u5176\u4f59 {count} \u9879",
            Extension: string.Empty,
            SizeBytes: sizeBytes,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [DisplayNodeMetadata.AggregateRiskTag],
            Children: children);
    }
}

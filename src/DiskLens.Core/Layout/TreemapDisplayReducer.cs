using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public sealed class TreemapDisplayReducer
{
    public IReadOnlyList<TreemapRectangle> Reduce(
        IReadOnlyList<TreemapRectangle> rectangles,
        TreemapDisplayOptions options)
    {
        if (rectangles.Count == 0)
        {
            return rectangles;
        }

        var collapsedNodes = new List<FileSystemNode>();
        var reduced = new List<TreemapRectangle>(rectangles.Count);

        foreach (var rectangle in rectangles
                     .OrderBy(item => item.Depth)
                     .ThenByDescending(item => item.Width * item.Height))
        {
            if (collapsedNodes.Any(node => IsDescendantNode(rectangle.Node, node)))
            {
                continue;
            }

            reduced.Add(rectangle);

            if (!rectangle.Node.IsDirectory || rectangle.Node.Children.Count == 0)
            {
                continue;
            }

            if (ShouldCollapse(rectangle, options))
            {
                collapsedNodes.Add(rectangle.Node);
            }
        }

        return reduced;
    }

    private static bool ShouldCollapse(TreemapRectangle rectangle, TreemapDisplayOptions options)
    {
        var minimumWidth = Math.Max(options.MinVisibleEdge, options.MinLabelWidth);
        var minimumHeight = Math.Max(options.MinVisibleEdge, options.MinLabelHeight);

        return rectangle.Node.Children.Count > Math.Max(1, options.MaxVisibleChildrenPerRegion)
               || rectangle.Width < minimumWidth
               || rectangle.Height < minimumHeight
               || rectangle.Width * rectangle.Height < options.MinVisibleArea;
    }

    private static bool IsDescendantNode(FileSystemNode candidateNode, FileSystemNode collapsedNode)
    {
        if (collapsedNode.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            return collapsedNode.Children.Any(child => ContainsNodePath(child, candidateNode.Path));
        }

        return IsDescendantPath(candidateNode.Path, collapsedNode.Path);
    }

    private static bool ContainsNodePath(FileSystemNode node, string candidatePath)
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

        return node.Children.Any(child => ContainsNodePath(child, candidatePath));
    }

    private static bool IsDescendantPath(string candidatePath, string ancestorPath)
    {
        var normalizedAncestor = ancestorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedAncestor))
        {
            return false;
        }

        var prefixWithDirectorySeparator = normalizedAncestor + Path.DirectorySeparatorChar;
        var prefixWithAltSeparator = normalizedAncestor + Path.AltDirectorySeparatorChar;

        return candidatePath.StartsWith(prefixWithDirectorySeparator, StringComparison.Ordinal)
               || candidatePath.StartsWith(prefixWithAltSeparator, StringComparison.Ordinal);
    }
}

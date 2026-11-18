using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public static class TreemapAnimationSourceResolver
{
    public static IReadOnlyList<TreemapRectangle> ResolveSourceRectangles(
        IReadOnlyList<TreemapRectangle> previousRectangles,
        IReadOnlyList<FileSystemNode> targetNodes)
    {
        if (previousRectangles.Count == 0 || targetNodes.Count == 0)
        {
            return [];
        }

        var previousRectanglesByPath = BuildRectangleMap(previousRectangles);
        var resolvedRectangles = new List<TreemapRectangle>(targetNodes.Count);

        foreach (var targetNode in targetNodes)
        {
            var sourceRectangle = ResolveSourceRectangle(previousRectanglesByPath, targetNode.Path);
            if (sourceRectangle is not null)
            {
                resolvedRectangles.Add(sourceRectangle);
            }
        }

        return resolvedRectangles;
    }

    public static IReadOnlyDictionary<string, TreemapRectangle> ResolveSourceRectangleMap(
        IReadOnlyList<TreemapRectangle> previousRectangles,
        IReadOnlyList<TreemapRectangle> targetRectangles)
    {
        if (previousRectangles.Count == 0 || targetRectangles.Count == 0)
        {
            return new Dictionary<string, TreemapRectangle>(StringComparer.Ordinal);
        }

        var previousRectanglesByPath = BuildRectangleMap(previousRectangles);
        var resolvedRectangles = new Dictionary<string, TreemapRectangle>(StringComparer.Ordinal);

        foreach (var targetRectangle in targetRectangles)
        {
            var sourceRectangle = ResolveSourceRectangle(previousRectanglesByPath, targetRectangle.Node.Path);
            if (sourceRectangle is not null)
            {
                resolvedRectangles[targetRectangle.Node.Path] = sourceRectangle;
            }
        }

        return resolvedRectangles;
    }

    private static IReadOnlyDictionary<string, TreemapRectangle> BuildRectangleMap(IReadOnlyList<TreemapRectangle> rectangles)
    {
        return rectangles
            .GroupBy(rectangle => NormalizePath(rectangle.Node.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static TreemapRectangle? ResolveSourceRectangle(
        IReadOnlyDictionary<string, TreemapRectangle> previousRectanglesByPath,
        string targetPath)
    {
        var candidatePath = NormalizePath(targetPath);

        while (!string.IsNullOrWhiteSpace(candidatePath))
        {
            if (previousRectanglesByPath.TryGetValue(candidatePath, out var sourceRectangle))
            {
                return sourceRectangle;
            }

            candidatePath = GetAncestorPath(candidatePath);
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmedPath = path.Trim();
        var normalizedPath = trimmedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(trimmedPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return normalizedPath;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? root
            : normalizedPath;
    }

    private static string? GetAncestorPath(string path)
    {
        var aggregateMarkerIndex = path.LastIndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
        if (aggregateMarkerIndex >= 0)
        {
            var aggregateParentPath = NormalizePath(path[..aggregateMarkerIndex]);
            return string.IsNullOrWhiteSpace(aggregateParentPath) || string.Equals(aggregateParentPath, path, StringComparison.Ordinal)
                ? null
                : aggregateParentPath;
        }

        var parentPath = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var normalizedRoot = NormalizePath(root);
            return string.Equals(normalizedRoot, path, StringComparison.Ordinal)
                ? null
                : normalizedRoot;
        }

        var normalizedParentPath = NormalizePath(parentPath);
        return string.Equals(normalizedParentPath, path, StringComparison.Ordinal)
            ? null
            : normalizedParentPath;
    }
}

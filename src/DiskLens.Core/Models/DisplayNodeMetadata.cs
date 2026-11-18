namespace DiskLens.Core.Models;

public static class DisplayNodeMetadata
{
    public const string AggregateRiskTag = "aggregate";
    public const string AggregatePathMarker = "::aggregate::";
    public const string OverflowRiskTag = "display-overflow";

    public static bool IsOverflow(FileSystemNode node) => node.RiskTags.Contains(OverflowRiskTag);

    // The first marker identifies the real directory, even after several overflow pages.
    public static string? GetOverflowParentPath(FileSystemNode node)
    {
        if (!IsOverflow(node))
        {
            return null;
        }

        var marker = node.Path.IndexOf(AggregatePathMarker, StringComparison.Ordinal);
        if (marker <= 0)
        {
            return null;
        }

        var path = node.Path[..marker];
        return path.Length == 2 && path[1] == ':' ? path + Path.DirectorySeparatorChar : path;
    }

    // Each page appends a marker, so the last marker identifies the preceding view.
    public static string? GetOverflowNavigationParentPath(FileSystemNode node)
    {
        if (!IsOverflow(node))
        {
            return null;
        }

        var marker = node.Path.LastIndexOf(AggregatePathMarker, StringComparison.Ordinal);
        if (marker <= 0)
        {
            return null;
        }

        var path = node.Path[..marker];
        return path.Length == 2 && path[1] == ':' ? path + Path.DirectorySeparatorChar : path;
    }
}

using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public static class TreemapTransitionPresentation
{
    public static bool HasExactSourceRectangle(
        FileSystemNode node,
        IReadOnlyDictionary<string, TreemapRectangle> sourceRectangles)
    {
        return sourceRectangles.TryGetValue(node.Path, out var sourceRectangle)
               && string.Equals(sourceRectangle.Node.Path, node.Path, StringComparison.Ordinal);
    }

    public static double GetOpacity(double progress, double fadeStart, bool hasSourceRectangle)
    {
        if (hasSourceRectangle)
        {
            return 1;
        }

        var normalizedProgress = Math.Clamp(progress, 0, 1);
        var normalizedStart = Math.Clamp(fadeStart, 0, 1);
        if (normalizedProgress <= normalizedStart)
        {
            return 0;
        }

        var fadeProgress = (normalizedProgress - normalizedStart) / (1 - normalizedStart);
        return 1 - Math.Pow(1 - fadeProgress, 3);
    }

    public static bool ShouldRenderExitingRectangles(bool isViewportTransitionActive, double progress)
    {
        return isViewportTransitionActive && Math.Clamp(progress, 0, 1) < 1;
    }
}

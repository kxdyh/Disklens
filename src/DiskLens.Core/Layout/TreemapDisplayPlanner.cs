using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public sealed class TreemapDisplayPlanner
{
    private readonly ITreemapLayoutEngine _layoutEngine = new SquarifiedTreemapLayoutEngine();

    public TreemapDisplayPlan Plan(FileSystemNode root, TreemapDisplayPlannerOptions options)
    {
        var width = Math.Max(0, options.ViewportWidth);
        var height = Math.Max(0, options.ViewportHeight);
        // The explorer has already selected the largest 50 items plus its overflow shortcut.
        // A second compaction pass would silently hide those items, particularly during scans.
        var rectangles = width > 0 && height > 0
            ? _layoutEngine.Layout(root, 0, 0, width, height, Math.Clamp(options.MaxDepth, 1, 12))
            : [];
        return new TreemapDisplayPlan(root, rectangles);
    }
}

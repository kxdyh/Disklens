using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public sealed record TreemapDisplayPlan(
    FileSystemNode DisplayRoot,
    IReadOnlyList<TreemapRectangle> Rectangles);

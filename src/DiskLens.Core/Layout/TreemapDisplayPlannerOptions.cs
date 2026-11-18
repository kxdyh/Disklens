namespace DiskLens.Core.Layout;

public sealed record TreemapDisplayPlannerOptions(
    double ViewportWidth,
    double ViewportHeight,
    int MaxDepth,
    int MaxVisibleChildrenPerRegion,
    int CompactionLevel,
    bool IsScanning,
    bool IsOverviewMode);

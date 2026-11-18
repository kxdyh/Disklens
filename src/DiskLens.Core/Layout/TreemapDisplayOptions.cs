namespace DiskLens.Core.Layout;

public sealed record TreemapDisplayOptions(
    int MaxVisibleChildrenPerRegion = 24,
    double MinVisibleEdge = 14,
    double MinVisibleArea = 1600,
    double MinLabelWidth = 88,
    double MinLabelHeight = 26);

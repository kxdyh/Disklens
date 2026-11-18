namespace DiskLens.Core.Models;

public sealed record TreemapRectangle(
    FileSystemNode Node,
    double X,
    double Y,
    double Width,
    double Height,
    int Depth);

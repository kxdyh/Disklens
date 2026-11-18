using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public interface ITreemapLayoutEngine
{
    IReadOnlyList<TreemapRectangle> Layout(FileSystemNode root, double x, double y, double width, double height, int maxDepth);
}

using DiskLens.Core.Models;

namespace DiskLens.Core.Layout;

public sealed class SquarifiedTreemapLayoutEngine : ITreemapLayoutEngine
{
    private const double RectanglePadding = 1.5;
    private const double DirectoryPadding = 3;
    private const double DirectoryHeaderBandHeight = 34;
    private const double DirectoryChromeMinimumWidth = 96;
    private const double DirectoryChromeMinimumHeight = 64;
    private const double MinimumRecursiveSize = 18;

    public IReadOnlyList<TreemapRectangle> Layout(FileSystemNode root, double x, double y, double width, double height, int maxDepth)
    {
        var rectangles = new List<TreemapRectangle>();
        if (width <= 0 || height <= 0)
        {
            return rectangles;
        }

        LayoutChildren(root, x, y, width, height, depth: 1, maxDepth, rectangles);
        return rectangles;
    }

    private static void LayoutChildren(
        FileSystemNode parent,
        double x,
        double y,
        double width,
        double height,
        int depth,
        int maxDepth,
        List<TreemapRectangle> rectangles)
    {
        var children = parent.Children.ToArray();

        if (children.Length == 0)
        {
            return;
        }

        var viewportArea = width * height;
        if (!double.IsFinite(viewportArea) || viewportArea <= 0)
        {
            return;
        }

        // Half the canvas preserves relative size, half provides a readable share to every item.
        // SizeBytes remains untouched: labels, totals and AI always use the actual disk size.
        var largestSize = Math.Max(1d, children.Where(child => !DisplayNodeMetadata.IsOverflow(child))
            .Select(child => (double)child.SizeBytes).DefaultIfEmpty(1).Max());
        var weights = children.Select(child => Math.Sqrt(Math.Max(0, child.SizeBytes) / largestSize)).ToArray();
        var regularWeights = children.Select((child, index) => (child, index))
            .Where(item => !DisplayNodeMetadata.IsOverflow(item.child))
            .Select(item => weights[item.index]).ToArray();
        var overflowWeight = regularWeights.Length == 0 ? 1 : regularWeights.Average();
        for (var index = 0; index < children.Length; index++)
        {
            if (DisplayNodeMetadata.IsOverflow(children[index]))
            {
                weights[index] = overflowWeight;
            }
        }

        var totalWeight = weights.Sum();
        var items = children
            .Select((child, index) => new LayoutItem(child, viewportArea *
                (0.5 / children.Length + 0.5 * (totalWeight > 0 ? weights[index] / totalWeight : 1d / children.Length))))
            .ToArray();
        LayoutSquarified(items, new RectBounds(x, y, width, height), rectangles, depth, maxDepth);
    }

    private static void LayoutSquarified(
        IReadOnlyList<LayoutItem> items,
        RectBounds bounds,
        List<TreemapRectangle> rectangles,
        int depth,
        int maxDepth)
    {
        if (items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (items.Count == 1)
        {
            AddRectangle(items[0].Node, bounds.X, bounds.Y, bounds.Width, bounds.Height, depth, maxDepth, rectangles);
            return;
        }

        var nextItemIndex = 0;
        var remainingBounds = bounds;

        while (nextItemIndex < items.Count && remainingBounds.Width > 0 && remainingBounds.Height > 0)
        {
            var shortSide = Math.Min(remainingBounds.Width, remainingBounds.Height);
            var row = new List<LayoutItem>();
            var rowArea = 0d;
            var smallestArea = double.PositiveInfinity;
            var largestArea = 0d;

            while (nextItemIndex < items.Count)
            {
                var candidate = items[nextItemIndex];
                var nextArea = rowArea + candidate.Area;
                var nextSmallestArea = Math.Min(smallestArea, candidate.Area);
                var nextLargestArea = Math.Max(largestArea, candidate.Area);
                if (row.Count > 0
                    && WorstAspectRatio(nextArea, nextSmallestArea, nextLargestArea, shortSide)
                    > WorstAspectRatio(rowArea, smallestArea, largestArea, shortSide))
                {
                    break;
                }

                row.Add(candidate);
                rowArea = nextArea;
                smallestArea = nextSmallestArea;
                largestArea = nextLargestArea;
                nextItemIndex++;
            }

            if (row.Count == 0 || rowArea <= 0)
            {
                break;
            }

            remainingBounds = LayoutRow(row, rowArea, remainingBounds, rectangles, depth, maxDepth);
        }
    }

    private static double WorstAspectRatio(double totalArea, double smallestArea, double largestArea, double shortSide)
    {
        if (totalArea <= 0 || smallestArea <= 0 || shortSide <= 0)
        {
            return double.PositiveInfinity;
        }

        var scaledSide = shortSide * shortSide;
        var scaledTotal = totalArea * totalArea;
        return Math.Max(
            scaledSide * largestArea / scaledTotal,
            scaledTotal / (scaledSide * smallestArea));
    }

    private static RectBounds LayoutRow(
        IReadOnlyList<LayoutItem> row,
        double rowArea,
        RectBounds bounds,
        List<TreemapRectangle> rectangles,
        int depth,
        int maxDepth)
    {
        if (bounds.Width >= bounds.Height)
        {
            var rowWidth = Math.Min(bounds.Width, rowArea / bounds.Height);
            var y = bounds.Y;
            var remainingHeight = bounds.Height;
            for (var index = 0; index < row.Count; index++)
            {
                var itemHeight = index == row.Count - 1
                    ? remainingHeight
                    : Math.Min(bounds.Height, row[index].Area / rowWidth);
                AddRectangle(row[index].Node, bounds.X, y, rowWidth, itemHeight, depth, maxDepth, rectangles);
                y += itemHeight;
                remainingHeight = Math.Max(0, bounds.Y + bounds.Height - y);
            }

            return new RectBounds(bounds.X + rowWidth, bounds.Y, Math.Max(0, bounds.Width - rowWidth), bounds.Height);
        }

        var rowHeight = Math.Min(bounds.Height, rowArea / bounds.Width);
        var x = bounds.X;
        var remainingWidth = bounds.Width;
        for (var index = 0; index < row.Count; index++)
        {
            var itemWidth = index == row.Count - 1
                ? remainingWidth
                : Math.Min(remainingWidth, row[index].Area / rowHeight);
            AddRectangle(row[index].Node, x, bounds.Y, itemWidth, rowHeight, depth, maxDepth, rectangles);
            x += itemWidth;
            remainingWidth = Math.Max(0, bounds.X + bounds.Width - x);
        }

        return new RectBounds(bounds.X, bounds.Y + rowHeight, bounds.Width, Math.Max(0, bounds.Height - rowHeight));
    }

    private static void AddRectangle(
        FileSystemNode node,
        double x,
        double y,
        double width,
        double height,
        int depth,
        int maxDepth,
        List<TreemapRectangle> rectangles)
    {
        var visualBounds = Inset(x, y, width, height, RectanglePadding);
        rectangles.Add(new TreemapRectangle(node, visualBounds.X, visualBounds.Y, visualBounds.Width, visualBounds.Height, depth));

        if (!node.IsDirectory || DisplayNodeMetadata.IsOverflow(node) || node.Children.Count == 0 || depth >= maxDepth)
        {
            return;
        }

        var reserveHeader = visualBounds.Width >= DirectoryChromeMinimumWidth
                            && visualBounds.Height >= DirectoryChromeMinimumHeight;
        var childBounds = GetChildBounds(visualBounds, reserveHeader);
        if (childBounds.Width < MinimumRecursiveSize || childBounds.Height < MinimumRecursiveSize)
        {
            return;
        }

        LayoutChildren(node, childBounds.X, childBounds.Y, childBounds.Width, childBounds.Height, depth + 1, maxDepth, rectangles);
    }

    private static RectBounds Inset(double x, double y, double width, double height, double padding)
    {
        var insetWidth = Math.Max(0, width - (padding * 2));
        var insetHeight = Math.Max(0, height - (padding * 2));
        return new RectBounds(x + padding, y + padding, insetWidth, insetHeight);
    }

    private static RectBounds GetChildBounds(RectBounds bounds, bool reserveHeader)
    {
        var contentX = bounds.X + DirectoryPadding;
        var headerHeight = reserveHeader ? DirectoryHeaderBandHeight : 0;
        var contentY = bounds.Y + DirectoryPadding;
        var contentWidth = Math.Max(0, bounds.Width - (DirectoryPadding * 2));
        var contentHeight = Math.Max(0, bounds.Height - (DirectoryPadding * 2) - headerHeight);
        return new RectBounds(contentX, contentY, contentWidth, contentHeight);
    }

    private sealed record LayoutItem(FileSystemNode Node, double Area);
    private sealed record RectBounds(double X, double Y, double Width, double Height);
}

namespace DiskLens.Core.Layout;

public sealed record TreemapLabelLayout(
    double Padding,
    double FontSize,
    int MaxLineCount,
    bool IncludeSize);

public sealed record TreemapPurposeBandLayout(
    double LabelHeight,
    double BandTop,
    double BandHeight,
    double TextInset);

public static class TreemapLabelPlanner
{
    public const double MinimumWidth = 32;
    public const double MinimumHeight = 18;

    /// <summary>Reserves disjoint measured areas; a purpose must never replace a name or size.</summary>
    public static TreemapPurposeBandLayout? PlanPurposeBand(
        double width, double height, double minimumLabelHeight, double measuredPurposeHeight)
    {
        const double textInset = 6;
        const double verticalInset = 3;
        const double gap = 4;
        if (!double.IsFinite(width) || !double.IsFinite(height)
            || !double.IsFinite(minimumLabelHeight) || !double.IsFinite(measuredPurposeHeight)
            || width < textInset * 2 + 12 || minimumLabelHeight <= 0 || measuredPurposeHeight <= 0)
            return null;

        var bandHeight = measuredPurposeHeight + verticalInset * 2;
        var top = height - bandHeight;
        var labelHeight = top - gap;
        return labelHeight >= minimumLabelHeight
            ? new TreemapPurposeBandLayout(labelHeight, top, bandHeight, textInset)
            : null;
    }

    public static TreemapLabelLayout? Plan(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)
            || width < MinimumWidth || height < MinimumHeight)
        {
            return null;
        }

        if (width >= 220 && height >= 120)
        {
            return new TreemapLabelLayout(
                Padding: 12,
                FontSize: 15.5,
                MaxLineCount: 2,
                IncludeSize: true);
        }

        if (width >= 140 && height >= 58)
        {
            return new TreemapLabelLayout(
                Padding: 7,
                FontSize: 12,
                MaxLineCount: 2,
                IncludeSize: true);
        }

        var padding = width >= 80 && height >= 40 ? 5 : 3;
        var fontSize = width >= 80 && height >= 40 ? 10 : 8.5;
        return new TreemapLabelLayout(
            Padding: padding,
            FontSize: fontSize,
            MaxLineCount: height - padding * 2 >= fontSize * 2.8 + 2 ? 2 : 1,
            IncludeSize: true);
    }
}

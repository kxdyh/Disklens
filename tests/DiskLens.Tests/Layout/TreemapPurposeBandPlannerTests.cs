using DiskLens.Core.Layout;

namespace DiskLens.Tests.Layout;

public sealed class TreemapPurposeBandPlannerTests
{
    [Theory]
    [InlineData(260, 130, 40, 19)]
    [InlineData(96, 80, 28, 18)]
    [InlineData(40, 65, 26, 18)]
    [InlineData(180, 48, 16, 18)]
    public void Purpose_owns_an_independent_bottom_band_with_a_gap(double width, double height,
        double minimumLabelHeight, double measuredPurposeHeight)
    {
        var band = Assert.IsType<TreemapPurposeBandLayout>(TreemapLabelPlanner.PlanPurposeBand(
            width, height, minimumLabelHeight, measuredPurposeHeight));

        Assert.True(band.LabelHeight >= minimumLabelHeight);
        Assert.Equal(4, band.BandTop - band.LabelHeight);
        Assert.Equal(height, band.BandTop + band.BandHeight);
        Assert.True(band.BandHeight > measuredPurposeHeight);
        Assert.True(band.TextInset * 2 < width);
    }

    [Theory]
    [InlineData(100, 28, 16, 18)]
    [InlineData(20, 200, 16, 18)]
    [InlineData(100, 50, 28, 18)]
    [InlineData(double.NaN, 100, 28, 18)]
    [InlineData(100, double.PositiveInfinity, 28, 18)]
    public void Insufficient_space_falls_back_to_name_size_and_tooltip_without_overlap(
        double width, double height, double minimumLabelHeight, double measuredPurposeHeight)
    {
        Assert.Null(TreemapLabelPlanner.PlanPurposeBand(width, height, minimumLabelHeight, measuredPurposeHeight));
    }
}

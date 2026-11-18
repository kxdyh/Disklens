using DiskLens.Core.Layout;

namespace DiskLens.Tests.Layout;

public sealed class TreemapLabelPlannerTests
{
    [Fact]
    public void Plan_should_render_compact_labels_in_blocks_that_were_previously_hidden()
    {
        var layout = TreemapLabelPlanner.Plan(64, 22);

        Assert.NotNull(layout);
        Assert.Equal(3, layout.Padding);
        Assert.Equal(8.5, layout.FontSize);
        Assert.Equal(1, layout.MaxLineCount);
        Assert.True(layout.IncludeSize);
    }

    [Theory]
    [InlineData(31.9, 30)]
    [InlineData(80, 17.9)]
    [InlineData(double.NaN, 40)]
    [InlineData(80, double.PositiveInfinity)]
    public void Plan_should_leave_too_small_blocks_for_the_full_name_tooltip(double width, double height)
    {
        Assert.Null(TreemapLabelPlanner.Plan(width, height));
    }

    [Fact]
    public void Plan_should_scale_padding_type_and_details_with_block_size()
    {
        var compact = Assert.IsType<TreemapLabelLayout>(TreemapLabelPlanner.Plan(96, 40));
        var medium = Assert.IsType<TreemapLabelLayout>(TreemapLabelPlanner.Plan(180, 72));
        var large = Assert.IsType<TreemapLabelLayout>(TreemapLabelPlanner.Plan(260, 180));

        Assert.True(compact.Padding < medium.Padding);
        Assert.True(compact.FontSize < medium.FontSize);
        Assert.True(compact.IncludeSize);
        Assert.Equal(2, compact.MaxLineCount);
        Assert.True(medium.IncludeSize);
        Assert.Equal(2, medium.MaxLineCount);
        Assert.Equal(12, large.Padding);
        Assert.Equal(15.5, large.FontSize);
        Assert.Equal(2, large.MaxLineCount);
        Assert.True(large.IncludeSize);
    }

    [Theory]
    [InlineData(100, 88)]
    [InlineData(120, 110)]
    [InlineData(140, 180)]
    [InlineData(80, 32)]
    [InlineData(32, 48)]
    public void Small_blocks_should_reserve_a_separate_capacity_row_when_height_allows(double width, double height)
    {
        var layout = Assert.IsType<TreemapLabelLayout>(TreemapLabelPlanner.Plan(width, height));

        Assert.True(layout.IncludeSize);
        Assert.Equal(2, layout.MaxLineCount);
    }

    [Theory]
    [InlineData(64, 22)]
    [InlineData(80, 30)]
    [InlineData(32, 18)]
    public void Shallow_blocks_should_keep_capacity_without_forcing_two_rows(double width, double height)
    {
        var layout = Assert.IsType<TreemapLabelLayout>(TreemapLabelPlanner.Plan(width, height));

        Assert.True(layout.IncludeSize);
        Assert.Equal(1, layout.MaxLineCount);
    }
}

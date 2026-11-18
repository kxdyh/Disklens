using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Layout;

public sealed class TreemapTransitionPresentationTests
{
    [Fact]
    public void Labels_should_fade_in_during_the_final_part_of_a_viewport_transition()
    {
        Assert.Equal(0, TreemapTransitionPresentation.GetOpacity(0.78, 0.78, hasSourceRectangle: false));
        Assert.Equal(0.875, TreemapTransitionPresentation.GetOpacity(0.89, 0.78, hasSourceRectangle: false), precision: 3);
        Assert.Equal(1, TreemapTransitionPresentation.GetOpacity(1, 0.78, hasSourceRectangle: false));
        Assert.Equal(0, TreemapTransitionPresentation.GetOpacity(-1, 0.78, hasSourceRectangle: false));
        Assert.Equal(1, TreemapTransitionPresentation.GetOpacity(2, 0.78, hasSourceRectangle: false));
        Assert.Equal(1, TreemapTransitionPresentation.GetOpacity(0, 0.78, hasSourceRectangle: true));
    }

    [Fact]
    public void A_parent_rectangle_used_as_a_fallback_should_not_keep_a_new_child_label_visible()
    {
        var parent = CreateDirectory(@"E:\Sample\Parent", "Parent");
        var child = CreateDirectory(@"E:\Sample\Parent\Child", "Child");
        var fallbackSources = new Dictionary<string, TreemapRectangle>(StringComparer.OrdinalIgnoreCase)
        {
            [child.Path] = new(parent, 10, 20, 300, 200, 1),
        };
        var existingSources = new Dictionary<string, TreemapRectangle>(StringComparer.OrdinalIgnoreCase)
        {
            [child.Path] = new(child, 10, 20, 300, 200, 1),
        };

        Assert.False(TreemapTransitionPresentation.HasExactSourceRectangle(child, fallbackSources));
        Assert.True(TreemapTransitionPresentation.HasExactSourceRectangle(child, existingSources));
    }

    [Fact]
    public void Exiting_rectangles_should_remain_renderable_until_the_viewport_transition_finishes()
    {
        Assert.True(TreemapTransitionPresentation.ShouldRenderExitingRectangles(isViewportTransitionActive: true, progress: 0));
        Assert.True(TreemapTransitionPresentation.ShouldRenderExitingRectangles(isViewportTransitionActive: true, progress: 0.5));
        Assert.False(TreemapTransitionPresentation.ShouldRenderExitingRectangles(isViewportTransitionActive: true, progress: 1));
        Assert.False(TreemapTransitionPresentation.ShouldRenderExitingRectangles(isViewportTransitionActive: false, progress: 0.5));
    }

    private static FileSystemNode CreateDirectory(string path, string name)
    {
        return new FileSystemNode(path, name, string.Empty, 1, FileAttributes.Directory,
            null, null, true, false, [], []);
    }
}

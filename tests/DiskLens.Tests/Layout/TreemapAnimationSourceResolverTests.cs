using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Layout;

public sealed class TreemapAnimationSourceResolverTests
{
    [Fact]
    public void Resolve_should_fall_back_to_nearest_visible_ancestor_when_exact_path_is_missing()
    {
        var root = CreateDirectory(@"C:\Users", "Users");
        var appData = CreateDirectory(@"C:\Users\AppData", "AppData");
        var packages = CreateDirectory(@"C:\Users\AppData\Packages", "Packages");

        var previousRectangles = new[]
        {
            new TreemapRectangle(appData, 100, 120, 320, 280, 1),
        };

        var resolved = TreemapAnimationSourceResolver.ResolveSourceRectangles(previousRectangles, [packages]);

        var sourceRectangle = Assert.Single(resolved);
        Assert.Equal(appData.Path, sourceRectangle.Node.Path);
        Assert.Equal(100, sourceRectangle.X);
        Assert.Equal(120, sourceRectangle.Y);
    }

    [Fact]
    public void Resolve_should_prefer_exact_match_when_it_exists()
    {
        var appData = CreateDirectory(@"C:\Users\AppData", "AppData");

        var previousRectangles = new[]
        {
            new TreemapRectangle(appData, 100, 120, 320, 280, 1),
        };

        var resolved = TreemapAnimationSourceResolver.ResolveSourceRectangles(previousRectangles, [appData]);

        var sourceRectangle = Assert.Single(resolved);
        Assert.Equal(appData.Path, sourceRectangle.Node.Path);
        Assert.Equal(100, sourceRectangle.X);
        Assert.Equal(120, sourceRectangle.Y);
    }

    [Fact]
    public void Resolve_should_skip_node_when_no_visible_ancestor_exists()
    {
        var users = CreateDirectory(@"C:\Users", "Users");
        var packages = CreateDirectory(@"C:\Program Files\Packages", "Packages");

        var previousRectangles = new[]
        {
            new TreemapRectangle(users, 20, 30, 400, 260, 1),
        };

        var resolved = TreemapAnimationSourceResolver.ResolveSourceRectangles(previousRectangles, [packages]);

        Assert.Empty(resolved);
    }

    private static FileSystemNode CreateDirectory(string path, string name)
    {
        return new FileSystemNode(
            Path: path,
            Name: name,
            Extension: string.Empty,
            SizeBytes: 1,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);
    }
}

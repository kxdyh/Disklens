using DiskLens.Core.Models;

namespace DiskLens.Tests.Models;

public sealed class FileSystemNodeIndexTests
{
    [Fact]
    public void Root_only_preview_should_keep_full_tree_without_indexing_hidden_descendants()
    {
        var file = Node("E:/scan/folder/file.bin", false, []);
        var folder = Node("E:/scan/folder", true, [file]);
        var root = Node("E:/scan", true, [folder]);

        var preview = FileSystemNodeIndex.CreateRootOnly(root);

        Assert.Same(root, preview.FindNode(root.Path));
        Assert.Same(file, preview.FindNode(root.Path)!.Children[0].Children[0]);
        Assert.Null(preview.FindNode(folder.Path));
        Assert.Null(preview.FindNode(file.Path));
        Assert.Null(preview.FindParentNode(root.Path));

        var completed = FileSystemNodeIndex.Build(root);
        Assert.Same(file, completed.FindNode(file.Path));
        Assert.Same(folder, completed.FindParentNode(file.Path));
        Assert.Same(root, completed.FindParentNode(folder.Path));
    }

    [Fact]
    public void New_preview_should_not_change_the_previous_preview_or_completed_index()
    {
        var root = Node("E:/scan", true, []);
        var oldPreview = FileSystemNodeIndex.CreateRootOnly(root);
        var oldCompleted = FileSystemNodeIndex.Build(root);
        var file = Node("E:/scan/new.bin", false, []);
        var updated = root with { Children = [file] };

        var newPreview = FileSystemNodeIndex.CreateRootOnly(updated);

        Assert.Same(updated, newPreview.FindNode(root.Path));
        Assert.Same(root, oldPreview.FindNode(root.Path));
        Assert.Same(root, oldCompleted.FindNode(root.Path));
        Assert.Null(newPreview.FindNode(file.Path));
        Assert.Same(file, FileSystemNodeIndex.Build(updated).FindNode(file.Path));
    }

    private static FileSystemNode Node(string path, bool directory, IReadOnlyList<FileSystemNode> children) => new(
        path, Path.GetFileName(path), string.Empty, 0,
        directory ? FileAttributes.Directory : FileAttributes.Normal,
        null, null, directory, false, [], children);
}

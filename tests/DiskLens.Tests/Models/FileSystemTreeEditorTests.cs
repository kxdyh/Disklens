using DiskLens.Core.Models;

namespace DiskLens.Tests.Models;

public sealed class FileSystemTreeEditorTests
{
    [Theory]
    [InlineData(4096)]
    [InlineData(0)]
    public void Removing_an_unrelated_file_should_preserve_existing_storage_owners_and_folder_sizes(long allocatedBytes)
    {
        var storage = new FileStorageInfo("volume:file-id", 100, allocatedBytes, LinkCount: 2);
        var owner = CreateFile(@"E:\Data\A\owner.bin", "owner.bin", allocatedBytes) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage,
        };
        var ownerDirectory = CreateDirectory(@"E:\Data\A", "A", allocatedBytes, [owner]) with { LogicalSizeBytes = 100 };
        var alias = CreateFile(@"E:\Data\B\alias.bin", "alias.bin", 0) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage, RiskTags = ["hard-link-shared"],
        };
        var padding = CreateFile(@"E:\Data\B\padding.bin", "padding.bin", 8192) with { LogicalSizeBytes = 200 };
        var aliasDirectory = CreateDirectory(@"E:\Data\B", "B", 8192, [padding, alias]) with { LogicalSizeBytes = 300 };
        var unrelated = CreateFile(@"E:\Data\unrelated.bin", "unrelated.bin", 4096) with { LogicalSizeBytes = 50 };
        // B precedes A in the display tree even though the original scan assigned storage to A.
        var root = CreateDirectory(@"E:\Data", "Data", allocatedBytes + 12288, [aliasDirectory, ownerDirectory, unrelated]) with
        {
            LogicalSizeBytes = 450,
        };

        Assert.True(FileSystemTreeEditor.TryRemovePath(root, unrelated.Path, out var updated));

        var updatedOwnerDirectory = updated.Children.Single(node => node.Path == ownerDirectory.Path);
        var updatedAliasDirectory = updated.Children.Single(node => node.Path == aliasDirectory.Path);
        Assert.Equal(allocatedBytes + 8192, updated.SizeBytes);
        Assert.Equal(400, updated.LogicalSizeBytes);
        Assert.Equal(ownerDirectory.SizeBytes, updatedOwnerDirectory.SizeBytes);
        Assert.Equal(aliasDirectory.SizeBytes, updatedAliasDirectory.SizeBytes);
        Assert.DoesNotContain("hard-link-shared", Assert.Single(updatedOwnerDirectory.Children).RiskTags);
        Assert.Contains("hard-link-shared", updatedAliasDirectory.Children.Single(node => node.Path == alias.Path).RiskTags);
    }

    [Fact]
    public void Removing_the_owner_file_should_transfer_storage_to_a_link_in_another_folder()
    {
        var storage = new FileStorageInfo("volume:file-id", 100, 4096, LinkCount: 2);
        var owner = CreateFile(@"E:\Data\A\owner.bin", "owner.bin", 4096) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage,
        };
        var ownerDirectory = CreateDirectory(@"E:\Data\A", "A", 4096, [owner]) with { LogicalSizeBytes = 100 };
        var alias = CreateFile(@"E:\Data\B\alias.bin", "alias.bin", 0) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage, RiskTags = ["hard-link-shared"],
        };
        var aliasDirectory = CreateDirectory(@"E:\Data\B", "B", 0, [alias]) with { LogicalSizeBytes = 100 };
        var root = CreateDirectory(@"E:\Data", "Data", 4096, [aliasDirectory, ownerDirectory]) with { LogicalSizeBytes = 200 };

        Assert.True(FileSystemTreeEditor.TryRemovePath(root, owner.Path, out var updated));

        Assert.Equal(4096, updated.SizeBytes);
        Assert.Equal(100, updated.LogicalSizeBytes);
        Assert.Equal(0, updated.Children.Single(node => node.Path == ownerDirectory.Path).SizeBytes);
        var updatedAliasDirectory = updated.Children.Single(node => node.Path == aliasDirectory.Path);
        Assert.Equal(4096, updatedAliasDirectory.SizeBytes);
        var updatedAlias = Assert.Single(updatedAliasDirectory.Children);
        Assert.Equal(4096, updatedAlias.SizeBytes);
        Assert.DoesNotContain("hard-link-shared", updatedAlias.RiskTags);
    }

    [Theory]
    [InlineData("owner.bin")]
    [InlineData("alias.bin")]
    public void Removing_one_hard_link_should_keep_the_shared_allocation_and_update_logical_size(string removedName)
    {
        var storage = new FileStorageInfo("volume:file-id", 100, 4096, LinkCount: 2);
        var owner = CreateFile(@"E:\Data\owner.bin", "owner.bin", 4096) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage,
        };
        var alias = CreateFile(@"E:\Data\alias.bin", "alias.bin", 0) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage, RiskTags = ["hard-link-shared"],
        };
        var root = CreateDirectory(@"E:\Data", "Data", 4096, [owner, alias]) with { LogicalSizeBytes = 200 };

        Assert.True(FileSystemTreeEditor.TryRemovePath(root, @"E:\Data\" + removedName, out var updated));

        Assert.Equal(4096, updated.SizeBytes);
        Assert.Equal(100, updated.LogicalSizeBytes);
        var remaining = Assert.Single(updated.Children);
        Assert.Equal(4096, remaining.SizeBytes);
        Assert.DoesNotContain("hard-link-shared", remaining.RiskTags);
        Assert.Equal(4096, root.SizeBytes);
        Assert.Equal(200, root.LogicalSizeBytes);
    }

    [Fact]
    public void Removing_a_directory_should_transfer_allocation_to_a_remaining_link_in_another_directory()
    {
        var storage = new FileStorageInfo("volume:file-id", 100, 4096, LinkCount: 2);
        var owner = CreateFile(@"E:\Data\nested\owner.bin", "owner.bin", 4096) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage,
        };
        var nested = CreateDirectory(@"E:\Data\nested", "nested", 4096, [owner]) with { LogicalSizeBytes = 100 };
        var alias = CreateFile(@"E:\Data\alias.bin", "alias.bin", 0) with
        {
            LogicalSizeBytes = 100, StorageInfo = storage, RiskTags = ["hard-link-shared"],
        };
        var root = CreateDirectory(@"E:\Data", "Data", 4096, [nested, alias]) with { LogicalSizeBytes = 200 };

        Assert.True(FileSystemTreeEditor.TryRemovePath(root, nested.Path, out var updated));

        Assert.Equal(4096, updated.SizeBytes);
        Assert.Equal(100, updated.LogicalSizeBytes);
        Assert.Equal(4096, Assert.Single(updated.Children).SizeBytes);
    }

    [Fact]
    public void TryRemovePath_should_remove_file_and_reduce_parent_sizes()
    {
        var nestedDirectory = CreateDirectory(
            path: @"C:\Data\Users",
            name: "Users",
            sizeBytes: 70,
            children:
            [
                CreateFile(@"C:\Data\Users\a.bin", "a.bin", 30),
                CreateFile(@"C:\Data\Users\b.bin", "b.bin", 40),
            ]);

        var root = CreateDirectory(
            path: @"C:\Data",
            name: "Data",
            sizeBytes: 100,
            children:
            [
                nestedDirectory,
                CreateFile(@"C:\Data\root.bin", "root.bin", 30),
            ]);

        var removed = FileSystemTreeEditor.TryRemovePath(root, @"C:\Data\Users\b.bin", out var updatedRoot);

        Assert.True(removed);
        Assert.Equal(60, updatedRoot.SizeBytes);

        var updatedUsers = updatedRoot.DescendantsAndSelf().Single(node => node.Path == @"C:\Data\Users");
        Assert.Equal(30, updatedUsers.SizeBytes);
        Assert.Single(updatedUsers.Children);
        Assert.DoesNotContain(updatedRoot.DescendantsAndSelf(), node => node.Path == @"C:\Data\Users\b.bin");
    }

    [Fact]
    public void TryRemovePath_should_remove_directory_and_reduce_root_size()
    {
        var removableDirectory = CreateDirectory(
            path: @"C:\Data\Cache",
            name: "Cache",
            sizeBytes: 40,
            children:
            [
                CreateFile(@"C:\Data\Cache\a.tmp", "a.tmp", 10),
                CreateFile(@"C:\Data\Cache\b.tmp", "b.tmp", 30),
            ]);

        var root = CreateDirectory(
            path: @"C:\Data",
            name: "Data",
            sizeBytes: 100,
            children:
            [
                removableDirectory,
                CreateFile(@"C:\Data\keep.bin", "keep.bin", 60),
            ]);

        var removed = FileSystemTreeEditor.TryRemovePath(root, @"C:\Data\Cache", out var updatedRoot);

        Assert.True(removed);
        Assert.Equal(60, updatedRoot.SizeBytes);
        Assert.Single(updatedRoot.Children);
        Assert.DoesNotContain(updatedRoot.DescendantsAndSelf(), node => node.Path == @"C:\Data\Cache");
    }

    [Fact]
    public void TryRemovePath_should_update_cached_descendant_counts_after_removal()
    {
        var removableDirectory = CreateDirectory(
            path: @"C:\Data\Cache",
            name: "Cache",
            sizeBytes: 40,
            children:
            [
                CreateFile(@"C:\Data\Cache\a.tmp", "a.tmp", 10),
                CreateFile(@"C:\Data\Cache\b.tmp", "b.tmp", 30),
            ]);

        var root = CreateDirectory(
            path: @"C:\Data",
            name: "Data",
            sizeBytes: 100,
            children:
            [
                removableDirectory,
                CreateFile(@"C:\Data\keep.bin", "keep.bin", 60),
            ]);

        var removed = FileSystemTreeEditor.TryRemovePath(root, @"C:\Data\Cache", out var updatedRoot);

        Assert.True(removed);
        Assert.Equal(1, updatedRoot.DescendantFileCount);
        Assert.Equal(1, updatedRoot.DescendantDirectoryCount);
    }

    [Fact]
    public void TryRemovePath_should_not_remove_root_node()
    {
        var root = CreateDirectory(
            path: @"C:\Data",
            name: "Data",
            sizeBytes: 30,
            children:
            [
                CreateFile(@"C:\Data\keep.bin", "keep.bin", 30),
            ]);

        var removed = FileSystemTreeEditor.TryRemovePath(root, @"C:\Data", out var updatedRoot);

        Assert.False(removed);
        Assert.Same(root, updatedRoot);
    }

    private static FileSystemNode CreateDirectory(string path, string name, long sizeBytes, IReadOnlyList<FileSystemNode> children)
    {
        var descendantFileCount = children.Sum(child => child.DescendantFileCount);
        var descendantDirectoryCount = 1 + children.Sum(child => child.DescendantDirectoryCount);
        return new FileSystemNode(
            Path: path,
            Name: name,
            Extension: string.Empty,
            SizeBytes: sizeBytes,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: children,
            DescendantFileCount: descendantFileCount,
            DescendantDirectoryCount: descendantDirectoryCount);
    }

    private static FileSystemNode CreateFile(string path, string name, long sizeBytes)
    {
        return new FileSystemNode(
            Path: path,
            Name: name,
            Extension: Path.GetExtension(name),
            SizeBytes: sizeBytes,
            Attributes: FileAttributes.Normal,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: false,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 0);
    }
}

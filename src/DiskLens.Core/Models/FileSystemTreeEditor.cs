namespace DiskLens.Core.Models;

public static class FileSystemTreeEditor
{
    public static bool TryRemovePath(FileSystemNode root, string targetPath, out FileSystemNode updatedRoot)
    {
        if (string.IsNullOrWhiteSpace(targetPath)
            || string.Equals(root.Path, targetPath, StringComparison.Ordinal))
        {
            updatedRoot = root;
            return false;
        }

        var changed = TryRemovePathCore(root, targetPath, out updatedRoot, out _);
        if (changed && root.LogicalSizeBytes is not null)
        {
            // Tree children are sorted for display, not in original scan order.
            // Keep surviving owners before selecting replacements for removed owners.
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in updatedRoot.EnumerateFiles())
            {
                if (file.StorageInfo?.Identity is { } identity
                    && !file.RiskTags.Contains("hard-link-shared"))
                {
                    owners.TryAdd(identity, file.Path);
                }
            }

            updatedRoot = RecalculateStorage(updatedRoot, owners);
        }
        return changed;
    }

    private static FileSystemNode RecalculateStorage(FileSystemNode node, Dictionary<string, string> owners)
    {
        if (!node.IsDirectory)
        {
            if (node.StorageInfo is not { } storage)
            {
                return node;
            }

            var isShared = false;
            if (storage.Identity is { } identity)
            {
                if (!owners.TryGetValue(identity, out var ownerPath))
                {
                    // The former owner was removed, so the first remaining link takes over.
                    ownerPath = node.Path;
                    owners.Add(identity, ownerPath);
                }

                isShared = !string.Equals(ownerPath, node.Path, StringComparison.Ordinal);
            }

            var tags = node.RiskTags.Where(tag => tag != "hard-link-shared").ToList();
            if (isShared)
            {
                tags.Add("hard-link-shared");
            }

            return node with { SizeBytes = isShared ? 0 : storage.AllocatedBytes, RiskTags = tags };
        }

        var children = node.Children.Select(child => RecalculateStorage(child, owners)).ToArray();
        return node with
        {
            Children = children,
            SizeBytes = children.Sum(child => child.SizeBytes),
            LogicalSizeBytes = children.Sum(child => child.LogicalSizeBytes ?? child.SizeBytes),
        };
    }

    private static bool TryRemovePathCore(
        FileSystemNode node,
        string targetPath,
        out FileSystemNode updatedNode,
        out long removedBytes)
    {
        updatedNode = node;
        removedBytes = 0;

        if (node.Children.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < node.Children.Count; index++)
        {
            var child = node.Children[index];
            if (string.Equals(child.Path, targetPath, StringComparison.Ordinal))
            {
                removedBytes = child.SizeBytes;
                var updatedChildren = node.Children.Where((_, childIndex) => childIndex != index).ToArray();
                updatedNode = node with
                {
                    SizeBytes = Math.Max(0, node.SizeBytes - removedBytes),
                    Children = updatedChildren,
                    DescendantFileCount = node.IsDirectory ? updatedChildren.Sum(item => item.DescendantFileCount) : 1,
                    DescendantDirectoryCount = node.IsDirectory ? 1 + updatedChildren.Sum(item => item.DescendantDirectoryCount) : 0,
                };
                return true;
            }

            if (TryRemovePathCore(child, targetPath, out var updatedChild, out removedBytes))
            {
                var children = node.Children.ToArray();
                children[index] = updatedChild;
                updatedNode = node with
                {
                    SizeBytes = Math.Max(0, node.SizeBytes - removedBytes),
                    Children = children,
                    DescendantFileCount = node.IsDirectory ? children.Sum(item => item.DescendantFileCount) : 1,
                    DescendantDirectoryCount = node.IsDirectory ? 1 + children.Sum(item => item.DescendantDirectoryCount) : 0,
                };
                return true;
            }
        }

        return false;
    }
}

using DiskLens.Core.Models;

namespace DiskLens.Core.Scanning;

/// <summary>Reconciles a refreshed subtree without losing unreadable old entries or double-counting links.</summary>
public static class RefreshTreeMerger
{
    public sealed record Result(FileSystemNode Root, int PreservedUnreadableCount);

    public static Result Replace(FileSystemNode root, string path, FileSystemNode replacement)
    {
        if (!string.Equals(path, replacement.Path, StringComparison.Ordinal))
            throw new ArgumentException("刷新结果与目标路径不一致。", nameof(replacement));
        var preserved = 0;
        var updated = ReplaceCore(root, path, replacement, ref preserved);
        return new Result(ReconcileAllocation(root, updated), preserved);
    }

    /// <summary>Apply one watcher micro-batch and reconcile physical allocation only once.</summary>
    public static Result ApplyEntries(FileSystemNode root, IReadOnlyList<FileSystemNode> replacements, IReadOnlySet<string> removedPaths)
    {
        var preserved = 0;
        var updated = removedPaths.Count == 0 ? root : RemoveCore(root, removedPaths, Ancestors(removedPaths));
        foreach (var replacement in replacements)
            updated = ReplaceCore(updated, replacement.Path, replacement, ref preserved);
        return new Result(ReconcileAllocation(root, updated), preserved);
    }

    public static FileSystemNode Remove(FileSystemNode root, IReadOnlySet<string> paths)
    {
        // A disappeared scan root is an unavailable task, never an empty successful scan.
        if (paths.Contains(root.Path)) return root;
        if (paths.Count == 0) return root;
        return ReconcileAllocation(root, RemoveCore(root, paths, Ancestors(paths)));
    }

    private static IReadOnlySet<string> Ancestors(IEnumerable<string> paths)
    {
        var affectedAncestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrWhiteSpace(parent); parent = Path.GetDirectoryName(parent))
                affectedAncestors.Add(TrimDirectoryEnd(parent));
        }
        return affectedAncestors;
    }

    private static FileSystemNode ReplaceCore(FileSystemNode node, string path, FileSystemNode replacement, ref int preserved)
    {
        if (string.Equals(node.Path, path, StringComparison.Ordinal)) return Merge(node, replacement, ref preserved);
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (!Within(path, child.Path)) continue;
            var updated = ReplaceCore(child, path, replacement, ref preserved);
            if (ReferenceEquals(child, updated)) return node;
            var children = node.Children.ToArray();
            children[i] = updated;
            return WithChildren(node, children, invalidateAnalysis: true);
        }
        if (node.IsDirectory && TrimDirectoryEnd(Path.GetDirectoryName(path) ?? "") == TrimDirectoryEnd(node.Path))
            return WithChildren(node, node.Children.Append(replacement).ToArray(), invalidateAnalysis: true);
        return node;
    }

    private static FileSystemNode RemoveCore(FileSystemNode node, IReadOnlySet<string> paths, IReadOnlySet<string> affectedAncestors)
    {
        if (!affectedAncestors.Contains(TrimDirectoryEnd(node.Path))) return node;
        List<FileSystemNode>? children = null;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var updated = paths.Contains(child.Path) ? null : RemoveCore(child, paths, affectedAncestors);
            if (!ReferenceEquals(child, updated) && children is null)
                children = node.Children.Take(i).ToList();
            if (children is not null && updated is not null) children.Add(updated);
        }
        return children is null ? node : WithChildren(node, children, invalidateAnalysis: true);
    }

    private static FileSystemNode Merge(FileSystemNode old, FileSystemNode fresh, ref int preserved)
    {
        var unreadable = fresh.RiskTags.Any(tag => tag is "error" or "skipped");
        if (unreadable)
        {
            preserved++;
            return MarkUnverified(old);
        }
        if (old.IsDirectory != fresh.IsDirectory) return fresh;
        if (!fresh.IsDirectory)
        {
            var same = SameMetadata(old, fresh) && old.StorageInfo == fresh.StorageInfo;
            return same ? old : fresh with { Analysis = null };
        }

        var oldByPath = old.Children.ToDictionary(child => child.Path, StringComparer.Ordinal);
        var children = new List<FileSystemNode>(fresh.Children.Count);
        var sameChildren = fresh.Children.Count == old.Children.Count;
        foreach (var child in fresh.Children)
        {
            var updated = oldByPath.Remove(child.Path, out var previous)
                ? Merge(previous, child, ref preserved) : child;
            children.Add(updated);
            sameChildren &= ReferenceEquals(previous, updated);
        }
        if (fresh.RiskTags.Contains("scan-error"))
        {
            // Enumeration can fail halfway through. Absence from that listing is not proof of deletion.
            foreach (var missingFromPartialListing in oldByPath.Values)
            {
                children.Add(MarkUnverified(missingFromPartialListing));
                preserved++;
                sameChildren = false;
            }
        }
        if (sameChildren && SameMetadata(old, fresh)) return old;
        // A path-purpose answer describes this directory, not its changing children.
        // Structural advice still expires when the contents change.
        var purpose = old.Analysis is { AnalysisEvidenceScope: AiAnalysisEvidenceScope.PathPurpose } analysis
            && old.Name == fresh.Name ? analysis : null;
        return WithChildren(fresh with { Analysis = purpose }, children, invalidateAnalysis: true);
    }

    private static FileSystemNode MarkUnverified(FileSystemNode node) => node with
    {
        Analysis = null,
        RiskTags = node.RiskTags.Contains("scan-error") ? node.RiskTags : node.RiskTags.Append("scan-error").ToArray(),
        Children = node.Children.Select(MarkUnverified).ToArray(),
    };

    private static bool SameMetadata(FileSystemNode left, FileSystemNode right) =>
        left.IsDirectory == right.IsDirectory && left.Name == right.Name && left.Extension == right.Extension
        && left.Attributes == right.Attributes && left.CreatedAt == right.CreatedAt && left.ModifiedAt == right.ModifiedAt
        && left.IsProtectedLocation == right.IsProtectedLocation
        && left.LogicalSizeBytes == right.LogicalSizeBytes
        && (left.StorageInfo is not null && right.StorageInfo is not null || left.SizeBytes == right.SizeBytes)
        && left.RiskTags.Where(tag => tag != "hard-link-shared")
            .SequenceEqual(right.RiskTags.Where(tag => tag != "hard-link-shared"));

    private static FileSystemNode WithChildren(FileSystemNode node, IReadOnlyList<FileSystemNode> children, bool invalidateAnalysis) => node with
    {
        Children = children,
        SizeBytes = children.Sum(child => child.SizeBytes),
        LogicalSizeBytes = node.LogicalSizeBytes is null ? null : children.Sum(child => child.LogicalSizeBytes ?? child.SizeBytes),
        DescendantFileCount = children.Sum(child => child.DescendantFileCount),
        DescendantDirectoryCount = 1 + children.Sum(child => child.DescendantDirectoryCount),
        Analysis = invalidateAnalysis && node.Analysis?.AnalysisEvidenceScope != AiAnalysisEvidenceScope.PathPurpose
            ? null : node.Analysis,
    };

    private static FileSystemNode ReconcileAllocation(FileSystemNode original, FileSystemNode updated)
    {
        if (ReferenceEquals(original, updated) || original.LogicalSizeBytes is null) return updated;
        var oldOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in original.EnumerateFiles())
            if (file.StorageInfo is { Identity: { } identity, LinkCount: > 1 } && !file.RiskTags.Contains("hard-link-shared"))
                oldOwners.TryAdd(identity, file.Path);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in updated.EnumerateFiles())
            if (file.StorageInfo?.Identity is { } identity && oldOwners.TryGetValue(identity, out var owner) && owner == file.Path)
                owners.TryAdd(identity, file.Path);
        return Recalculate(updated, owners);
    }

    private static FileSystemNode Recalculate(FileSystemNode node, Dictionary<string, string> owners)
    {
        if (!node.IsDirectory)
        {
            if (node.StorageInfo is not { } storage) return node;
            var shared = false;
            if (storage.Identity is { } identity && (storage.LinkCount > 1 || node.RiskTags.Contains("hard-link-shared") || owners.ContainsKey(identity)))
            {
                owners.TryAdd(identity, node.Path);
                shared = owners[identity] != node.Path;
            }
            var size = shared ? 0 : storage.AllocatedBytes;
            if (size == node.SizeBytes && shared == node.RiskTags.Contains("hard-link-shared")) return node;
            var tags = node.RiskTags.Where(tag => tag != "hard-link-shared").ToList();
            if (shared) tags.Add("hard-link-shared");
            return node with { SizeBytes = size, RiskTags = tags, Analysis = null };
        }
        FileSystemNode[]? children = null;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var updated = Recalculate(node.Children[i], owners);
            if (!ReferenceEquals(updated, node.Children[i]) && children is null) children = node.Children.ToArray();
            if (children is not null) children[i] = updated;
        }
        return children is null ? node : WithChildren(node, children, invalidateAnalysis: true);
    }

    private static bool Within(string path, string ancestor) => path == ancestor
        || path.StartsWith(ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string TrimDirectoryEnd(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

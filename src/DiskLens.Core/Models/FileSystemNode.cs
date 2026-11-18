namespace DiskLens.Core.Models;

public sealed record FileSystemNode(
    string Path,
    string Name,
    string Extension,
    long SizeBytes,
    FileAttributes Attributes,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    bool IsDirectory,
    bool IsProtectedLocation,
    IReadOnlyList<string> RiskTags,
    IReadOnlyList<FileSystemNode> Children,
    int DescendantFileCount = 0,
    int DescendantDirectoryCount = 0,
    AiAnalysisResult? Analysis = null)
{
    /// <summary>The sum of file lengths, including every hard-link path.</summary>
    public long? LogicalSizeBytes { get; init; }

    /// <summary>Unattributed storage metadata, including the full allocation of shared files.</summary>
    public FileStorageInfo? StorageInfo { get; init; }

    public bool HasChildren => Children.Count > 0;

    public IEnumerable<FileSystemNode> DescendantsAndSelf()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    public IEnumerable<FileSystemNode> EnumerateFiles() => DescendantsAndSelf().Where(node => !node.IsDirectory);
}

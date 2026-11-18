namespace DiskLens.Core.Models;

public sealed class FileSystemNodeIndex
{
    private readonly IReadOnlyDictionary<string, FileSystemNode> _nodesByPath;
    private readonly IReadOnlyDictionary<string, string?> _parentPathsByPath;

    private FileSystemNodeIndex(
        IReadOnlyDictionary<string, FileSystemNode> nodesByPath,
        IReadOnlyDictionary<string, string?> parentPathsByPath)
    {
        _nodesByPath = nodesByPath;
        _parentPathsByPath = parentPathsByPath;
    }

    public static FileSystemNodeIndex Empty { get; } = new(
        new Dictionary<string, FileSystemNode>(StringComparer.Ordinal),
        new Dictionary<string, string?>(StringComparer.Ordinal));

    /// <summary>
    /// Indexes only the root while navigation is disabled during a scan.
    /// Full descendant lookup is prepared once the completed tree is available.
    /// </summary>
    public static FileSystemNodeIndex CreateRootOnly(FileSystemNode root) => new(
        new Dictionary<string, FileSystemNode>(StringComparer.Ordinal) { [root.Path] = root },
        new Dictionary<string, string?>(StringComparer.Ordinal) { [root.Path] = null });

    public static FileSystemNodeIndex Build(FileSystemNode? root) => Build(root, CancellationToken.None);

    public static FileSystemNodeIndex Build(FileSystemNode? root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root is null)
        {
            return Empty;
        }

        var nodesByPath = new Dictionary<string, FileSystemNode>(StringComparer.Ordinal);
        var parentPathsByPath = new Dictionary<string, string?>(StringComparer.Ordinal);
        AddNode(root, parentPath: null, nodesByPath, parentPathsByPath, cancellationToken);
        return new FileSystemNodeIndex(nodesByPath, parentPathsByPath);
    }

    public FileSystemNode? FindNode(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return _nodesByPath.TryGetValue(path, out var node)
            ? node
            : null;
    }

    public string? FindParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return _parentPathsByPath.TryGetValue(path, out var parentPath)
            ? parentPath
            : null;
    }

    public FileSystemNode? FindParentNode(string? path)
    {
        var parentPath = FindParentPath(path);
        return string.IsNullOrWhiteSpace(parentPath)
            ? null
            : FindNode(parentPath);
    }

    private static void AddNode(
        FileSystemNode node,
        string? parentPath,
        IDictionary<string, FileSystemNode> nodesByPath,
        IDictionary<string, string?> parentPathsByPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nodesByPath[node.Path] = node;
        parentPathsByPath[node.Path] = parentPath;

        foreach (var child in node.Children)
        {
            AddNode(child, node.Path, nodesByPath, parentPathsByPath, cancellationToken);
        }
    }
}

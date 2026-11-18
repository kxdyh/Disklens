namespace DiskLens.Core.Models;

public static class FileSystemTreeMapper
{
    public static FileSystemNode ApplyAnalysisResults(FileSystemNode node, IReadOnlyDictionary<string, AiAnalysisResult> results)
    {
        // Snapshot once with an ordinal comparer; the input dictionary may use a
        // case-insensitive comparer and tree mapping must remain O(nodes + results).
        var resultsByExactPath = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            resultsByExactPath.TryAdd(result.Key, result.Value);
        }

        return ApplyAnalysisResultsCore(node, resultsByExactPath);
    }

    private static FileSystemNode ApplyAnalysisResultsCore(
        FileSystemNode node,
        IReadOnlyDictionary<string, AiAnalysisResult> resultsByExactPath)
    {
        var analysis = resultsByExactPath.TryGetValue(node.Path, out var result) ? result : node.Analysis;
        var children = node.Children.Select(child => ApplyAnalysisResultsCore(child, resultsByExactPath)).ToArray();
        return node with { Analysis = analysis, Children = children };
    }

    public static FileSystemNode? FindByPath(FileSystemNode? node, string? path)
    {
        if (node is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (string.Equals(node.Path, path, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindByPath(child, path);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}

using DiskLens.Core.Models;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Path-only cloud lookup candidates, independent of paid-analysis and display thresholds.</summary>
public sealed class SharedLookupCandidateBuilder
{
    public static IReadOnlyList<AiCandidateRecord> Build(FileSystemNode root,
        Func<string, bool> hasOwnIdentity, IReadOnlyList<string> registeredRoots,
        CancellationToken cancellationToken = default, SharedPathIndex? sharedPathIndex = null)
    {
        ArgumentNullException.ThrowIfNull(hasOwnIdentity);
        ArgumentNullException.ThrowIfNull(registeredRoots);
        return Build(root, cancellationToken);
    }

    /// <summary>Visit every scanned item; cloud index checks happen after candidate collection.</summary>
    public static IReadOnlyList<AiCandidateRecord> Build(FileSystemNode root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var results = new List<AiCandidateRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<FileSystemNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeLocalPath(node.Path, out var normalized) || !seen.Add(normalized)) continue;
            // Linked and duplicate subtrees never become a second route to a private target.
            if (node.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || node.RiskTags.Any(tag => tag.Equals("reparse-point", StringComparison.OrdinalIgnoreCase)
                    || tag.Equals("reparse-cycle", StringComparison.OrdinalIgnoreCase)
                    || tag.Equals("duplicate-target", StringComparison.OrdinalIgnoreCase))) continue;
            for (var index = node.Children.Count - 1; index >= 0; index--) pending.Push(node.Children[index]);
            if (normalized.Length <= 3 || node.SizeBytes < 0 || string.IsNullOrWhiteSpace(node.Name)
                || !node.Name.Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase)) continue;

            var unread = node.RiskTags.Any(tag => tag.Equals("scan-error", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("error", StringComparison.OrdinalIgnoreCase) || tag.Equals("skipped", StringComparison.OrdinalIgnoreCase));
            var candidate = new AiCandidateRecord(node.Path, node.Name, node.SizeBytes, node.IsDirectory,
                node.IsDirectory ? node.DescendantFileCount : 1, node.IsProtectedLocation, string.Empty, node)
            {
                AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
                HasUnreadItem = unread,
                HasIncompleteScan = unread,
                ScopeLimitation = unread ? "扫描存在未读取内容。" : null,
            };
            if (candidate.HasUsablePathMetadata && !candidate.HasLimitedAnalysisEvidence) results.Add(candidate);
        }
        return results;
    }

    private static bool TryNormalizeLocalPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] is not ('\\' or '/') || path[2..].Contains(':') || path.Any(char.IsControl)) return false;
        try
        {
            normalized = Path.GetFullPath(path.Replace('/', '\\'));
            if (normalized.Length > 3) normalized = normalized.TrimEnd('\\');
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { return false; }
    }


}

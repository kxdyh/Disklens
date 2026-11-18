using System.Security.Cryptography;
using System.Text;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Core.Ai;

public sealed class AiCandidateBuilder
{
    private const int SummaryChildLimit = 8;

    private readonly ILocationPolicy _locationPolicy;

    public AiCandidateBuilder(ILocationPolicy locationPolicy)
    {
        _locationPolicy = locationPolicy;
    }

    public IReadOnlyList<AiCandidateRecord> BuildCandidates(
        FileSystemNode root,
        long thresholdBytes,
        int maxUnitTokens,
        bool useTokenSavingMode = false,
        IReadOnlySet<string>? includedPaths = null)
    {
        if (includedPaths is { Count: 0 }) return [];

        var safeThreshold = Math.Max(1, thresholdBytes);
        var safeBudget = Math.Max(256, maxUnitTokens);
        var candidates = new List<AiCandidateRecord>();
        var signatureCache = new Dictionary<string, string>(StringComparer.Ordinal);

        if (includedPaths is null)
            CollectCandidates(root, safeThreshold, safeBudget, useTokenSavingMode, isRoot: true, signatureCache, candidates);
        else
        {
            // Explicit reanalysis targets the chosen items, including small items,
            // without creating candidates or estimating payloads for every other file.
            foreach (var node in root.DescendantsAndSelf())
                if (includedPaths.Contains(node.Path) && !IsVolumeRoot(node))
                    candidates.Add(BuildCandidate(node, safeBudget, useTokenSavingMode, signatureCache));
        }

        // Every caller (including standalone shared lookup/history) must carry
        // scan evidence, not only the paid-analysis service.
        var incompletePaths = FindIncompleteSubtreePaths(root);
        return candidates
            .Select(candidate => candidate with
            {
                AnalysisEvidenceScope = useTokenSavingMode
                    ? AiAnalysisEvidenceScope.PathPurpose : AiAnalysisEvidenceScope.DirectoryStructure,
            })
            .Select(candidate => incompletePaths.TryGetValue(candidate.Path, out var omission)
                ? candidate with
                {
                    HasIncompleteScan = true,
                    HasUnreadItem = GetDirectScanOmission(candidate.Structure) != ScanOmissionKind.None,
                    ScopeLimitation = FormatIncompleteScanReason(omission),
                } : candidate)
            .OrderByDescending(record => record.SizeBytes)
            .ThenBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CollectCandidates(
        FileSystemNode node,
        long thresholdBytes,
        int maxUnitTokens,
        bool useTokenSavingMode,
        bool isRoot,
        IDictionary<string, string> signatureCache,
        ICollection<AiCandidateRecord> candidates)
    {
        if (isRoot && IsVolumeRoot(node))
        {
            // A whole volume is only a scan container. A selected ordinary
            // directory is an item in its own right and follows the same rules
            // as every descendant below, including its own size threshold.
            foreach (var child in node.Children.OrderByDescending(child => child.SizeBytes))
            {
                CollectCandidates(child, thresholdBytes, maxUnitTokens, useTokenSavingMode, isRoot: false, signatureCache, candidates);
            }

            return;
        }

        var isDiagnosticNode = IsDiagnosticNode(node);
        if (node.SizeBytes < thresholdBytes && !isDiagnosticNode)
        {
            // Candidate inclusion is based on each file's own size. Directory
            // totals normally include child sizes, but keep walking small folders
            // so an inconsistent or partial snapshot cannot hide a large file.
            if (node.IsDirectory)
            {
                foreach (var child in node.Children.OrderByDescending(child => child.SizeBytes))
                {
                    CollectCandidates(child, thresholdBytes, maxUnitTokens, useTokenSavingMode, isRoot: false, signatureCache, candidates);
                }
            }

            return;
        }

        if (!node.IsDirectory)
        {
            candidates.Add(BuildRecord(node, signatureCache));
            return;
        }

        candidates.Add(BuildCandidate(node, maxUnitTokens, useTokenSavingMode, signatureCache));

        foreach (var child in node.Children.OrderByDescending(child => child.SizeBytes))
        {
            // Keep collecting oversized child files even when the parent directory
            // itself fits in one request, so each threshold-sized file gets its own result.
            CollectCandidates(child, thresholdBytes, maxUnitTokens, useTokenSavingMode, isRoot: false, signatureCache, candidates);
        }
    }

    private static bool IsVolumeRoot(FileSystemNode node)
    {
        if (!node.IsDirectory || !Path.IsPathFullyQualified(node.Path)) return false;
        var path = Path.GetFullPath(node.Path);
        var volume = Path.GetPathRoot(path);
        return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            volume?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private AiCandidateRecord BuildCandidate(FileSystemNode node, int maxUnitTokens, bool useTokenSavingMode,
        IDictionary<string, string> signatureCache)
    {
        var record = BuildRecord(node, signatureCache);
        return !node.IsDirectory || node.Children.Count == 0
            || AiPromptFactory.EstimateRecordTokens(record, useTokenSavingMode) <= maxUnitTokens
            ? record : BuildSummaryRecord(node, maxUnitTokens, useTokenSavingMode, signatureCache);
    }

    private static bool IsDiagnosticNode(FileSystemNode node)
    {
        bool HasTag(string tag) => node.RiskTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        if (HasTag("scan-error") || HasTag("error")) return true;
        if (!HasTag("skipped")) return false;

        // A zero-byte placeholder for a deliberately excluded link/duplicate target is
        // not a large file awaiting AI analysis. Windows trees can contain hundreds of
        // these aliases; promoting every alias made the analysis appear mostly failed.
        // Keep real read errors and unclassified omissions visible. Ancestors retain
        // internal completeness guards for removal, cache and shared contributions;
        // an excluded alias does not reduce confidence in their named purpose.
        var isExcludedAlias = node.SizeBytes == 0
            && (HasTag("reparse-point") || HasTag("reparse-cycle") || HasTag("duplicate-target"));
        return !isExcludedAlias;
    }

    private static Dictionary<string, ScanOmissionKind> FindIncompleteSubtreePaths(FileSystemNode root)
    {
        var incompletePaths = new Dictionary<string, ScanOmissionKind>(StringComparer.Ordinal);
        var nodes = root.DescendantsAndSelf().ToArray();
        for (var index = nodes.Length - 1; index >= 0; index--)
        {
            var node = nodes[index];
            var omission = GetDirectScanOmission(node);
            foreach (var child in node.Children)
                if (incompletePaths.TryGetValue(child.Path, out var childOmission)) omission |= childOmission;
            if (omission != ScanOmissionKind.None) incompletePaths[node.Path] = omission;
        }
        return incompletePaths;
    }

    private static ScanOmissionKind GetDirectScanOmission(FileSystemNode node)
    {
        bool HasTag(string tag) => node.RiskTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        var omission = HasTag("scan-error") || HasTag("error") ? ScanOmissionKind.ReadError : ScanOmissionKind.None;
        if (HasTag("skipped"))
            omission |= HasTag("reparse-point") || HasTag("reparse-cycle") || HasTag("duplicate-target")
                ? ScanOmissionKind.ExcludedAlias : ScanOmissionKind.OtherSkipped;
        return omission;
    }

    private static string FormatIncompleteScanReason(ScanOmissionKind omission)
    {
        if (omission.HasFlag(ScanOmissionKind.ReadError)) return "扫描存在读取错误。";
        if (omission.HasFlag(ScanOmissionKind.OtherSkipped)) return "扫描存在未读取内容。";
        return "扫描策略跳过了链接或重复目标。";
    }

    [Flags]
    private enum ScanOmissionKind { None = 0, ReadError = 1, ExcludedAlias = 2, OtherSkipped = 4 }

    private AiCandidateRecord BuildRecord(
        FileSystemNode node,
        IDictionary<string, string> signatureCache,
        FileSystemNode? structureOverride = null)
    {
        var structureNode = structureOverride ?? node;
        return new AiCandidateRecord(
            Path: node.Path,
            Name: node.Name,
            SizeBytes: node.SizeBytes,
            IsDirectory: node.IsDirectory,
            DescendantFileCount: node.IsDirectory ? node.DescendantFileCount : 1,
            IsProtectedLocation: node.IsProtectedLocation || _locationPolicy.IsProtectedLocation(node.Path),
            Signature: ComputeSignature(node, signatureCache),
            Structure: structureNode)
        { IsStructureSummary = structureOverride is not null };
    }

    private AiCandidateRecord BuildSummaryRecord(
        FileSystemNode node,
        int maxUnitTokens,
        bool useTokenSavingMode,
        IDictionary<string, string> signatureCache)
    {
        for (var childLimit = Math.Min(SummaryChildLimit, node.Children.Count); childLimit >= 1; childLimit--)
        {
            var summaryChildren = node.Children
                .OrderByDescending(child => child.SizeBytes)
                .Take(childLimit)
                .Select(child => child with { Children = [] })
                .ToArray();

            var summaryRecord = BuildRecord(node, signatureCache, node with { Children = summaryChildren });
            if (AiPromptFactory.EstimateRecordTokens(summaryRecord, useTokenSavingMode) <= maxUnitTokens)
            {
                return summaryRecord;
            }
        }

        return BuildRecord(node, signatureCache, node with { Children = [] });
    }

    private static string ComputeSignature(FileSystemNode node, IDictionary<string, string> signatureCache)
    {
        if (signatureCache.TryGetValue(node.Path, out var cachedSignature))
        {
            return cachedSignature;
        }

        var builder = new StringBuilder();
        AppendSignature(node, builder, signatureCache);
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        var signature = Convert.ToHexString(hash);
        signatureCache[node.Path] = signature;
        return signature;
    }

    private static void AppendSignature(
        FileSystemNode node,
        StringBuilder builder,
        IDictionary<string, string> signatureCache)
    {
        builder.Append(node.Path)
            .Append('|')
            .Append(node.IsDirectory)
            .Append('|')
            .Append(node.SizeBytes)
            .Append('|')
            .Append(node.LogicalSizeBytes ?? node.SizeBytes)
            .Append('|')
            .Append(node.CreatedAt?.UtcDateTime.Ticks ?? 0)
            .Append('|')
            // Replacements can retain their path, length and modification time.
            // Losing a known identity must also invalidate that object's cache.
            .Append(node.StorageInfo?.Identity ?? "<identity-unavailable>")
            .Append('|')
            .Append(node.ModifiedAt?.UtcDateTime.Ticks ?? 0)
            .Append('|')
            .Append(node.Children.Count)
            .AppendLine();

        foreach (var child in node.Children.OrderBy(child => child.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(ComputeSignature(child, signatureCache)).AppendLine();
        }
    }
}

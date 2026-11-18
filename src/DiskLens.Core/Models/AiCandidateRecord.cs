namespace DiskLens.Core.Models;

public sealed record AiCandidateRecord(
    string Path,
    string Name,
    long SizeBytes,
    bool IsDirectory,
    int DescendantFileCount,
    bool IsProtectedLocation,
    string Signature,
    FileSystemNode Structure)
{
    public bool HasIncompleteScan { get; init; }
    public bool HasUnreadItem { get; init; }
    public bool IsStructureSummary { get; init; }
    public string? ScopeLimitation { get; init; }
    public bool HasLimitedScope => HasIncompleteScan || HasUnreadItem || IsStructureSummary;
    public AiAnalysisEvidenceScope AnalysisEvidenceScope { get; init; }

    // Child coverage is not evidence for a request that contains only this item's
    // path and size. Keep scan completeness available for whole-directory advice.
    public bool HasLimitedAnalysisEvidence => AnalysisEvidenceScope == AiAnalysisEvidenceScope.PathPurpose
        ? HasUnreadItem && !HasUsablePathMetadata : HasLimitedScope;

    public bool HasUsablePathMetadata => !string.IsNullOrWhiteSpace(Path) && !string.IsNullOrWhiteSpace(Name)
        && (SizeBytes > 0 || Structure.CreatedAt is not null || Structure.ModifiedAt is not null
            || Structure.StorageInfo is not null || Structure.Children.Count > 0 || IsStructureSummary);
}

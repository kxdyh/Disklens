namespace DiskLens.Core.Models;

// Frozen after memory and cloud lookup; these are the actual model candidates.
public sealed record AiAnalysisSubmissionPreview(
    string RootPath,
    IReadOnlyList<AiCandidateRecord> Items,
    int LocalCacheCount,
    int SharedExperienceCount,
    int ProtectedCount)
{
    public int TotalCandidateCount { get; init; } = Items.Count + LocalCacheCount + SharedExperienceCount;
    public int UnreadableCount { get; init; }
    public bool ProviderConfigured { get; init; } = true;
}

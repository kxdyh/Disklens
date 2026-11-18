namespace DiskLens.Core.Models;

public sealed record ScanSessionOptions(
    string RootPath,
    long AnalysisThresholdBytes = 512L * 1024L * 1024L,
    bool ExcludeSystemProtected = true,
    bool FollowReparsePoints = false,
    bool IncludeIntermediateTreeSnapshots = false,
    bool UseTokenSavingMode = false,
    int MaxAiChunkTokens = 2400,
    int MaxAiBatchSize = 20,
    int ProgressReportInterval = 128,
    int TreeSnapshotIntervalMilliseconds = 250,
    int MaxAiAnalysisTokens = 100_000,
    string? AnalysisMemoryRootPath = null,
    // Callers may attach the scan snapshot's monotonic generation. Zero preserves
    // compatibility by allocating a stable generation for the supplied snapshot object.
    long AnalysisSnapshotGeneration = 0,
    IReadOnlySet<string>? ExcludedAnalysisPaths = null,
    bool BypassAnalysisMemory = false)
{
    /// <summary>Explicit batch selection; an empty set means no analysis, never the entire scope.</summary>
    public IReadOnlySet<string>? IncludedAnalysisPaths { get; init; }

    /// <summary>A recent, consent-bound lookup explicitly supplied by the owning scan.</summary>
    public AiSharedLookupReuse? SharedLookupReuse { get; init; }
}

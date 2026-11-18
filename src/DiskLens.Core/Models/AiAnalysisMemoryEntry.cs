using System.Text.Json.Serialization;

namespace DiskLens.Core.Models;

public sealed record AiAnalysisMemoryEntry(
    string Path,
    bool IsDirectory,
    string Signature,
    AiAnalysisResult Result,
    DateTimeOffset AnalyzedAt,
    AiProviderKind ProviderKind = AiProviderKind.Disabled,
    string Endpoint = "",
    string Model = "",
    string PromptFingerprint = "",
    bool UseTokenSavingMode = false,
    long AnalysisThresholdBytes = 0,
    int MaxAiChunkTokens = 2400,
    string? ReasoningEffort = null,
    double Temperature = 0.1,
    long SnapshotGeneration = 0)
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);

    // Kept locally until a consent-bound contribution is durably queued or ruled ineligible.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SharedContributionPending { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SharedContributionCompleted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SharedContributionConsentFingerprint { get; init; }

    // AnalyzedAt is the original analysis time; reading or checkpointing never renews it.
    public bool IsUsableAt(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(Path) && !string.IsNullOrWhiteSpace(Signature)
        && Result is not null && !string.IsNullOrWhiteSpace(Result.Reason)
        && !string.Equals(Result.Reason.Trim(), "AI 未提供解释。", StringComparison.Ordinal)
        && Result.Origin is AiAnalysisResultOrigin.AiAnalyzed or AiAnalysisResultOrigin.CacheReused
        && string.Equals(Path, Result.Path, StringComparison.Ordinal)
        && AnalyzedAt > now - MaximumAge && AnalyzedAt <= now
        && (Result.OriginalAnalyzedAt is null || Result.OriginalAnalyzedAt > now - MaximumAge && Result.OriginalAnalyzedAt <= now)
        && !Result.IsAnalysisEvidenceLimited && !Result.LooksLikeFallback();

    public bool MatchesAnalysisContext(
        AiProviderKind providerKind,
        string endpoint,
        string model,
        string promptFingerprint,
        bool useTokenSavingMode,
        long analysisThresholdBytes,
        int maxAiChunkTokens,
        string? reasoningEffort = null,
        double temperature = 0.1)
    {
        return ProviderKind == providerKind
               && string.Equals(NormalizeEndpoint(Endpoint), NormalizeEndpoint(endpoint), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Model, model, StringComparison.OrdinalIgnoreCase)
               && string.Equals(PromptFingerprint, promptFingerprint, StringComparison.OrdinalIgnoreCase)
               && UseTokenSavingMode == useTokenSavingMode
               && AnalysisThresholdBytes == analysisThresholdBytes
               && string.Equals(ReasoningEffort?.Trim() ?? "", reasoningEffort?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)
               && double.IsFinite(Temperature) && Temperature == temperature
               && (useTokenSavingMode
                   || !IsDirectory
                   || MaxAiChunkTokens == Math.Max(256, maxAiChunkTokens));
    }

    private static string NormalizeEndpoint(string? endpoint)
    {
        return endpoint?.Trim().TrimEnd('/') ?? "";
    }
}

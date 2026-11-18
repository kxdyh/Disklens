using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed record AiBatch(
    IReadOnlyList<AiCandidateRecord> Records,
    int EstimatedTokens,
    bool UseTokenSavingMode = false,
    bool UseCompactPayload = false)
{
    public AiRequestContext? RequestContext { get; init; }
}

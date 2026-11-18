using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public interface IAiProvider
{
    AiProviderKind Kind { get; }

    Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
        AiBatch batch,
        AiProviderOptions options,
        CancellationToken cancellationToken = default);
}

using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed record AiProviderOptions(
    AiProviderKind Kind,
    string Endpoint,
    string Model,
    string? ApiKey,
    double Temperature = 0.1,
    string? ReasoningEffort = null)
{
    public bool IsConfigured =>
        Kind != AiProviderKind.Disabled
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Model);
}

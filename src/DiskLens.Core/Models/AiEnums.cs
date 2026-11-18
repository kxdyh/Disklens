namespace DiskLens.Core.Models;

public enum RiskLevel
{
    Unknown,
    Low,
    Review,
    High,
    Protected,
}

public enum SuggestedAction
{
    Review,
    Keep,
    RecycleCandidate,
    DoNotDelete,
}

public enum AiProviderKind
{
    Disabled,
    OpenAiCompatible,
    Ollama,
}

public enum AiAnalysisResultOrigin
{
    AiAnalyzed,
    CacheReused,
    ProtectedByRule,
    Incomplete,
    SharedExperience,
}

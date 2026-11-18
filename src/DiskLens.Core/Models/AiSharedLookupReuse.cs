using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Core.Models;

/// <summary>Read-only cloud answers and explicit misses bound to one scan's consent and lookup time.</summary>
public sealed record AiSharedLookupReuse(
    SharedConsentSnapshot Consent,
    IReadOnlyDictionary<string, AiAnalysisResult> ResultsByPath,
    IReadOnlySet<string> MissingPaths,
    DateTimeOffset CapturedAt);

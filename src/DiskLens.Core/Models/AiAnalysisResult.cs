namespace DiskLens.Core.Models;

public sealed record AiAnalysisResult(
    string Path,
    RiskLevel RiskLevel,
    SuggestedAction SuggestedAction,
    string Reason,
    double Confidence,
    bool IsFallback = false)
{
    private const string StaleReuseReasonPrefix = "沿用上次分析结果；本次重新分析失败：";

    // The model produces both texts in one response; older records may omit them.
    // Presentation can display a controlled cloud category without changing these fields.
    public string ShortPurpose { get; init; } = string.Empty;

    // Generated with the Chinese texts in the same analysis. Legacy records stay empty.
    public string ShortPurposeEn { get; init; } = string.Empty;
    public string ReasonEn { get; init; } = string.Empty;

    public AiAnalysisResultOrigin Origin { get; init; } = AiAnalysisResultOrigin.AiAnalyzed;

    public AiConfidenceLevel ConfidenceLevel { get; init; } = AiConfidenceLevel.Unknown;
    public string PurposeCategory { get; init; } = AiPurposeCategory.Unknown;
    public bool IsScopeLimited { get; init; }
    public AiScopeWarning ScopeWarning { get; init; }
    public AiAnalysisEvidenceScope AnalysisEvidenceScope { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAnalysisEvidenceLimited => LooksLikeFallback()
        || IsScopeLimited && AnalysisEvidenceScope != AiAnalysisEvidenceScope.PathPurpose;

    // null retains the required legacy constructor's numeric value; false means
    // the new response omitted it. Absence must not turn an explicit grade into zero.
    public bool? HasExplicitNumericConfidence { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public AiConfidenceLevel EffectiveConfidenceLevel
    {
        get
        {
            if (ConfidenceLevel is not (AiConfidenceLevel.Low or AiConfidenceLevel.Medium or AiConfidenceLevel.High))
                return AiConfidenceLevel.Unknown;
            if (ConfidenceLevel == AiConfidenceLevel.Low || HasExplicitNumericConfidence == false)
                return ConfidenceLevel;
            if (!double.IsFinite(Confidence) || Confidence < 0.5)
                return AiConfidenceLevel.Low;
            return Confidence < 0.8 && ConfidenceLevel == AiConfidenceLevel.High
                ? AiConfidenceLevel.Medium : ConfidenceLevel;
        }
    }

    public string? OriginalAnalysisId { get; init; }
    public DateTimeOffset? OriginalAnalyzedAt { get; init; }
    public DiskLens.Core.SharedKnowledge.SharedExperienceMetadata? SharedEntry { get; init; }

    public bool LooksLikeFallback()
    {
        if (Origin == AiAnalysisResultOrigin.SharedExperience && SharedEntry is not null) return false;
        return Origin == AiAnalysisResultOrigin.Incomplete
               || IsFallback
               || Reason.StartsWith(StaleReuseReasonPrefix, StringComparison.Ordinal)
               || (RiskLevel == RiskLevel.Review
                   && SuggestedAction == SuggestedAction.Review
                   && ConfidenceLevel is not (AiConfidenceLevel.Low or AiConfidenceLevel.Medium or AiConfidenceLevel.High)
                   && Confidence <= 0);
    }

    public static AiAnalysisResult CreateFallback(string path, string reason)
    {
        return new AiAnalysisResult(
            Path: path,
            RiskLevel: RiskLevel.Review,
            SuggestedAction: SuggestedAction.Review,
            Reason: reason,
            Confidence: 0,
            IsFallback: true)
        {
            Origin = AiAnalysisResultOrigin.Incomplete,
        };
    }

    public static AiAnalysisResult CreateStaleReuse(string path, AiAnalysisResult previousResult, string failureReason)
    {
        return previousResult with
        {
            Path = path,
            Reason = $"沿用上次分析结果；本次重新分析失败：{failureReason}",
            // The old explanation no longer describes this failure state.
            ReasonEn = string.Empty,
            Confidence = Math.Min(previousResult.Confidence, 0.45),
            ConfidenceLevel = AiConfidenceLevel.Unknown,
            IsFallback = false,
            Origin = AiAnalysisResultOrigin.Incomplete,
        };
    }
}

namespace DiskLens.Core.Policies;

public sealed record LocationAssessment(
    bool IsProtectedLocation,
    IReadOnlyList<string> RiskTags);

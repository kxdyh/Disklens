namespace DiskLens.Core.Models;

/// <summary>The evidence actually supplied to the model, independent of scan coverage.</summary>
public enum AiAnalysisEvidenceScope
{
    DirectoryStructure = 0,
    PathPurpose = 1,
}

/// <summary>Evidence completeness, independent of confidence in an item's named purpose.</summary>
[Flags]
public enum AiScopeWarning
{
    None = 0,
    ItemReadIncomplete = 1,
    DescendantReadIncomplete = 2,
    StructureSummary = 4,
}

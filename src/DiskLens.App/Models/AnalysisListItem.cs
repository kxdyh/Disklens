using DiskLens.Core.Models;

namespace DiskLens.App.Models;

public sealed record AnalysisListItem(
    string Path,
    string Name,
    string SizeText,
    RiskLevel RiskLevel,
    SuggestedAction SuggestedAction,
    string OriginalReason,
    FileSystemNode Node)
{
    public string Reason => Node.Analysis is { } analysis ? L.Reason(analysis) : L.T(OriginalReason);
    public string SourceLabel => AnalysisSourcePresentation.LabelFor(Node.Analysis);
    public string SourceIcon => AnalysisSourcePresentation.IconFor(Node.Analysis);
}

internal static class AnalysisSourcePresentation
{
    public static string LabelFor(AiAnalysisResult? analysis) => analysis?.Origin switch
    {
        AiAnalysisResultOrigin.SharedExperience => analysis.SharedEntry?.IsOwnObservation == true
            ? L.T("本人的云端分析") : L.T("云端经验"),
        AiAnalysisResultOrigin.AiAnalyzed => L.T("AI 分析"),
        AiAnalysisResultOrigin.CacheReused => L.T("AI 分析 · 本地缓存"),
        AiAnalysisResultOrigin.ProtectedByRule => L.T("本地保护规则"),
        _ => L.T("尚未完成")
    };

    public static string IconFor(AiAnalysisResult? analysis) => analysis?.Origin switch
    {
        AiAnalysisResultOrigin.SharedExperience => "☁",
        AiAnalysisResultOrigin.AiAnalyzed => "✦",
        AiAnalysisResultOrigin.CacheReused => "↺",
        AiAnalysisResultOrigin.ProtectedByRule => "◆",
        _ => "…"
    };
}

using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed record AiAnalysisSummary(
    int AiAnalyzedCount,
    int CacheReusedCount,
    int ProtectedCount,
    int IncompleteCount,
    int SharedExperienceCount = 0)
{
    public bool IsComplete => IncompleteCount == 0;

    public static AiAnalysisSummary FromResults(IEnumerable<AiAnalysisResult> results)
    {
        var aiAnalyzedCount = 0;
        var cacheReusedCount = 0;
        var protectedCount = 0;
        var incompleteCount = 0;
        var sharedCount = 0;

        foreach (var result in results)
        {
            if (result.LooksLikeFallback())
            {
                incompleteCount++;
                continue;
            }

            switch (result.Origin)
            {
                case AiAnalysisResultOrigin.AiAnalyzed:
                    aiAnalyzedCount++;
                    break;
                case AiAnalysisResultOrigin.CacheReused:
                    cacheReusedCount++;
                    break;
                case AiAnalysisResultOrigin.ProtectedByRule:
                    protectedCount++;
                    break;
                case AiAnalysisResultOrigin.SharedExperience:
                    sharedCount++;
                    break;
                case AiAnalysisResultOrigin.Incomplete:
                default:
                    incompleteCount++;
                    break;
            }
        }

        return new AiAnalysisSummary(aiAnalyzedCount, cacheReusedCount, protectedCount, incompleteCount, sharedCount);
    }

    public string FormatStatusMessage(bool wasCanceled)
    {
        var resultCounts = $"AI 分析 {AiAnalyzedCount} 项，缓存复用 {CacheReusedCount} 项，本地保护 {ProtectedCount} 项，未完成 {IncompleteCount} 项";
        if (SharedExperienceCount > 0) resultCounts += $"，共享经验 {SharedExperienceCount} 项";

        if (wasCanceled)
        {
            return $"本次扫描 AI 分析已取消：{resultCounts}。未完成项目可再次分析。";
        }

        return IsComplete
            ? $"本次扫描 AI 分析完成：{resultCounts}。"
            : $"本次扫描 AI 分析部分完成：{resultCounts}。未完成项目可再次分析。";
    }
}

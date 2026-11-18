using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiAnalysisSummaryTests
{
    [Fact]
    public void FromResults_should_distinguish_analyzed_cached_protected_and_incomplete_candidates()
    {
        var results = new[]
        {
            new AiAnalysisResult("analyzed.bin", RiskLevel.Low, SuggestedAction.RecycleCandidate, "已分析。", 0.9),
            new AiAnalysisResult("cached.bin", RiskLevel.Review, SuggestedAction.Review, "复用缓存。", 0.8)
            {
                Origin = AiAnalysisResultOrigin.CacheReused,
            },
            new AiAnalysisResult("protected.bin", RiskLevel.Protected, SuggestedAction.DoNotDelete, "本地保护。", 1.0)
            {
                Origin = AiAnalysisResultOrigin.ProtectedByRule,
            },
            AiAnalysisResult.CreateFallback("incomplete.bin", "AI 尚未分析，可重试。"),
        };

        var summary = AiAnalysisSummary.FromResults(results);

        Assert.Equal(1, summary.AiAnalyzedCount);
        Assert.Equal(1, summary.CacheReusedCount);
        Assert.Equal(1, summary.ProtectedCount);
        Assert.Equal(1, summary.IncompleteCount);
        Assert.False(summary.IsComplete);
        Assert.Equal(
            "本次扫描 AI 分析部分完成：AI 分析 1 项，缓存复用 1 项，本地保护 1 项，未完成 1 项。未完成项目可再次分析。",
            summary.FormatStatusMessage(wasCanceled: false));

        var completeSummary = AiAnalysisSummary.FromResults(results.Take(3));
        Assert.True(completeSummary.IsComplete);
        Assert.Equal(
            "本次扫描 AI 分析完成：AI 分析 1 项，缓存复用 1 项，本地保护 1 项，未完成 0 项。",
            completeSummary.FormatStatusMessage(wasCanceled: false));
    }

    [Fact]
    public void FormatStatusMessage_should_not_call_an_incomplete_run_completed()
    {
        var summary = AiAnalysisSummary.FromResults(
        [
            AiAnalysisResult.CreateFallback("unavailable.bin", "AI 服务暂不可用，可重试。"),
        ]);

        var message = summary.FormatStatusMessage(wasCanceled: false);

        Assert.Contains("部分完成", message, StringComparison.Ordinal);
        Assert.Contains("未完成 1 项", message, StringComparison.Ordinal);
        Assert.Contains("可再次分析", message, StringComparison.Ordinal);
        Assert.DoesNotContain("分析完成，共返回", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromResults_should_treat_stale_reuse_as_incomplete_and_retryable()
    {
        var staleResult = AiAnalysisResult.CreateStaleReuse(
            "stale.bin",
            new AiAnalysisResult("stale.bin", RiskLevel.High, SuggestedAction.Keep, "旧结果。", 0.9),
            "AI 服务暂不可用。");

        var summary = AiAnalysisSummary.FromResults([staleResult]);

        Assert.True(staleResult.LooksLikeFallback());
        Assert.Equal(1, summary.IncompleteCount);
        Assert.Contains("可再次分析", summary.FormatStatusMessage(wasCanceled: false), StringComparison.Ordinal);
    }

    [Fact]
    public void FromResults_should_not_treat_a_legacy_stale_cache_entry_as_completed()
    {
        var legacyStaleResult = new AiAnalysisResult(
            "legacy-stale.bin",
            RiskLevel.Protected,
            SuggestedAction.DoNotDelete,
            "沿用上次分析结果；本次重新分析失败：AI 服务暂不可用。",
            0.45);

        var summary = AiAnalysisSummary.FromResults([legacyStaleResult]);

        Assert.True(legacyStaleResult.LooksLikeFallback());
        Assert.Equal(1, summary.IncompleteCount);
        Assert.False(summary.IsComplete);
    }
}

using System.Globalization;

namespace DiskLens.Core.Ai;

public sealed record AiUsageCostEstimate(decimal MinimumCny, decimal MaximumCny, bool UsesConservativeCachePricing)
{
    public static DateOnly SourceDate => new(2026, 11, 6);
    public const string SourceUrl = "https://api-docs.deepseek.com/zh-cn/quick_start/pricing/";

    public string FormatStatusMessage()
    {
        if (MaximumCny == 0) return "本次接口用量费用估算 ¥0。";
        var low = MinimumCny.ToString("0.0000", CultureInfo.InvariantCulture);
        var high = MaximumCny.ToString("0.0000", CultureInfo.InvariantCulture);
        return $"按 DeepSeek 官方单价估算 ¥{low}–{high}（空闲至高峰时段{(UsesConservativeCachePricing ? "；未提供缓存明细的输入按未命中计" : string.Empty)}），实际扣费以服务商账单为准。";
    }
}

/// <summary>Explicit, dated tariff estimate. Never assumes that an arbitrary compatible endpoint has DeepSeek prices.</summary>
public static class AiUsageCostEstimator
{
    public static AiUsageCostEstimate? Estimate(AiProviderOptions options, AiTokenBudgetReport report)
    {
        if (options.Kind != Models.AiProviderKind.OpenAiCompatible
            || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "https" || !endpoint.IsDefaultPort || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.Equals(endpoint.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase)
            || endpoint.AbsolutePath.Trim('/') is not ("" or "v1")
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return null;
        }

        // RMB per million tokens, verified from the official pricing page on SourceDate.
        // A range avoids silently guessing peak hours or the Chinese public-holiday calendar.
        var rates = options.Model.Trim().ToLowerInvariant() switch
        {
            "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp" => (Hit: 0.02m, Miss: 1m, Output: 4m),
            "deepseek-v4-pro" => (Hit: 0.15m, Miss: 4.5m, Output: 13.5m),
            _ => (Hit: -1m, Miss: -1m, Output: -1m),
        };
        if (rates.Hit < 0 || report.EstimatedTokens != 0 || !report.HasCompleteUsageBreakdown
            || report.ProviderInputTokens < 0 || report.ProviderOutputTokens < 0
            || report.ProviderInputTokens > long.MaxValue - report.ProviderOutputTokens
            || report.ProviderReportedTokens != report.ProviderInputTokens + report.ProviderOutputTokens
            || report.ProviderCacheHitTokens < 0 || report.ProviderCacheMissTokens < 0
            || report.ProviderInputTokensWithoutCacheBreakdown < 0
            || report.ProviderCacheHitTokens > report.ProviderInputTokens
            || report.ProviderCacheMissTokens > report.ProviderInputTokens - report.ProviderCacheHitTokens
            || report.ProviderInputTokensWithoutCacheBreakdown != report.ProviderInputTokens - report.ProviderCacheHitTokens - report.ProviderCacheMissTokens)
        {
            return null;
        }

        var minimum = (report.ProviderCacheHitTokens * rates.Hit
            + (report.ProviderCacheMissTokens + (decimal)report.ProviderInputTokensWithoutCacheBreakdown) * rates.Miss
            + report.ProviderOutputTokens * rates.Output) / 1_000_000m;
        return new AiUsageCostEstimate(minimum, minimum * 2, report.ProviderInputTokensWithoutCacheBreakdown > 0);
    }
}

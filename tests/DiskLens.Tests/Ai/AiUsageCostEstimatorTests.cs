using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiUsageCostEstimatorTests
{
    private static readonly AiProviderOptions Options = new(AiProviderKind.OpenAiCompatible, "https://api.deepseek.com", "deepseek-flash", "test");
    private static AiTokenBudgetReport Usage => new(100_000, 1600, 1600, 0, 1, false)
    {
        ProviderInputTokens = 1000, ProviderOutputTokens = 600,
        ProviderCacheHitTokens = 800, ProviderCacheMissTokens = 200,
        HasCompleteUsageBreakdown = true,
    };

    [Fact]
    public void DeepSeek_cache_figures_are_not_added_to_total_and_can_be_inferred_from_miss()
    {
        var context = Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":600,"total_tokens":1600,"prompt_cache_miss_tokens":200}}""");
        Assert.Equal(800, context.Usage!.InputCacheHitTokens);
        Assert.Equal(200, context.Usage.InputCacheMissTokens);
        Assert.Equal(1600, context.Usage.TotalTokens);
    }

    [Fact]
    public void OpenAi_cached_tokens_detail_is_supported()
    {
        var context = Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":600,"prompt_tokens_details":{"cached_tokens":800}}}""");
        Assert.Equal(800, context.Usage!.InputCacheHitTokens);
        Assert.Equal(200, context.Usage.InputCacheMissTokens);
    }

    [Theory]
    [InlineData(1001, 0)]
    [InlineData(700, 200)]
    [InlineData(0, 1001)]
    public void Inconsistent_cache_usage_is_unknown_instead_of_underbilling(int hit, int miss)
    {
        var context = Parse(JsonSerializer.Serialize(new { usage = new { prompt_tokens = 1000, completion_tokens = 600, prompt_cache_hit_tokens = hit, prompt_cache_miss_tokens = miss } }));
        Assert.Null(context.Usage!.InputCacheHitTokens);
        Assert.Null(context.Usage.InputCacheMissTokens);
    }

    [Fact]
    public void Current_flash_RMB_tariff_produces_an_explicit_offpeak_to_peak_range()
    {
        var estimate = AiUsageCostEstimator.Estimate(Options, Usage)!;
        Assert.Equal(0.002616m, estimate.MinimumCny);
        Assert.Equal(0.005232m, estimate.MaximumCny);
        Assert.False(estimate.UsesConservativeCachePricing);
        Assert.Contains("估算", estimate.FormatStatusMessage());
        Assert.Equal(new DateOnly(2026, 11, 6), AiUsageCostEstimate.SourceDate);
    }

    [Fact]
    public void Unknown_cache_input_uses_full_miss_price()
    {
        var report = Usage with { ProviderCacheHitTokens = 0, ProviderCacheMissTokens = 0, ProviderInputTokensWithoutCacheBreakdown = 1000 };
        var estimate = AiUsageCostEstimator.Estimate(Options, report)!;
        Assert.Equal(0.0034m, estimate.MinimumCny);
        Assert.True(estimate.UsesConservativeCachePricing);
    }

    [Theory]
    [InlineData("https://api.deepseek.com.evil.invalid", "deepseek-flash")]
    [InlineData("http://api.deepseek.com", "deepseek-flash")]
    [InlineData("https://api.deepseek.com:8443", "deepseek-flash")]
    [InlineData("https://api.deepseek.com/forward", "deepseek-flash")]
    [InlineData("https://api.deepseek.com", "future-unknown-model")]
    public void Unverified_models_or_endpoints_never_get_DeepSeek_prices(string endpoint, string model)
    {
        Assert.Null(AiUsageCostEstimator.Estimate(Options with { Endpoint = endpoint, Model = model }, Usage));
    }

    [Fact]
    public void Partial_usage_or_uncertain_requests_do_not_claim_a_cost()
    {
        Assert.Null(AiUsageCostEstimator.Estimate(Options, Usage with { EstimatedTokens = 300 }));
        Assert.Null(AiUsageCostEstimator.Estimate(Options, Usage with { HasCompleteUsageBreakdown = false }));
        Assert.Null(AiUsageCostEstimator.Estimate(Options, Usage with { ProviderReportedTokens = 2000 }));
        Assert.Null(AiUsageCostEstimator.Estimate(Options, Usage with { ProviderCacheHitTokens = 2000 }));
        Assert.False(Parse("""{"usage":{"total_tokens":300}}""").Usage!.HasInputOutputBreakdown);
    }

    [Fact]
    public async Task Aggregation_includes_each_request_once_and_keeps_mixed_cache_coverage()
    {
        var files = Enumerable.Range(0, 2).Select(i => new FileSystemNode($"E:\\Usage\\{i}.bin", $"{i}.bin", ".bin", 2000, FileAttributes.Normal, null, null, false, false, [], [])).ToArray();
        var root = new FileSystemNode(@"E:\", "E:", "", 4000, FileAttributes.Directory, null, null, true, false, [], files);
        var service = new AiAnalysisService([new Provider()], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner());
        var report = await service.AnalyzeWithReportAsync(root, new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1, UseTokenSavingMode: true, MaxAiBatchSize: 1), Options);
        Assert.Equal(2, report.TokenBudget.RequestCount);
        Assert.Equal(2000, report.TokenBudget.ProviderInputTokens);
        Assert.Equal(1200, report.TokenBudget.ProviderOutputTokens);
        Assert.Equal(800, report.TokenBudget.ProviderCacheHitTokens);
        Assert.Equal(200, report.TokenBudget.ProviderCacheMissTokens);
        Assert.Equal(1000, report.TokenBudget.ProviderInputTokensWithoutCacheBreakdown);
        Assert.True(report.TokenBudget.HasCompleteUsageBreakdown);
        Assert.Equal(3200, report.TokenBudget.ProviderReportedTokens);
    }

    private static AiRequestContext Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var context = new AiRequestContext("", "", 1024);
        context.ReadOpenAiUsage(doc.RootElement);
        return context;
    }

    private sealed class Provider : IAiProvider
    {
        private int _calls;
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            var usage = new AiTokenUsage(1000, 600);
            if (++_calls == 1) usage = usage with { InputCacheHitTokens = 800, InputCacheMissTokens = 200 };
            batch.RequestContext!.ReportUsage(usage);
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(r => new AiAnalysisResult(r.Path, RiskLevel.Review, SuggestedAction.Review, "test", 0.5)).ToArray());
        }
    }
}

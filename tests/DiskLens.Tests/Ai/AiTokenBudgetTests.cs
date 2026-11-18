using System.Net;
using System.Text;
using System.Text.Json;
using DiskLens.App.Models;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiTokenBudgetTests
{
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo", "test-key");

    [Fact]
    public async Task Total_budget_is_shared_across_batches_and_preserves_completed_results_and_cache()
    {
        var store = new MemoryStore();
        var provider = new ReportingProvider((batch, _) =>
        {
            batch.RequestContext!.ReportUsage(new AiTokenUsage(8_000, 2_000));
            return ResultsFor(batch);
        });
        var report = await Service(provider, store).AnalyzeWithReportAsync(Root(3), Options(20_000), ProviderOptions);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(20_000, report.TokenBudget.ProviderReportedTokens);
        Assert.Equal(0, report.TokenBudget.EstimatedTokens);
        Assert.True(report.TokenBudget.StoppedForBudget);
        Assert.Equal(2, report.Results.Count(result => !result.LooksLikeFallback()));
        Assert.Contains("预算", Assert.Single(report.Results, result => result.LooksLikeFallback()).Reason);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public async Task Malformed_result_usage_counts_before_recovery_and_does_not_reset_budget()
    {
        var provider = new ReportingProvider((batch, call) =>
        {
            batch.RequestContext!.ReportUsage(new AiTokenUsage(8_000, 2_000));
            if (call == 2) throw new JsonException("Truncated model output");
            return ResultsFor(batch);
        });
        var report = await Service(provider).AnalyzeWithReportAsync(Root(3), Options(20_000), ProviderOptions);

        Assert.Equal(2, provider.Calls);
        Assert.True(report.TokenBudget.StoppedForBudget);
        Assert.Equal(20_000, report.TokenBudget.AccountedTokens);
        Assert.Single(report.Results, result => !result.LooksLikeFallback());
        Assert.All(report.Results.Where(result => result.LooksLikeFallback()), result => Assert.Contains("预算", result.Reason));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_usage_and_uncertain_failures_retain_reservations(bool fail)
    {
        var provider = new ReportingProvider((batch, _) => fail
            ? throw new TimeoutException("Unknown server outcome")
            : ResultsFor(batch));
        var report = await Service(provider).AnalyzeWithReportAsync(Root(12), Options(10_000), ProviderOptions);

        Assert.InRange(provider.Calls, 1, 11);
        Assert.True(report.TokenBudget.StoppedForBudget);
        Assert.True(report.TokenBudget.EstimatedTokens > 0);
        Assert.Equal(0, report.TokenBudget.ProviderReportedTokens);
        Assert.InRange(report.TokenBudget.AccountedTokens, 1, 10_000);
    }

    [Fact]
    public async Task Cache_hits_cost_zero_and_budget_resets_for_the_next_click()
    {
        var store = new MemoryStore();
        var provider = new ReportingProvider((batch, _) =>
        {
            batch.RequestContext!.ReportUsage(new AiTokenUsage(300, 100));
            return ResultsFor(batch);
        });
        var service = Service(provider, store);
        var first = await service.AnalyzeWithReportAsync(Root(2), Options(20_000), ProviderOptions);
        var cached = await service.AnalyzeWithReportAsync(Root(2), Options(1), ProviderOptions);
        Assert.Equal(800, first.TokenBudget.AccountedTokens);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(0, cached.TokenBudget.RequestCount);
        Assert.Equal(0, cached.TokenBudget.AccountedTokens);
        Assert.False(cached.TokenBudget.StoppedForBudget);
        Assert.All(cached.Results, result => Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin));
    }

    [Fact]
    public async Task Insufficient_input_allowance_never_contacts_provider()
    {
        var provider = new ReportingProvider((batch, _) => ResultsFor(batch));
        var report = await Service(provider).AnalyzeWithReportAsync(Root(2), Options(1), ProviderOptions);
        Assert.Equal(0, provider.Calls);
        Assert.True(report.TokenBudget.StoppedForBudget);
        Assert.Equal(0, report.TokenBudget.AccountedTokens);
        Assert.All(report.Results, result => Assert.Contains("预算", result.Reason));
    }

    [Fact]
    public void Insufficient_remaining_output_allowance_stops_before_sending_an_incomplete_bilingual_batch()
    {
        var candidate = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(Root(1), 1, 2400, true).Single();
        var batch = new AiBatch([candidate], 100, true);
        var ampleBudget = new AiTokenBudgetSession(100_000, useTokenSavingMode: true);
        var context = ampleBudget.Reserve(batch);
        Assert.Equal(1024, context.MaxOutputTokens);
        var insufficient = new AiTokenBudgetSession(ampleBudget.Report.AccountedTokens - 1, useTokenSavingMode: true);
        Assert.Throws<AiTokenBudgetExceededException>(() => insufficient.Reserve(batch));
        Assert.True(insufficient.IsStopped);
        Assert.Equal(0, insufficient.Report.RequestCount);
        Assert.Equal(0, insufficient.Report.AccountedTokens);
    }

    [Theory]
    [InlineData(false, "demo", "max_tokens")]
    [InlineData(false, "gpt-5", "max_completion_tokens")]
    [InlineData(true, "local", "num_predict")]
    public async Task Providers_enforce_output_allowance_and_report_usage_without_double_counting(bool ollama, string model, string limitField)
    {
        var candidate = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(Root(1), 1, 2400, true).Single();
        var batch = new AiBatch([candidate], 100, true);
        var context = new AiRequestContext("Frozen system", "Frozen user", 345);
        batch = batch with { RequestContext = context };
        string? body = null;
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            var content = JsonSerializer.Serialize(new { items = new[] { new { path = candidate.Path, riskLevel = "Review", suggestedAction = "Review", shortPurpose = "用途待确认", reason = "需确认", shortPurposeEn = "Purpose unclear", reasonEn = "Purpose unclear", confidence = 0.5 } } });
            var response = ollama
                ? JsonSerializer.Serialize(new { message = new { content }, prompt_eval_count = 300, eval_count = 100 })
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } }, usage = new { prompt_tokens = 300, completion_tokens = 100, total_tokens = 400, prompt_cache_hit_tokens = 200, completion_tokens_details = new { reasoning_tokens = 50 } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        });
        IAiProvider provider = ollama ? new OllamaAiProvider(new HttpClient(handler)) : new OpenAiCompatibleProvider(new HttpClient(handler));
        await provider.AnalyzeAsync(batch, ProviderOptions with { Model = model });

        using var payload = JsonDocument.Parse(body!);
        var limit = ollama ? payload.RootElement.GetProperty("options").GetProperty(limitField) : payload.RootElement.GetProperty(limitField);
        Assert.Equal(345, limit.GetInt32());
        Assert.Equal("Frozen system", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Frozen user", payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(400, context.Usage!.TotalTokens);
    }

    [Fact]
    public void Usage_is_recorded_before_malformed_content_and_invalid_usage_is_not_trusted()
    {
        var context = new AiRequestContext("", "", 100);
        using var usage = JsonDocument.Parse("""{"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":9}}""");
        context.ReadOpenAiUsage(usage.RootElement);
        Assert.Equal(9, context.Usage!.TotalTokens);
        var invalid = new AiRequestContext("", "", 100);
        using var malformed = JsonDocument.Parse("""{"usage":{"prompt_tokens":-1,"completion_tokens":4}}""");
        invalid.ReadOpenAiUsage(malformed.RootElement);
        Assert.Null(invalid.Usage);
    }

    [Fact]
    public void Legacy_settings_get_default_budget_and_roundtrip_preserves_new_value()
    {
        const string legacy = """{"RootPath":"E:\\","AnalysisThresholdMb":512,"VisualFolderDisplayThresholdMb":512,"ProviderKind":0,"AiEndpoint":"","AiModel":""}""";
        var settings = JsonSerializer.Deserialize<AppSettings>(legacy)!;
        Assert.Equal(100_000, settings.MaxAiAnalysisTokens);
        var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings with { MaxAiAnalysisTokens = 250_000 }))!;
        Assert.Equal(250_000, roundtrip.MaxAiAnalysisTokens);
    }

    private static ScanSessionOptions Options(int budget) => new(@"E:\", AnalysisThresholdBytes: 1, UseTokenSavingMode: true, MaxAiBatchSize: 1, MaxAiAnalysisTokens: budget);
    private static AiAnalysisService Service(IAiProvider provider, MemoryStore? store = null) => new([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), store);
    private static IReadOnlyList<AiAnalysisResult> ResultsFor(AiBatch batch) => batch.Records.Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "需确认", 0.5)).ToArray();
    private static FileSystemNode Root(int count)
    {
        var files = Enumerable.Range(1, count).Select(index => new FileSystemNode($"E:\\Budget\\{index}.bin", $"{index}.bin", ".bin", 1024, FileAttributes.Normal, null, null, false, false, [], [])).ToArray();
        return new FileSystemNode(@"E:\", "E:", "", count * 1024, FileAttributes.Directory, null, null, true, false, [], files);
    }

    private sealed class ReportingProvider(Func<AiBatch, int, IReadOnlyList<AiAnalysisResult>> respond) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default) => Task.FromResult(respond(batch, ++Calls));
    }

    private sealed class MemoryStore : IAiAnalysisMemoryStore
    {
        public IReadOnlyDictionary<string, AiAnalysisMemoryEntry> Entries { get; private set; } = new Dictionary<string, AiAnalysisMemoryEntry>();
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath, CancellationToken cancellationToken = default) => Task.FromResult(Entries);
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries, CancellationToken cancellationToken = default)
        {
            Entries = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}

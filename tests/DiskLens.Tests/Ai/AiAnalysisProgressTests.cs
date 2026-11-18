using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiAnalysisProgressTests
{
    private static readonly AiProviderOptions ProviderOptions = new(
        AiProviderKind.OpenAiCompatible, "https://example.invalid", "test-model", "test-key");

    [Fact]
    public async Task Waiting_request_reports_real_counts_before_the_provider_returns()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (batch, token) =>
        {
            received.TrySetResult();
            await release.Task.WaitAsync(token);
            return Results(batch);
        });
        var progress = new ProgressLog();
        var root = Root(3);
        root = root with { Children = root.Children.Select((file, index) =>
            index == 0 ? file with { IsProtectedLocation = true } : file).ToArray() };

        var pending = Service(provider).AnalyzeWithReportAsync(root, Options(), ProviderOptions, progress: progress);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pending.IsCompleted);
        var waiting = progress.Items.Last();
        Assert.Equal(AiAnalysisStage.Analyzing, waiting.Stage);
        Assert.True(waiting.IsWaitingForResponse);
        Assert.Equal(3, waiting.TotalCandidates);
        Assert.Equal(0, waiting.ProcessedCandidates);
        Assert.Equal(0, waiting.ProtectedCount);
        Assert.Equal(0, waiting.AiAnalyzedCount);
        Assert.Equal(1, waiting.RequestCount);
        Assert.Equal(1, waiting.CurrentBatchSize);
        Assert.Equal(0, waiting.Fraction);

        release.TrySetResult();
        var result = await pending;
        var completed = progress.Items.Last();
        Assert.Equal(AiAnalysisStage.Completed, completed.Stage);
        Assert.False(completed.IsWaitingForResponse);
        Assert.Equal(3, completed.ProcessedCandidates);
        Assert.Equal(3, completed.AiAnalyzedCount);
        Assert.Equal(0, completed.ProtectedCount);
        Assert.Equal(result.TokenBudget.RequestCount, completed.RequestCount);
        Assert.Equal(1, completed.Fraction);
        Assert.Equal(AiAnalysisStage.Preparing, progress.Items[0].Stage);
        Assert.Contains(progress.Items, item => item.Stage == AiAnalysisStage.LoadingCache);
        Assert.Contains(progress.Items, item => item.Stage == AiAnalysisStage.Saving);
    }

    [Fact]
    public async Task Cache_only_analysis_reports_no_request_or_network_wait()
    {
        var provider = new Provider((batch, _) => Task.FromResult(Results(batch)));
        var service = Service(provider, new MemoryStore());
        await service.AnalyzeWithReportAsync(Root(2), Options(), ProviderOptions);
        var callsBefore = provider.Calls;
        var progress = new ProgressLog();

        var result = await service.AnalyzeWithReportAsync(Root(2), Options(), ProviderOptions, progress: progress);

        Assert.Equal(callsBefore, provider.Calls);
        Assert.Equal(0, result.TokenBudget.RequestCount);
        Assert.DoesNotContain(progress.Items, item => item.IsWaitingForResponse);
        var completed = progress.Items.Last();
        Assert.Equal(2, completed.CacheReusedCount);
        Assert.Equal(0, completed.AiAnalyzedCount);
        Assert.Equal(0, completed.RequestCount);
        Assert.Equal(AiAnalysisStage.Completed, completed.Stage);
    }

    [Fact]
    public async Task Incomplete_response_recovery_reports_retry_without_resetting_request_count()
    {
        var first = true;
        var provider = new Provider((batch, _) =>
        {
            if (first)
            {
                first = false;
                return Task.FromResult<IReadOnlyList<AiAnalysisResult>>([]);
            }
            return Task.FromResult(Results(batch));
        });
        var progress = new ProgressLog();

        var result = await Service(provider).AnalyzeWithReportAsync(Root(1), Options(), ProviderOptions, progress: progress);

        Assert.Equal(2, result.TokenBudget.RequestCount);
        var retry = Assert.Single(progress.Items, item => item.Stage == AiAnalysisStage.Retrying && item.IsWaitingForResponse);
        Assert.True(retry.IsRetry);
        Assert.Equal(2, retry.RequestCount);
        Assert.Equal(0, retry.ProcessedCandidates);
        Assert.Equal(1, progress.Items.Last().AiAnalyzedCount);
        Assert.Equal(0, progress.Items.Last().IncompleteCount);
    }

    [Fact]
    public async Task Cancellation_ends_waiting_and_keeps_incomplete_results_explicit()
    {
        using var cancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (_, token) =>
        {
            received.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        });
        var progress = new ProgressLog();
        var pending = Service(provider).AnalyzeWithReportAsync(Root(1), Options(), ProviderOptions,
            cancellation.Token, progress);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAsync<AiAnalysisCanceledException>(() => pending);

        var last = progress.Items.Last();
        Assert.Equal(AiAnalysisStage.Canceled, last.Stage);
        Assert.True(last.IsTerminal);
        Assert.False(last.IsWaitingForResponse);
        Assert.Equal(0, last.AiAnalyzedCount);
        Assert.Equal(1, last.IncompleteCount);
    }

    [Fact]
    public async Task Insufficient_budget_does_not_report_a_request_that_never_reached_provider()
    {
        var provider = new Provider((batch, _) => Task.FromResult(Results(batch)));
        var progress = new ProgressLog();
        var result = await Service(provider).AnalyzeWithReportAsync(Root(1), Options() with { MaxAiAnalysisTokens = 1 },
            ProviderOptions, progress: progress);

        Assert.True(result.TokenBudget.StoppedForBudget);
        Assert.Equal(0, provider.Calls);
        Assert.DoesNotContain(progress.Items, item => item.IsWaitingForResponse);
        var last = progress.Items.Last();
        Assert.Equal(AiAnalysisStage.StoppedForBudget, last.Stage);
        Assert.Equal(0, last.RequestCount);
        Assert.Equal(0, last.AiAnalyzedCount);
        Assert.Equal(1, last.IncompleteCount);
    }

    [Fact]
    public async Task Concurrent_analyses_keep_candidate_counts_and_requests_isolated()
    {
        var provider = new Provider(async (batch, _) =>
        {
            await Task.Yield();
            return Results(batch);
        });
        var service = Service(provider);
        var one = new ProgressLog();
        var two = new ProgressLog();

        await Task.WhenAll(
            service.AnalyzeWithReportAsync(Root(1), Options(), ProviderOptions, progress: one),
            service.AnalyzeWithReportAsync(Root(3), Options(), ProviderOptions, progress: two));

        Assert.Equal(1, one.Items.Last().TotalCandidates);
        Assert.Equal(1, one.Items.Last().RequestCount);
        Assert.Equal(1, one.Items.Last().AiAnalyzedCount);
        Assert.Equal(3, two.Items.Last().TotalCandidates);
        Assert.Equal(3, two.Items.Last().RequestCount);
        Assert.Equal(3, two.Items.Last().AiAnalyzedCount);
        Assert.All(one.Items, item => Assert.InRange(item.ProcessedCandidates, 0, 1));
    }

    [Fact]
    public async Task Nothing_above_threshold_finishes_without_claiming_network_progress()
    {
        var provider = new Provider((batch, _) => Task.FromResult(Results(batch)));
        var progress = new ProgressLog();
        await Service(provider).AnalyzeWithReportAsync(Root(1), Options() with { AnalysisThresholdBytes = long.MaxValue },
            ProviderOptions, progress: progress);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, progress.Items.Last().TotalCandidates);
        Assert.Equal(AiAnalysisStage.Completed, progress.Items.Last().Stage);
        Assert.DoesNotContain(progress.Items, item => item.IsWaitingForResponse);
    }

    private static ScanSessionOptions Options() => new(
        @"E:\", AnalysisThresholdBytes: 1,
        MaxAiChunkTokens: 2400, MaxAiBatchSize: 1, UseTokenSavingMode: true);

    private static FileSystemNode Root(int count)
    {
        var files = Enumerable.Range(0, count).Select(index => new FileSystemNode(
            $@"E:\DiskLens-test\file{index}.bin", $"file{index}.bin", ".bin", 1024,
            FileAttributes.Normal, null, null, false, false, [], [], DescendantFileCount: 1)).ToArray();
        return new FileSystemNode(@"E:\", "E:", "", count * 1024,
            FileAttributes.Directory, null, null, true, false, [], files,
            DescendantFileCount: count, DescendantDirectoryCount: 1);
    }

    private static AiAnalysisService Service(IAiProvider provider, IAiAnalysisMemoryStore? store = null) => new(
        [provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), store);

    private static IReadOnlyList<AiAnalysisResult> Results(AiBatch batch) => batch.Records.Select(record =>
        new AiAnalysisResult(record.Path, RiskLevel.Low, SuggestedAction.Review, "确认用途后处理。", 0.8)).ToArray();

    private sealed class Provider(Func<AiBatch, CancellationToken, Task<IReadOnlyList<AiAnalysisResult>>> action) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            batch.RequestContext!.ReportUsage(new AiTokenUsage(300, 100));
            return await action(batch, cancellationToken);
        }
    }

    private sealed class ProgressLog : IProgress<AiAnalysisProgress>
    {
        public List<AiAnalysisProgress> Items { get; } = [];
        public void Report(AiAnalysisProgress value) => Items.Add(value);
    }

    private sealed class MemoryStore : IAiAnalysisMemoryStore
    {
        private Dictionary<string, AiAnalysisMemoryEntry> _entries = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(_entries);

        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            _entries = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }
}

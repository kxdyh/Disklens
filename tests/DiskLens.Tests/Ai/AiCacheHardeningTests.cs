using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.Ai;

public sealed class AiCacheHardeningTests
{
    private static readonly AiProviderOptions Provider = new(AiProviderKind.OpenAiCompatible, "https://cache.invalid", "model", "synthetic", ReasoningEffort: "medium");
    private static ScanSessionOptions Options => new(@"E:\", AnalysisThresholdBytes: 1, MaxAiBatchSize: 1);
    private static FileSystemNode FileNode(string name = "item.bin", long size = 1024) => new("E:\\CacheFixture\\" + name, name,
        ".bin", size, FileAttributes.Normal, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false, [], [], 1);
    private static FileSystemNode Root(params FileSystemNode[] children) => new(Options.RootPath, "root", "",
        children.Sum(child => child.SizeBytes), FileAttributes.Directory, null, null, true, false, [], children, children.Length);
    private static AiAnalysisService Service(IAiProvider provider, Memory memory, ISharedKnowledgePipeline? pipeline = null) =>
        new([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory, pipeline);

    [Fact]
    public async Task Reasoning_temperature_and_bypass_invalidate_but_key_change_preserves_original_identity()
    {
        var memory = new Memory(); var provider = new ProviderStub(); var service = Service(provider, memory);
        var root = Root(FileNode());
        var first = Assert.Single(await service.AnalyzeAsync(root, Options, Provider));
        var cached = Assert.Single(await service.AnalyzeAsync(root, Options, Provider with { ApiKey = "other" }));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(first.OriginalAnalysisId, cached.OriginalAnalysisId);
        Assert.Equal(first.OriginalAnalyzedAt, cached.OriginalAnalyzedAt);
        await service.AnalyzeAsync(root, Options, Provider with { ReasoningEffort = "high" });
        await service.AnalyzeAsync(root, Options, Provider with { ReasoningEffort = "high", Temperature = .2 });
        await service.AnalyzeAsync(root, Options with { BypassAnalysisMemory = true }, Provider with { ReasoningEffort = "high", Temperature = .2 });
        Assert.Equal(4, provider.Calls);
    }

    [Fact]
    public async Task Expired_and_null_entries_never_hit_and_repeated_cache_does_not_renew_time()
    {
        var memory = new Memory(); var provider = new ProviderStub(); var service = Service(provider, memory);
        var root = Root(FileNode());
        await service.AnalyzeAsync(root, Options, Provider);
        var original = Assert.Single(memory.Entries).Value;
        var originalTime = DateTimeOffset.UtcNow.AddDays(-29);
        memory.Entries[original.Path] = original with { AnalyzedAt = originalTime };
        await service.AnalyzeAsync(root, Options, Provider);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(originalTime, memory.Entries[original.Path].AnalyzedAt);
        memory.Entries[original.Path] = original with { AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-31) };
        memory.Entries["damaged"] = null!;
        await service.AnalyzeAsync(root, Options, Provider);
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("AI 未提供解释。")]
    public async Task Empty_and_legacy_placeholder_explanations_are_not_successful_cache_entries(string reason)
    {
        var memory = new Memory();
        var provider = new ProviderStub();
        var service = Service(provider, memory);
        var root = Root(FileNode());
        await service.AnalyzeAsync(root, Options, Provider);
        var original = Assert.Single(memory.Entries).Value;
        memory.Entries[original.Path] = original with
        {
            Result = original.Result with { Reason = reason, ConfidenceLevel = AiConfidenceLevel.High }
        };

        var result = Assert.Single(await service.AnalyzeAsync(root, Options, Provider));

        Assert.Equal(2, provider.Calls);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
        Assert.Equal("已分析", result.Reason);
    }

    [Fact]
    public async Task Singleflight_follower_cancellation_does_not_cancel_owner_and_owner_cancel_allows_retry()
    {
        var memory = new Memory(); var provider = new ProviderStub { Block = true }; var service = Service(provider, memory);
        var root = Root(FileNode());
        var owner = service.AnalyzeWithReportAsync(root, Options, Provider);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var canceledFollower = new CancellationTokenSource();
        var follower = service.AnalyzeWithReportAsync(root, Options, Provider, canceledFollower.Token);
        canceledFollower.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        provider.Release.TrySetResult();
        await owner;
        Assert.Equal(1, provider.Calls);

        memory = new Memory(); provider = new ProviderStub { Block = true }; service = Service(provider, memory);
        using var canceledOwner = new CancellationTokenSource();
        owner = service.AnalyzeWithReportAsync(root, Options, Provider, canceledOwner.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        follower = service.AnalyzeWithReportAsync(root, Options, Provider);
        canceledOwner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        provider.Release.TrySetResult();
        var retried = await follower.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, provider.Calls);
        Assert.False(Assert.Single(retried.Results).LooksLikeFallback());
    }

    [Fact]
    public async Task Singleflight_reuses_one_request_and_checkpoints_before_the_next_batch()
    {
        var memory = new Memory(); var provider = new ProviderStub { Block = true }; var service = Service(provider, memory);
        var root = Root(FileNode());
        var first = service.AnalyzeWithReportAsync(root, Options, Provider);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.AnalyzeWithReportAsync(root, Options, Provider);
        provider.Release.TrySetResult();
        var reports = await Task.WhenAll(first, second);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, reports[1].TokenBudget.RequestCount);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, Assert.Single(reports[1].Results).Origin);
        Assert.Equal(Assert.Single(reports[0].Results).OriginalAnalysisId, Assert.Single(reports[1].Results).OriginalAnalysisId);
        provider = new ProviderStub { OnCall = call => { if (call == 2) Assert.Contains(FileNode("a.bin", 2048).Path, memory.Entries.Keys); } };
        memory.Entries.Clear();
        await Service(provider, memory).AnalyzeAsync(Root(FileNode("a.bin", 2048), FileNode("b.bin")), Options, Provider);
    }

    [Fact]
    public async Task Recovery_batches_checkpoint_before_the_next_request_and_keep_one_original_identity()
    {
        var memory = new Memory();
        var provider = new ProviderStub { FailFirst = true, OnCall = call =>
        {
            if (call == 3) Assert.Contains(FileNode("a.bin", 2048).Path, memory.Entries.Keys);
        } };
        var results = await Service(provider, memory).AnalyzeAsync(Root(FileNode("a.bin", 2048), FileNode("b.bin")),
            Options with { MaxAiBatchSize = 20 }, Provider);
        Assert.Equal(3, provider.Calls);
        Assert.All(results, result => Assert.Equal(result.OriginalAnalysisId, memory.Entries[result.Path].Result.OriginalAnalysisId));
    }

    [Fact]
    public async Task Unavailable_optional_cache_does_not_prevent_ai_analysis()
    {
        var memory = new Memory { FailRead = true }; var provider = new ProviderStub();
        var result = Assert.Single(await Service(provider, memory).AnalyzeAsync(Root(FileNode()), Options, Provider));
        Assert.False(result.LooksLikeFallback()); Assert.Empty(memory.Entries); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Memory_then_shared_then_provider_and_shared_never_enters_ai_memory_in_protected_locations()
    {
        var memory = new Memory(); var provider = new ProviderStub(); var pipeline = new Pipeline();
        var root = Root(FileNode()); var service = Service(provider, memory, pipeline);
        var shared = await service.AnalyzeWithReportAsync(root, Options, Provider with { Kind = AiProviderKind.Disabled });
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, Assert.Single(shared.Results).Origin);
        Assert.Empty(memory.Entries);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, AiAnalysisSummary.FromResults(shared.Results).SharedExperienceCount);
        Assert.True(AiAnalysisSummary.FromResults(shared.Results).IsComplete);
        await service.AnalyzeWithReportAsync(root, Options with { BypassAnalysisMemory = true }, Provider,
            provenance: new("openai-compatible", "fixture", true));
        Assert.Equal(1, pipeline.ResolveCalls);
        Assert.Equal(1, provider.Calls);
        Assert.Single(pipeline.Contributed);
        Assert.False(string.IsNullOrWhiteSpace(pipeline.Contributed[0].OriginalAnalysisId));
        await service.AnalyzeWithReportAsync(root, Options, Provider, provenance: new("openai-compatible", "fixture", true));
        Assert.Equal(1, pipeline.ResolveCalls);
        Assert.Single(pipeline.Contributed);

        var protectedRoot = Root(FileNode() with { IsProtectedLocation = true });
        var protectedResult = Assert.Single(await service.AnalyzeAsync(protectedRoot, Options, Provider with { Kind = AiProviderKind.Disabled }));
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, protectedResult.Origin);
        Assert.Equal(2, pipeline.ResolveCalls);
        Assert.DoesNotContain(memory.Entries.Values, entry => entry.Result.Origin == AiAnalysisResultOrigin.ProtectedByRule);
    }

    [Fact]
    public async Task Exclusion_avoids_shared_and_provider_and_failed_sharing_preserves_success_report()
    {
        var memory = new Memory(); var provider = new ProviderStub(); var pipeline = new Pipeline { Throw = true };
        var service = Service(provider, memory, pipeline); var root = Root(FileNode());
        var excluded = await service.AnalyzeWithReportAsync(root,
            Options with { ExcludedAnalysisPaths = new HashSet<string>([FileNode().Path], StringComparer.Ordinal) }, Provider);
        Assert.Empty(excluded.Results); Assert.Equal(0, pipeline.ResolveCalls); Assert.Equal(0, provider.Calls);
        var report = await service.AnalyzeWithReportAsync(root, Options, Provider, provenance: new("test", "test", true));
        Assert.False(Assert.Single(report.Results).LooksLikeFallback());
        Assert.NotNull(report.SharedKnowledgeWarning);
    }

    private sealed class Memory : IAiAnalysisMemoryStore
    {
        public bool FailRead { get; init; }
        public Dictionary<string, AiAnalysisMemoryEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath, CancellationToken cancellationToken = default)
        { if (FailRead) throw new IOException("synthetic unavailable cache"); lock (Entries) return Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(new Dictionary<string, AiAnalysisMemoryEntry>(Entries, StringComparer.Ordinal)); }
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); lock (Entries) foreach (var entry in entries) Entries[entry.Path] = entry; return Task.CompletedTask; }
    }

    private sealed class ProviderStub : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls;
        public bool Block { get; init; }
        public bool FailFirst { get; init; }
        public Action<int>? OnCall { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref Calls); OnCall?.Invoke(call); Entered.TrySetResult();
            if (FailFirst && call == 1) throw new InvalidOperationException("synthetic truncated response");
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            batch.RequestContext?.ReportUsage(new AiTokenUsage(20, 10));
            return batch.Records.Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "已分析", .8)).ToArray();
        }
    }

    private sealed class Pipeline : ISharedKnowledgePipeline
    {
        public int ResolveCalls;
        public bool Throw { get; init; }
        public List<AiAnalysisResult> Contributed { get; } = [];
        public Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            if (Throw) throw new IOException("synthetic pipeline failure");
            return Task.FromResult(new SharedResolutionBatch(candidates.ToDictionary(candidate => candidate.Path,
                candidate => new AiAnalysisResult(candidate.Path, RiskLevel.Review, SuggestedAction.Review, "共享结论", .8)
                    { Origin = AiAnalysisResultOrigin.SharedExperience }), candidates.Count, 0, false));
        }
        public Task<SharedContributionReceipt> ContributeAsync(IReadOnlyList<AiCandidateRecord> candidates, IReadOnlyList<AiAnalysisResult> results,
            SharedAnalysisProvenance provenance, bool historical = false, CancellationToken cancellationToken = default)
        {
            if (Throw) throw new IOException("synthetic contribution failure");
            Contributed.AddRange(results); return Task.FromResult(new SharedContributionReceipt(results.Count, 0, 1));
        }
    }
}

using System.Collections.Concurrent;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiSingleFlightScopeRecoveryTests
{
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://example.invalid", "synthetic-model", "synthetic-key");
    private static readonly ScanSessionOptions Options = new(@"E:\", AnalysisThresholdBytes: 1024,
        UseTokenSavingMode: true, MaxAiAnalysisTokens: 100_000, AnalysisMemoryRootPath: @"E:\ScopeFlight");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_owner_retries_only_its_item_and_preserves_completed_path_only_cache_advice_even_with_insufficient_budget(bool exhaustBudget)
    {
        var pending = File(@"E:\ScopeFlight\pending.zip");
        var child = File(@"E:\ScopeFlight\CacheRoot\known.zip");
        var parent = Directory(@"E:\ScopeFlight\CacheRoot", child,
            Directory(@"E:\ScopeFlight\CacheRoot\alias") with { RiskTags = ["skipped", "reparse-point"] });
        var root = Directory(Options.RootPath, parent, pending);
        var memory = new Memory();
        var ownerProvider = new FailingOwner();
        var followerProvider = new FollowerProvider(exhaustBudget ? Options.MaxAiAnalysisTokens - 100 : 200);
        var owner = Service(ownerProvider, memory).AnalyzeWithReportAsync(pending, Options, ProviderOptions);
        await ownerProvider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var follower = Service(followerProvider, memory).AnalyzeWithReportAsync(root, Options, ProviderOptions);
        await followerProvider.FirstResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ownerProvider.Release.TrySetResult();
        await owner.WaitAsync(TimeSpan.FromSeconds(5));
        var report = await follower.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, report.TotalCandidates);
        Assert.Equal(3, report.Results.Count);
        var parentResult = Assert.Single(report.Results, result => result.Path == parent.Path);
        Assert.False(parentResult.LooksLikeFallback());
        Assert.True(parentResult.IsScopeLimited);
        Assert.Equal(SuggestedAction.RecycleCandidate, parentResult.SuggestedAction);
        Assert.Equal("cache", parentResult.PurposeCategory);
        Assert.Equal(AiConfidenceLevel.High, parentResult.EffectiveConfidenceLevel);
        Assert.Equal(AiScopeWarning.DescendantReadIncomplete, parentResult.ScopeWarning);
        Assert.DoesNotContain("已读范围用途", parentResult.Reason);
        Assert.Contains("已知可重建缓存", parentResult.Reason);
        Assert.False(Assert.Single(report.Results, result => result.Path == child.Path).LooksLikeFallback());
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, parentResult.AnalysisEvidenceScope);
        Assert.False(parentResult.IsAnalysisEvidenceLimited);
        Assert.NotNull(parentResult.OriginalAnalysisId);
        Assert.Equal(parentResult.OriginalAnalysisId, memory.Entries[parent.Path].Result.OriginalAnalysisId);
        Assert.Single(followerProvider.Requests.SelectMany(paths => paths), path => path == parent.Path);
        Assert.Single(followerProvider.Requests.SelectMany(paths => paths), path => path == child.Path);
        var pendingResult = Assert.Single(report.Results, result => result.Path == pending.Path);
        if (exhaustBudget)
        {
            Assert.True(pendingResult.LooksLikeFallback());
            Assert.Contains("预算", pendingResult.Reason);
            Assert.Single(followerProvider.Requests);
            Assert.Equal(1, report.TokenBudget.RequestCount);
            Assert.Equal(2, report.SubmittedCandidateCount);
            Assert.True(report.TokenBudget.StoppedForBudget);
        }
        else
        {
            Assert.False(pendingResult.LooksLikeFallback());
            Assert.Equal(2, followerProvider.Requests.Count);
            Assert.Equal(pending.Path, Assert.Single(followerProvider.Requests[1]));
            Assert.Equal(2, report.TokenBudget.RequestCount);
            Assert.Equal(3, report.SubmittedCandidateCount);
        }
    }

    private static FileSystemNode File(string path) => new(path, Path.GetFileName(path), Path.GetExtension(path),
        1024, FileAttributes.Normal, null, null, false, false, [], [], 1);
    private static FileSystemNode Directory(string path, params FileSystemNode[] children) => new(path,
        Path.GetFileName(path), "", children.Sum(child => child.SizeBytes), FileAttributes.Directory,
        null, null, true, false, [], children, children.Sum(child => child.DescendantFileCount),
        1 + children.Sum(child => child.DescendantDirectoryCount));
    private static AiAnalysisService Service(IAiProvider provider, Memory memory) =>
        new([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory);

    private sealed class Memory : IAiAnalysisMemoryStore
    {
        public ConcurrentDictionary<string, AiAnalysisMemoryEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(new Dictionary<string, AiAnalysisMemoryEntry>(Entries, StringComparer.Ordinal));
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries, CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries) Entries[entry.Path] = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingOwner : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new IOException("synthetic owner failure");
        }
    }

    private sealed class FollowerProvider(long firstUsage) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public List<string[]> Requests { get; } = [];
        public TaskCompletionSource FirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Requests.Add(batch.Records.Select(record => record.Path).ToArray());
            batch.RequestContext!.ReportUsage(new(Requests.Count == 1 ? firstUsage : 200, 0));
            var results = batch.Records.Select(record => new AiAnalysisResult(record.Path, RiskLevel.Low,
                SuggestedAction.RecycleCandidate, "已知可重建缓存。", .9)
                { ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "cache" }).ToArray();
            FirstResponse.TrySetResult();
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(results);
        }
    }
}

using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class CacheReleaseBoundaryTests
{
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://cache-boundary.invalid", "synthetic-model", "synthetic-never-sent", ReasoningEffort: "medium");
    private static readonly FileSystemNode SharedFile = new(@"E:\BoundaryFixture\shared.bin", "shared.bin", ".bin", 1024,
        FileAttributes.Normal, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false, [], [], 1);

    [Fact]
    public async Task Failed_cache_save_keeps_the_completed_analysis_and_exposes_a_persistence_warning()
    {
        var memory = new ThrowingStore();
        var provider = new GatedProvider();
        var root = Root(@"E:\");

        var report = await Service(provider, memory).AnalyzeWithReportAsync(root, Options(root.Path), ProviderOptions);

        var result = Assert.Single(report.Results);
        Assert.False(result.LooksLikeFallback());
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
        Assert.False(string.IsNullOrWhiteSpace(result.OriginalAnalysisId));
        Assert.False(string.IsNullOrWhiteSpace(report.PersistenceWarning));
        Assert.True(memory.SaveAttempts > 0);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, report.TokenBudget.RequestCount);
    }

    [Fact]
    public async Task Identical_candidate_across_different_scan_roots_shares_one_request_and_original_identity()
    {
        var memory = new PartitionedStore();
        var provider = new GatedProvider { Block = true };
        var firstRoot = Root(@"E:\");
        // A directory scan and a later scan scoped to its file are both valid trees.
        var secondRoot = SharedFile;
        Assert.NotEqual(firstRoot.Path, secondRoot.Path);
        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());
        var firstCandidate = Assert.Single(builder.BuildCandidates(firstRoot, 1, 2400));
        var secondCandidate = Assert.Single(builder.BuildCandidates(secondRoot, 1, 2400));
        Assert.Equal(firstCandidate.Path, secondCandidate.Path);
        Assert.Equal(firstCandidate.Signature, secondCandidate.Signature);

        var owner = Service(provider, memory).AnalyzeWithReportAsync(firstRoot, Options(firstRoot.Path), ProviderOptions);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var follower = Service(provider, memory).AnalyzeWithReportAsync(secondRoot, Options(secondRoot.Path), ProviderOptions);
        provider.Release.TrySetResult();
        var reports = await Task.WhenAll(owner, follower).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, reports[0].TokenBudget.RequestCount);
        Assert.Equal(0, reports[1].TokenBudget.RequestCount);
        var original = Assert.Single(reports[0].Results);
        var reused = Assert.Single(reports[1].Results);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, original.Origin);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, reused.Origin);
        Assert.False(string.IsNullOrWhiteSpace(original.OriginalAnalysisId));
        Assert.Equal(original.OriginalAnalysisId, reused.OriginalAnalysisId);
        Assert.Equal(original.OriginalAnalyzedAt, reused.OriginalAnalyzedAt);
        Assert.Equal(original.OriginalAnalysisId,
            Assert.Single(await memory.LoadEntriesAsync(secondRoot.Path)).Value.Result.OriginalAnalysisId);
    }

    private static ScanSessionOptions Options(string rootPath) => new(rootPath, AnalysisThresholdBytes: 1, MaxAiBatchSize: 1);
    private static FileSystemNode Root(string path) => new(path, Path.GetFileName(path), "", SharedFile.SizeBytes,
        FileAttributes.Directory, null, null, true, false, [], [SharedFile], 1, 1);
    private static AiAnalysisService Service(IAiProvider provider, IAiAnalysisMemoryStore memory) =>
        new([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory);

    private sealed class ThrowingStore : IAiAnalysisMemoryStore
    {
        public int SaveAttempts;
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(new Dictionary<string, AiAnalysisMemoryEntry>());
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SaveAttempts);
            throw new IOException("Synthetic unavailable cache storage.");
        }
    }

    private sealed class PartitionedStore : IAiAnalysisMemoryStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, AiAnalysisMemoryEntry>> _partitions = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
                    _partitions.TryGetValue(rootPath, out var entries)
                        ? new Dictionary<string, AiAnalysisMemoryEntry>(entries, StringComparer.Ordinal)
                        : new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal));
        }
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_partitions.TryGetValue(rootPath, out var partition))
                    _partitions.Add(rootPath, partition = new(StringComparer.Ordinal));
                foreach (var entry in entries) partition[entry.Path] = entry;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class GatedProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls;
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            batch.RequestContext?.ReportUsage(new AiTokenUsage(10, 5));
            return batch.Records.Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review,
                SuggestedAction.Review, "Synthetic analysis completed.", .8)).ToArray();
        }
    }
}

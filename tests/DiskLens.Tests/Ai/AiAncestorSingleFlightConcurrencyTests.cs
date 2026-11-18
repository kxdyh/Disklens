using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiAncestorSingleFlightConcurrencyTests
{
    private const string ParentPath = @"E:\AncestorFlight\cache";
    private const string ChildPath = ParentPath + @"\runtime.dll";
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://example.invalid", "synthetic-model", "synthetic-key");
    private static readonly ScanSessionOptions Options = new(@"E:\", AnalysisThresholdBytes: 1024,
        UseTokenSavingMode: true, MaxAiBatchSize: 1, MaxAiAnalysisTokens: 100_000);

    [Fact]
    public async Task Parent_follower_waits_for_later_owned_child_and_receives_the_final_veto()
    {
        var root = Root();
        var memory = new Memory();
        var ownerProvider = new ControlledProvider();
        var followerProvider = new RejectProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var owner = Service(ownerProvider, memory).AnalyzeAsync(root, Options, ProviderOptions, cancellation.Token);
        await ownerProvider.ParentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Join before the parent response exists, so this caller must obtain
        // its result from singleflight rather than a later cache lookup.
        var follower = Service(followerProvider, memory).AnalyzeAsync(root, Select(ParentPath), ProviderOptions, cancellation.Token);
        Assert.Equal(2, memory.LoadCount);
        try
        {
            ownerProvider.AllowParent.TrySetResult();
            await ownerProvider.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(follower.IsCompleted);
            ownerProvider.AllowChild.TrySetResult();
            var results = await Task.WhenAll(owner, follower).WaitAsync(TimeSpan.FromSeconds(5));

            var finalParent = Assert.Single(results[0], item => item.Path == ParentPath);
            var followedParent = Assert.Single(results[1]);
            Assert.Equal(SuggestedAction.DoNotDelete, finalParent.SuggestedAction);
            Assert.Equal(SuggestedAction.DoNotDelete, followedParent.SuggestedAction);
            Assert.Equal(AiAnalysisResultOrigin.CacheReused, followedParent.Origin);
            Assert.Equal(finalParent.OriginalAnalysisId, followedParent.OriginalAnalysisId);
            Assert.Equal(finalParent.OriginalAnalyzedAt, followedParent.OriginalAnalyzedAt);
            Assert.NotNull(finalParent.OriginalAnalysisId);
            Assert.NotNull(finalParent.OriginalAnalyzedAt);
            Assert.Equal(SuggestedAction.DoNotDelete, memory.Read(ParentPath).Result.SuggestedAction);
            Assert.Equal(0, followerProvider.Calls);
        }
        finally
        {
            cancellation.Cancel();
            ownerProvider.AllowParent.TrySetResult();
            ownerProvider.AllowChild.TrySetResult();
            await DrainAsync(owner, follower);
        }
    }

    [Fact]
    public async Task Owned_parent_is_published_before_waiting_for_a_child_owned_by_another_caller()
    {
        var root = Root();
        var memory = new Memory();
        var childProvider = new ControlledProvider();
        var parentProvider = new ControlledProvider();
        var followerProvider = new RejectProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var childOwner = Service(childProvider, memory).AnalyzeAsync(root, Select(ChildPath), ProviderOptions, cancellation.Token);
        await childProvider.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var parentOwner = Service(parentProvider, memory).AnalyzeAsync(root, Options, ProviderOptions, cancellation.Token);
        await parentProvider.ParentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var follower = Service(followerProvider, memory).AnalyzeAsync(root, Select(ParentPath), ProviderOptions, cancellation.Token);
        Assert.Equal(3, memory.LoadCount);
        try
        {
            parentProvider.AllowParent.TrySetResult();
            // The external child deliberately stays pending until an owned
            // parent follower completes. Publishing after followed waits would
            // deadlock this dependency chain instead of reaching this point.
            var earlyParent = Assert.Single(await follower.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SuggestedAction.RecycleCandidate, earlyParent.SuggestedAction);
            Assert.False(parentOwner.IsCompleted);
            childProvider.AllowChild.TrySetResult();
            await childOwner.WaitAsync(TimeSpan.FromSeconds(5));
            var finalParent = Assert.Single(await parentOwner.WaitAsync(TimeSpan.FromSeconds(5)), item => item.Path == ParentPath);

            Assert.Equal(SuggestedAction.DoNotDelete, finalParent.SuggestedAction);
            Assert.Equal(earlyParent.OriginalAnalysisId, finalParent.OriginalAnalysisId);
            Assert.Equal(earlyParent.OriginalAnalyzedAt, finalParent.OriginalAnalyzedAt);
            Assert.Equal(SuggestedAction.DoNotDelete, memory.Read(ParentPath).Result.SuggestedAction);
            Assert.Equal(0, followerProvider.Calls);
        }
        finally
        {
            cancellation.Cancel();
            childProvider.AllowChild.TrySetResult();
            parentProvider.AllowParent.TrySetResult();
            await DrainAsync(childOwner, parentOwner, follower);
        }
    }

    private static ScanSessionOptions Select(string path) => Options with
    {
        IncludedAnalysisPaths = new HashSet<string> { path },
    };
    private static AiAnalysisService Service(IAiProvider provider, Memory memory) => new(
        [provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory);
    private static FileSystemNode Root()
    {
        var child = Node(ChildPath, false, 2048);
        var parent = Node(ParentPath, true, 2148, child, Node(ParentPath + @"\small.tmp", false, 100));
        return Node(Options.RootPath, true, parent.SizeBytes, parent);
    }
    private static FileSystemNode Node(string path, bool directory, long size, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path), directory ? "" : Path.GetExtension(path), size,
        directory ? FileAttributes.Directory : FileAttributes.Normal, DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch, directory, false, [], children);
    private static async Task DrainAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* Preserve the test assertion while cancelled services release their flights. */ }
    }

    private sealed class ControlledProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public TaskCompletionSource ParentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowParent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowChild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            var item = Assert.Single(batch.Records);
            var child = item.Path == ChildPath;
            Assert.True(child || item.Path == ParentPath);
            (child ? ChildEntered : ParentEntered).TrySetResult();
            await (child ? AllowChild : AllowParent).Task.WaitAsync(cancellationToken);
            batch.RequestContext!.ReportUsage(new(100, 50));
            return [new AiAnalysisResult(item.Path, child ? RiskLevel.High : RiskLevel.Low,
                child ? SuggestedAction.DoNotDelete : SuggestedAction.RecycleCandidate,
                child ? "程序运行依赖" : "可重建缓存目录", .95)
            {
                ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = child ? "runtime" : "cache",
            }];
        }
    }
    private sealed class RejectProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A follower must not issue a model request.");
        }
    }
    private sealed class Memory : IAiAnalysisMemoryStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AiAnalysisMemoryEntry> _entries = new(StringComparer.Ordinal);
        public int LoadCount { get; private set; }
        public AiAnalysisMemoryEntry Read(string path) { lock (_gate) return _entries[path]; }
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                LoadCount++;
                return Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
                    new Dictionary<string, AiAnalysisMemoryEntry>(_entries, StringComparer.Ordinal));
            }
        }
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) foreach (var entry in entries) _entries[entry.Path] = entry;
            return Task.CompletedTask;
        }
    }
}

using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiAncestorAdviceCheckpointTests
{
    private const string ParentPath = @"E:\AncestorAudit\cache";
    private const string ChildPath = ParentPath + @"\runtime.dll";
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://example.invalid", "synthetic-model", "synthetic-key");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_runtime_child_must_revise_a_fresh_or_cached_parent_and_persist_across_repeated_reads(
        bool parentWasCached)
    {
        var child = Node(ChildPath, false, 2048);
        var tiny = Node(ParentPath + @"\generated.tmp", false, 100);
        var parent = Node(ParentPath, true, 2148, child, tiny);
        var root = Node(@"E:\", true, parent.SizeBytes, parent);
        var options = new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1024,
            UseTokenSavingMode: true, MaxAiBatchSize: 1, MaxAiAnalysisTokens: 100_000);
        var memory = new Memory();
        var provider = new Provider(memory);
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(), memory);

        if (parentWasCached)
        {
            var selected = options with { IncludedAnalysisPaths = new HashSet<string> { ParentPath } };
            var seededParent = Assert.Single(await service.AnalyzeAsync(root, selected, ProviderOptions));
            Assert.Equal(SuggestedAction.RecycleCandidate, seededParent.SuggestedAction);
            Assert.Equal(SuggestedAction.RecycleCandidate, memory.Entries[ParentPath].Result.SuggestedAction);
        }

        var first = await service.AnalyzeAsync(root, options, ProviderOptions);

        // One item per batch makes the parent complete and get checkpointed
        // before the provider reveals a retained runtime descendant.
        Assert.Equal(new[] { ParentPath, ChildPath }, provider.Requests);
        Assert.Equal(SuggestedAction.RecycleCandidate, provider.ParentActionBeforeChild);
        Assert.Contains(memory.SavedParentActions, action => action == SuggestedAction.RecycleCandidate);
        Assert.Equal(SuggestedAction.DoNotDelete, memory.SavedParentActions.Last());
        var finalParent = Assert.Single(first, item => item.Path == ParentPath);
        Assert.Equal(SuggestedAction.DoNotDelete, finalParent.SuggestedAction);
        Assert.Equal(RiskLevel.High, finalParent.RiskLevel);
        Assert.Equal(SuggestedAction.DoNotDelete, Assert.Single(first, item => item.Path == ChildPath).SuggestedAction);
        Assert.Equal(SuggestedAction.DoNotDelete, memory.Entries[ParentPath].Result.SuggestedAction);
        Assert.NotNull(finalParent.OriginalAnalysisId);

        var repeated = await service.AnalyzeAsync(root, options, ProviderOptions);

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(repeated, item => Assert.Equal(AiAnalysisResultOrigin.CacheReused, item.Origin));
        var repeatedParent = Assert.Single(repeated, item => item.Path == ParentPath);
        Assert.Equal(SuggestedAction.DoNotDelete, repeatedParent.SuggestedAction);
        Assert.Equal(finalParent.OriginalAnalysisId, repeatedParent.OriginalAnalysisId);
        Assert.Equal(SuggestedAction.DoNotDelete, memory.Entries[ParentPath].Result.SuggestedAction);

        // Re-reading only the parent cannot rely on seeing the child result to
        // repair stale storage: its saved recommendation must already be final.
        var parentOnly = Assert.Single(await service.AnalyzeAsync(root,
            options with { IncludedAnalysisPaths = new HashSet<string> { ParentPath } }, ProviderOptions));
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, parentOnly.Origin);
        Assert.Equal(SuggestedAction.DoNotDelete, parentOnly.SuggestedAction);
        Assert.Equal(SuggestedAction.DoNotDelete, memory.Entries[ParentPath].Result.SuggestedAction);
    }

    private static FileSystemNode Node(string path, bool directory, long size, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path), directory ? "" : Path.GetExtension(path), size,
        directory ? FileAttributes.Directory : FileAttributes.Normal, DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch, directory, false, [], children);

    private sealed class Provider(Memory memory) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public List<string> Requests { get; } = [];
        public SuggestedAction? ParentActionBeforeChild { get; private set; }

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            var candidate = Assert.Single(batch.Records);
            Requests.Add(candidate.Path);
            batch.RequestContext!.ReportUsage(new(100, 50));
            var isChild = candidate.Path == ChildPath;
            if (isChild) ParentActionBeforeChild = memory.Entries[ParentPath].Result.SuggestedAction;
            var result = new AiAnalysisResult(candidate.Path, isChild ? RiskLevel.High : RiskLevel.Low,
                isChild ? SuggestedAction.DoNotDelete : SuggestedAction.RecycleCandidate,
                isChild ? "程序运行依赖" : "可重建缓存目录", .95)
            {
                ConfidenceLevel = AiConfidenceLevel.High,
                PurposeCategory = isChild ? "runtime" : "cache",
            };
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>([result]);
        }
    }

    private sealed class Memory : IAiAnalysisMemoryStore
    {
        public Dictionary<string, AiAnalysisMemoryEntry> Entries { get; private set; } = new(StringComparer.Ordinal);
        public List<SuggestedAction> SavedParentActions { get; } = [];

        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
                new Dictionary<string, AiAnalysisMemoryEntry>(Entries, StringComparer.Ordinal));

        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            Entries = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            if (Entries.TryGetValue(ParentPath, out var parent)) SavedParentActions.Add(parent.Result.SuggestedAction);
            return Task.CompletedTask;
        }
    }
}

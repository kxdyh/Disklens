using System.Collections.Concurrent;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiPathPurposeEvidenceTests
{
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://example.invalid", "synthetic-model", "synthetic-key");
    private static readonly ScanSessionOptions Options = new(@"E:\", AnalysisThresholdBytes: 1024,
        UseTokenSavingMode: true, MaxAiAnalysisTokens: 100_000);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Own_read_warning_with_known_metadata_can_cache_only_a_path_purpose_request(bool saving, bool directory)
    {
        var item = Node(@"E:\Evidence\Roaming", directory, 2048) with { RiskTags = ["scan-error"] };
        var root = Node(Options.RootPath, true, 2048, item);
        var options = Options with { UseTokenSavingMode = saving };
        var candidate = Assert.Single(new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(root, 1024, 2400, saving));
        Assert.True(candidate.HasUnreadItem);
        Assert.True(candidate.HasUsablePathMetadata);
        Assert.Equal(!saving, candidate.HasLimitedAnalysisEvidence);
        var provider = new Provider();
        var memory = new Memory();
        var service = Service(provider, memory);

        var first = Assert.Single(await service.AnalyzeAsync(root, options, ProviderOptions));
        var second = Assert.Single(await service.AnalyzeAsync(root, options, ProviderOptions));

        if (saving)
        {
            Assert.False(first.LooksLikeFallback());
            Assert.True(first.IsScopeLimited);
            Assert.Equal(AiScopeWarning.ItemReadIncomplete, first.ScopeWarning);
            Assert.False(first.IsAnalysisEvidenceLimited);
            Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, first.AnalysisEvidenceScope);
            Assert.Equal(AiConfidenceLevel.High, first.EffectiveConfidenceLevel);
            Assert.Equal(SuggestedAction.RecycleCandidate, first.SuggestedAction);
            Assert.Equal("本项是应用缓存目录。", first.Reason);
            Assert.NotNull(first.OriginalAnalysisId);
            Assert.Equal(first.OriginalAnalysisId, second.OriginalAnalysisId);
            Assert.Equal(AiAnalysisResultOrigin.CacheReused, second.Origin);
            Assert.Single(memory.Entries);
            Assert.Equal(1, provider.Calls);
        }
        else
        {
            Assert.True(first.LooksLikeFallback());
            Assert.True(first.IsAnalysisEvidenceLimited);
            Assert.Empty(memory.Entries);
            Assert.Equal(0, provider.Calls);
        }
    }

    [Fact]
    public async Task Unread_placeholder_without_metadata_stays_incomplete_and_uncached_in_path_mode()
    {
        var unread = Node(@"E:\Evidence\locked", true, 0) with
        {
            CreatedAt = null, ModifiedAt = null, RiskTags = ["scan-error"],
        };
        var provider = new Provider();
        var memory = new Memory();
        var result = Assert.Single(await Service(provider, memory).AnalyzeAsync(
            Node(Options.RootPath, true, 0, unread), Options, ProviderOptions));

        Assert.True(result.LooksLikeFallback());
        Assert.True(result.IsAnalysisEvidenceLimited);
        Assert.Null(result.OriginalAnalysisId);
        Assert.Empty(memory.Entries);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void Typed_scope_round_trips_but_legacy_limited_results_and_fallbacks_remain_ineligible()
    {
        var result = new AiAnalysisResult(@"E:\Evidence\Users", RiskLevel.Review,
            SuggestedAction.Review, "用户资料目录。", .95)
        {
            ConfidenceLevel = AiConfidenceLevel.High, IsScopeLimited = true,
            ScopeWarning = AiScopeWarning.DescendantReadIncomplete | AiScopeWarning.StructureSummary,
            AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
        };
        var restored = JsonSerializer.Deserialize<AiAnalysisResult>(JsonSerializer.Serialize(result))!;

        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, restored.AnalysisEvidenceScope);
        Assert.True(restored.IsScopeLimited);
        Assert.False(restored.IsAnalysisEvidenceLimited);
        Assert.True((restored with { AnalysisEvidenceScope = default }).IsAnalysisEvidenceLimited);
        Assert.True((restored with { Origin = AiAnalysisResultOrigin.Incomplete }).IsAnalysisEvidenceLimited);
        Assert.True((restored with { IsFallback = true }).IsAnalysisEvidenceLimited);
    }

    [Fact]
    public async Task Partial_parent_path_purpose_joins_singleflight_and_keeps_one_original_identity()
    {
        var parent = PartialParent();
        var root = Node(Options.RootPath, true, parent.SizeBytes, parent);
        var provider = new Provider { Block = true };
        var memory = new Memory();
        var service = Service(provider, memory);
        var first = service.AnalyzeAsync(root, Options, ProviderOptions);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.AnalyzeAsync(root, Options, ProviderOptions);
        provider.Release.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, provider.Calls);
        var owner = Assert.Single(results[0]);
        var follower = Assert.Single(results[1]);
        Assert.True(owner.IsScopeLimited);
        Assert.False(owner.IsAnalysisEvidenceLimited);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, follower.Origin);
        Assert.Equal(owner.OriginalAnalysisId, follower.OriginalAnalysisId);
        Assert.NotNull(owner.OriginalAnalysisId);
    }

    [Fact]
    public async Task Path_purpose_cache_keeps_full_context_and_descendant_signature_invalidation()
    {
        var parent = PartialParent();
        var root = Node(Options.RootPath, true, parent.SizeBytes, parent);
        var provider = new Provider();
        var memory = new Memory();
        var service = Service(provider, memory);
        var original = Assert.Single(await service.AnalyzeAsync(root, Options, ProviderOptions));
        var repeated = Assert.Single(await service.AnalyzeAsync(root, Options, ProviderOptions));
        Assert.Equal(original.OriginalAnalysisId, repeated.OriginalAnalysisId);
        Assert.Equal(1, provider.Calls);
        var changedProvider = ProviderOptions with { ReasoningEffort = "high" };
        await service.AnalyzeAsync(root, Options, changedProvider);
        changedProvider = changedProvider with { Model = "different-model" };
        await service.AnalyzeAsync(root, Options, changedProvider);
        var changedOptions = Options with { AnalysisThresholdBytes = 1025 };
        await service.AnalyzeAsync(root, changedOptions, changedProvider);
        var changedParent = parent with
        {
            Children = [parent.Children[0] with { ModifiedAt = DateTimeOffset.UnixEpoch.AddDays(1) }],
        };
        var refreshed = await service.AnalyzeAsync(root with { Children = [changedParent] }, changedOptions, changedProvider);

        Assert.Equal(5, provider.Calls);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(refreshed).Origin);
        Assert.NotEqual(original.OriginalAnalysisId, Assert.Single(refreshed).OriginalAnalysisId);
    }

    [Fact]
    public async Task Reused_path_purpose_applies_current_scan_warning_even_when_metadata_signature_is_unchanged()
    {
        var item = Node(@"E:\Evidence\Roaming", true, 2048);
        var root = Node(Options.RootPath, true, 2048, item);
        var provider = new Provider();
        var memory = new Memory();
        var service = Service(provider, memory);
        var first = Assert.Single(await service.AnalyzeAsync(root, Options, ProviderOptions));
        Assert.False(first.IsScopeLimited);
        Assert.Equal(SuggestedAction.RecycleCandidate, first.SuggestedAction);

        var partialRoot = root with { Children = [item with { RiskTags = ["scan-error"] }] };
        var cached = Assert.Single(await service.AnalyzeAsync(partialRoot, Options, ProviderOptions));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, cached.Origin);
        Assert.Equal(first.OriginalAnalysisId, cached.OriginalAnalysisId);
        Assert.True(cached.IsScopeLimited);
        Assert.Equal(AiScopeWarning.ItemReadIncomplete, cached.ScopeWarning);
        Assert.Equal(SuggestedAction.RecycleCandidate, cached.SuggestedAction);
        Assert.False(cached.IsAnalysisEvidenceLimited);
        Assert.True(memory.Entries[item.Path].Result.IsScopeLimited);
    }

    [Fact]
    public async Task Structure_response_cannot_claim_path_evidence_to_bypass_incomplete_scan_guards()
    {
        var parent = PartialParent();
        var root = Node(Options.RootPath, true, parent.SizeBytes, parent);
        var provider = new Provider();
        var memory = new Memory();
        var result = Assert.Single(await Service(provider, memory).AnalyzeAsync(root,
            Options with { UseTokenSavingMode = false }, ProviderOptions));

        Assert.Equal(1, provider.Calls);
        Assert.False(result.LooksLikeFallback());
        Assert.Equal(AiAnalysisEvidenceScope.DirectoryStructure, result.AnalysisEvidenceScope);
        Assert.True(result.IsAnalysisEvidenceLimited);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.Review, result.SuggestedAction);
        Assert.Null(result.OriginalAnalysisId);
        Assert.Empty(memory.Entries);
    }

    [Fact]
    public async Task Clearing_memory_epoch_isolates_inflight_path_purpose_and_prevents_old_save_repopulation()
    {
        var parent = PartialParent();
        var root = Node(Options.RootPath, true, parent.SizeBytes, parent);
        var memory = new EpochMemory();
        var oldProvider = new Provider { Block = true };
        var oldService = new AiAnalysisService([oldProvider], new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(), memory);
        var oldAnalysis = oldService.AnalyzeAsync(root, Options, ProviderOptions);
        await oldProvider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        memory.Clear();
        var newProvider = new Provider();
        var newService = new AiAnalysisService([newProvider], new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(), memory);
        var afterClear = Assert.Single(await newService.AnalyzeAsync(root, Options, ProviderOptions)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        oldProvider.Release.TrySetResult();
        var beforeClear = Assert.Single(await oldAnalysis.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, oldProvider.Calls);
        Assert.Equal(1, newProvider.Calls);
        Assert.NotEqual(beforeClear.OriginalAnalysisId, afterClear.OriginalAnalysisId);
        Assert.Equal(afterClear.OriginalAnalysisId, memory.Entries[parent.Path].Result.OriginalAnalysisId);
        Assert.False(afterClear.IsAnalysisEvidenceLimited);
    }

    private static FileSystemNode PartialParent() => Node(@"E:\Evidence\CacheRoot", true, 4096,
        Node(@"E:\Evidence\CacheRoot\alias", true, 0) with { RiskTags = ["skipped", "reparse-point"] });

    private static FileSystemNode Node(string path, bool directory, long size, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path), directory ? "" : Path.GetExtension(path), size,
        directory ? FileAttributes.Directory : FileAttributes.Normal,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, directory, false, [], children);

    private static AiAnalysisService Service(IAiProvider provider, Memory memory) => new(
        [provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory);

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            batch.RequestContext!.ReportUsage(new(100, 50));
            return batch.Records.Select(record => new AiAnalysisResult(record.Path,
                RiskLevel.Low, SuggestedAction.RecycleCandidate, "本项是应用缓存目录。", .95)
            {
                ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "cache",
                // The service must derive scope from the actual request even if a
                // custom provider incorrectly claims to have used path evidence.
                AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
            }).ToArray();
        }
    }

    private sealed class Memory : IAiAnalysisMemoryStore
    {
        public ConcurrentDictionary<string, AiAnalysisMemoryEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
                new Dictionary<string, AiAnalysisMemoryEntry>(Entries, StringComparer.Ordinal));
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries) Entries[entry.Path] = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class EpochMemory : IVersionedAiAnalysisMemoryStore
    {
        private long _epoch;
        public Dictionary<string, AiAnalysisMemoryEntry> Entries { get; } = new(StringComparer.Ordinal);
        public void Clear() { _epoch++; Entries.Clear(); }
        public Task<AiAnalysisMemorySession> BeginSessionAsync(string rootPath, long snapshotGeneration,
            CancellationToken cancellationToken = default) => Task.FromResult(new AiAnalysisMemorySession(_epoch, snapshotGeneration));
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
                new Dictionary<string, AiAnalysisMemoryEntry>(Entries, StringComparer.Ordinal));
        public Task SaveSessionEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            AiAnalysisMemorySession session, CancellationToken cancellationToken = default)
        {
            if (session.Epoch == _epoch)
                foreach (var entry in entries) Entries[entry.Path] = entry;
            return Task.CompletedTask;
        }
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("A versioned save is required.");
    }
}

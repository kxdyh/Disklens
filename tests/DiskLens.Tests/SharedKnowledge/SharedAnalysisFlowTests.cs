using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class SharedAnalysisFlowTests
{
    // This fixture exercises individual child flows; a volume is only their scope.
    private const string RootPath = @"E:\";
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://test.invalid", "fixture", "synthetic");

    [Fact]
    public async Task Batch_analysis_submits_only_cloud_misses_and_keeps_shared_results_out_of_contributions()
    {
        var first = File("known.zip"); var second = File("unknown.zip");
        var shared = new Pipeline { HitPath = first.Path, Missing = [second.Path] };
        var provider = new Provider();
        var report = await Service(provider, shared).AnalyzeWithReportAsync(Folder(RootPath, first, second),
            new(RootPath, AnalysisThresholdBytes: 1), ProviderOptions, provenance: new("custom", "other", true));

        Assert.Equal([second.Path], provider.Paths);
        Assert.Equal([second.Path], report.SharedMissingPaths);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, report.Results.Single(x => x.Path == first.Path).Origin);
        Assert.DoesNotContain(shared.Uploaded, x => x.Path == first.Path);
    }

    [Fact]
    public async Task Explicit_batch_reanalysis_ignores_cloud_hits_and_analyzes_exact_selected_items_in_one_request()
    {
        var nested = File("folder\\child.bin");
        var directory = Folder(Path.Combine(RootPath, "folder"), nested);
        var selected = File("small.txt", 32); var ignored = File("ignored.zip");
        var shared = new Pipeline { HitPath = selected.Path };
        var provider = new Provider();
        var report = await Service(provider, shared).AnalyzeWithReportAsync(Folder(RootPath, directory, selected, ignored),
            new(RootPath, AnalysisThresholdBytes: 1024 * 1024, BypassAnalysisMemory: true)
            { IncludedAnalysisPaths = new HashSet<string>([directory.Path, selected.Path], StringComparer.Ordinal) }, ProviderOptions);

        Assert.Equal(0, shared.ResolveCalls);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(new[] { directory.Path, selected.Path }.Order(), provider.Paths.Order());
        Assert.DoesNotContain(nested.Path, provider.Paths);
        Assert.DoesNotContain(ignored.Path, provider.Paths);
        Assert.Equal(2, report.SubmittedCandidateCount);
    }

    [Fact]
    public async Task Empty_selection_never_falls_back_to_analyzing_the_entire_scan()
    {
        var provider = new Provider();
        var report = await Service(provider, new Pipeline()).AnalyzeWithReportAsync(Folder(RootPath, File("a.zip")),
            new(RootPath, AnalysisThresholdBytes: 1, BypassAnalysisMemory: true)
            { IncludedAnalysisPaths = new HashSet<string>() }, ProviderOptions);
        Assert.Empty(report.Results);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Transport_upload_failure_is_visible_without_discarding_successful_analysis()
    {
        var provider = new Provider(); var shared = new Pipeline { UploadFails = true };
        var report = await Service(provider, shared).AnalyzeWithReportAsync(Folder(RootPath, File("a.zip")),
            new(RootPath, AnalysisThresholdBytes: 1), ProviderOptions, provenance: new("custom", "other", true));
        Assert.False(Assert.Single(report.Results).LooksLikeFallback());
        Assert.Contains("上传至云端经验库失败", report.SharedKnowledgeWarning);
        Assert.DoesNotContain("自己的", report.SharedKnowledgeWarning);
    }

    [Fact]
    public async Task Queued_upload_network_failure_does_not_mark_successful_analysis_as_globally_offline()
    {
        var provider = new ShareableProvider();
        var shared = new Pipeline { UploadOffline = true };
        var report = await Service(provider, shared).AnalyzeWithReportAsync(Folder(RootPath, File("offline-upload.zip")),
            new(RootPath, AnalysisThresholdBytes: 1), ProviderOptions, provenance: new("custom", "other", true));

        Assert.False(report.NetworkUnavailable);
        Assert.False(Assert.Single(report.Results).LooksLikeFallback());
        Assert.Single(shared.Uploaded);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("已保存到本机待上传队列", report.SharedKnowledgeWarning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_contribution_is_retried_from_cache_after_restart_with_the_original_analysis_id(bool protectedLocation)
    {
        var root = Folder(RootPath, File("recoverable.zip") with { IsProtectedLocation = protectedLocation });
        var memory = new DurableMemory();
        var firstProvider = new ShareableProvider();
        var firstPipeline = new Pipeline { ContributionException = new IOException("simulated upload failure") };
        var options = new ScanSessionOptions(RootPath, AnalysisThresholdBytes: 1, UseTokenSavingMode: true);
        var provenance = new SharedAnalysisProvenance("custom", "other", true)
        { PromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(useTokenSavingMode: true) };

        var first = await Service(firstProvider, firstPipeline, memory).AnalyzeWithReportAsync(root,
            options, ProviderOptions, provenance: provenance);
        var original = Assert.Single(first.Results);
        var saved = Assert.Single(memory.Entries.Values);
        Assert.True(saved.SharedContributionPending);
        Assert.False(saved.SharedContributionCompleted);

        // Recreate service and pipeline as an app restart would. The cache hit must
        // not suppress retry of the contribution intent persisted with the result.
        var secondProvider = new ShareableProvider();
        var secondPipeline = new Pipeline();
        var second = await Service(secondProvider, secondPipeline, memory).AnalyzeWithReportAsync(root,
            options, ProviderOptions, provenance: provenance);

        Assert.Equal(0, secondProvider.Calls);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, Assert.Single(second.Results).Origin);
        var uploaded = Assert.Single(secondPipeline.Uploaded);
        Assert.Equal(original.OriginalAnalysisId, uploaded.OriginalAnalysisId);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, uploaded.Origin);
        saved = Assert.Single(memory.Entries.Values);
        Assert.False(saved.SharedContributionPending);
        Assert.True(saved.SharedContributionCompleted);
    }

    [Fact]
    public async Task Low_confidence_local_result_is_not_marked_for_shared_contribution_retry()
    {
        var root = Folder(RootPath, File("uncertain.zip"));
        var memory = new DurableMemory();
        var provider = new ShareableProvider { ConfidenceLevel = AiConfidenceLevel.Low };
        var pipeline = new Pipeline { ContributionException = new IOException("should not be reached") };
        var options = new ScanSessionOptions(RootPath, AnalysisThresholdBytes: 1, UseTokenSavingMode: true);
        var provenance = new SharedAnalysisProvenance("custom", "other", true)
        { PromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(useTokenSavingMode: true) };

        await Service(provider, pipeline, memory).AnalyzeWithReportAsync(root, options, ProviderOptions, provenance: provenance);
        var saved = Assert.Single(memory.Entries.Values);
        Assert.False(saved.SharedContributionPending);
        Assert.True(saved.SharedContributionCompleted);
        Assert.Empty(pipeline.Uploaded);
    }

    private static AiAnalysisService Service(IAiProvider provider, Pipeline shared, IAiAnalysisMemoryStore? memory = null) =>
        new([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory, shared);

    private static FileSystemNode File(string name, long bytes = 2_000_000) => new(Path.Combine(RootPath, name),
        Path.GetFileName(name), Path.GetExtension(name), bytes, FileAttributes.Normal, null, null, false, false, [], [], 1);

    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(path, Path.GetFileName(path),
        "", children.Sum(x => x.SizeBytes), FileAttributes.Directory, null, null, true, false, [], children, children.Length);

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls;
        public List<string> Paths { get; } = [];
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Calls++; Paths.AddRange(batch.Records.Select(x => x.Path));
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(x =>
                new AiAnalysisResult(x.Path, RiskLevel.Review, SuggestedAction.Review, "合成用途说明", .9)).ToArray());
        }
    }

    private sealed class Pipeline : ISharedKnowledgePipeline
    {
        public string? HitPath;
        public string[] Missing = [];
        public bool UploadFails;
        public bool UploadOffline;
        public Exception? ContributionException;
        public int ResolveCalls;
        public List<AiAnalysisResult> Uploaded { get; } = [];
        private static readonly SharedConsentSnapshot Consent = new(true, SharedConsentSnapshot.CurrentAgreementVersion,
            false, DateTimeOffset.UtcNow.AddHours(-1), 4, "test-policy", [], new("test-installation", "test-management-token", 4));
        public Task<SharedConsentSnapshot?> CaptureConsentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SharedConsentSnapshot?>(Consent);
        public Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            var hits = candidates.Where(x => x.Path == HitPath).ToDictionary(x => x.Path,
                x => new AiAnalysisResult(x.Path, RiskLevel.Review, SuggestedAction.Review, "云端用途说明", .9)
                { Origin = AiAnalysisResultOrigin.SharedExperience });
            return Task.FromResult(new SharedResolutionBatch(hits, hits.Count, 0, false) { MissingPaths = Missing });
        }
        public Task<SharedContributionReceipt> ContributeAsync(IReadOnlyList<AiCandidateRecord> candidates,
            IReadOnlyList<AiAnalysisResult> results, SharedAnalysisProvenance provenance, bool historical = false,
            CancellationToken cancellationToken = default)
        {
            if (ContributionException is not null) throw ContributionException;
            Uploaded.AddRange(results);
            if (UploadOffline) return Task.FromResult(new SharedContributionReceipt(0, 0, 1)
                { Failed = results.Count, Pending = results.Count, NetworkUnavailable = true });
            return Task.FromResult(new SharedContributionReceipt(UploadFails ? 0 : results.Count, UploadFails ? results.Count : 0, 1)
            { Failed = UploadFails ? results.Count : 0 });
        }
    }

    private sealed class ShareableProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls;
        public AiConfidenceLevel ConfidenceLevel { get; init; } = AiConfidenceLevel.High;
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record => new AiAnalysisResult(
                record.Path, RiskLevel.Review, SuggestedAction.Review, "应用运行缓存，可在关闭程序后清理", .93)
            {
                ShortPurpose = "应用运行缓存", ConfidenceLevel = ConfidenceLevel, PurposeCategory = "cache",
                AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
            }).ToArray());
        }
    }

    private sealed class DurableMemory : IAiAnalysisMemoryStore
    {
        public Dictionary<string, AiAnalysisMemoryEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            lock (Entries) return Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
                new Dictionary<string, AiAnalysisMemoryEntry>(Entries, StringComparer.Ordinal));
        }
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries, CancellationToken cancellationToken = default)
        {
            lock (Entries) foreach (var entry in entries) Entries[entry.Path] = entry;
            return Task.CompletedTask;
        }
    }
}

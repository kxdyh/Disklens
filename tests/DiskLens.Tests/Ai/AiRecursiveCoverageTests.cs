using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.Ai;

public sealed class AiRecursiveCoverageTests
{
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://example.invalid", "synthetic-model", "synthetic-key");
    private static readonly ScanSessionOptions Options = new(@"E:\", AnalysisThresholdBytes: 1024,
        UseTokenSavingMode: true, MaxAiAnalysisTokens: 500_000);

    [Fact]
    public async Task Whole_scan_covers_every_threshold_parent_and_file_across_more_than_one_hundred_candidates()
    {
        var root = LargeTree();
        var provider = new Provider();
        var expected = root.DescendantsAndSelf().Skip(1).Where(node => node.SizeBytes >= 1024)
            .Select(node => node.Path).ToHashSet(StringComparer.Ordinal);
        var report = await Service(provider).AnalyzeWithReportAsync(root, Options, ProviderOptions);

        Assert.True(expected.Count > 100);
        Assert.Equal(expected.Count, report.TotalCandidates);
        Assert.Equal(expected.Count, report.SubmittedCandidateCount);
        Assert.Equal(expected.Count, report.Results.Count);
        Assert.True(expected.SetEquals(report.Results.Select(result => result.Path)));
        Assert.True(expected.SetEquals(provider.Requested.Select(record => record.Path)));
        Assert.All(report.Results, result => Assert.False(result.LooksLikeFallback()));
        Assert.Contains(report.Results, result => result.Path == @"E:\Coverage\CacheRoot" && result.IsScopeLimited);
        Assert.DoesNotContain(report.Results, result => result.Path.EndsWith("small.tmp", StringComparison.Ordinal));
        Assert.Equal(2, report.LimitedScopeCount);
    }

    [Fact]
    public async Task Path_only_cache_advice_is_cached_queried_and_contributed_while_scan_coverage_stays_limited()
    {
        var root = LargeTree();
        var provider = new Provider();
        var store = new Memory();
        var shared = new Shared();
        var service = Service(provider, store, shared);
        var provenance = new SharedAnalysisProvenance("official", "openai", true)
            { PromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(true) };
        var first = await service.AnalyzeWithReportAsync(root, Options, ProviderOptions, provenance: provenance);
        var limitedPaths = first.Results.Where(result => result.IsScopeLimited).Select(result => result.Path).ToHashSet();
        Assert.Equal(2, limitedPaths.Count);
        foreach (var result in first.Results.Where(result => result.IsScopeLimited))
        {
            Assert.Equal("cache", result.PurposeCategory);
            Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
            Assert.Equal(SuggestedAction.RecycleCandidate, result.SuggestedAction);
            Assert.DoesNotContain("已读范围用途", result.Reason);
            Assert.Equal(AiScopeWarning.DescendantReadIncomplete, result.ScopeWarning);
            Assert.Contains("模型用途说明", result.Reason);
            Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, result.AnalysisEvidenceScope);
            Assert.False(result.IsAnalysisEvidenceLimited);
            Assert.NotNull(result.OriginalAnalysisId);
            Assert.NotNull(result.OriginalAnalyzedAt);
            Assert.Contains(result.Path, store.Entries.Keys);
            Assert.True(store.Entries[result.Path].IsUsableAt(DateTimeOffset.UtcNow));
        }
        Assert.Equal(2, shared.Queried.Count(candidate => limitedPaths.Contains(candidate.Path)));
        Assert.Equal(2, shared.Contributed.Count(result => limitedPaths.Contains(result.Path)));
        var second = await service.AnalyzeWithReportAsync(root, Options, ProviderOptions, provenance: provenance);
        Assert.Equal(0, second.SubmittedCandidateCount);
        Assert.All(second.Results.Where(result => limitedPaths.Contains(result.Path)), result => Assert.True(result.IsScopeLimited));
        Assert.All(second.Results, result =>
        {
            Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin);
            Assert.Equal(first.Results.Single(original => original.Path == result.Path).OriginalAnalysisId, result.OriginalAnalysisId);
        });
        Assert.All(provider.Payloads, payload => Assert.DoesNotContain("analysisScope", payload));
    }

    [Fact]
    public async Task Insufficient_budget_returns_an_explicit_budget_status_for_every_threshold_candidate()
    {
        var provider = new Provider();
        var root = LargeTree();
        var report = await Service(provider).AnalyzeWithReportAsync(root,
            Options with { MaxAiAnalysisTokens = 1 }, ProviderOptions);
        Assert.Equal(0, report.SubmittedCandidateCount);
        Assert.Empty(provider.Requested);
        Assert.Equal(report.TotalCandidates, report.Results.Count);
        Assert.All(report.Results, result =>
        {
            Assert.True(result.LooksLikeFallback());
            Assert.Contains("预算", result.Reason);
        });
    }

    [Fact]
    public async Task Missing_parent_response_is_retried_without_losing_its_path_only_cache_advice_or_scope_warning()
    {
        var file = File(@"E:\Coverage\CacheRoot\large.zip");
        var parent = Directory(@"E:\Coverage\CacheRoot", file,
            Directory(@"E:\Coverage\CacheRoot\alias") with { RiskTags = ["skipped", "reparse-point"] });
        var provider = new Provider { OmitDirectoryOnFirstRequest = true };
        var memory = new Memory();
        var report = await Service(provider, memory).AnalyzeWithReportAsync(Directory(Options.RootPath, parent), Options, ProviderOptions);
        var result = Assert.Single(report.Results, item => item.Path == parent.Path);
        Assert.False(result.LooksLikeFallback());
        Assert.True(result.IsScopeLimited);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.RecycleCandidate, result.SuggestedAction);
        Assert.Equal("cache", result.PurposeCategory);
        Assert.Equal(2, report.TotalCandidates);
        Assert.Equal(2, report.SubmittedCandidateCount);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, result.AnalysisEvidenceScope);
        Assert.False(result.IsAnalysisEvidenceLimited);
        Assert.NotNull(result.OriginalAnalysisId);
        Assert.Equal(result.OriginalAnalysisId, memory.Entries[parent.Path].Result.OriginalAnalysisId);
    }

    [Fact]
    public async Task Summarized_directory_gets_an_explicit_scope_and_preserves_its_purpose_without_safe_delete_advice()
    {
        var children = Enumerable.Range(0, 20).Select(index => File($@"E:\Coverage\cache\payload-{index}.tmp") with { SizeBytes = 100 }).ToArray();
        var root = Directory(Options.RootPath, Directory(@"E:\Coverage\cache", children));
        var provider = new Provider();
        var report = await Service(provider).AnalyzeWithReportAsync(root,
            Options with { UseTokenSavingMode = false, MaxAiChunkTokens = 256 }, ProviderOptions);
        var result = Assert.Single(report.Results);
        Assert.True(result.IsScopeLimited);
        Assert.False(result.LooksLikeFallback());
        Assert.Equal("cache", result.PurposeCategory);
        Assert.Equal(SuggestedAction.Review, result.SuggestedAction);
        Assert.DoesNotContain("结构摘要", result.Reason);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.Equal(AiScopeWarning.StructureSummary, result.ScopeWarning);
        Assert.Contains("structure-summary", Assert.Single(provider.Payloads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_omissions_stay_internal_and_saving_mode_always_sends_only_the_item_path_and_size(bool saving)
    {
        var child = File(@"E:\Coverage\folder\large.zip");
        var folder = Directory(@"E:\Coverage\folder", child);
        var candidate = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(
            Directory(Options.RootPath, folder), 1024, 2400, saving).Single(record => record.IsDirectory)
            with { HasIncompleteScan = true, ScopeLimitation = "部分子项读取失败。" };
        var payload = AiPromptFactory.CreateUserPayload(new([candidate], 100, saving));
        Assert.DoesNotContain("partial-scan", payload);
        Assert.DoesNotContain("scopeLimitation", payload);
        Assert.DoesNotContain("读取失败", payload);
        var compact = AiPromptFactory.CreateUserPayload(new AiBatch([candidate], 100, saving) { UseCompactPayload = true });
        Assert.DoesNotContain("partial-scan", compact);
        if (saving)
        {
            Assert.Equal(payload, compact);
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            Assert.Equal(new[] { "Path", "SizeBytes" }, json.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name));
        }
        else
        {
            Assert.Contains("path-purpose", payload);
            Assert.Contains("structure-summary", compact);
        }
    }

    [Fact]
    public async Task Limited_scope_never_downgrades_model_keep_or_protected_advice_into_removal()
    {
        var child = File(@"E:\Coverage\folder\large.zip");
        var folder = Directory(@"E:\Coverage\folder", child,
            Directory(@"E:\Coverage\folder\alias") with { RiskTags = ["skipped", "reparse-point"] });
        foreach (var action in new[] { SuggestedAction.Keep, SuggestedAction.DoNotDelete })
        {
            var provider = new Provider { ReturnedAction = action };
            var report = await Service(provider).AnalyzeWithReportAsync(Directory(Options.RootPath, folder), Options, ProviderOptions);
            var result = Assert.Single(report.Results, item => item.Path == folder.Path);
            Assert.Equal(action, result.SuggestedAction);
            Assert.True(result.IsScopeLimited);
            Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, true)]
    public async Task Empty_parent_explanation_is_recovered_as_a_missing_item_without_repeating_valid_children(int emptyReasonRequests, bool remainsIncomplete)
    {
        var child = File(@"E:\Coverage\folder\large.zip");
        var folder = Directory(@"E:\Coverage\folder", child,
            Directory(@"E:\Coverage\folder\alias") with { RiskTags = ["skipped", "reparse-point"] });
        var provider = new Provider { EmptyDirectoryReasonRequests = emptyReasonRequests };
        var memory = new Memory();
        var report = await Service(provider, memory).AnalyzeWithReportAsync(Directory(Options.RootPath, folder), Options, ProviderOptions);
        var parentResult = Assert.Single(report.Results, result => result.Path == folder.Path);
        Assert.Equal(remainsIncomplete, parentResult.LooksLikeFallback());
        Assert.False(Assert.Single(report.Results, result => result.Path == child.Path).LooksLikeFallback());
        Assert.Single(provider.Requested, record => record.Path == child.Path);
        Assert.Equal(2, provider.Requested.Count(record => record.Path == folder.Path));
        Assert.Equal(!remainsIncomplete, memory.Entries.ContainsKey(folder.Path));
        Assert.Contains(child.Path, memory.Entries.Keys);
        if (!remainsIncomplete)
        {
            Assert.True(parentResult.IsScopeLimited);
            Assert.Contains("模型用途说明", parentResult.Reason);
            Assert.Equal(AiConfidenceLevel.High, parentResult.EffectiveConfidenceLevel);
            Assert.False(parentResult.IsAnalysisEvidenceLimited);
            Assert.NotNull(parentResult.OriginalAnalysisId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_results_cannot_inject_an_unqueried_partial_or_summarized_parent(bool summarized)
    {
        var child = File(@"E:\Coverage\folder\large.zip");
        var children = summarized
            ? Enumerable.Range(0, 20).Select(index => File($@"E:\Coverage\folder\small-{index}.zip") with { SizeBytes = 100 }).Append(child).ToArray()
            : new[] { child, Directory(@"E:\Coverage\folder\alias") with { RiskTags = ["skipped", "reparse-point"] } };
        var folder = Directory(@"E:\Coverage\folder", children);
        const string outsidePath = @"E:\Unrequested\outside.zip";
        var injected = new[] { folder.Path, child.Path, outsidePath }.ToDictionary(path => path,
            path => new AiAnalysisResult(path, RiskLevel.Review, SuggestedAction.Review, "越界共享实现返回的结论。", .9)
            { Origin = AiAnalysisResultOrigin.SharedExperience, ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "downloads" },
            StringComparer.Ordinal);
        var shared = new Shared { InjectedResults = injected };
        var provider = new Provider();
        var memory = new Memory();
        var report = await Service(provider, memory, shared).AnalyzeWithReportAsync(Directory(Options.RootPath, folder),
            Options with { UseTokenSavingMode = false, MaxAiChunkTokens = summarized ? 256 : 2400 }, ProviderOptions);

        Assert.Equal(child.Path, Assert.Single(shared.Queried).Path);
        Assert.Equal(folder.Path, Assert.Single(provider.Requested).Path);
        var parentResult = Assert.Single(report.Results, result => result.Path == folder.Path);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, parentResult.Origin);
        Assert.True(parentResult.IsScopeLimited);
        Assert.False(parentResult.LooksLikeFallback());
        Assert.Equal(AiConfidenceLevel.High, parentResult.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.Review, parentResult.SuggestedAction);
        Assert.Contains("模型用途说明", parentResult.Reason);
        Assert.DoesNotContain("越界共享", parentResult.Reason);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, Assert.Single(report.Results, result => result.Path == child.Path).Origin);
        Assert.DoesNotContain(report.Results, result => result.Path == outsidePath);
        Assert.Equal(2, report.TotalCandidates);
        Assert.Equal(1, report.SubmittedCandidateCount);
        Assert.Equal(1, report.LimitedScopeCount);
        Assert.Empty(memory.Entries);
    }

    private static FileSystemNode LargeTree()
    {
        const string profile = @"E:\Coverage\CacheRoot\user";
        var groups = Enumerable.Range(0, 10).Select(group =>
            Directory($@"{profile}\Downloads\group-{group}", Enumerable.Range(0, 10)
                .Select(file => File($@"{profile}\Downloads\group-{group}\file-{file}.zip")).ToArray())).ToArray();
        var downloads = Directory(profile + @"\Downloads", groups);
        var alias = Directory(profile + @"\alias") with { RiskTags = ["skipped", "reparse-point"] };
        var user = Directory(profile, downloads, alias, File(profile + @"\small.tmp") with { SizeBytes = 100 });
        return Directory(Options.RootPath, Directory(@"E:\Coverage\CacheRoot", user));
    }

    private static FileSystemNode File(string path) => new(path, Path.GetFileName(path), Path.GetExtension(path),
        1024, FileAttributes.Normal, null, null, false, false, [], [], 1);
    private static FileSystemNode Directory(string path, params FileSystemNode[] children) => new(path,
        Path.GetFileName(path), "", children.Sum(child => child.SizeBytes), FileAttributes.Directory,
        null, null, true, false, [], children, children.Sum(child => child.DescendantFileCount),
        1 + children.Sum(child => child.DescendantDirectoryCount));
    private static AiAnalysisService Service(Provider provider, Memory? memory = null, Shared? shared = null)
        => new([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory, shared);

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public bool OmitDirectoryOnFirstRequest { get; init; }
        public int EmptyDirectoryReasonRequests { get; init; }
        public SuggestedAction ReturnedAction { get; init; } = SuggestedAction.RecycleCandidate;
        public List<AiCandidateRecord> Requested { get; } = [];
        public List<string> Payloads { get; } = [];
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requested.AddRange(batch.Records);
            Payloads.Add(batch.RequestContext!.UserPayload);
            batch.RequestContext.ReportUsage(new(100, 100));
            var records = OmitDirectoryOnFirstRequest && Calls == 1 ? batch.Records.Where(record => !record.IsDirectory) : batch.Records;
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(records.Select(record => new AiAnalysisResult(record.Path,
                RiskLevel.Low, ReturnedAction,
                record.IsDirectory && Calls <= EmptyDirectoryReasonRequests ? " \t" : "模型用途说明：可重建缓存。", .9)
                { ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "cache" }).ToArray());
        }
    }
    private sealed class Memory : IAiAnalysisMemoryStore
    {
        public IReadOnlyDictionary<string, AiAnalysisMemoryEntry> Entries { get; private set; } = new Dictionary<string, AiAnalysisMemoryEntry>();
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath, CancellationToken cancellationToken = default) => Task.FromResult(Entries);
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries, CancellationToken cancellationToken = default)
        { Entries = entries.ToDictionary(entry => entry.Path); return Task.CompletedTask; }
    }
    private sealed class Shared : ISharedKnowledgePipeline
    {
        public List<AiCandidateRecord> Queried { get; } = [];
        public List<AiAnalysisResult> Contributed { get; } = [];
        public IReadOnlyDictionary<string, AiAnalysisResult>? InjectedResults { get; init; }
        public Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates, CancellationToken cancellationToken = default)
        {
            Queried.AddRange(candidates);
            return Task.FromResult(InjectedResults is null ? SharedResolutionBatch.Empty : new SharedResolutionBatch(InjectedResults, InjectedResults.Count, 0, false));
        }
        public Task<SharedContributionReceipt> ContributeAsync(IReadOnlyList<AiCandidateRecord> candidates,
            IReadOnlyList<AiAnalysisResult> results, SharedAnalysisProvenance provenance, bool historical = false, CancellationToken cancellationToken = default)
        { Contributed.AddRange(results); return Task.FromResult(new SharedContributionReceipt(results.Count, 0, 0)); }
    }
}

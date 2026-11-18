using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiCandidateCompletenessTests
{
    private static readonly AiProviderOptions ProviderOptions = new(
        AiProviderKind.OpenAiCompatible, "https://example.invalid", "test-model", "test-key");

    [Theory]
    [InlineData("reparse-point")]
    [InlineData("reparse-cycle")]
    [InlineData("duplicate-target")]
    public void Intentionally_skipped_zero_byte_aliases_do_not_inflate_AI_candidate_count(string aliasTag)
    {
        var aliases = Enumerable.Range(0, 800).Select(index => Node($@"E:\Audit\alias{index}", 0,
            directory: true, tags: ["skipped", aliasTag]));
        var largeFile = Node(@"E:\Audit\large.bin", 2048);
        var root = Node(@"E:\", 2048, directory: true, children: [.. aliases, largeFile]);

        var candidates = Builder().BuildCandidates(root, 1024, 2400);

        Assert.Equal(largeFile.Path, Assert.Single(candidates).Path);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("scan-error")]
    [InlineData("skipped")]
    public void Real_errors_and_unclassified_omissions_stay_visible_even_below_threshold(string tag)
    {
        var unread = Node(@"E:\Audit\unknown", 0, directory: true, tags: [tag]);
        var root = Node(@"E:\", 0, directory: true, children: [unread]);

        Assert.Equal(unread.Path, Assert.Single(Builder().BuildCandidates(root, 1024, 2400)).Path);
    }

    [Fact]
    public void Alias_also_reporting_a_real_read_error_is_not_silently_discarded()
    {
        var unread = Node(@"E:\Audit\unknown", 0, directory: true,
            tags: ["skipped", "reparse-point", "scan-error"]);
        var root = Node(@"E:\", 0, directory: true, children: [unread]);

        Assert.Equal(unread.Path, Assert.Single(Builder().BuildCandidates(root, 1024, 2400)).Path);
    }

    [Fact]
    public async Task Excluded_link_keeps_scope_warning_without_overriding_known_cache_path_advice()
    {
        var link = Node(@"E:\Audit\folder\alias", 0, directory: true, tags: ["skipped", "reparse-point"]);
        var readable = Node(@"E:\Audit\folder\large.bin", 2048);
        var folder = Node(@"E:\Audit\folder", 2048, directory: true, children: [link, readable]);
        var root = Node(@"E:\", 2048, directory: true, children: [folder]);
        var provider = new Provider();

        var report = await Service(provider).AnalyzeWithReportAsync(root, Options(), ProviderOptions);

        Assert.Equal(2, report.Results.Count);
        var skippedDirectory = Assert.Single(report.Results, item => item.Path == folder.Path);
        Assert.False(skippedDirectory.LooksLikeFallback());
        Assert.Equal("测试提供方：可能清理。", skippedDirectory.Reason);
        Assert.Equal(AiScopeWarning.DescendantReadIncomplete, skippedDirectory.ScopeWarning);
        Assert.DoesNotContain("读取错误", skippedDirectory.Reason);
        Assert.False(Assert.Single(report.Results, item => item.Path == readable.Path).LooksLikeFallback());
        Assert.Contains(folder.Path, provider.RequestedPaths);
        Assert.Contains(readable.Path, provider.RequestedPaths);
        Assert.Equal(SuggestedAction.RecycleCandidate, skippedDirectory.SuggestedAction);
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, skippedDirectory.AnalysisEvidenceScope);
        Assert.False(skippedDirectory.IsAnalysisEvidenceLimited);
        Assert.Equal(AiConfidenceLevel.High, skippedDirectory.EffectiveConfidenceLevel);
        Assert.Equal("cache", skippedDirectory.PurposeCategory);
        Assert.DoesNotContain(report.Results, item => item.Path == link.Path);
    }

    [Fact]
    public async Task Child_read_error_keeps_known_cache_path_advice_and_does_not_skip_readable_candidates()
    {
        var unread = Node(@"E:\Audit\folder\locked.bin", 0, tags: ["error"]);
        var descendant = Node(@"E:\Audit\folder\large.bin", 2048);
        var folder = Node(@"E:\Audit\folder", 2048, directory: true, children: [unread, descendant]);
        var sibling = Node(@"E:\Audit\other.bin", 4096);
        var root = Node(@"E:\", 6144, directory: true, children: [folder, sibling]);
        var provider = new Provider();

        var report = await Service(provider).AnalyzeWithReportAsync(root, Options(), ProviderOptions);

        var partial = Assert.Single(report.Results, item => item.Path == folder.Path);
        Assert.False(partial.LooksLikeFallback());
        Assert.Equal("测试提供方：可能清理。", partial.Reason);
        Assert.Equal(AiConfidenceLevel.High, partial.EffectiveConfidenceLevel);
        Assert.Equal(AiScopeWarning.DescendantReadIncomplete, partial.ScopeWarning);
        Assert.Equal(SuggestedAction.RecycleCandidate, partial.SuggestedAction);
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, partial.AnalysisEvidenceScope);
        Assert.False(partial.IsAnalysisEvidenceLimited);
        Assert.True(Assert.Single(report.Results, item => item.Path == unread.Path).LooksLikeFallback());
        Assert.False(Assert.Single(report.Results, item => item.Path == descendant.Path).LooksLikeFallback());
        Assert.False(Assert.Single(report.Results, item => item.Path == sibling.Path).LooksLikeFallback());
        Assert.Equal(3, provider.RequestedPaths.Count);
        Assert.Contains(folder.Path, provider.RequestedPaths);
        Assert.Contains(descendant.Path, provider.RequestedPaths);
        Assert.Contains(sibling.Path, provider.RequestedPaths);
    }

    [Fact]
    public async Task Read_incomplete_parent_with_known_metadata_keeps_path_only_cache_advice()
    {
        var file = Node(@"E:\Audit\folder\large.bin", 2048);
        var folder = Node(@"E:\Audit\folder", 2048, directory: true, tags: ["scan-error"], children: [file]);
        var root = Node(@"E:\", 2048, directory: true, children: [folder]);
        var provider = new Provider();

        var report = await Service(provider).AnalyzeWithReportAsync(root, Options(), ProviderOptions);

        var incomplete = Assert.Single(report.Results, item => item.Path == folder.Path);
        Assert.False(incomplete.LooksLikeFallback());
        Assert.Equal("测试提供方：可能清理。", incomplete.Reason);
        Assert.Equal(AiConfidenceLevel.High, incomplete.EffectiveConfidenceLevel);
        Assert.Equal(AiScopeWarning.ItemReadIncomplete, incomplete.ScopeWarning);
        Assert.Equal(SuggestedAction.RecycleCandidate, incomplete.SuggestedAction);
        Assert.True(incomplete.IsScopeLimited);
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, incomplete.AnalysisEvidenceScope);
        Assert.False(incomplete.IsAnalysisEvidenceLimited);
        Assert.Contains(folder.Path, provider.RequestedPaths);
        Assert.Contains(file.Path, provider.RequestedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_cache_parent_keeps_its_confidence_and_only_path_evidence_preserves_removal_advice(bool saving)
    {
        var unread = Node(@"E:\Audit\CacheRoot\locked", 0, directory: true, tags: ["scan-error"]);
        var alias = Node(@"E:\Audit\CacheRoot\alias", 0, directory: true, tags: ["skipped", "reparse-point"]);
        var parent = Node(@"E:\Audit\CacheRoot", 2048, directory: true, children: [unread, alias]);
        var provider = new Provider();
        var report = await Service(provider).AnalyzeWithReportAsync(
            Node(@"E:\", 2048, directory: true, children: [parent]),
            Options() with { UseTokenSavingMode = saving }, ProviderOptions);

        var purpose = Assert.Single(report.Results, result => result.Path == parent.Path);
        Assert.False(purpose.LooksLikeFallback());
        Assert.Equal("测试提供方：可能清理。", purpose.Reason);
        Assert.Equal(AiConfidenceLevel.High, purpose.EffectiveConfidenceLevel);
        Assert.Equal(.8, purpose.Confidence);
        Assert.Equal(AiScopeWarning.DescendantReadIncomplete, purpose.ScopeWarning);
        // Child coverage constrains a structure-based recommendation; path-only
        // evidence retains the model's explicit known-cache recommendation.
        Assert.Equal(saving ? SuggestedAction.RecycleCandidate : SuggestedAction.Review, purpose.SuggestedAction);
        Assert.Equal(saving ? AiAnalysisEvidenceScope.PathPurpose : AiAnalysisEvidenceScope.DirectoryStructure,
            purpose.AnalysisEvidenceScope);
        Assert.Equal(!saving, purpose.IsAnalysisEvidenceLimited);
        Assert.Equal(parent.Path, Assert.Single(provider.RequestedPaths));
        var missing = Assert.Single(report.Results, result => result.Path == unread.Path);
        Assert.True(missing.LooksLikeFallback());
        Assert.Equal(AiScopeWarning.ItemReadIncomplete, missing.ScopeWarning);
        Assert.Contains("本项未能读取", missing.Reason);
        Assert.DoesNotContain(report.Results, result => result.Path == alias.Path);
    }

    [Theory]
    [InlineData(.2, AiConfidenceLevel.Low, SuggestedAction.DoNotDelete)]
    [InlineData(.65, AiConfidenceLevel.Medium, SuggestedAction.RecycleCandidate)]
    [InlineData(.9, AiConfidenceLevel.High, SuggestedAction.RecycleCandidate)]
    public async Task Path_only_scope_preserves_confidence_while_low_confidence_still_blocks_cache_removal(
        double confidence, AiConfidenceLevel grade, SuggestedAction expectedAction)
    {
        var parent = Node(@"E:\Audit\folder", 2048, directory: true,
            children: [Node(@"E:\Audit\folder\alias", 0, directory: true, tags: ["skipped", "reparse-point"])]);
        var provider = new Provider { Confidence = confidence, Grade = grade };
        var result = Assert.Single(await Service(provider).AnalyzeAsync(
            Node(@"E:\", 2048, directory: true, children: [parent]), Options(), ProviderOptions));

        Assert.Equal(confidence, result.Confidence);
        Assert.Equal(grade, result.EffectiveConfidenceLevel);
        Assert.False(result.LooksLikeFallback());
        Assert.True(result.IsScopeLimited);
        Assert.Equal("cache", result.PurposeCategory);
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, result.AnalysisEvidenceScope);
        Assert.Equal(expectedAction, result.SuggestedAction);
    }

    private static AiCandidateBuilder Builder() => new(new WindowsLocationPolicy());
    private static AiAnalysisService Service(Provider provider) => new([provider], Builder(), new AiBatchPlanner());
    private static ScanSessionOptions Options() => new(@"E:\", AnalysisThresholdBytes: 1024,
        UseTokenSavingMode: true, MaxAiBatchSize: 20);

    private static FileSystemNode Node(string path, long size, bool directory = false,
        IReadOnlyList<string>? tags = null, IReadOnlyList<FileSystemNode>? children = null) => new(
        path, Path.GetFileName(path), directory ? "" : Path.GetExtension(path), size,
        directory ? FileAttributes.Directory : FileAttributes.Normal,
        null, null, directory, false, tags ?? [], children ?? []);

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public double Confidence { get; init; } = .8;
        public AiConfidenceLevel Grade { get; init; } = AiConfidenceLevel.High;
        public List<string> RequestedPaths { get; } = [];
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            RequestedPaths.AddRange(batch.Records.Select(record => record.Path));
            batch.RequestContext!.ReportUsage(new AiTokenUsage(300, 100));
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record =>
                new AiAnalysisResult(record.Path, RiskLevel.Low, SuggestedAction.RecycleCandidate,
                    "测试提供方：可能清理。", Confidence)
                    { ConfidenceLevel = Grade, PurposeCategory = "cache" }).ToArray());
        }
    }
}

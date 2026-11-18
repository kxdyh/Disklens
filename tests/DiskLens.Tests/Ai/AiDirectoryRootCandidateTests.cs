using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.Ai;

public sealed class AiDirectoryRootCandidateTests
{
    private static readonly AiProviderOptions ProviderOptions = new(AiProviderKind.OpenAiCompatible,
        "https://example.invalid", "synthetic-root-model", "synthetic-key");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_scan_root_and_each_descendant_use_their_own_size_threshold(bool saving)
    {
        var child = File(@"E:\FolderScope\nested\large.bin", 2048);
        var nested = Folder(@"E:\FolderScope\nested", 2048, child);
        var small = File(@"E:\FolderScope\small.bin", 100);
        var root = Folder(@"E:\FolderScope", 4096, nested, small);

        var candidates = Builder().BuildCandidates(root, 1024, 2400, saving);

        Assert.Equal(new[] { root.Path, nested.Path, child.Path }, candidates.Select(item => item.Path));
        Assert.DoesNotContain(candidates, item => item.Path == small.Path);
        Assert.All(candidates, item => Assert.Equal(saving ? AiAnalysisEvidenceScope.PathPurpose
            : AiAnalysisEvidenceScope.DirectoryStructure, item.AnalysisEvidenceScope));
    }

    [Fact]
    public void Small_directory_root_is_omitted_but_its_large_file_is_still_collected()
    {
        var child = File(@"E:\FolderScope\large.bin", 2048);
        var root = Folder(@"E:\FolderScope", 100, child);

        Assert.Equal(child.Path, Assert.Single(Builder().BuildCandidates(root, 1024, 2400)).Path);
        Assert.Equal(root.Path, Assert.Single(Builder().BuildCandidates(root, long.MaxValue, 2400,
            includedPaths: new HashSet<string> { root.Path })).Path);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("c:/")]
    [InlineData(@"C:\\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData("//server/share/")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\?\UNC\server\share\")]
    public void Volume_roots_are_never_paid_candidates_even_when_explicitly_selected(string volumePath)
    {
        var child = File(Path.Combine(volumePath, "large.bin"), 2048);
        var volume = Folder(volumePath, 4096, child);

        Assert.Equal(child.Path, Assert.Single(Builder().BuildCandidates(volume, 1024, 2400)).Path);
        Assert.Empty(Builder().BuildCandidates(volume, 1024, 2400,
            includedPaths: new HashSet<string> { volumePath }));
        Assert.Equal(child.Path, Assert.Single(Builder().BuildCandidates(volume, long.MaxValue, 2400,
            includedPaths: new HashSet<string> { volumePath, child.Path })).Path);
    }

    [Theory]
    [InlineData(@"E:\FolderScope\")]
    [InlineData("e:/FolderScope/")]
    [InlineData(@"\\server\share\FolderScope\")]
    [InlineData("//server/share/FolderScope/")]
    public void Trailing_separator_and_UNC_directory_roots_remain_normal_items(string path)
    {
        var root = Folder(path, 2048);
        Assert.Equal(path, Assert.Single(Builder().BuildCandidates(root, 1024, 2400)).Path);
    }

    [Fact]
    public async Task Ordinary_folder_root_flows_through_shared_query_AI_cache_and_shared_hit()
    {
        var root = Folder(@"E:\FolderScope", 4096);
        var provider = new Provider();
        var memory = new Memory();
        var shared = new Shared();
        var options = new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1024, UseTokenSavingMode: true);
        var service = Service(provider, memory, shared);

        var analyzed = Assert.Single(await service.AnalyzeAsync(root, options, ProviderOptions));
        Assert.Equal(root.Path, Assert.Single(shared.Queried).Path);
        Assert.Equal(root.Path, Assert.Single(provider.Requested));
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, analyzed.Origin);
        Assert.NotNull(analyzed.OriginalAnalysisId);
        var cached = Assert.Single(await service.AnalyzeAsync(root, options, ProviderOptions));
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, cached.Origin);
        Assert.Equal(analyzed.OriginalAnalysisId, cached.OriginalAnalysisId);
        Assert.Single(provider.Requested);
        Assert.Single(shared.Queried);

        provider = new Provider();
        memory = new Memory();
        shared = new Shared { MatchPath = root.Path };
        var reused = Assert.Single(await Service(provider, memory, shared).AnalyzeAsync(root, options, ProviderOptions));
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, reused.Origin);
        Assert.Equal(root.Path, Assert.Single(shared.Queried).Path);
        Assert.Empty(provider.Requested);
        Assert.Empty(memory.Entries);
    }

    [Fact]
    public async Task Protected_directory_root_and_its_independent_child_both_get_real_purpose_analysis()
    {
        var child = File(@"E:\FolderScope\large.bin", 2048);
        var root = Folder(@"E:\FolderScope", 4096, child) with { IsProtectedLocation = true };
        var provider = new Provider();
        var report = await Service(provider, new Memory(), new Shared()).AnalyzeWithReportAsync(root,
            new(root.Path, AnalysisThresholdBytes: 1024, UseTokenSavingMode: true), ProviderOptions);

        Assert.Equal(2, report.TotalCandidates);
        Assert.Equal(new[] { root.Path, child.Path }.Order(), provider.Requested.Order());
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results,
            item => item.Path == root.Path).Origin);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results,
            item => item.Path == child.Path).Origin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Volume_only_scan_or_selection_makes_no_cloud_query_or_model_request(bool explicitSelection)
    {
        var root = Folder(@"C:\", 4096);
        var provider = new Provider();
        var shared = new Shared();
        var options = new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1024, UseTokenSavingMode: true)
        {
            IncludedAnalysisPaths = explicitSelection ? new HashSet<string> { root.Path } : null,
        };

        var report = await Service(provider, new Memory(), shared).AnalyzeWithReportAsync(root, options, ProviderOptions);

        Assert.Empty(report.Results);
        Assert.Equal(0, report.TotalCandidates);
        Assert.Equal(0, report.TokenBudget.RequestCount);
        Assert.Empty(provider.Requested);
        Assert.Empty(shared.Queried);
    }

    [Fact]
    public async Task Unread_ordinary_directory_root_is_visible_as_incomplete_without_a_paid_request()
    {
        var root = Folder(@"E:\FolderScope", 0) with { RiskTags = ["scan-error"] };
        var provider = new Provider();
        var shared = new Shared();
        Assert.Equal(root.Path, Assert.Single(Builder().BuildCandidates(root, 1024, 2400)).Path);

        var report = await Service(provider, new Memory(), shared).AnalyzeWithReportAsync(root,
            new(root.Path, AnalysisThresholdBytes: 1024, UseTokenSavingMode: true), ProviderOptions);

        Assert.True(Assert.Single(report.Results).LooksLikeFallback());
        Assert.Equal(1, report.TotalCandidates);
        Assert.Equal(0, report.TokenBudget.RequestCount);
        Assert.Empty(provider.Requested);
        Assert.Empty(shared.Queried);
    }

    private static AiCandidateBuilder Builder() => new(new WindowsLocationPolicy());
    private static AiAnalysisService Service(Provider provider, Memory memory, Shared shared) => new(
        [provider], Builder(), new AiBatchPlanner(), memory, shared);
    private static FileSystemNode File(string path, long size) => new(path, Path.GetFileName(path),
        Path.GetExtension(path), size, FileAttributes.Normal, null, null, false, false, [], []);
    private static FileSystemNode Folder(string path, long size, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path.TrimEnd('\\', '/')), "", size, FileAttributes.Directory,
        null, null, true, false, [], children);

    private static AiAnalysisResult Result(string path) => new(path, RiskLevel.Review,
        SuggestedAction.Review, "普通目录自身的用途。", .9)
        { ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "user-data" };

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public List<string> Requested { get; } = [];
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Requested.AddRange(batch.Records.Select(item => item.Path));
            batch.RequestContext!.ReportUsage(new(100, 50));
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(item => Result(item.Path)).ToArray());
        }
    }

    private sealed class Memory : IAiAnalysisMemoryStore
    {
        public IReadOnlyDictionary<string, AiAnalysisMemoryEntry> Entries { get; private set; }
            = new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult(Entries);
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        { Entries = entries.ToDictionary(item => item.Path, StringComparer.Ordinal); return Task.CompletedTask; }
    }

    private sealed class Shared : ISharedKnowledgePipeline
    {
        public List<AiCandidateRecord> Queried { get; } = [];
        public string? MatchPath { get; init; }
        public Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates,
            CancellationToken cancellationToken = default)
        {
            Queried.AddRange(candidates);
            if (MatchPath is null) return Task.FromResult(SharedResolutionBatch.Empty);
            var result = Result(MatchPath) with { Origin = AiAnalysisResultOrigin.SharedExperience };
            return Task.FromResult(new SharedResolutionBatch(new Dictionary<string, AiAnalysisResult>
                { [MatchPath] = result }, 1, 0, false));
        }
        public Task<SharedContributionReceipt> ContributeAsync(IReadOnlyList<AiCandidateRecord> candidates,
            IReadOnlyList<AiAnalysisResult> results, SharedAnalysisProvenance provenance, bool historical = false,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture never contributes.");
    }
}

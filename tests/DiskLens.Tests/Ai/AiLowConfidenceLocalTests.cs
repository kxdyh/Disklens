using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiLowConfidenceLocalTests
{
    [Fact]
    public async Task Low_confidence_results_remain_available_locally_and_can_be_reused_from_cache()
    {
        var provider = new Provider();
        var memory = new Memory();
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory);
        var file = new FileSystemNode(@"E:\fixture\file.bin", "file.bin", ".bin", 1024, FileAttributes.Normal,
            null, null, false, false, [], [], 1);
        var root = new FileSystemNode(@"E:\", "E:", "", 1024, FileAttributes.Directory, null, null, true, false, [], [file], 1, 1);
        var options = new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1, UseTokenSavingMode: true);
        var providerOptions = new AiProviderOptions(AiProviderKind.OpenAiCompatible, "http://localhost:1234", "synthetic", null);

        var first = await service.AnalyzeWithReportAsync(root, options, providerOptions);
        var second = await service.AnalyzeWithReportAsync(root, options, providerOptions);

        Assert.Equal(1, provider.Calls);
        var fresh = Assert.Single(first.Results);
        Assert.Equal(AiConfidenceLevel.Low, fresh.EffectiveConfidenceLevel);
        Assert.False(fresh.LooksLikeFallback());
        Assert.Equal("用途尚需人工核验", fresh.Reason);
        var cached = Assert.Single(second.Results);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, cached.Origin);
        Assert.Equal(AiConfidenceLevel.Low, cached.EffectiveConfidenceLevel);
        Assert.Equal(fresh.ShortPurpose, cached.ShortPurpose);
        Assert.Equal(fresh.Reason, cached.Reason);
    }

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record =>
                new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "用途尚需人工核验", .3)
                { ConfidenceLevel = AiConfidenceLevel.Low, ShortPurpose = "待核验文件" }).ToArray());
        }
    }

    private sealed class Memory : IAiAnalysisMemoryStore
    {
        private IReadOnlyDictionary<string, AiAnalysisMemoryEntry> _entries = new Dictionary<string, AiAnalysisMemoryEntry>();
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries, CancellationToken cancellationToken = default)
        {
            _entries = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }
}

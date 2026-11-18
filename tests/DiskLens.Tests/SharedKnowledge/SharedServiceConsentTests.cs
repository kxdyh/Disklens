using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class SharedServiceConsentTests
{
    [Theory]
    [InlineData("unchanged", 1)]
    [InlineData("epoch", 0)]
    [InlineData("exclusions", 0)]
    [InlineData("re-enabled", 0)]
    [InlineData("enabled-midflight", 0)]
    public async Task Analysis_start_consent_controls_late_contribution(string change, int expectedUploads)
    {
        // Only the temp directory supplies this fixture's single reusable bucket.
        // A volume root remains a scope while ordinary directory roots are analyzed.
        var rootPath = @"D:\"; var path = Path.Combine(rootPath, "temp");
        var tree = SharedFingerprintTests.Node(rootPath,
            SharedFingerprintTests.Node(path, SharedFingerprintTests.Node(path + @"\cache.tmp", 1024)));
        var consent = new Consent();
        if (change == "enabled-midflight") consent.Snapshot = consent.Snapshot with { Enabled = false };
        var provider = new BlockedProvider(); var client = new Client();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), sharedKnowledge: pipeline);
        var options = new ScanSessionOptions(rootPath, AnalysisThresholdBytes: 1, UseTokenSavingMode: true);
        var running = service.AnalyzeWithReportAsync(tree, options,
            new(AiProviderKind.OpenAiCompatible, "https://api.deepseek.com", "deepseek-flash", "test-only"),
            provenance: new SharedAnalysisProvenance("official", "deepseek", true)
            { PromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(true) });
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        consent.Snapshot = change switch
        {
            "epoch" => consent.Snapshot with { Epoch = 2, PolicyRevision = "v2" },
            "exclusions" => consent.Snapshot with { ExcludedPaths = [path] },
            "re-enabled" => consent.Snapshot with { EnabledAt = DateTimeOffset.UtcNow.AddSeconds(1) },
            "enabled-midflight" => consent.Snapshot with { Epoch = 2, Enabled = true, EnabledAt = DateTimeOffset.UtcNow },
            _ => consent.Snapshot
        };
        provider.Resume.TrySetResult();
        var report = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(report.Results, item => item.Origin == AiAnalysisResultOrigin.AiAnalyzed);
        Assert.Equal(expectedUploads, client.Uploads);
    }

    private sealed class BlockedProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken);
            return batch.Records.Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "测试结论", .8)
                { OriginalAnalysisId = Guid.NewGuid().ToString("N"), OriginalAnalyzedAt = DateTimeOffset.UtcNow,
                    ConfidenceLevel = AiConfidenceLevel.High }).ToArray();
        }
    }
    private sealed class Consent : ISharedConsentSource
    {
        public SharedConsentSnapshot Snapshot { get; set; } = new(true, SharedConsentSnapshot.CurrentAgreementVersion, false,
            DateTimeOffset.UtcNow.AddMinutes(-5), 1, "v1", [], new("installation", "not-a-real-token", 1));
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }
    private sealed class Extractor : ISharedFingerprintExtractor
    {
        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
            => Task.FromResult<SharedFingerprint?>(candidate.IsDirectory && !SharedFingerprintExtractor.IsExcluded(candidate.Path, exclusions)
                ? new("android-sdk", "36", "sdk-temp", "cache-only-v1") : null);
    }
    private sealed class Client : ISharedExperienceClient
    {
        public int Uploads;
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
            => Task.FromResult(new SharedQueryResponse(items.Select(item => new SharedQueryItem(item.Key, false, null)).ToArray()));
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default)
        { Uploads++; return Task.FromResult(new SharedContributionReceipt(items.Count, 0, epoch)); }
        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}

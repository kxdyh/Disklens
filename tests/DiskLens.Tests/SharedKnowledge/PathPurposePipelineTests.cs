using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class PathPurposePipelineTests
{
    private static readonly SharedFingerprint Fingerprint = new("generic-v3", "0", "path3:os-users", "purpose3-directory");
    private static readonly DateTimeOffset EnabledAt = DateTimeOffset.UtcNow.AddMinutes(-5);

    [Fact]
    public async Task Parent_purpose_can_contribute_despite_unread_descendants_and_is_returned_without_a_tree_claim()
    {
        var client = new Client(); var consent = new Consent();
        var candidate = Candidate();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);
        var result = new AiAnalysisResult(candidate.Path, RiskLevel.High, SuggestedAction.DoNotDelete, "用户配置文件根目录。", .95)
        {
            ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "user-data",
            OriginalAnalysisId = "path-purpose-test-123", OriginalAnalyzedAt = DateTimeOffset.UtcNow,
            AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose, IsScopeLimited = true,
            ScopeWarning = AiScopeWarning.DescendantReadIncomplete,
        };
        Assert.Equal(1, (await pipeline.ContributeAsync([candidate], [result],
            new("official", "deepseek", true) { ConsentAtAnalysisStart = consent.Value })).Accepted);
        Assert.Equal("High", Assert.Single(client.Contributions).ConfidenceLevel);
        var resolved = Assert.Single((await pipeline.ResolveAsync([candidate])).ResultsByPath).Value;
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, resolved.AnalysisEvidenceScope);
        Assert.True(resolved.IsScopeLimited);
        Assert.False(resolved.IsAnalysisEvidenceLimited);
        Assert.Equal(AiConfidenceLevel.High, resolved.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.DoNotDelete, resolved.SuggestedAction);
        Assert.DoesNotContain("结构摘要匹配", resolved.SharedEntry!.MatchExplanation);
        Assert.DoesNotContain("读取错误", resolved.Reason);
        Assert.Null(resolved.OriginalAnalysisId);
    }

    [Fact]
    public async Task Structure_history_cannot_be_relabeled_as_new_path_purpose_evidence()
    {
        var client = new Client(); var consent = new Consent();
        var candidate = Candidate() with { HasIncompleteScan = false };
        var result = new AiAnalysisResult(candidate.Path, RiskLevel.High, SuggestedAction.Keep, "旧结构结果", .9)
        {
            OriginalAnalysisId = "old-structure-test-123", OriginalAnalyzedAt = DateTimeOffset.UtcNow,
            ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "user-data",
        };
        await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync([candidate], [result],
            new("official", "deepseek", true) { ConsentAtAnalysisStart = consent.Value });
        Assert.Empty(client.Contributions);
    }

    [Theory]
    [InlineData(AiAnalysisResultOrigin.AiAnalyzed, 1)]
    [InlineData(AiAnalysisResultOrigin.ProtectedByRule, 0)]
    [InlineData(AiAnalysisResultOrigin.SharedExperience, 0)]
    public async Task Protected_location_contributes_only_independent_AI_purpose_not_local_or_shared_rules(
        AiAnalysisResultOrigin origin, int expected)
    {
        var client = new Client(); var consent = new Consent();
        var candidate = Candidate() with { IsProtectedLocation = true };
        var result = new AiAnalysisResult(candidate.Path, RiskLevel.Protected, SuggestedAction.DoNotDelete,
            "用户配置文件根目录", .95)
        {
            ShortPurpose = "用户配置根目录", Origin = origin,
            ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "user-data",
            OriginalAnalysisId = "protected-purpose-original", OriginalAnalyzedAt = DateTimeOffset.UtcNow,
            AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
        };
        var receipt = await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync(
            [candidate], [result], new("official", "deepseek", true) { ConsentAtAnalysisStart = consent.Value });
        Assert.Equal(expected, receipt.Accepted);
        Assert.Equal(expected, client.Contributions.Count);
        if (expected > 0)
        {
            var contribution = Assert.Single(client.Contributions);
            Assert.Equal(result.ShortPurpose, contribution.ShortPurpose);
            Assert.Equal(result.Reason, contribution.Reason);
            Assert.Equal("DoNotDelete", contribution.Action);
        }
    }

    [Fact]
    public async Task Missing_own_metadata_cannot_use_the_path_only_exception()
    {
        var client = new Client();
        var node = SharedFingerprintTests.Node(@"C:\Users", size: 0);
        var candidate = Candidate() with { Structure = node, SizeBytes = 0, HasUnreadItem = true };
        Assert.True(candidate.HasLimitedAnalysisEvidence);
        Assert.Empty((await new SharedKnowledgePipeline(new Consent(), new Extractor(), client)
            .ResolveAsync([candidate])).ResultsByPath);
        Assert.Equal(0, client.QueryCalls);
    }

    private static AiCandidateRecord Candidate() => SharedFingerprintTests.Record(@"C:\Users",
        SharedFingerprintTests.Node(@"C:\Users", SharedFingerprintTests.Node(@"C:\Users\PrivateAccount", size: 1024)))
        with { HasIncompleteScan = true, AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
    private sealed class Consent : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; } = new(true, SharedConsentSnapshot.CurrentAgreementVersion,
            false, EnabledAt, 1, "policy", [], new("synthetic-install", "synthetic-secret", 1));
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }
    private sealed class Extractor : ISharedFingerprintExtractor
    {
        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default) => Task.FromResult<SharedFingerprint?>(Fingerprint);
    }
    private sealed class Client : ISharedExperienceClient
    {
        public int QueryCalls;
        public List<SharedContribution> Contributions { get; } = [];
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items,
            CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            var entry = new SharedExperienceEntry(Fingerprint.Key, Fingerprint.SoftwareId, "untrusted text", Fingerprint.RoleId,
                "untrusted text", "user-data", "DoNotDelete", [], "untrusted text", 2, 1, "active",
                DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddMinutes(-1)) { ConfidenceLevel = "High" };
            return Task.FromResult(new SharedQueryResponse([new(Fingerprint.Key, true, entry)]));
        }
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items,
            CancellationToken cancellationToken = default)
        { Contributions.AddRange(items); return Task.FromResult(new SharedContributionReceipt(items.Count, 0, epoch)); }
        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

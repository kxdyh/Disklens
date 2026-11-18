using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class OwnCloudObservationTests
{
    private static readonly SharedFingerprint Opaque = new("generic-v3", "0", "path3:volume/mask",
        "purpose3-observation-directory-0123456789abcdef0123456789abcdef");

    [Fact]
    public async Task Own_opaque_result_uses_separate_authenticated_query_and_never_public_consensus()
    {
        var client = new Client();
        var result = await Pipeline(client).ResolveAsync([Candidate()]);
        var restored = Assert.Single(result.ResultsByPath).Value;
        Assert.Equal(1, client.OwnCalls);
        Assert.Equal(0, client.PublicCalls);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, restored.Origin);
        Assert.True(restored.SharedEntry!.IsOwnObservation);
        Assert.Equal("系统运行组件", restored.ShortPurpose);
        Assert.False(SharedFingerprintProtocol.CanReuse(Opaque));
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("unknown")]
    [InlineData("empty-text")]
    [InlineData("expired")]
    [InlineData("wrong-key")]
    [InlineData("low")]
    public async Task Unverifiable_own_cloud_result_is_not_admitted(string variation)
    {
        var client = new Client { Variation = variation };
        var result = await Pipeline(client).ResolveAsync([Candidate()]);
        Assert.Empty(result.ResultsByPath);
        Assert.Equal(1, client.OwnCalls);
        Assert.Equal(0, client.PublicCalls);
    }

    [Fact]
    public async Task Disabled_consent_does_not_query_own_history()
    {
        var client = new Client();
        var result = await Pipeline(client, enabled: false).ResolveAsync([Candidate()]);
        Assert.Empty(result.ResultsByPath);
        Assert.Equal(0, client.OwnCalls);
    }

    private static SharedKnowledgePipeline Pipeline(Client client, bool enabled = true) =>
        new(new Consent(enabled), new Extractor(), client, requiresNetwork: false);

    private static AiCandidateRecord Candidate()
    {
        const string path = @"D:\SyntheticPrivateDirectory";
        var node = new FileSystemNode(path, "SyntheticPrivateDirectory", "", 1024,
            FileAttributes.Directory, null, null, true, false, [], []);
        return new(path, node.Name, node.SizeBytes, true, 0, false, "signature", node)
            { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
    }

    private sealed class Extractor : ISharedFingerprintExtractor
    {
        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default) => Task.FromResult<SharedFingerprint?>(Opaque);
    }
    private sealed class Consent(bool enabled) : ISharedConsentSource
    {
        private readonly SharedConsentSnapshot _value = new(enabled, SharedConsentSnapshot.CurrentAgreementVersion, false,
            DateTimeOffset.UtcNow.AddHours(-1), 1, "policy", [], new("synthetic-installation", "synthetic-token", 1));
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_value);
    }
    private sealed class Client : ISharedExperienceClient
    {
        public string Variation { get; init; } = "valid";
        public int OwnCalls { get; private set; }
        public int PublicCalls { get; private set; }
        public Task<SharedQueryResponse> QueryOwnObservationsAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
        {
            OwnCalls++;
            var now = DateTimeOffset.UtcNow;
            var entry = new SharedExperienceEntry(Opaque.Key, Opaque.SoftwareId, "本人的云端分析", Opaque.RoleId,
                "同一匿名观察", "runtime", "DoNotDelete", [], "系统运行必需组件", 2, 1, "active", now.AddMinutes(15), now)
                { IsOwnObservation = true, ConfidenceLevel = "High", ShortPurpose = "系统运行组件", Reason = "系统运行必需组件",
                    ShortPurposeEn = "System components", ReasonEn = "Required system runtime components" };
            entry = Variation switch
            {
                "missing-marker" => entry with { IsOwnObservation = false },
                "unknown" => entry with { Purpose = "unknown" },
                "empty-text" => entry with { ShortPurpose = "", Reason = "", ShortPurposeEn = "", ReasonEn = "" },
                "expired" => entry with { ExpiresAt = now.AddSeconds(-1) },
                "wrong-key" => entry with { Key = Opaque.Key + "-wrong" },
                "low" => entry with { ConfidenceLevel = "Low" },
                _ => entry,
            };
            return Task.FromResult(new SharedQueryResponse([new(Opaque.Key, true, entry)]));
        }
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
        { PublicCalls++; throw new InvalidOperationException("Opaque observations must not use public consensus."); }
        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

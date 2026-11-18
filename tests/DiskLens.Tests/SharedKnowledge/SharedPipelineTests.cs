using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed partial class SharedPipelineTests
{
    private static readonly SharedFingerprint Fingerprint = new("generic-v2", "0", "path2:user-temp", "tree2-" + new string('a', 64));
    private static readonly DateTimeOffset EnabledAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    private static readonly AiCandidateRecord Candidate = SharedFingerprintTests.Record(@"D:\Android\temp",
        SharedFingerprintTests.Node(@"D:\Android\temp", SharedFingerprintTests.Node(@"D:\Android\temp\private.tmp", size: 10)));

    [Fact]
    public async Task Disabled_or_unaccepted_agreement_never_queries_or_uploads()
    {
        var consent = new Consent { Value = Active() with { Enabled = false } }; var client = new Client();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);
        Assert.Equal(0, (await pipeline.ResolveAsync([Candidate])).MatchedCount);
        await pipeline.ContributeAsync([Candidate], [Fresh()], Provenance());
        Assert.Equal(0, client.Queries + client.Uploads);
        consent.Value = Active() with { AgreementVersion = "old" };
        await pipeline.ResolveAsync([Candidate]);
        Assert.Equal(0, client.Queries);
    }

    [Fact]
    public async Task Uses_local_template_and_never_treats_cloud_as_new_ai_evidence()
    {
        var consent = new Consent(); var client = new Client();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);
        var result = Assert.Single((await pipeline.ResolveAsync([Candidate])).ResultsByPath).Value;
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, result.Origin);
        Assert.NotNull(result.SharedEntry);
        Assert.False(result.LooksLikeFallback());
        Assert.DoesNotContain("remote-private-secret", result.Reason);
        Assert.DoesNotContain("remote-private-secret", result.SharedEntry.SoftwareLabel);
        await pipeline.ContributeAsync([Candidate], [result], Provenance());
        Assert.Equal(0, client.Uploads);
    }

    [Fact]
    public async Task Midflight_disable_discards_cloud_response()
    {
        var consent = new Consent(); var client = new Client { OnQuery = () => consent.Value = consent.Value with { Epoch = 2, Enabled = false } };
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);
        Assert.Empty((await pipeline.ResolveAsync([Candidate])).ResultsByPath);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-key")]
    [InlineData("unknown-rule")]
    [InlineData("unsafe-action")]
    [InlineData("unknown-purpose")]
    [InlineData("personal-purpose")]
    public async Task Invalid_or_expired_entries_never_reused(string kind)
    {
        var client = new Client { AlterEntry = entry => kind switch
        {
            "expired" => entry with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "wrong-key" => entry with { Key = "unrelated" },
            "unknown-rule" => entry with { RuleVersion = 999 },
            "unknown-purpose" => entry with { Purpose = "unknown" },
            "personal-purpose" => entry with { Purpose = "user-data" },
            _ => entry with { Action = "DeleteNow" }
        } };
        var result = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client).ResolveAsync([Candidate]);
        Assert.Empty(result.ResultsByPath);
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("logs")]
    [InlineData("downloads")]
    public async Task Explicit_disposable_candidate_is_preserved_in_contribution_and_cloud_reuse(string purpose)
    {
        var client = new Client { AlterEntry = entry => entry with { Purpose = purpose, Action = "RecycleCandidate" } };
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);
        await pipeline.ContributeAsync([Candidate], [Fresh() with { PurposeCategory = purpose }], Provenance());
        var contribution = Assert.Single(client.LastContribution!);
        Assert.Equal(purpose, contribution.Purpose);
        Assert.Equal("RecycleCandidate", contribution.Action);

        var result = Assert.Single((await pipeline.ResolveAsync([Candidate])).ResultsByPath).Value;
        Assert.Equal(SuggestedAction.RecycleCandidate, result.SuggestedAction);
        Assert.Equal(RiskLevel.Low, result.RiskLevel);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, result.Origin);
        Assert.Null(result.OriginalAnalysisId);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
    }

    [Fact]
    public async Task Legacy_review_is_never_promoted_by_its_cache_purpose_or_high_confidence()
    {
        var client = new Client();
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);
        await pipeline.ContributeAsync([Candidate], [Fresh() with { SuggestedAction = SuggestedAction.Review }], Provenance());
        Assert.Equal("Review", Assert.Single(client.LastContribution!).Action);
        var result = Assert.Single((await pipeline.ResolveAsync([Candidate])).ResultsByPath).Value;
        Assert.Equal("cache", result.PurposeCategory);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.Review, result.SuggestedAction);
        Assert.Equal(RiskLevel.Review, result.RiskLevel);
    }

    [Theory]
    [InlineData(@"C:\Users\PipelineFixture\Downloads", "path3:os-downloads")]
    [InlineData(@"C:\Users\PipelineFixture\Documents\cache", "path3:os-documents/cache")]
    public async Task Direct_cloud_lookup_cannot_make_personal_roots_or_documents_removable(string path, string role)
    {
        var node = SharedFingerprintTests.Node(path) with { SizeBytes = 1024 };
        var candidate = SharedFingerprintTests.Record(path, node)
            with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
        var fingerprint = new SharedFingerprint("generic-v3", "0", role, "purpose3-directory");
        var client = new Client { AlterEntry = entry => entry with { Action = "RecycleCandidate" } };
        var result = Assert.Single((await new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client)
            .ResolveAsync([candidate])).ResultsByPath).Value;
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, result.Origin);
        Assert.Equal("cache", result.PurposeCategory);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.DoNotDelete, result.SuggestedAction);
        Assert.Equal(RiskLevel.High, result.RiskLevel);
    }

    [Theory]
    [InlineData(@"D:\Applications\cache\history.sqlite", "file-sqlite")]
    [InlineData(@"D:\Applications\cache\history.db-wal", "file-other")]
    public async Task Direct_cloud_lookup_cannot_make_persistent_databases_removable_when_mislabeled_cache(string path, string fileToken)
    {
        var candidate = SharedFingerprintTests.Record(path, SharedFingerprintTests.Node(path, 1024));
        var fingerprint = new SharedFingerprint("app-" + new string('b', 64), "1", "path2:cache/" + fileToken,
            "tree2-" + new string('c', 64));
        var client = new Client { AlterEntry = entry => entry with { Action = "RecycleCandidate" } };
        // A controlled reusable database type exercises the result guard; arbitrary
        // extensions are already ineligible for shared reuse before any request.
        if (fileToken == "file-other")
        {
            Assert.Empty((await new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client)
                .ResolveAsync([candidate])).ResultsByPath);
            Assert.Equal(0, client.Queries);
            return;
        }
        var result = Assert.Single((await new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client)
            .ResolveAsync([candidate])).ResultsByPath).Value;
        Assert.Equal("cache", result.PurposeCategory);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.Equal(SuggestedAction.DoNotDelete, result.SuggestedAction);
    }

    [Fact]
    public async Task Direct_cloud_lookup_still_allows_an_explicit_independent_cache_subdirectory()
    {
        const string path = @"C:\Users\PipelineFixture\AppData\Local\cache";
        var node = SharedFingerprintTests.Node(path) with { SizeBytes = 1024 };
        var candidate = SharedFingerprintTests.Record(path, node)
            with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
        var fingerprint = new SharedFingerprint("generic-v3", "0", "path3:os-local/cache", "purpose3-directory");
        var client = new Client { AlterEntry = entry => entry with { Action = "RecycleCandidate" } };
        var result = Assert.Single((await new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client)
            .ResolveAsync([candidate])).ResultsByPath).Value;
        Assert.Equal(SuggestedAction.RecycleCandidate, result.SuggestedAction);
        Assert.Equal(RiskLevel.Low, result.RiskLevel);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.False(string.IsNullOrWhiteSpace(result.ShortPurpose));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("application")]
    [InlineData("configuration")]
    [InlineData("user-data")]
    [InlineData("unknown")]
    public async Task Positive_runtime_personal_or_unknown_purposes_neither_contribute_nor_reuse(string purpose)
    {
        var client = new Client { AlterEntry = entry => entry with { Purpose = purpose, Action = "RecycleCandidate" } };
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);
        Assert.Equal(0, (await pipeline.ContributeAsync([Candidate], [Fresh() with { PurposeCategory = purpose }], Provenance())).Accepted);
        Assert.Equal(0, client.Uploads);
        Assert.Empty((await pipeline.ResolveAsync([Candidate])).ResultsByPath);
    }

    [Theory]
    [InlineData(RiskLevel.High)]
    [InlineData(RiskLevel.Protected)]
    public async Task Conflicting_high_risk_candidate_cannot_enter_cloud_contributions(RiskLevel risk)
    {
        var client = new Client();
        await new SharedKnowledgePipeline(new Consent(), new Extractor(), client)
            .ContributeAsync([Candidate], [Fresh() with { RiskLevel = risk }], Provenance());
        Assert.Equal(0, client.Uploads);
    }

    [Theory]
    [InlineData("candidate-flag")]
    [InlineData("structure-flag")]
    [InlineData("protected-path")]
    public async Task Protected_purposes_can_reuse_and_contribute_without_bypassing_deletion_protection(string protection)
    {
        var candidate = protection switch
        {
            "candidate-flag" => Candidate with { IsProtectedLocation = true },
            "structure-flag" => Candidate with { Structure = Candidate.Structure with { IsProtectedLocation = true } },
            _ => Candidate with { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DiskLensSynthetic", "..", "DiskLensSynthetic", "cache") },
        };
        var client = new Client { AlterEntry = entry => entry with { Action = "RecycleCandidate" } };
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);
        var resolved = Assert.Single((await pipeline.ResolveAsync([candidate])).ResultsByPath).Value;
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, resolved.Origin);
        Assert.Equal(SuggestedAction.DoNotDelete, resolved.SuggestedAction);
        Assert.Equal(RiskLevel.Protected, resolved.RiskLevel);
        await pipeline.ContributeAsync([candidate], [Fresh() with { Path = candidate.Path }], Provenance());
        Assert.Equal(1, client.Queries);
        Assert.Equal(1, client.Uploads);
        Assert.Equal("DoNotDelete", Assert.Single(client.LastContribution!).Action);
    }

    [Fact]
    public async Task Path_purpose_candidate_preserves_explicit_action_despite_unread_descendants()
    {
        var candidate = Candidate with { HasIncompleteScan = true, AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
        var fingerprint = new SharedFingerprint("generic-v3", "0", "path3:os-temp", "purpose3-directory");
        var client = new Client { AlterEntry = entry => entry with { Action = "RecycleCandidate" } };
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client);
        var result = Assert.Single((await pipeline.ResolveAsync([candidate])).ResultsByPath).Value;
        Assert.Equal(SuggestedAction.RecycleCandidate, result.SuggestedAction);
        Assert.True(result.IsScopeLimited);
        Assert.False(result.IsAnalysisEvidenceLimited);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        await pipeline.ContributeAsync([candidate], [Fresh() with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose }], Provenance());
        Assert.Equal(1, client.Uploads);
    }

    [Fact]
    public async Task Outbox_receives_explicit_candidate_without_downgrading_it()
    {
        var client = new Client(); var outbox = new RecordingOutbox();
        var receipt = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client, outbox: outbox)
            .ContributeAsync([Candidate], [Fresh()], Provenance());
        Assert.Equal("RecycleCandidate", Assert.Single(outbox.Items).Contribution.Action);
        Assert.Equal(1, receipt.Accepted);
        Assert.Equal(0, client.Uploads);
    }

    [Fact]
    public async Task Offline_service_falls_back_without_throwing()
    {
        var result = await new SharedKnowledgePipeline(new Consent(), new Extractor(), new Client { Offline = true }).ResolveAsync([Candidate]);
        Assert.True(result.Unavailable);
        Assert.Empty(result.ResultsByPath);
    }

    [Fact]
    public async Task Reject_history_then_cache_reuse_never_becomes_fresh_contribution()
    {
        var client = new Client(); var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);
        await pipeline.ContributeAsync([Candidate], [Fresh() with { OriginalAnalyzedAt = DateTimeOffset.UtcNow.AddDays(-1) }], Provenance());
        await pipeline.ContributeAsync([Candidate], [Fresh() with { Origin = AiAnalysisResultOrigin.CacheReused }], Provenance());
        await pipeline.ContributeAsync([Candidate], [Fresh()], Provenance(), historical: true);
        Assert.Equal(0, client.Uploads);
        await pipeline.ContributeAsync([Candidate], [Fresh()], Provenance());
        Assert.Equal(1, client.Uploads);
        Assert.Equal("RecycleCandidate", Assert.Single(client.LastContribution!).Action);
        Assert.Equal("fresh-ai", Assert.Single(client.LastContribution!).Source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Custom_prompt_or_shared_context_cannot_contribute_independent_evidence(bool defaultPrompt, bool shared)
    {
        var client = new Client();
        await new SharedKnowledgePipeline(new Consent(), new Extractor(), client).ContributeAsync([Candidate], [Fresh()], new("official", "deepseek", defaultPrompt, shared));
        Assert.Equal(0, client.Uploads);
    }

    [Fact]
    public async Task Explicit_historical_consent_retains_original_analysis_id()
    {
        var consent = new Consent { Value = Active() with { ShareHistory = true } }; var client = new Client();
        var result = Fresh() with { OriginalAnalyzedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync([Candidate], [result], Provenance() with { ConsentAtAnalysisStart = consent.Value }, historical: true);
        Assert.Equal("historical-ai", Assert.Single(client.LastContribution!).Source);
        Assert.Equal(result.OriginalAnalysisId, Assert.Single(client.LastContribution!).AnalysisId);
    }

    [Fact]
    public async Task Keeps_opposing_keep_advice_as_evidence_instead_of_artificially_inflating_consensus()
    {
        var client = new Client();
        await new SharedKnowledgePipeline(new Consent(), new Extractor(), client).ContributeAsync([Candidate], [Fresh() with { SuggestedAction = SuggestedAction.Keep }], Provenance());
        Assert.Equal("Keep", Assert.Single(client.LastContribution!).Action);
    }

    [Fact]
    public async Task Changed_authorization_or_missing_start_binding_cannot_relabel_inflight_analysis()
    {
        var consent = new Consent(); var client = new Client();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);
        var binding = await pipeline.CaptureConsentAsync();
        consent.Value = consent.Value with { Epoch = 2, EnabledAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await pipeline.ContributeAsync([Candidate], [Fresh()], Provenance() with { ConsentAtAnalysisStart = binding });
        await pipeline.ContributeAsync([Candidate], [Fresh()], Provenance() with { ConsentAtAnalysisStart = null });
        Assert.Equal(0, client.Uploads);
    }

    [Fact]
    public async Task Capture_clones_exclusions_so_mutable_old_array_cannot_rebind_authorization()
    {
        var exclusions = new[] { @"D:\First" };
        var consent = new Consent { Value = Active() with { ExcludedPaths = exclusions } };
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), new Client());
        var captured = await pipeline.CaptureConsentAsync();
        exclusions[0] = @"D:\Second";
        Assert.Equal(@"D:\First", Assert.Single(captured!.ExcludedPaths));
    }

    [Theory]
    [InlineData("High", true)]
    [InlineData("Medium", true)]
    [InlineData("Low", false)]
    [InlineData("Unknown", false)]
    [InlineData("高", false)]
    [InlineData("High!", false)]
    public async Task Only_explicit_medium_or_high_cloud_entries_can_be_reused(string grade, bool reusable)
    {
        var client = new Client { AlterEntry = entry => entry with { ConfidenceLevel = grade } };
        var batch = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client).ResolveAsync([Candidate]);
        Assert.Equal(reusable ? 1 : 0, batch.MatchedCount);
        if (reusable)
        {
            var result = Assert.Single(batch.ResultsByPath).Value;
            Assert.Equal(grade, result.SharedEntry!.ConfidenceLevel);
            Assert.Equal(grade, result.EffectiveConfidenceLevel.ToString());
            Assert.Null(result.OriginalAnalysisId);
            Assert.Null(result.OriginalAnalyzedAt);
        }
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("partial")]
    public async Task Anonymous_masked_or_partial_contributions_are_not_queried_for_reuse(string kind)
    {
        var fingerprint = kind == "mask" ? Fingerprint with { RoleId = "path2:volume/mask/temp" }
            : Fingerprint with { Shape = "partial2-" + new string('b', 64) };
        var client = new Client();
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client);
        Assert.Empty((await pipeline.ResolveAsync([Candidate])).ResultsByPath);
        Assert.Equal(0, client.Queries);
        await pipeline.ContributeAsync([Candidate], [Fresh()], Provenance());
        Assert.Equal(fingerprint.RoleId, Assert.Single(client.LastContribution!).RoleId);
        Assert.Equal(1, client.Uploads);
    }

    [Theory]
    [InlineData(AiConfidenceLevel.High, .9, "High")]
    [InlineData(AiConfidenceLevel.Medium, .7, "Medium")]
    [InlineData(AiConfidenceLevel.High, .2, "Low")]
    [InlineData(AiConfidenceLevel.Low, .9, "Low")]
    [InlineData(AiConfidenceLevel.Unknown, .99, "Unknown")]
    public async Task Contribution_preserves_effective_grade_without_upgrading_old_numeric_scores(
        AiConfidenceLevel grade, double numeric, string expected)
    {
        var client = new Client();
        var result = Fresh() with { ConfidenceLevel = grade, Confidence = numeric };
        await new SharedKnowledgePipeline(new Consent(), new Extractor(), client).ContributeAsync([Candidate], [result], Provenance());
        if (expected is "Medium" or "High")
            Assert.Equal(expected, Assert.Single(client.LastContribution!).ConfidenceLevel);
        else
        {
            Assert.Null(client.LastContribution);
            Assert.Equal(0, client.Uploads);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Limited_scope_result_never_contributes_even_with_original_identity_and_a_claimed_high_grade(bool historical)
    {
        var consent = new Consent { Value = Active() with { ShareHistory = true } };
        var client = new Client();
        var result = Fresh() with { IsScopeLimited = true };
        var receipt = await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync(
            [Candidate], [result], Provenance() with { ConsentAtAnalysisStart = consent.Value }, historical);
        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(0, client.Uploads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Summary_hiding_an_unread_descendant_cannot_reuse_or_contribute_an_old_complete_result(bool historical)
    {
        var path = Candidate.Path;
        var unread = SharedFingerprintTests.Node(path + @"\locked") with { RiskTags = ["scan-error"] };
        var children = Enumerable.Range(0, 80).Select(index =>
            SharedFingerprintTests.Node(path + "\\" + new string('x', 160) + index + ".tmp", 10)).ToArray();
        var directory = SharedFingerprintTests.Node(path, [.. children, unread]);
        var root = SharedFingerprintTests.Node(@"D:\Android", directory);
        var candidate = Assert.Single(new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(
            root, long.MaxValue, 256, includedPaths: new HashSet<string> { path }));
        Assert.True(candidate.IsStructureSummary);
        Assert.True(candidate.HasIncompleteScan);
        Assert.False(candidate.HasUnreadItem);
        Assert.DoesNotContain(candidate.Structure.DescendantsAndSelf(), node => node.RiskTags.Contains("scan-error"));
        var consent = new Consent { Value = Active() with { ShareHistory = true } };
        var client = new Client();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client);

        var resolution = await pipeline.ResolveAsync([candidate]);
        // A historical result can be complete even when the current snapshot is not.
        var receipt = await pipeline.ContributeAsync([candidate], [Fresh()],
            Provenance() with { ConsentAtAnalysisStart = consent.Value }, historical);

        Assert.Empty(resolution.ResultsByPath);
        Assert.Equal(1, resolution.SkippedCount);
        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(1, receipt.Skipped);
        Assert.Equal(0, client.Queries + client.Uploads);
    }

    [Fact]
    public async Task Newly_excluded_path_during_query_discards_inflight_response()
    {
        var consent = new Consent();
        var client = new Client { OnQuery = () => consent.Value = consent.Value with
            { Epoch = 2, ExcludedPaths = [Candidate.Path] } };
        var batch = await new SharedKnowledgePipeline(consent, new Extractor(), client).ResolveAsync([Candidate]);
        Assert.Empty(batch.ResultsByPath);
    }

    [Fact]
    public async Task Ten_files_with_one_fingerprint_contribute_one_vote_with_the_lowest_explicit_grade()
    {
        var candidates = Enumerable.Range(0, 10).Select(index =>
        {
            var path = $@"C:\Users\PipelineFixture\Downloads\archive-{index:00}.zip";
            return SharedFingerprintTests.Record(path, SharedFingerprintTests.Node(path, 10));
        }).ToArray();
        var results = candidates.Select((candidate, index) => Fresh() with
        {
            Path = candidate.Path, OriginalAnalysisId = "independent-analysis-" + index,
            Confidence = index == 9 ? .7 : .9,
            ConfidenceLevel = index == 9 ? AiConfidenceLevel.Medium : AiConfidenceLevel.High,
        }).ToArray();
        var client = new Client();
        var receipt = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client)
            .ContributeAsync(candidates, results, Provenance());
        Assert.Equal(1, client.Uploads);
        Assert.Equal(1, receipt.Accepted);
        Assert.Equal("Medium", Assert.Single(client.LastContribution!).ConfidenceLevel);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("purpose")]
    public async Task Conflicting_local_files_in_one_bucket_are_skipped_without_sending_an_unknown_observation(string conflict)
    {
        var secondPath = @"C:\Users\PipelineFixture\Downloads\another.zip";
        var second = SharedFingerprintTests.Record(secondPath, SharedFingerprintTests.Node(secondPath, 10));
        var opposing = Fresh() with
        {
            Path = secondPath, OriginalAnalysisId = "independent-analysis-other",
            SuggestedAction = conflict == "action" ? SuggestedAction.Keep : SuggestedAction.RecycleCandidate,
            PurposeCategory = conflict == "purpose" ? "downloads" : "cache",
        };
        var client = new Client();
        var receipt = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client)
            .ContributeAsync([Candidate, second], [Fresh(), opposing], Provenance());
        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(2, receipt.Skipped);
        Assert.Equal(0, client.Uploads);
        Assert.Null(client.LastContribution);
    }

    [Fact]
    public async Task A_new_local_conflict_skips_the_bucket_without_sending_a_replacement()
    {
        var client = new Client();
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);
        var previous = Fresh() with { OriginalAnalysisId = "previous-analysis-review" };
        await pipeline.ContributeAsync([Candidate], [previous], Provenance());
        var secondPath = @"C:\Users\PipelineFixture\Downloads\another.zip";
        var second = SharedFingerprintTests.Record(secondPath, SharedFingerprintTests.Node(secondPath, 10));
        var freshReview = Fresh() with { OriginalAnalysisId = "new-analysis-review" };
        var freshKeep = Fresh() with { Path = secondPath, SuggestedAction = SuggestedAction.Keep,
            OriginalAnalysisId = "new-analysis-keep" };
        await pipeline.ContributeAsync([Candidate, second], [freshReview, freshKeep], Provenance());
        Assert.Single(client.Contributions);
        var first = Assert.Single(client.Contributions[0]);
        Assert.Equal("RecycleCandidate", first.Action);
        Assert.Equal("High", first.ConfidenceLevel);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("purpose")]
    public async Task Reversing_conflicting_source_order_never_sends_either_payload(string conflict)
    {
        var (second, original, opposing) = ConflictingPair();
        if (conflict == "purpose")
            opposing = opposing with { SuggestedAction = SuggestedAction.Review, PurposeCategory = "downloads" };
        var client = new Client();
        var pipeline = new SharedKnowledgePipeline(new Consent(), new Extractor(), client);

        await pipeline.ContributeAsync([Candidate, second], [original, opposing], Provenance());
        await pipeline.ContributeAsync([second, Candidate], [opposing, original], Provenance());

        Assert.Empty(client.Contributions);
        Assert.Equal(0, client.Uploads);
    }

    [Theory]
    [InlineData("analysis-id")]
    [InlineData("purpose")]
    [InlineData("action")]
    [InlineData("confidence")]
    [InlineData("bucket")]
    public async Task Different_source_identity_or_conclusion_or_bucket_still_skips_the_conflict(string change)
    {
        var (second, original, opposing) = ConflictingPair();
        var client = new Client();
        await new SharedKnowledgePipeline(new Consent(), new Extractor(), client)
            .ContributeAsync([Candidate, second], [original, opposing], Provenance());
        var changed = change switch
        {
            "analysis-id" => opposing with { OriginalAnalysisId = "new-independent-analysis-other" },
            "purpose" => opposing with { PurposeCategory = "downloads" },
            "action" => opposing with { SuggestedAction = SuggestedAction.DoNotDelete },
            "confidence" => opposing with { ConfidenceLevel = AiConfidenceLevel.Medium, Confidence = .7 },
            _ => opposing,
        };
        var fingerprint = change == "bucket" ? Fingerprint with { Shape = "tree2-" + new string('b', 64) } : Fingerprint;

        await new SharedKnowledgePipeline(new Consent(), new Extractor(fingerprint), client)
            .ContributeAsync([Candidate, second], [original, changed], Provenance());

        Assert.Empty(client.Contributions);
        Assert.Equal(0, client.Uploads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_conflict_is_never_added_to_the_outbox_in_either_source_order(bool reverse)
    {
        var (second, original, opposing) = ConflictingPair();
        var earliest = DateTimeOffset.UtcNow.AddDays(-2);
        original = original with { OriginalAnalyzedAt = earliest };
        opposing = opposing with { OriginalAnalyzedAt = earliest.AddDays(1) };
        var consent = new Consent { Value = Active() with { ShareHistory = true } };
        var client = new Client();
        var outbox = new RecordingOutbox();
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client, outbox: outbox);

        var receipt = await pipeline.ContributeAsync(reverse ? [second, Candidate] : [Candidate, second],
            reverse ? [opposing, original] : [original, opposing],
            Provenance() with { ConsentAtAnalysisStart = consent.Value }, historical: true);

        Assert.Empty(outbox.Items);
        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(2, receipt.Skipped);
        Assert.Equal(0, client.Uploads);
        Assert.Equal(1, outbox.Flushes);
    }

    private static (AiCandidateRecord Second, AiAnalysisResult Original, AiAnalysisResult Opposing) ConflictingPair()
    {
        const string secondPath = @"C:\Users\PipelineFixture\Downloads\another.zip";
        var second = SharedFingerprintTests.Record(secondPath, SharedFingerprintTests.Node(secondPath, 10));
        var original = Fresh();
        return (second, original, original with
        {
            Path = secondPath, SuggestedAction = SuggestedAction.Keep,
            OriginalAnalysisId = "independent-analysis-other",
        });
    }

    private static SharedConsentSnapshot Active() => new(true, SharedConsentSnapshot.CurrentAgreementVersion, false,
        EnabledAt, 1, "test-policy", [], new("install", "secret", 1));
    private static AiAnalysisResult Fresh() => new(Candidate.Path, RiskLevel.Review, SuggestedAction.RecycleCandidate, "应用生成的可重建临时缓存", .8)
        { OriginalAnalysisId = "original-analysis-1234", OriginalAnalyzedAt = DateTimeOffset.UtcNow,
            ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "cache", ShortPurpose = "应用临时缓存" };
    private static SharedAnalysisProvenance Provenance() => new("official", "deepseek", true) { ConsentAtAnalysisStart = Active() };
    private sealed class Consent : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; set; } = Active();
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }
    private sealed class Extractor(SharedFingerprint? fingerprint = null) : ISharedFingerprintExtractor
    {
        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
            => Task.FromResult<SharedFingerprint?>(SharedFingerprintExtractor.IsExcluded(candidate.Path, exclusions) ? null : fingerprint ?? Fingerprint);
    }
    private sealed class RecordingOutbox : ISharedContributionOutbox
    {
        private long _epoch;
        public IReadOnlyList<SharedQueuedContribution> Items { get; private set; } = [];
        public int Flushes { get; private set; }
        public Task EnqueueAsync(SharedConsentSnapshot consent, IReadOnlyList<SharedQueuedContribution> items,
            CancellationToken cancellationToken = default)
        { _epoch = consent.Epoch; Items = items.ToArray(); return Task.CompletedTask; }
        public Task<SharedContributionReceipt> FlushAsync(CancellationToken cancellationToken = default)
        { Flushes++; return Task.FromResult(new SharedContributionReceipt(Items.Count, 0, _epoch)); }
        public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.Count);
        public Task SynchronizeConsentAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Client : ISharedExperienceClient
    {
        public int Queries, Uploads; public Action? OnQuery; public bool Offline; public Func<SharedExperienceEntry, SharedExperienceEntry>? AlterEntry;
        public IReadOnlyList<SharedContribution>? LastContribution;
        public List<IReadOnlyList<SharedContribution>> Contributions { get; } = [];
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
        {
            Queries++; OnQuery?.Invoke(); if (Offline) throw new HttpRequestException("offline");
            var fingerprint = items[0];
            var entry = new SharedExperienceEntry(fingerprint.Key, fingerprint.SoftwareId, "remote-private-secret", fingerprint.RoleId, "remote-private-secret", "cache", "Review", ["remote-private-secret"], "remote-private-secret", 2, 1, "active", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddMinutes(-1))
                { ConfidenceLevel = "High", ShortPurpose = "应用临时缓存", Reason = "应用生成的可重建临时缓存" };
            return Task.FromResult(new SharedQueryResponse([new(fingerprint.Key, true, AlterEntry?.Invoke(entry) ?? entry)]));
        }
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default)
        { Uploads++; LastContribution = items; Contributions.Add(items.ToArray()); return Task.FromResult(new SharedContributionReceipt(items.Count, 0, epoch)); }
        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}

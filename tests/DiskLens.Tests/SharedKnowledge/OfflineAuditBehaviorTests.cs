using System.Net;
using System.Text;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

// These tests characterize current behavior. Every HTTP request ends in the in-memory
// handler; no listener, real shared service, or paid AI provider is used.
public sealed class OfflineAuditBehaviorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fully_offline_or_transport_timed_out_query_reports_unavailable_without_a_match(bool timeout)
    {
        var fixture = new Fixture(26);
        using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(timeout
            ? new TaskCanceledException("Synthetic transport timeout.")
            : new HttpRequestException("Synthetic offline connection.")));
        using var client = Client(handler);

        var resolution = await fixture.Pipeline(client).ResolveAsync(fixture.Candidates);

        Assert.True(resolution.Unavailable);
        Assert.Empty(resolution.ResultsByPath);
        Assert.Empty(resolution.MissingPaths);
        Assert.Equal(0, resolution.MatchedCount);
        Assert.Equal(0, resolution.SkippedCount); // A transport failure is not an extraction rejection.
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Later_transport_timeout_preserves_matches_from_the_first_query_chunk()
    {
        var fixture = new Fixture(26);
        var served = new List<string>();
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("/v1/query", request.RequestUri!.AbsolutePath);
            if (served.Count > 0) throw new TaskCanceledException("Synthetic second-chunk timeout.");
            var items = await ReadItemsAsync<SharedFingerprint>(request, token);
            served.AddRange(items.Select(item => item.Key));
            return Reply(fixture.QueryResponse(items));
        });
        using var client = Client(handler);

        var resolution = await fixture.Pipeline(client).ResolveAsync(fixture.Candidates);

        Assert.True(resolution.Unavailable);
        Assert.Empty(resolution.MissingPaths);
        Assert.Equal(25, served.Count);
        Assert.Equal(25, resolution.MatchedCount);
        Assert.True(fixture.Candidates.Take(25).Select(item => item.Path).ToHashSet(StringComparer.Ordinal)
            .SetEquals(resolution.ResultsByPath.Keys));
        Assert.DoesNotContain(fixture.Candidates[25].Path, resolution.ResultsByPath.Keys);
        Assert.All(resolution.ResultsByPath.Values, result =>
        {
            Assert.Equal(AiAnalysisResultOrigin.SharedExperience, result.Origin);
            Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        });
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Explicit_cloud_miss_is_distinct_from_a_hit_an_omitted_reply_and_an_unqueried_path()
    {
        var fixture = new Fixture(4);
        fixture.Consent.Value = fixture.Consent.Value with { ExcludedPaths = [fixture.Candidates[3].Path] };
        using var handler = new Handler(async (request, token) =>
        {
            var items = await ReadItemsAsync<SharedFingerprint>(request, token);
            Assert.Equal(3, items.Length);
            return Reply(new SharedQueryResponse([
                fixture.QueryResponse(items.Take(1)).Items[0],
                new SharedQueryItem(items[1].Key, false, null),
            ]));
        });
        using var client = Client(handler);

        var resolution = await fixture.Pipeline(client).ResolveAsync(fixture.Candidates);

        Assert.False(resolution.Unavailable);
        Assert.Equal(1, resolution.MatchedCount);
        Assert.Equal(fixture.Candidates[0].Path, Assert.Single(resolution.ResultsByPath).Key);
        Assert.Equal(fixture.Candidates[1].Path, Assert.Single(resolution.MissingPaths));
        Assert.Equal(1, resolution.SkippedCount);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task One_explicit_fingerprint_miss_maps_back_to_all_corresponding_local_paths()
    {
        var fixture = new Fixture(2);
        var fingerprint = new SharedFingerprint("generic-v2", "0", "path2:user-downloads/cache",
            "tree2-" + new string('a', 64));
        var extractor = new DictionaryExtractor(fixture.Candidates.ToDictionary(candidate => candidate.Path,
            _ => fingerprint, StringComparer.Ordinal));
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Single(await ReadItemsAsync<SharedFingerprint>(request, token));
            return Reply(new SharedQueryResponse([new SharedQueryItem(fingerprint.Key, false, null)]));
        });
        using var client = Client(handler);
        var pipeline = new SharedKnowledgePipeline(fixture.Consent, extractor, client, new FrozenTimeProvider());

        var resolution = await pipeline.ResolveAsync(fixture.Candidates);

        Assert.False(resolution.Unavailable);
        Assert.Empty(resolution.ResultsByPath);
        Assert.Equal(2, resolution.MissingPaths.Count);
        Assert.True(fixture.Candidates.Select(candidate => candidate.Path).ToHashSet(StringComparer.Ordinal)
            .SetEquals(resolution.MissingPaths));
    }

    [Theory]
    [InlineData("omitted", false)]
    [InlineData("invalid-hit", false)]
    [InlineData("duplicate", true)]
    [InlineData("unknown-key", true)]
    [InlineData("contradictory", true)]
    [InlineData("missing-matched", true)]
    [InlineData("malformed-json", true)]
    [InlineData("wrong-content-type", true)]
    public async Task Missing_or_invalid_query_evidence_is_never_reported_as_an_explicit_cloud_miss(
        string kind, bool unavailable)
    {
        var fixture = new Fixture(1);
        using var handler = new Handler(async (request, token) =>
        {
            var item = Assert.Single(await ReadItemsAsync<SharedFingerprint>(request, token));
            var entry = fixture.QueryResponse([item]).Items[0].Entry!;
            if (kind == "malformed-json")
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{", Encoding.UTF8, "application/json") };
            if (kind == "wrong-content-type")
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("not json", Encoding.UTF8, "text/plain") };
            if (kind == "missing-matched")
                return Reply(new { items = new[] { new { key = item.Key } } });
            SharedQueryItem miss = new(item.Key, false, null);
            return Reply(new SharedQueryResponse(kind switch
            {
                "omitted" => [],
                "invalid-hit" => [new(item.Key, true, entry with { ConfidenceLevel = "Unknown" })],
                "duplicate" => [miss, miss],
                "unknown-key" => [miss with { Key = "not-requested" }],
                _ => [miss with { Entry = entry }],
            }));
        });
        using var client = Client(handler);

        var resolution = await fixture.Pipeline(client).ResolveAsync(fixture.Candidates);

        Assert.Equal(unavailable, resolution.Unavailable);
        Assert.Empty(resolution.ResultsByPath);
        Assert.Empty(resolution.MissingPaths);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Caller_deadline_after_the_first_query_chunk_throws_instead_of_returning_partial_matches()
    {
        var fixture = new Fixture(26);
        using var deadline = new CancellationTokenSource();
        var served = 0;
        using var handler = new Handler(async (request, token) =>
        {
            if (served > 0)
            {
                deadline.Cancel();
                return await Task.FromCanceled<HttpResponseMessage>(deadline.Token);
            }
            var items = await ReadItemsAsync<SharedFingerprint>(request, token);
            served += items.Length;
            return Reply(fixture.QueryResponse(items));
        });
        using var client = Client(handler);

        // The analysis service supplies a linked deadline token. Once that caller token
        // is canceled, the pipeline throws; its earlier hits are not returned in a batch.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Pipeline(client)
            .ResolveAsync(fixture.Candidates, deadline.Token));

        Assert.Equal(25, served);
        Assert.True(deadline.IsCancellationRequested);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Contribution_connection_loss_after_the_first_chunk_returns_accepted_and_skipped_counts()
    {
        var fixture = new Fixture(12);
        var attempted = new List<SharedContribution[]>();
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("/v1/contributions", request.RequestUri!.AbsolutePath);
            var items = await ReadItemsAsync<SharedContribution>(request, token);
            attempted.Add(items);
            if (attempted.Count == 2) throw new HttpRequestException("Synthetic connection loss.");
            return Reply(new SharedContributionReceipt(items.Length, 0, fixture.Consent.Value.Epoch));
        });
        using var client = Client(handler);

        var receipt = await fixture.Pipeline(client).ContributeAsync(fixture.Candidates, fixture.Results,
            fixture.Provenance);

        Assert.Equal(10, receipt.Accepted);
        Assert.Equal(2, receipt.Skipped);
        Assert.Equal(2, receipt.Failed);
        Assert.Equal(fixture.Consent.Value.Epoch, receipt.Epoch);
        Assert.Equal(new[] { 10, 2 }, attempted.Select(items => items.Length));
        Assert.Equal(12, attempted.SelectMany(items => items).Select(item => item.AnalysisId).Distinct().Count());
        Assert.Equal(2, handler.RequestCount); // Failed chunks are not retried within this call.
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("malformed-json")]
    [InlineData("wrong-content-type")]
    [InlineData("wrong-epoch")]
    [InlineData("negative-count")]
    [InlineData("overflow-count")]
    public async Task Contribution_transport_or_invalid_receipt_marks_failed_items_separately_from_server_skips(string kind)
    {
        var fixture = new Fixture(1);
        using var handler = new Handler((_, _) => kind switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            "io" => Task.FromException<HttpResponseMessage>(new IOException("Synthetic read failure.")),
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic transport timeout.")),
            "malformed-json" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{", Encoding.UTF8, "application/json") }),
            "wrong-content-type" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("not json", Encoding.UTF8, "text/plain") }),
            "wrong-epoch" => Task.FromResult(Reply(new SharedContributionReceipt(1, 0, fixture.Consent.Value.Epoch + 1))),
            "negative-count" => Task.FromResult(Reply(new SharedContributionReceipt(-1, 0, fixture.Consent.Value.Epoch))),
            _ => Task.FromResult(Reply(new SharedContributionReceipt(int.MaxValue, int.MaxValue, fixture.Consent.Value.Epoch))),
        });
        using var client = Client(handler);

        var receipt = await fixture.Pipeline(client).ContributeAsync(fixture.Candidates, fixture.Results,
            fixture.Provenance);

        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(1, receipt.Skipped);
        Assert.Equal(1, receipt.Failed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Failed_count_survives_the_early_return_when_consent_changes_before_a_later_chunk()
    {
        var fixture = new Fixture(22);
        var attempts = 0;
        using var handler = new Handler(async (request, token) =>
        {
            var items = await ReadItemsAsync<SharedContribution>(request, token);
            if (++attempts == 1)
                return Reply(new SharedContributionReceipt(items.Length, 0, fixture.Consent.Value.Epoch));
            fixture.Consent.Value = fixture.Consent.Value with { Enabled = false, Epoch = 8 };
            throw new HttpRequestException("Synthetic second-chunk failure with consent withdrawal.");
        });
        using var client = Client(handler);

        var receipt = await fixture.Pipeline(client).ContributeAsync(fixture.Candidates, fixture.Results,
            fixture.Provenance);

        Assert.Equal(10, receipt.Accepted);
        Assert.Equal(12, receipt.Skipped);
        Assert.Equal(10, receipt.Failed);
        Assert.Equal(8, receipt.Epoch);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Server_duplicate_skip_is_not_a_failure_and_cannot_supply_a_local_failed_count()
    {
        var fixture = new Fixture(2);
        using var handler = new Handler((_, _) => Task.FromResult(Reply(new
            { accepted = 0, skipped = 2, epoch = fixture.Consent.Value.Epoch, failed = 2 })));
        using var client = Client(handler);

        var receipt = await fixture.Pipeline(client).ContributeAsync(fixture.Candidates, fixture.Results,
            fixture.Provenance);

        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(2, receipt.Skipped);
        Assert.Equal(0, receipt.Failed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Explicit_retry_after_an_ambiguous_response_loss_preserves_original_analysis_ids_and_payload()
    {
        var fixture = new Fixture(3);
        var payloads = new List<string>();
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("/v1/contributions", request.RequestUri!.AbsolutePath);
            payloads.Add(await request.Content!.ReadAsStringAsync(token));
            if (payloads.Count == 1) throw new HttpRequestException("Synthetic response lost after dispatch.");
            return Reply(new SharedContributionReceipt(3, 0, fixture.Consent.Value.Epoch));
        });
        using var client = Client(handler);
        var pipeline = fixture.Pipeline(client);

        var uncertain = await pipeline.ContributeAsync(fixture.Candidates, fixture.Results, fixture.Provenance);
        Assert.Equal(0, uncertain.Accepted);
        Assert.Equal(3, uncertain.Skipped);
        Assert.Equal(3, uncertain.Failed);
        Assert.Single(payloads);

        // Retrying here is an explicit caller action. This verifies stable client-side
        // identity, not server deduplication or the existence of an automatic retry queue.
        var retried = await pipeline.ContributeAsync(fixture.Candidates, fixture.Results, fixture.Provenance);
        Assert.Equal(3, retried.Accepted);
        Assert.Equal(0, retried.Skipped);
        Assert.Equal(0, retried.Failed);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(payloads[0], payloads[1]);
        using var document = JsonDocument.Parse(payloads[1]);
        Assert.Equal(fixture.Results.Select(result => result.OriginalAnalysisId), document.RootElement
            .GetProperty("items").EnumerateArray().Select(item => item.GetProperty("analysisId").GetString()));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("disabled")]
    [InlineData("old-agreement")]
    [InlineData("no-installation")]
    public async Task Rejected_or_disabled_consent_never_reaches_the_http_handler(string state)
    {
        var fixture = new Fixture(1);
        fixture.Consent.Value = state switch
        {
            "rejected" => fixture.Consent.Value with { Enabled = false, AgreementVersion = "" },
            "disabled" => fixture.Consent.Value with { Enabled = false },
            "old-agreement" => fixture.Consent.Value with { AgreementVersion = "superseded-agreement" },
            _ => fixture.Consent.Value with { Installation = null },
        };
        using var handler = new Handler((_, _) => throw new InvalidOperationException("HTTP must not be sent."));
        using var client = Client(handler);
        var pipeline = fixture.Pipeline(client);

        var resolution = await pipeline.ResolveAsync(fixture.Candidates);
        var receipt = await pipeline.ContributeAsync(fixture.Candidates, fixture.Results, fixture.Provenance);

        Assert.False(resolution.Unavailable);
        Assert.Empty(resolution.ResultsByPath);
        Assert.Empty(resolution.MissingPaths);
        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(1, receipt.Skipped);
        Assert.Equal(0, receipt.Failed);
        Assert.Equal(0, fixture.Extractor.Calls);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Analysis_report_does_not_warn_when_the_contribution_receipt_skips_every_item()
    {
        var fixture = new Fixture(1);
        var provider = new FixtureProvider(fixture.Results);
        var uploaded = new List<SharedContribution>();
        using var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/query")
                return Reply(new SharedQueryResponse([]));
            Assert.Equal("/v1/contributions", request.RequestUri.AbsolutePath);
            uploaded.AddRange(await ReadItemsAsync<SharedContribution>(request, token));
            return Reply(new SharedContributionReceipt(0, 1, fixture.Consent.Value.Epoch));
        });
        using var client = Client(handler);
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(), sharedKnowledge: fixture.Pipeline(client));
        var root = fixture.ScanRoot;

        var report = await service.AnalyzeWithReportAsync(root,
            new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1, UseTokenSavingMode: true),
            new AiProviderOptions(provider.Kind, "https://offline-audit.invalid", "synthetic-model", null),
            provenance: fixture.Provenance with
                { PromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(true) });

        var result = Assert.Single(report.Results);
        Assert.False(result.LooksLikeFallback());
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
        Assert.Equal(result.OriginalAnalysisId, Assert.Single(uploaded).AnalysisId);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, report.SubmittedCandidateCount);
        // A server duplicate or rejection is not a transport failure. The service's
        // transport warning must not be triggered by a successful skipped receipt.
        Assert.Null(report.SharedKnowledgeWarning);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Offline_analysis_without_a_provider_reports_query_failure_without_claiming_an_ai_fallback()
    {
        var fixture = new Fixture(1);
        using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("Synthetic offline connection.")));
        using var client = Client(handler);
        var service = new AiAnalysisService([], new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(), sharedKnowledge: fixture.Pipeline(client));
        var root = fixture.ScanRoot;
        Assert.True(fixture.Consent.Value.CanShare);

        var report = await service.AnalyzeWithReportAsync(root,
            new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1, UseTokenSavingMode: true),
            new AiProviderOptions(AiProviderKind.Disabled, "", "", null),
            provenance: fixture.Provenance with
                { PromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(true) });

        var result = Assert.Single(report.Results);
        Assert.True(result.LooksLikeFallback());
        Assert.Equal(AiAnalysisResultOrigin.Incomplete, result.Origin);
        Assert.Contains("尚未配置可用的 AI 提供者", result.Reason);
        Assert.Equal("云端经验库查询失败，暂时无法获取文件用途经验，请检查网络后重试。", report.SharedKnowledgeWarning);
        Assert.Equal(1, report.TotalCandidates);
        Assert.Equal(0, report.SubmittedCandidateCount);
        Assert.Equal(0, report.TokenBudget.RequestCount);
        Assert.Equal(1, handler.RequestCount);
    }

    private static SharedExperienceClient Client(HttpMessageHandler handler)
        => new(new Uri("http://127.0.0.1:8791/"), handler);

    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
    };

    private static async Task<T[]> ReadItemsAsync<T>(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("items").Deserialize<T[]>(Json)!;
    }

    private sealed class Fixture
    {
        public AiCandidateRecord[] Candidates { get; }
        public AiAnalysisResult[] Results { get; }
        public ConsentSource Consent { get; } = new();
        public DictionaryExtractor Extractor { get; }
        // The root is a synthetic scan container, not an additional item under test.
        // Ordinary directory roots now receive their own purpose analysis.
        public FileSystemNode ScanRoot => new(@"D:\", "D:", "",
            Candidates.Sum(candidate => candidate.SizeBytes), FileAttributes.Directory, null, null, true, false,
            [], Candidates.Select(candidate => candidate.Structure).ToArray(), DescendantDirectoryCount: Candidates.Length);
        public SharedAnalysisProvenance Provenance => new("custom", "other", true)
            { ConsentAtAnalysisStart = Consent.Value };

        public Fixture(int count)
        {
            Candidates = Enumerable.Range(0, count).Select(index =>
            {
                // A reusable controlled directory, rather than an arbitrary file
                // whose extension cannot identify its purpose. The extractor is
                // deliberately synthetic so each record can exercise a batch slot.
                var path = $@"D:\OfflineAudit\case-{index:00}\Downloads\cache";
                var node = new FileSystemNode(path, Path.GetFileName(path), "", 2048,
                    FileAttributes.Directory, null, null, true, false, [], []);
                return new AiCandidateRecord(path, node.Name, node.SizeBytes, true, 0, false,
                    "synthetic-signature-" + index, node);
            }).ToArray();
            Results = Candidates.Select((candidate, index) => new AiAnalysisResult(candidate.Path,
                RiskLevel.Review, SuggestedAction.Review, "Synthetic local purpose explanation.", .9)
            {
                OriginalAnalysisId = "offline-audit-analysis-" + index,
                OriginalAnalyzedAt = Now.AddMinutes(-1),
                ConfidenceLevel = AiConfidenceLevel.High,
                PurposeCategory = "downloads",
            }).ToArray();
            Extractor = new DictionaryExtractor(Candidates.Select((candidate, index) =>
                new KeyValuePair<string, SharedFingerprint>(candidate.Path,
                    new("generic-v2", "0", "path2:user-downloads/cache", "tree2-" + index.ToString("x64"))))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        public SharedKnowledgePipeline Pipeline(ISharedExperienceClient client)
            => new(Consent, Extractor, client, new FrozenTimeProvider());

        public SharedQueryResponse QueryResponse(IEnumerable<SharedFingerprint> items) => new(items.Select(item =>
            new SharedQueryItem(item.Key, true, new SharedExperienceEntry(item.Key, item.SoftwareId,
                "Synthetic software", item.RoleId, "Synthetic role", "downloads", "Review", [],
                "Synthetic shared explanation", 2, 1, "active", Now.AddMinutes(15), Now.AddMinutes(-1))
                { ConfidenceLevel = "High" })).ToArray());
    }

    private sealed class ConsentSource : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; set; } = new(true,
            SharedConsentSnapshot.CurrentAgreementVersion, false, Now.AddMinutes(-5), 7,
            "offline-audit-policy", [], new("offline-audit-installation", "synthetic-audit-token", 7));

        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
    }

    private sealed class DictionaryExtractor(IReadOnlyDictionary<string, SharedFingerprint> fingerprints)
        : ISharedFingerprintExtractor
    {
        public int Calls { get; private set; }

        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult<SharedFingerprint?>(SharedFingerprintExtractor.IsExcluded(candidate.Path, exclusions)
                ? null : fingerprints[candidate.Path]);
        }
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureProvider(IReadOnlyList<AiAnalysisResult> results) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record =>
                results.Single(result => string.Equals(result.Path, record.Path, StringComparison.Ordinal))).ToArray());
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return send(request, cancellationToken);
        }
    }
}

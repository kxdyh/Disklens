using System.Net;
using System.Text;
using System.Text.Json;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class OpaqueObservationPipelineTests
{
    [Fact]
    public async Task Distinct_masked_paths_preserve_original_observations_and_query_only_own_installation()
    {
        var consent = new Consent(); var handler = new Handler();
        using var client = new SharedExperienceClient(new Uri("http://localhost/"), handler);
        var extractor = new SharedFingerprintExtractor(new NoProduct(), new Identities());
        var pipeline = new SharedKnowledgePipeline(consent, extractor, client, requiresNetwork: false);
        var first = Candidate(@"D:\DiskLensOpaqueFixture\first-private");
        var second = Candidate(@"D:\DiskLensOpaqueFixture\second-private");
        var originals = new[] { Result(first.Path, "original_analysis_01", "application"), Result(second.Path, "original_analysis_02", "cache") };

        var receipt = await pipeline.ContributeAsync([first, second], originals, Provenance(consent));

        Assert.Equal(2, receipt.Accepted);
        Assert.Equal(2, handler.Items.Count);
        Assert.Equal(originals.Select(result => result.OriginalAnalysisId), handler.Items.Select(item => item.GetProperty("analysisId").GetString()));
        Assert.NotEqual(handler.Items[0].GetProperty("shape").GetString(), handler.Items[1].GetProperty("shape").GetString());
        Assert.All(handler.Items, item =>
        {
            Assert.Equal("fresh-ai", item.GetProperty("source").GetString());
            Assert.DoesNotContain("private", item.GetRawText());
            Assert.DoesNotContain("DiskLensOpaqueFixture", item.GetRawText());
        });
        var resolved = await pipeline.ResolveAsync([first, second]);
        Assert.Empty(resolved.ResultsByPath);
        Assert.Equal(0, handler.PublicQueries);
        Assert.Equal(1, handler.OwnQueries);
        Assert.DoesNotContain("private", handler.QueryBody);
        Assert.DoesNotContain("DiskLensOpaqueFixture", handler.QueryBody);
        using var query = JsonDocument.Parse(handler.QueryBody);
        Assert.Equal(1, query.RootElement.GetProperty("epoch").GetInt64());
        Assert.Equal(handler.Items.Select(item => item.GetProperty("shape").GetString()),
            query.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("shape").GetString()));
    }

    [Fact]
    public async Task Contrary_conclusions_for_the_same_real_path_still_abstain()
    {
        var consent = new Consent(); var handler = new Handler();
        using var client = new SharedExperienceClient(new Uri("http://localhost/"), handler);
        var pipeline = new SharedKnowledgePipeline(consent,
            new SharedFingerprintExtractor(new NoProduct(), new Identities()), client, requiresNetwork: false);
        var candidate = Candidate(@"D:\DiskLensOpaqueFixture\one-private");
        var receipt = await pipeline.ContributeAsync([candidate],
            [Result(candidate.Path, "same_path_analysis_01", "application"), Result(candidate.Path, "same_path_analysis_02", "cache")], Provenance(consent));
        Assert.Equal(0, receipt.Accepted);
        Assert.Equal(2, receipt.Skipped);
        Assert.Empty(handler.Items);
    }

    private static AiCandidateRecord Candidate(string path)
    {
        var node = new FileSystemNode(path, Path.GetFileName(path), "", 1, FileAttributes.Directory,
            null, null, true, false, [], []);
        return new(path, node.Name, 1, true, 0, false, "original-signature", node)
        { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
    }
    private static AiAnalysisResult Result(string path, string id, string purpose) => new(path,
        RiskLevel.Low, SuggestedAction.Keep, "本项用途需要结合上下文核验", .9)
    {
        ShortPurpose = "用途观察", ShortPurposeEn = "Purpose observation", ReasonEn = "Purpose from the original analysis",
        PurposeCategory = purpose, ConfidenceLevel = AiConfidenceLevel.High,
        AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
        OriginalAnalysisId = id, OriginalAnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };
    private static SharedAnalysisProvenance Provenance(Consent consent) => new("official", "deepseek", true)
    { ConsentAtAnalysisStart = consent.Value };
    private sealed class Consent : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; } = new(true, SharedConsentSnapshot.CurrentAgreementVersion, true,
            DateTimeOffset.UtcNow.AddHours(-1), 1, "fixture-policy", [], new("fixture-installation", "fixture-token", 1));
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }
    private sealed class NoProduct : ISoftwareIdentityResolver
    {
        public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default)
            => Task.FromResult<VerifiedSoftwareInstallation?>(null);
    }
    private sealed class Identities : ISharedObservationIdentityProvider
    {
        private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
        public Task<string> GetOrCreateAsync(string normalizedLocalPath, CancellationToken cancellationToken = default)
        {
            if (!_ids.TryGetValue(normalizedLocalPath, out var id)) _ids[normalizedLocalPath] = id = _ids.Count.ToString("x32");
            return Task.FromResult(id);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonElement> Items { get; } = [];
        public int PublicQueries { get; private set; }
        public int OwnQueries { get; private set; }
        public string QueryBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("query", StringComparison.Ordinal))
            {
                if (request.RequestUri.AbsolutePath == "/v1/observations/query") OwnQueries++;
                else PublicQueries++;
                QueryBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Reply("{\"items\":[]}");
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var submitted = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Items.AddRange(submitted.Select(item => item.Clone()));
            return Reply($"{{\"accepted\":{submitted.Length},\"skipped\":0,\"epoch\":1}}");
        }
        private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class GeneralSharedClientTests
{
    private static readonly SharedFingerprint Fingerprint = new("generic-v2", "0",
        "path2:user-downloads/cache", "tree2-" + new string('a', 64));
    private static readonly SharedInstallation Installation = new("test-installation", "synthetic-management-token", 1);

    [Fact]
    public async Task Generic_file_query_transmits_only_anonymous_fingerprint_fields()
    {
        string? payload = null;
        using var client = Client(async request =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            Assert.Equal("synthetic-management-token", request.Headers.Authorization!.Parameter);
            Assert.DoesNotContain("synthetic-management-token", request.RequestUri!.AbsoluteUri);
            return Reply("{\"items\":[]}");
        });
        await client.QueryAsync(Installation, 1, [Fingerprint]);
        using var document = JsonDocument.Parse(payload!);
        Assert.Equal(new[] { "softwareId", "versionMajor", "roleId", "shape" },
            document.RootElement.GetProperty("items")[0].EnumerateObject().Select(x => x.Name));
        Assert.DoesNotContain("synthetic-management-token", payload!);
        Assert.DoesNotContain("path\"", payload!);
    }

    [Theory]
    [InlineData("High")]
    [InlineData("Medium")]
    public async Task General_contribution_sends_only_an_eligible_explicit_string_grade(string grade)
    {
        string? payload = null;
        using var client = Client(async request =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            return Reply("{\"accepted\":1,\"skipped\":0,\"epoch\":1}");
        });
        var receipt = await client.ContributeAsync(Installation, 1, [Contribution(grade)]);
        Assert.Equal(1, receipt.Accepted);
        using var document = JsonDocument.Parse(payload!);
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.String, item.GetProperty("confidenceLevel").ValueKind);
        Assert.Equal(grade, item.GetProperty("confidenceLevel").GetString());
        Assert.Equal("downloads", item.GetProperty("purpose").GetString());
        Assert.Equal(Fingerprint.RoleId, item.GetProperty("roleId").GetString());
        Assert.False(item.TryGetProperty("path", out _));
        Assert.False(item.TryGetProperty("name", out _));
        Assert.False(item.TryGetProperty("reason", out _));
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("logs")]
    [InlineData("downloads")]
    public async Task Explicit_recycle_candidate_reaches_transport_without_becoming_review(string purpose)
    {
        string? payload = null;
        using var client = Client(async request =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            return Reply("{\"accepted\":1,\"skipped\":0,\"epoch\":1}");
        });
        Assert.Equal(1, (await client.ContributeAsync(Installation, 1,
            [Contribution("High") with { Purpose = purpose, Action = "RecycleCandidate",
                ShortPurpose = "可重建临时数据", Reason = "应用生成的可重新获取内容" }])).Accepted);
        using var document = JsonDocument.Parse(payload!);
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal("RecycleCandidate", item.GetProperty("action").GetString());
        Assert.Equal(purpose, item.GetProperty("purpose").GetString());
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("application")]
    [InlineData("configuration")]
    [InlineData("user-data")]
    [InlineData("unknown")]
    public async Task Unsafe_positive_categories_are_rejected_before_transport(string purpose)
    {
        using var client = Client(_ => throw new InvalidOperationException("Unsafe positive advice must not be sent."));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ContributeAsync(Installation, 1,
            [Contribution("High") with { Purpose = purpose, Action = "RecycleCandidate",
                ShortPurpose = "错误的清理建议", Reason = "不应作为清理许可的持久内容" }]));
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("partial")]
    public async Task Anonymous_observations_keep_fingerprint_compatibility_when_their_grade_is_eligible(string kind)
    {
        var item = kind == "mask" ? Contribution("Medium") with { RoleId = "path2:volume/mask/file-zip" }
            : Contribution("Medium") with { Shape = "partial2-" + new string('b', 64) };
        using var client = Client(_ => Task.FromResult(Reply("{\"accepted\":1,\"skipped\":0,\"epoch\":1}")));
        Assert.Equal(1, (await client.ContributeAsync(Installation, 1, [item])).Accepted);
    }

    [Theory]
    [InlineData("高")]
    [InlineData("high")]
    [InlineData("VeryHigh")]
    [InlineData("")]
    [InlineData("Low")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public async Task Wire_contract_rejects_ineligible_or_noncanonical_grades_before_sending(string? grade)
    {
        using var client = Client(_ => throw new InvalidOperationException("Invalid input must not be sent."));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ContributeAsync(Installation, 1, [Contribution(grade!)]));
    }

    [Theory]
    [InlineData("software")]
    [InlineData("version")]
    [InlineData("raw-path")]
    [InlineData("raw-extension")]
    [InlineData("shape")]
    public async Task Malformed_or_private_fingerprint_fields_are_rejected_before_sending(string kind)
    {
        var invalid = kind switch
        {
            "software" => Fingerprint with { SoftwareId = "private-software-name" },
            "version" => Fingerprint with { VersionMajor = "42.private" },
            "raw-path" => Fingerprint with { RoleId = "path2:user-downloads/PrivateCompany/file-zip" },
            "raw-extension" => Fingerprint with { RoleId = "path2:user-downloads/file-private-extension" },
            _ => Fingerprint with { Shape = "tree2-private-name" },
        };
        Assert.False(SharedFingerprintProtocol.IsValid(invalid));
        using var client = Client(_ => throw new InvalidOperationException("Invalid input must not be sent."));
        await Assert.ThrowsAsync<ArgumentException>(() => client.QueryAsync(Installation, 1, [invalid]));
    }

    [Fact]
    public async Task Unknown_purpose_is_preserved_but_arbitrary_prose_is_rejected()
    {
        using var client = Client(_ => Task.FromResult(Reply("{\"accepted\":1,\"skipped\":0,\"epoch\":1}")));
        Assert.Equal(1, (await client.ContributeAsync(Installation, 1, [Contribution("High") with { Purpose = "unknown" }])).Accepted);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ContributeAsync(Installation, 1,
            [Contribution("High") with { Purpose = "my private description" }]));
    }

    private static SharedContribution Contribution(string grade) => new("original-analysis-1234",
        Fingerprint.SoftwareId, Fingerprint.VersionMajor, Fingerprint.RoleId, Fingerprint.Shape,
        "downloads", "Review", "fresh-ai", "official", "openai", true, false, grade);
    private static SharedExperienceClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new Uri("http://127.0.0.1:8791/"), new Handler(send));
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

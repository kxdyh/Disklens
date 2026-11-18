using System.Net;
using System.Text;
using System.Text.Json;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class SharedClientTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/?key=secret")]
    [InlineData("file:///D:/private")]
    public void Rejects_unsecured_nonlocal_or_credentialed_endpoints(string endpoint)
        => Assert.Throws<ArgumentException>(() => new SharedExperienceClient(new Uri(endpoint)));

    [Fact]
    public async Task Query_wire_contains_only_catalog_fields_and_token_is_header_only()
    {
        string? body = null; Uri? url = null;
        var handler = new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync(); url = request.RequestUri;
            Assert.Equal("token-secret", request.Headers.Authorization?.Parameter);
            return Reply("{\"items\":[]}");
        });
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), handler);
        await client.QueryAsync(new("installation", "token-secret", 1), 1, [new("android-sdk", "36", "sdk-temp", "cache-only-v1")]);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(new[] { "softwareId", "versionMajor", "roleId", "shape" }, json.RootElement.GetProperty("items")[0].EnumerateObject().Select(x => x.Name));
        Assert.DoesNotContain("token-secret", body!);
        Assert.DoesNotContain("token-secret", url!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Error_does_not_surface_remote_arbitrary_content(HttpStatusCode status)
    {
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), new Handler(_ => Task.FromResult(Reply("private-secret-content", status))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync());
        Assert.DoesNotContain("private-secret-content", error.Message);
    }

    [Fact]
    public async Task Rejects_oversized_response()
    {
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), new Handler(_ => Task.FromResult(Reply(new string('a', 600 * 1024)))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RegisterAsync());
    }

    [Theory]
    [InlineData("/v1/path-index")]
    [InlineData("/v1/query")]
    public async Task Manual_matching_does_not_resend_a_failed_read(string path)
    {
        var calls = 0;
        var now = DateTimeOffset.UtcNow;
        var index = JsonSerializer.Serialize(new { version = 1, shapes = Array.Empty<string>(), complete = true,
            builtAt = now, expiresAt = now.AddMinutes(3) });
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), new Handler(request =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            return Task.FromResult(++calls == 1 ? Reply("{}", HttpStatusCode.ServiceUnavailable)
                : Reply(path == "/v1/query" ? "{\"items\":[]}" : index));
        }));
        Task ReadAsync() => path == "/v1/query"
            ? client.QueryAsync(new("installation", "synthetic-token", 1), 1,
                [new("android-sdk", "36", "sdk-temp", "cache-only-v1")], default, retryTransientFailure: false)
            : client.RefreshSharedPathIndexAsync(default, retryTransientFailure: false);

        await Assert.ThrowsAsync<HttpRequestException>(ReadAsync);
        Assert.Equal(1, calls);
        await ReadAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Parses_worker_numeric_rule_version()
    {
        var payload = "{\"items\":[{\"key\":\"android-sdk|36|sdk-temp|cache-only-v1\",\"matched\":true,\"entry\":{\"key\":\"android-sdk|36|sdk-temp|cache-only-v1\",\"softwareId\":\"android-sdk\",\"softwareLabel\":\"Android SDK\",\"roleId\":\"sdk-temp\",\"roleLabel\":\"缓存\",\"purpose\":\"cache\",\"action\":\"Review\",\"conditions\":[],\"explanation\":\"条件说明\",\"ruleVersion\":1,\"revision\":3,\"status\":\"active\",\"expiresAt\":\"2026-11-07T06:20:00Z\",\"updatedAt\":\"2026-11-07T06:05:00Z\"}}]}";
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), new Handler(_ => Task.FromResult(Reply(payload))));
        var result = await client.QueryAsync(new("installation", "token-secret", 1), 1, [new("android-sdk", "36", "sdk-temp", "cache-only-v1")]);
        Assert.Equal(1, Assert.Single(result.Items).Entry?.RuleVersion);
    }

    [Fact]
    public async Task Contribution_listing_follows_bounded_pages_without_silently_truncating()
    {
        var pages = 0;
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/consent") return Task.FromResult(Reply("{\"epoch\":1,\"enabled\":true,\"agreementVersion\":\"2026-11-beta1\",\"shareHistory\":false,\"policyRevision\":\"v1\"}"));
            pages++;
            return Task.FromResult(Reply(pages == 1
                ? "{\"items\":[{\"id\":\"first\",\"analysisId\":\"one\",\"key\":\"catalog\",\"purpose\":\"cache\",\"action\":\"Review\"}],\"nextCursor\":\"" + new string('a', 64) + "\"}"
                : "{\"items\":[{\"id\":\"second\",\"analysisId\":\"two\",\"key\":\"catalog\",\"purpose\":\"cache\",\"action\":\"Review\"}],\"nextCursor\":null}"));
        }));
        var result = await client.ListContributionsAsync(new("installation", "token-secret", 1));
        Assert.Equal(2, pages);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task Changed_epoch_during_listing_requires_retry()
    {
        var reads = 0;
        using var client = new SharedExperienceClient(new Uri("http://127.0.0.1:8791/"), new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/consent")
                return Task.FromResult(Reply("{\"epoch\":" + (++reads == 1 ? 1 : 2) + ",\"enabled\":true,\"agreementVersion\":\"2026-11-beta1\",\"shareHistory\":false,\"policyRevision\":\"v1\"}"));
            return Task.FromResult(Reply("{\"items\":[],\"nextCursor\":null}"));
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListContributionsAsync(new("installation", "token-secret", 1)));
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

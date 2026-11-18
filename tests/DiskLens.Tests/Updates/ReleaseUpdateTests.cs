using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DiskLens.Core.Updates;

namespace DiskLens.Tests.Updates;

public sealed class ReleaseUpdateTests
{
    private static byte[] Manifest(string version = "1.2.0", string page = "https://disklens.site/#download") =>
        JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, version, publishedAt = "2026-11-06", downloadPage = page,
            minimumSupportedVersion = "1.1.0", notes = new[] { "自动同步文件变化", "AI 分析支持当前目录" } });

    [Fact]
    public void Reads_production_contract_and_uses_canonical_download_page()
    {
        Assert.True(ReleaseManifest.TryRead(Manifest(), out var release));
        Assert.Equal(new Version(1, 2, 0), release!.Version);
        Assert.Equal(new DateOnly(2026, 11, 6), release.PublishedAt);
        Assert.Equal(2, release.Notes.Count);
        Assert.Empty(release.NotesEn);
        Assert.Equal(ReleaseManifest.OfficialDownloadPage, release.DownloadPage);
    }

    [Fact]
    public async Task Optional_English_notes_round_trip_without_changing_release_identity_or_Chinese_notes()
    {
        var node = JsonSerializer.SerializeToNode(JsonSerializer.Deserialize<JsonElement>(Manifest()))!;
        node["notesEn"] = JsonSerializer.SerializeToNode(new[] { "Automatically tracks file changes", "AI analysis supports the current folder" });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) }));
        var release = await new ReleaseFeedClient(client).FetchAsync(CancellationToken.None);
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal(new DateOnly(2026, 11, 6), release.PublishedAt);
        Assert.Equal("自动同步文件变化", release.Notes[0]);
        Assert.Equal("Automatically tracks file changes", release.NotesEn[0]);
        Assert.Equal(ReleaseManifest.OfficialDownloadPage, release.DownloadPage);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[42]")]
    [InlineData("[\"line\\nwith control\"]")]
    public void Invalid_optional_English_notes_do_not_bypass_feed_validation(string notesEn)
    {
        var json = Encoding.UTF8.GetString(Manifest());
        var bytes = Encoding.UTF8.GetBytes(json[..^1] + ",\"notesEn\":" + notesEn + "}");
        Assert.False(ReleaseManifest.TryRead(bytes, out _));
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("http://disklens.site/#download")]
    [InlineData("https://disklens.site.evil.test/")]
    [InlineData("https://user:password@disklens.site/#download")]
    [InlineData("https://disklens.site:444/#download")]
    [InlineData("https://disklens.site/redirect?to=evil")]
    [InlineData("file:///E:/program.exe")]
    public void Rejects_links_that_are_not_the_official_download_page(string page) =>
        Assert.False(ReleaseManifest.TryRead(Manifest(page: page), out _));

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.0")]
    [InlineData("1.2.0-preview")]
    [InlineData("01.2.0")]
    [InlineData("1.2.0.0")]
    [InlineData("999999999999999.2.0")]
    [InlineData("")]
    public void Rejects_ambiguous_or_nonstable_versions(string version) =>
        Assert.False(ReleaseManifest.TryRead(Manifest(version), out _));

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":99999999999999999999999}")]
    public void Malformed_or_wrong_schema_is_ignored_without_throwing(string json) =>
        Assert.False(ReleaseManifest.TryRead(Encoding.UTF8.GetBytes(json), out _));

    [Theory]
    [InlineData(@"\uD800")]
    [InlineData(@"\uDC00")]
    [InlineData(@"\uD800x")]
    [InlineData(@"\uD800\uD800")]
    public void Malformed_unicode_in_release_notes_is_an_invalid_feed_without_throwing(string encodedText)
    {
        const string json = """{"schemaVersion":1,"version":"1.2.0","publishedAt":"2026-11-06","downloadPage":"https://disklens.site/#download","notes":["TEXT"]}""";
        var bytes = Encoding.UTF8.GetBytes(json.Replace("TEXT", encodedText, StringComparison.Ordinal));
        Assert.False(ReleaseManifest.TryRead(bytes, out var release));
        Assert.Null(release);
    }

    [Fact]
    public void Exact_ignored_release_does_not_suppress_later_versions()
    {
        ReleaseManifest.TryRead(Manifest("1.2.0"), out var ignored);
        ReleaseManifest.TryRead(Manifest("1.7.0"), out var latest);
        Assert.False(ReleaseUpdatePolicy.ShouldOffer(ignored!, new Version(1, 1, 0), "1.2.0"));
        Assert.True(ReleaseUpdatePolicy.ShouldOffer(latest!, new Version(1, 1, 0), "1.2.0"));
        Assert.True(ReleaseUpdatePolicy.ShouldOffer(ignored!, new Version(1, 1, 0), "1.2.0", manual: true));
        Assert.False(ReleaseUpdatePolicy.ShouldOffer(ignored!, new Version(1, 2, 0), null));
        Assert.False(ReleaseUpdatePolicy.ShouldOffer(ignored!, new Version(2, 0, 0), null));
    }

    [Fact]
    public async Task Fetches_only_small_public_metadata_without_credentials()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(ReleaseManifest.FeedUri, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest()) };
        }));
        Assert.NotNull(await new ReleaseFeedClient(client).FetchAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Does_not_treat_http_errors_or_redirects_as_an_update(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(Manifest()) }));
        Assert.Null(await new ReleaseFeedClient(client).FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_large_body_even_without_content_length()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(new byte[65_537])) }));
        Assert.Null(await new ReleaseFeedClient(client).FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Closing_the_application_cancels_network_wait()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new BlockingHandler());
        var pending = new ReleaseFeedClient(client).FetchAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

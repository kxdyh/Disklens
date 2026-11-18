using System.Net.Http;

namespace DiskLens.Core.Updates;

public interface IReleaseFeedClient
{
    Task<ReleaseManifest?> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Small, bounded HTTPS metadata request; never downloads or executes an installer.</summary>
public sealed class ReleaseFeedClient(HttpClient client) : IReleaseFeedClient
{
    public async Task<ReleaseManifest?> FetchAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseManifest.FeedUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65_536) return null;
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > 65_536) return null;
            output.Write(buffer, 0, count);
        }
        return ReleaseManifest.TryRead(output.ToArray(), out var manifest) ? manifest : null;
    }
}

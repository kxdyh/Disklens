using System.Net;
using System.Text;

namespace DiskLens.Core.Ai;

internal sealed class AiPayloadTooLargeException(HttpStatusCode statusCode, string? provider = null)
    : HttpRequestException(
        string.IsNullOrWhiteSpace(provider)
            ? "AI 服务报告单批输入超过请求或模型上下文限制。"
            : $"{provider} 报告单批输入超过请求或模型上下文限制。",
        inner: null,
        statusCode)
{
}

internal static class AiProviderErrorClassifier
{
    public const int MaxErrorBodyPrefixBytes = 64 * 1024;

    private static readonly string[] ContextLimitMarkers =
    [
        "context_length_exceeded",
        "maximum context length",
        "context window exceeded",
        "context length exceeded",
        "exceeds the context length",
        "too long for the context",
        "context limit",
        "input length exceeds",
        "input is too long",
        "input too long",
        "prompt is too long",
        "prompt too long",
        "maximum prompt length",
        "reduce the length of the messages",
        "request entity too large",
        "request too large",
        "payload too large",
        "exceeds the available context size",
        "exceed the available context size",
        "context size exceeded",
    ];

    public static bool IsPayloadTooLarge(HttpStatusCode statusCode, string? responseBody)
    {
        if (statusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            return true;
        }

        if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        if (ContextLimitMarkers.Any(marker => responseBody.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var normalized = responseBody.Replace('_', ' ').ToLowerInvariant();
        var mentionsInput = new[] { "context", "prompt", "input", "message", "token" }
            .Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        var mentionsLimit = new[] { "too long", "exceed", "maximum", "max ", "limit", "length", "too large" }
            .Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        return mentionsInput && mentionsLimit;
    }

    public static async Task<string> ReadErrorBodyPrefixAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: MaxErrorBodyPrefixBytes);
        var chunk = new byte[4096];

        while (buffer.Length < MaxErrorBodyPrefixBytes)
        {
            var bytesToRead = (int)Math.Min(chunk.Length, MaxErrorBodyPrefixBytes - buffer.Length);
            var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}

using System.Text;
using System.Text.Json;
using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed class OllamaAiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;

    public OllamaAiProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public AiProviderKind Kind => AiProviderKind.Ollama;

    public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
        AiBatch batch,
        AiProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        batch.RequestContext?.ReportResponseDiagnostics(new(batch.Records.Count, 0, 0, []));
        using var schemaResponse = await SendAsync(batch, options, useSchema: true, cancellationToken).ConfigureAwait(false);
        if (schemaResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var errorBody = await AiProviderErrorClassifier.ReadErrorBodyPrefixAsync(schemaResponse.Content, cancellationToken).ConfigureAwait(false);
            if (AiProviderErrorClassifier.IsPayloadTooLarge(schemaResponse.StatusCode, errorBody))
            {
                throw new AiPayloadTooLargeException(schemaResponse.StatusCode, "Ollama");
            }

            // Retry only explicit format incompatibility, not unrelated validation errors.
            if (AiResponseSchema.IsFormatUnsupported(errorBody))
            {
                batch.RequestContext?.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(AiResponseIssue.FormatFallback));
                using var fallback = await SendAsync(batch, options, useSchema: false, cancellationToken).ConfigureAwait(false);
                return await ReadResultsAsync(fallback, batch, cancellationToken).ConfigureAwait(false);
            }

            schemaResponse.EnsureSuccessStatusCode();
        }

        return await ReadResultsAsync(schemaResponse, batch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AiAnalysisResult>> ReadResultsAsync(
        HttpResponseMessage response, AiBatch batch, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            throw new AiPayloadTooLargeException(response.StatusCode, "Ollama");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var errorBody = await AiProviderErrorClassifier.ReadErrorBodyPrefixAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (AiProviderErrorClassifier.IsPayloadTooLarge(response.StatusCode, errorBody))
                throw new AiPayloadTooLargeException(response.StatusCode, "Ollama");
        }

        response.EnsureSuccessStatusCode();

        using var document = AiProviderResponseDiagnostics.ParseEnvelope(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), batch.RequestContext);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw AiProviderResponseDiagnostics.InvalidEnvelope(batch.RequestContext);
        batch.RequestContext?.ReadOllamaUsage(root);
        AiProviderResponseDiagnostics.ReportFinishReason(root, "done_reason", batch.RequestContext);
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw AiProviderResponseDiagnostics.InvalidEnvelope(batch.RequestContext);
        var content = message.TryGetProperty("content", out var modelContent) && modelContent.ValueKind == JsonValueKind.String
            ? modelContent.GetString() : null;
        return AiProviderResponseDiagnostics.ParseContent(content, batch.RequestContext);
    }

    private async Task<HttpResponseMessage> SendAsync(AiBatch batch, AiProviderOptions options, bool useSchema,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = options.Model,
                    stream = false,
                    format = useSchema ? (object)AiResponseSchema.GetSchemaForPaths(batch.Records.Select(record => record.Path)) : "json",
                    options = new { num_predict = batch.RequestContext?.MaxOutputTokens ?? 8192 },
                    messages = new object[]
                    {
                        new { role = "system", content = batch.RequestContext?.SystemPrompt ?? AiPromptFactory.CreateSystemPrompt(batch.UseTokenSavingMode) },
                        new { role = "user", content = batch.RequestContext?.UserPayload ?? AiPromptFactory.CreateUserPayload(batch) },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };
        using (request)
        {
            batch.RequestContext?.ReportResponseDiagnostics(AiResponseDiagnostics.Empty with { HttpRequestCount = 1 });
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
    }
}

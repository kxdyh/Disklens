using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

    public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
        AiBatch batch,
        AiProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        batch.RequestContext?.ReportResponseDiagnostics(new(batch.Records.Count, 0, 0, []));
        var systemPrompt = batch.RequestContext?.SystemPrompt ?? AiPromptFactory.CreateSystemPrompt(batch.UseTokenSavingMode);
        var userPayload = batch.RequestContext?.UserPayload ?? AiPromptFactory.CreateUserPayload(batch);
        var useStrictSchema = Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            && AiResponseSchema.SupportsOpenAiStrictSchema(endpoint, options.Model);
        var payload = new Dictionary<string, object>
        {
            ["model"] = options.Model,
            ["temperature"] = options.Temperature,
            ["response_format"] = useStrictSchema
                ? new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "disklens_analysis",
                        strict = true,
                        schema = AiResponseSchema.GetSchemaForPaths(batch.Records.Select(record => record.Path)),
                    },
                }
                : new { type = "json_object" },
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPayload },
            },
        };
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            payload["reasoning_effort"] = options.ReasoningEffort;
        }

        if (batch.RequestContext is { } requestContext)
        {
            var model = options.Model.ToLowerInvariant();
            var usesCompletionLimit = model.StartsWith("o1", StringComparison.Ordinal)
                || model.StartsWith("o3", StringComparison.Ordinal)
                || model.StartsWith("o4", StringComparison.Ordinal)
                || model.StartsWith("gpt-5", StringComparison.Ordinal);
            payload[usesCompletionLimit ? "max_completion_tokens" : "max_tokens"] = requestContext.MaxOutputTokens;
        }

        using var response = await SendAsync(payload, options, batch.RequestContext, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var errorBody = await AiProviderErrorClassifier.ReadErrorBodyPrefixAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (AiProviderErrorClassifier.IsPayloadTooLarge(response.StatusCode, errorBody))
                throw new AiPayloadTooLargeException(response.StatusCode, "AI 服务");

            if (useStrictSchema && AiResponseSchema.IsFormatUnsupported(errorBody))
            {
                batch.RequestContext?.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(AiResponseIssue.FormatFallback));
                payload["response_format"] = new { type = "json_object" };
                using var fallback = await SendAsync(payload, options, batch.RequestContext, cancellationToken).ConfigureAwait(false);
                return await ReadResultsAsync(fallback, batch, cancellationToken).ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();
        }

        return await ReadResultsAsync(response, batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Dictionary<string, object> payload, AiProviderOptions options, AiRequestContext? context,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        context?.ReportResponseDiagnostics(AiResponseDiagnostics.Empty with { HttpRequestCount = 1 });
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AiAnalysisResult>> ReadResultsAsync(
        HttpResponseMessage response, AiBatch batch, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
        {
            throw new AiPayloadTooLargeException(response.StatusCode, "AI 服务");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var errorBody = await AiProviderErrorClassifier.ReadErrorBodyPrefixAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (AiProviderErrorClassifier.IsPayloadTooLarge(response.StatusCode, errorBody))
            {
                throw new AiPayloadTooLargeException(response.StatusCode, "AI 服务");
            }
        }

        response.EnsureSuccessStatusCode();

        using var document = AiProviderResponseDiagnostics.ParseEnvelope(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), batch.RequestContext);
        // Account usage before parsing model output: malformed/truncated JSON still costs tokens.
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object) batch.RequestContext?.ReadOpenAiUsage(root);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object)
            throw AiProviderResponseDiagnostics.InvalidEnvelope(batch.RequestContext);
        var choice = choices[0];
        AiProviderResponseDiagnostics.ReportFinishReason(choice, "finish_reason", batch.RequestContext);
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw AiProviderResponseDiagnostics.InvalidEnvelope(batch.RequestContext);
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(refusal.GetString()))
            batch.RequestContext?.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(AiResponseIssue.ModelRefusal));
        var content = message.TryGetProperty("content", out var modelContent) && modelContent.ValueKind == JsonValueKind.String
            ? modelContent.GetString() : null;
        return AiProviderResponseDiagnostics.ParseContent(content, batch.RequestContext);
    }
}

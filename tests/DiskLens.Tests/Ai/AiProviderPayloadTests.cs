using System.Net;
using System.Text;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiProviderPayloadTests
{
    [Fact]
    public void Parent_structure_omits_unread_and_skipped_placeholders_without_claiming_complete_evidence()
    {
        var original = CreateCandidateRecord();
        var unread = original.Structure.Children[0] with
        { Name = "UNREAD_PLACEHOLDER", Path = original.Path + @"\UNREAD_PLACEHOLDER", RiskTags = ["scan-error"] };
        var alias = unread with
        { Name = "SKIPPED_ALIAS", Path = original.Path + @"\SKIPPED_ALIAS", RiskTags = ["skipped", "reparse-point"] };
        var candidate = original with
        {
            HasIncompleteScan = true,
            ScopeLimitation = "SECRET_SCAN_DIAGNOSTIC",
            Structure = original.Structure with { Children = [unread, alias, .. original.Structure.Children] }
        };

        var payload = AiPromptFactory.CreateUserPayload(new AiBatch([candidate], 100));

        Assert.Contains("a.tmp", payload);
        Assert.DoesNotContain("UNREAD_PLACEHOLDER", payload);
        Assert.DoesNotContain("SKIPPED_ALIAS", payload);
        Assert.DoesNotContain("SECRET_SCAN_DIAGNOSTIC", payload);
        Assert.Contains("path-purpose", payload);
        Assert.True(candidate.HasIncompleteScan);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_should_omit_nonessential_fields_when_token_saving_mode_enabled()
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], EstimatedTokens: 128, UseTokenSavingMode: true),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.com", "demo-model", "test-key"));

        var userPayload = ExtractUserPayload(handler.LastRequestBody);

        Assert.Contains("\"Path\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"SizeBytes\"", userPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Name\"", userPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"IsDirectory\"", userPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DescendantFileCount\"", userPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"IsProtectedLocation\"", userPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"structure\"", userPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_should_use_compact_system_prompt_when_token_saving_mode_enabled()
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], EstimatedTokens: 128, UseTokenSavingMode: true),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.com", "demo-model", "test-key"));

        var systemPrompt = ExtractSystemPrompt(handler.LastRequestBody);

        Assert.Contains("节省模式", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Few-shot", systemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_should_keep_structured_fields_when_token_saving_mode_disabled()
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], EstimatedTokens: 128, UseTokenSavingMode: false),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.com", "demo-model", "test-key"));

        var userPayload = ExtractUserPayload(handler.LastRequestBody);

        Assert.Contains("\"Path\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"SizeBytes\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"Name\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"IsDirectory\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"DescendantFileCount\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"IsProtectedLocation\"", userPayload, StringComparison.Ordinal);
        Assert.Contains("\"structure\"", userPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_should_send_reasoning_effort_when_configured()
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], EstimatedTokens: 128),
            new AiProviderOptions(
                AiProviderKind.OpenAiCompatible,
                "https://api.deepseek.com",
                "deepseek-flash",
                "test-key",
                ReasoningEffort: "medium"));

        using var document = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("medium", document.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_should_leave_reasoning_effort_to_service_by_default()
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], EstimatedTokens: 128),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.com", "demo-model", "test-key"));

        using var document = JsonDocument.Parse(handler.LastRequestBody);
        Assert.False(document.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task OpenAi_official_supported_model_receives_strict_json_schema_for_the_existing_result_shape()
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://api.openai.com/v1", "gpt-4o-mini", "test-key"));

        using var document = JsonDocument.Parse(handler.LastRequestBody);
        var format = document.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schemaWrapper = format.GetProperty("json_schema");
        Assert.True(schemaWrapper.GetProperty("strict").GetBoolean());
        var schema = schemaWrapper.GetProperty("schema");
        var properties = schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
        Assert.Equal(30, properties.GetProperty("reason").GetProperty("maxLength").GetInt32());
        Assert.Equal(12, properties.GetProperty("shortPurpose").GetProperty("maxLength").GetInt32());
        Assert.Equal(80, properties.GetProperty("shortPurposeEn").GetProperty("maxLength").GetInt32());
        Assert.Equal(240, properties.GetProperty("reasonEn").GetProperty("maxLength").GetInt32());
        Assert.Equal(2, properties.GetProperty("suggestedAction").GetProperty("enum").GetArrayLength());
        Assert.True(properties.TryGetProperty("confidenceLevel", out _));
        Assert.True(properties.TryGetProperty("purposeCategory", out _));
        AssertUnchangedFieldSchemas(properties);
    }

    [Fact]
    public async Task DeepSeek_and_unknown_openai_compatible_endpoints_keep_json_object_mode()
    {
        foreach (var endpoint in new[] { "https://api.deepseek.com", "https://custom-proxy.example/v1" })
        {
            var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
            var provider = new OpenAiCompatibleProvider(new HttpClient(handler));
            await provider.AnalyzeAsync(new AiBatch([CreateCandidateRecord()], 128),
                new AiProviderOptions(AiProviderKind.OpenAiCompatible, endpoint, "demo-model", "test-key"));
            using var document = JsonDocument.Parse(handler.LastRequestBody);
            Assert.Equal("json_object", document.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("gpt-4o-2024-05-13")]
    [InlineData("o1-preview")]
    [InlineData("o1-mini-2024-09-12")]
    public async Task Older_official_models_keep_the_existing_json_mode(string model)
    {
        var handler = new CapturingHttpMessageHandler(CreateOpenAiCompatibleSuccessResponse);
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));
        await provider.AnalyzeAsync(new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://api.openai.com/v1", model, "test-key"));
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Official_OpenAi_retries_only_explicit_schema_incompatibility()
    {
        var calls = 0;
        var handler = new CapturingHttpMessageHandler(request => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("response_format json_schema is not supported with this model", Encoding.UTF8, "application/json"),
            }
            : CreateOpenAiCompatibleSuccessResponse(request));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        var results = await provider.AnalyzeAsync(new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://api.openai.com/v1", "gpt-4o-mini", "test-key"));

        Assert.Equal(2, calls);
        Assert.Equal("临时缓存数据", Assert.Single(results).ShortPurpose);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Ollama_receives_schema_as_format_while_preserving_all_result_fields()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"message":{"content":"{\"items\":[{\"path\":\"E:\\\\fixture\\\\file.bin\",\"riskLevel\":\"Review\",\"suggestedAction\":\"DoNotDelete\",\"shortPurpose\":\"用途标签\",\"shortPurposeEn\":\"Application data\",\"reasonEn\":\"Stores application data\",\"reason\":\"用途短句\",\"confidence\":0.5,\"confidenceLevel\":\"Medium\",\"purposeCategory\":\"unknown\"}]}"},"prompt_eval_count":1,"eval_count":1}
                """, Encoding.UTF8, "application/json"),
        });
        var provider = new OllamaAiProvider(new HttpClient(handler));

        await provider.AnalyzeAsync(new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.Ollama, "http://localhost:11434", "test-model", null));

        using var document = JsonDocument.Parse(handler.LastRequestBody);
        var format = document.RootElement.GetProperty("format");
        var properties = format.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
        Assert.Equal(30, properties.GetProperty("reason").GetProperty("maxLength").GetInt32());
        Assert.Equal(12, properties.GetProperty("shortPurpose").GetProperty("maxLength").GetInt32());
        Assert.Equal(80, properties.GetProperty("shortPurposeEn").GetProperty("maxLength").GetInt32());
        Assert.Equal(240, properties.GetProperty("reasonEn").GetProperty("maxLength").GetInt32());
        Assert.True(properties.TryGetProperty("confidenceLevel", out _));
        Assert.True(properties.TryGetProperty("purposeCategory", out _));
        AssertUnchangedFieldSchemas(properties);
    }

    [Fact]
    public async Task Ollama_retries_json_mode_when_an_older_server_rejects_schema_format()
    {
        var calls = 0;
        var handler = new CapturingHttpMessageHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("structured format is not supported", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"message":{"content":"{\"items\":[{\"path\":\"E:\\\\fixture\\\\file.bin\",\"riskLevel\":\"Review\",\"suggestedAction\":\"DoNotDelete\",\"shortPurpose\":\"用途标签\",\"shortPurposeEn\":\"Application data\",\"reasonEn\":\"Stores application data\",\"reason\":\"用途短句\",\"confidence\":0.5}]}"}}
                    """, Encoding.UTF8, "application/json"),
            });
        var provider = new OllamaAiProvider(new HttpClient(handler));

        var result = await provider.AnalyzeAsync(new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.Ollama, "http://localhost:11434", "test-model", null));

        Assert.Single(result);
        Assert.Equal(2, calls);
        using var document = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("json", document.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Ollama_does_not_retry_unrelated_validation_errors()
    {
        var calls = 0;
        var handler = new CapturingHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("invalid model name", Encoding.UTF8, "application/json"),
            };
        });
        var provider = new OllamaAiProvider(new HttpClient(handler));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.Ollama, "http://localhost:11434", "test-model", null)));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Ollama_classifies_context_limits_from_the_json_fallback(HttpStatusCode fallbackStatus)
    {
        var calls = 0;
        var handler = new CapturingHttpMessageHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("structured format is not supported", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(fallbackStatus)
            {
                Content = new StringContent("prompt too long", Encoding.UTF8, "application/json"),
            });
        var provider = new OllamaAiProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], 128),
            new AiProviderOptions(AiProviderKind.Ollama, "http://localhost:11434", "test-model", null)));

        Assert.Equal(2, calls);
        Assert.Contains("超过请求或模型上下文限制", error.Message);
    }

    private static void AssertUnchangedFieldSchemas(JsonElement properties)
    {
        foreach (var field in new[] { "path", "riskLevel", "confidence", "confidenceLevel", "purposeCategory" })
        {
            Assert.Single(properties.GetProperty(field).EnumerateObject());
            Assert.Equal(field == "confidence" ? "number" : "string",
                properties.GetProperty(field).GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task OpenAiCompatibleProvider_should_classify_unusual_400_prompt_limit_errors_for_retry()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("The prompt has exceeded our limit.", Encoding.UTF8, "application/json"),
        });
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler));

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.AnalyzeAsync(
            new AiBatch([CreateCandidateRecord()], EstimatedTokens: 128),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.com", "demo-model", "test-key")));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("超过请求或模型上下文限制", exception.Message, StringComparison.Ordinal);
    }

    private static string ExtractUserPayload(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private static string ExtractSystemPrompt(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private static HttpResponseMessage CreateOpenAiCompatibleSuccessResponse(HttpRequestMessage _)
    {
        var responseBody = """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"items\":[{\"path\":\"C:\\\\Users\\\\tester\\\\AppData\\\\Local\\\\Temp\\\\cache\",\"riskLevel\":\"Review\",\"suggestedAction\":\"Review\",\"shortPurpose\":\"临时缓存数据\",\"shortPurposeEn\":\"Application data\",\"reasonEn\":\"Stores application data\",\"reason\":\"需要人工确认。\",\"confidence\":0.5}]}"
                  }
                }
              ]
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }

    private static AiCandidateRecord CreateCandidateRecord()
    {
        var structure = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Temp\cache",
            Name: "cache",
            Extension: string.Empty,
            SizeBytes: 1024,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\AppData\Local\Temp\cache\a.tmp",
                    Name: "a.tmp",
                    Extension: ".tmp",
                    SizeBytes: 1024,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        return new AiCandidateRecord(
            Path: structure.Path,
            Name: structure.Name,
            SizeBytes: structure.SizeBytes,
            IsDirectory: structure.IsDirectory,
            DescendantFileCount: 1,
            IsProtectedLocation: false,
            Signature: "signature",
            Structure: structure);
    }

    private sealed class CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return _responder(request);
        }
    }
}

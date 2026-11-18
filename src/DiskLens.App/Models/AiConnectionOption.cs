using DiskLens.Core.Models;

namespace DiskLens.App.Models;

public sealed record AiConnectionOption(string Id, string Label)
{
    public const string DisabledId = "disabled";
    public const string DeepSeekId = "deepseek";
    public const string CustomId = "custom";
    public const string LegacyOllamaId = "ollama";
    public const string DeepSeekEndpoint = "https://api.deepseek.com";
    public const string DeepSeekModel = "deepseek-flash";
    public const string DeepSeekReasoningEffort = "medium";

    public static IReadOnlyList<AiConnectionOption> Standard =>
    [
        new(DisabledId, L.T("暂不启用")),
        new(DeepSeekId, "DeepSeek"),
        new(CustomId, L.T("自定义 OpenAI 兼容接口")),
    ];

    public static IReadOnlyList<AiConnectionOption> WithLegacyOllama =>
    [
        .. Standard,
        new(LegacyOllamaId, L.T("Ollama（已有配置）")),
    ];

    public static string ResolveProfile(AppSettings settings)
    {
        if (settings.ProviderKind == AiProviderKind.Disabled)
        {
            return DisabledId;
        }

        if (settings.ProviderKind == AiProviderKind.Ollama)
        {
            return LegacyOllamaId;
        }

        // A saved choice takes precedence over URL inference, including custom
        // connections that deliberately use the official DeepSeek endpoint.
        if (settings.AiConnectionProfileId is DeepSeekId or CustomId)
        {
            return settings.AiConnectionProfileId;
        }

        return IsDeepSeekEndpoint(settings.AiEndpoint) ? DeepSeekId : CustomId;
    }

    private static bool IsDeepSeekEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Trim('/').ToLowerInvariant() is "" or "v1";
}

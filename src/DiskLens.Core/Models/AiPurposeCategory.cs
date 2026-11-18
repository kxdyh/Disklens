namespace DiskLens.Core.Models;

/// <summary>Bounded purpose values; paths and free-form explanations are never categories.</summary>
public static class AiPurposeCategory
{
    public const string Unknown = "unknown";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cache" => "cache",
        "runtime" => "runtime",
        "application" => "application",
        "configuration" => "configuration",
        "logs" => "logs",
        "downloads" => "downloads",
        "user-data" => "user-data",
        _ => Unknown,
    };
}

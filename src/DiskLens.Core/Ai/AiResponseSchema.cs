using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiskLens.Core.Ai;

internal static class AiResponseSchema
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "riskLevel": { "type": "string", "enum": ["Low", "Review", "High", "Protected"] },
                  "suggestedAction": { "type": "string", "enum": ["RecycleCandidate", "DoNotDelete"] },
                  "shortPurpose": { "type": "string", "minLength": 1, "maxLength": 12 },
                  "reason": { "type": "string", "minLength": 1, "maxLength": 30 },
                  "shortPurposeEn": { "type": "string", "minLength": 1, "maxLength": 80 },
                  "reasonEn": { "type": "string", "minLength": 1, "maxLength": 240 },
                  "confidence": { "type": "number" },
                  "confidenceLevel": { "type": "string", "enum": ["High", "Medium", "Low"] },
                  "purposeCategory": { "type": "string", "enum": ["cache", "runtime", "application", "configuration", "logs", "downloads", "user-data", "unknown"] }
                },
                "required": ["path", "riskLevel", "suggestedAction", "shortPurpose", "reason", "shortPurposeEn", "reasonEn", "confidence", "confidenceLevel", "purposeCategory"],
                "additionalProperties": false
              }
            }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public static JsonElement GetSchema() => Schema;

    public static JsonElement GetSchemaForPaths(IEnumerable<string> paths)
    {
        var expected = paths.Distinct(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0) return Schema;
        // Enum is supported by both schema providers. Keep array cardinality in
        // the request contract rather than adding provider-specific keywords.
        var schema = JsonNode.Parse(Schema.GetRawText())!;
        var path = schema["properties"]!["items"]!["items"]!["properties"]!["path"]!;
        path["enum"] = new JsonArray(expected.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return JsonSerializer.SerializeToElement(schema);
    }

    public static bool SupportsOpenAiStrictSchema(Uri endpoint, string model)
    {
        if (!string.Equals(endpoint.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedModel = model.Trim().ToLowerInvariant();
        if (normalizedModel is "gpt-4o-2024-05-13" or "o1-preview" or "o1-mini"
            || normalizedModel.StartsWith("o1-preview-", StringComparison.Ordinal)
            || normalizedModel.StartsWith("o1-mini-", StringComparison.Ordinal))
            return false;

        return normalizedModel.StartsWith("gpt-4o", StringComparison.Ordinal)
            || normalizedModel.StartsWith("gpt-4.1", StringComparison.Ordinal)
            || normalizedModel.StartsWith("gpt-5", StringComparison.Ordinal)
            || normalizedModel.StartsWith("o1", StringComparison.Ordinal)
            || normalizedModel.StartsWith("o3", StringComparison.Ordinal)
            || normalizedModel.StartsWith("o4", StringComparison.Ordinal);
    }

    public static bool IsFormatUnsupported(string errorBody)
    {
        var mentionsFormat = errorBody.Contains("format", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("schema", StringComparison.OrdinalIgnoreCase);
        return mentionsFormat && new[] { "not support", "unsupported", "cannot unmarshal object", "expected a string", "must be a string" }
            .Any(marker => errorBody.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

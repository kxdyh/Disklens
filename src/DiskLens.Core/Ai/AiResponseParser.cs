using System.Globalization;
using System.Text;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

internal static class AiResponseParser
{
    public static IReadOnlyList<AiAnalysisResult> ParseResults(string content) => ParseResultsCore(content, requirePurposePair: false);

    public static IReadOnlyList<AiAnalysisResult> ParseStructuredResults(string content) => ParseResultsCore(content, requirePurposePair: true);

    public static IReadOnlyList<AiAnalysisResult> ParseStructuredResultsWithDiagnostics(string content, int requestedItems,
        Action<AiResponseDiagnostics> reportDiagnostics) =>
        ParseResultsCore(content, requirePurposePair: true, requestedItems, reportDiagnostics);

    private static IReadOnlyList<AiAnalysisResult> ParseResultsCore(string content, bool requirePurposePair,
        int requestedItems = 0, Action<AiResponseDiagnostics>? reportDiagnostics = null)
    {
        var issues = new Dictionary<AiResponseIssue, int>();
        var returnedItems = 0;
        var results = new List<AiAnalysisResult>();
        void AddIssue(AiResponseIssue issue) => issues[issue] = issues.GetValueOrDefault(issue) + 1;
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                AddIssue(AiResponseIssue.EmptyContent);
                throw new JsonException("AI 返回为空。");
            }
            using var document = JsonDocument.Parse(ExtractFirstJsonObject(content));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("AI 返回必须是 JSON 对象。");
            if (!TryGetPropertyIgnoreCase(document.RootElement, "items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                AddIssue(AiResponseIssue.MissingItemsArray);
                throw new JsonException("AI 返回中缺少 items 数组。");
            }

            returnedItems = items.GetArrayLength();

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    AddIssue(AiResponseIssue.NonObjectItem);
                    continue;
                }

                var path = GetStringProperty(item, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    AddIssue(AiResponseIssue.MissingPath);
                    continue;
                }

                var riskLevel = ParseRiskLevel(GetStringProperty(item, "riskLevel"));
                var suggestedAction = ParseSuggestedAction(GetStringProperty(item, "suggestedAction"));
                var reason = GetStringProperty(item, "reason")?.Trim();
                var shortPurpose = GetStringProperty(item, "shortPurpose")?.Trim() ?? string.Empty;
                var shortPurposeEn = GetStringProperty(item, "shortPurposeEn")?.Trim() ?? string.Empty;
                var reasonEn = GetStringProperty(item, "reasonEn")?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    AddIssue(GetStringProperty(item, "reason") is null
                        ? AiResponseIssue.MissingPurposeField : AiResponseIssue.EmptyPurposeField);
                    // A grade or action cannot replace a purpose explanation. Leaving this
                    // path absent lets the service recover it without discarding valid peers.
                    continue;
                }

                // Reject an incomplete new response for targeted retry. Never invent the
                // short text by truncating or summarizing the detailed explanation.
                if (requirePurposePair)
                {
                    if (new[] { "shortPurpose", "shortPurposeEn", "reasonEn" }
                        .Any(name => GetStringProperty(item, name) is null))
                    {
                        AddIssue(AiResponseIssue.MissingPurposeField);
                        continue;
                    }
                    var texts = new[] { shortPurpose, reason, shortPurposeEn, reasonEn };
                    if (texts.Any(string.IsNullOrWhiteSpace))
                    {
                        AddIssue(AiResponseIssue.EmptyPurposeField);
                        continue;
                    }
                    if (shortPurpose.EnumerateRunes().Count() > AiPurposeTextContract.MaximumShortPurposeLength
                        || reason.EnumerateRunes().Count() > AiPurposeTextContract.MaximumReasonLength
                        || shortPurposeEn.EnumerateRunes().Count() > AiPurposeTextContract.MaximumShortPurposeEnLength
                        || reasonEn.EnumerateRunes().Count() > AiPurposeTextContract.MaximumReasonEnLength)
                    {
                        AddIssue(AiResponseIssue.PurposeTextTooLong);
                        continue;
                    }
                    if (!AiPurposeTextContract.IsValidShortPurpose(shortPurpose)
                        || !AiPurposeTextContract.IsValidReason(reason))
                    {
                        AddIssue(AiResponseIssue.InvalidPurposeText);
                        continue;
                    }
                    if (!AiPurposeTextContract.IsValidShortPurposeEn(shortPurposeEn)
                        || !AiPurposeTextContract.IsValidReasonEn(reasonEn))
                    {
                        AddIssue(AiResponseIssue.InvalidEnglishText);
                        continue;
                    }
                }

                var confidence = ClampConfidence(GetDoubleProperty(item, "confidence"));

                results.Add(new AiAnalysisResult(path, riskLevel, suggestedAction, reason, confidence)
                {
                    ShortPurpose = shortPurpose,
                    ShortPurposeEn = shortPurposeEn,
                    ReasonEn = reasonEn,
                    ConfidenceLevel = ParseConfidenceLevel(GetStringProperty(item, "confidenceLevel")),
                    PurposeCategory = AiPurposeCategory.Normalize(GetStringProperty(item, "purposeCategory")),
                    HasExplicitNumericConfidence = TryGetPropertyIgnoreCase(item, "confidence", out _),
                });
            }

            if (results.Count == 0)
            {
                if (issues.Count == 0) AddIssue(AiResponseIssue.MissingResult);
                throw new JsonException("AI 返回中没有可用的结果项。");
            }

            return results;
        }
        catch (JsonException)
        {
            if (issues.Count == 0) AddIssue(AiResponseIssue.MalformedJson);
            throw;
        }
        finally
        {
            reportDiagnostics?.Invoke(new AiResponseDiagnostics(Math.Max(0, requestedItems), returnedItems, results.Count,
                issues.OrderBy(item => item.Key).Select(item => new AiResponseIssueCount(item.Key, item.Value)).ToArray()));
        }
    }

    private static AiConfidenceLevel ParseConfidenceLevel(string? value)
    {
        // Accept exact labels only. A malformed string such as "High!" must not
        // be upgraded by stripping punctuation or unrelated input characters.
        return value?.Trim().ToLowerInvariant() switch
        {
            "low" or "低" => AiConfidenceLevel.Low,
            "medium" or "中" => AiConfidenceLevel.Medium,
            "high" or "高" => AiConfidenceLevel.High,
            _ => AiConfidenceLevel.Unknown,
        };
    }

    private static SuggestedAction ParseSuggestedAction(string? value)
    {
        return NormalizeToken(value) switch
        {
            "recyclecandidate" => SuggestedAction.RecycleCandidate,
            "keep" => SuggestedAction.Keep,
            "donotdelete" => SuggestedAction.DoNotDelete,
            _ => SuggestedAction.Review,
        };
    }

    private static RiskLevel ParseRiskLevel(string? value)
    {
        return NormalizeToken(value) switch
        {
            "low" => RiskLevel.Low,
            "high" => RiskLevel.High,
            "protected" => RiskLevel.Protected,
            _ => RiskLevel.Review,
        };
    }

    private static string ExtractFirstJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new JsonException("AI 返回为空。");
        }

        var trimmed = content.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var startIndex = -1;
        var depth = 0;
        var inString = false;
        var escaping = false;

        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (startIndex < 0)
            {
                if (ch == '{')
                {
                    startIndex = index;
                    depth = 1;
                }

                continue;
            }

            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return content[startIndex..(index + 1)];
            }
        }

        return trimmed;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return TryGetPropertyIgnoreCase(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static double GetDoubleProperty(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numericValue))
        {
            return numericValue;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringValue))
        {
            return stringValue;
        }

        return 0;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static double ClampConfidence(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetter)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}

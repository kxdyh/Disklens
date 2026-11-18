using System.Text.Json;
using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

/// <summary>Closed, content-free response diagnostics; never contains model text or local identities.</summary>
public enum AiResponseIssue
{
    FinishLength,
    EmptyContent,
    ContentFilter,
    ModelRefusal,
    MalformedJson,
    MissingItemsArray,
    NonObjectItem,
    MissingPath,
    MissingPurposeField,
    EmptyPurposeField,
    PurposeTextTooLong,
    InvalidPurposeText,
    InvalidEnglishText,
    MissingResult,
    UnexpectedPath,
    DuplicatePath,
    FormatFallback,
}

internal static class AiProviderResponseDiagnostics
{
    public static JsonDocument ParseEnvelope(string content, AiRequestContext? context)
    {
        try { return JsonDocument.Parse(content); }
        catch (JsonException)
        {
            context?.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(AiResponseIssue.MalformedJson));
            throw;
        }
    }

    public static JsonException InvalidEnvelope(AiRequestContext? context)
    {
        context?.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(AiResponseIssue.MalformedJson));
        return new JsonException("AI 服务返回的响应结构不完整。");
    }

    public static void ReportFinishReason(JsonElement parent, string property, AiRequestContext? context)
    {
        if (context is null || !parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return;
        var issue = value.GetString()?.ToLowerInvariant() switch
        {
            "length" or "max_tokens" => (AiResponseIssue?)AiResponseIssue.FinishLength,
            "content_filter" => AiResponseIssue.ContentFilter,
            _ => null,
        };
        if (issue is { } reason) context.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(reason));
    }

    public static IReadOnlyList<AiAnalysisResult> ParseContent(string? content, AiRequestContext? context)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            context?.ReportResponseDiagnostics(AiResponseDiagnostics.ForIssue(AiResponseIssue.EmptyContent));
            throw new InvalidOperationException("AI 服务未返回分析内容。");
        }
        // RequestedItems was recorded once at the provider entry, including
        // attempts whose transport or response format later failed.
        return context is null ? AiResponseParser.ParseStructuredResults(content)
            : AiResponseParser.ParseStructuredResultsWithDiagnostics(content, 0, context.ReportResponseDiagnostics);
    }
}

public sealed record AiResponseIssueCount(AiResponseIssue Issue, int Count);

public sealed record AiResponseDiagnostics(
    int RequestedItems,
    int ReturnedItems,
    int AcceptedItems,
    IReadOnlyList<AiResponseIssueCount> Issues)
{
    public static AiResponseDiagnostics Empty { get; } = new(0, 0, 0, []);
    public int HttpRequestCount { get; init; }
    public int RejectedItems => Math.Max(0, ReturnedItems - AcceptedItems);

    public static AiResponseDiagnostics ForIssue(AiResponseIssue issue, int count = 1) =>
        new(0, 0, 0, count > 0 && Enum.IsDefined(issue) ? [new(issue, count)] : []);

    public int Count(AiResponseIssue issue) => Issues.Where(item => item.Issue == issue)
        .Aggregate(0, (total, item) => Add(total, item.Count));

    public AiResponseDiagnostics Merge(AiResponseDiagnostics other) => new(
        Add(RequestedItems, other.RequestedItems), Add(ReturnedItems, other.ReturnedItems),
        Add(AcceptedItems, other.AcceptedItems),
        Issues.Concat(other.Issues).Where(item => item.Count > 0 && Enum.IsDefined(item.Issue))
            .GroupBy(item => item.Issue).OrderBy(group => group.Key)
            .Select(group => new AiResponseIssueCount(group.Key,
                group.Aggregate(0, (total, item) => Add(total, item.Count)))).ToArray())
        { HttpRequestCount = Add(HttpRequestCount, other.HttpRequestCount) };

    public string Summary => string.Join("；", Issues.Where(item => item.Count > 0)
        .Select(item => $"{Label(item.Issue)} {item.Count:N0}"));

    public string FormatSummary() => Summary;

    private static int Add(int left, int right) => (int)Math.Min(int.MaxValue, (long)Math.Max(0, left) + Math.Max(0, right));

    private static string Label(AiResponseIssue issue) => issue switch
    {
        AiResponseIssue.FinishLength => "输出达到长度上限",
        AiResponseIssue.EmptyContent => "返回内容为空",
        AiResponseIssue.ContentFilter => "服务商内容过滤",
        AiResponseIssue.ModelRefusal => "模型拒绝回答",
        AiResponseIssue.MalformedJson => "JSON 格式不完整",
        AiResponseIssue.MissingItemsArray => "缺少结果数组",
        AiResponseIssue.NonObjectItem => "结果项格式不正确",
        AiResponseIssue.MissingPath => "结果缺少路径",
        AiResponseIssue.MissingPurposeField => "用途字段缺失",
        AiResponseIssue.EmptyPurposeField => "用途字段为空",
        AiResponseIssue.PurposeTextTooLong => "用途说明超长",
        AiResponseIssue.InvalidPurposeText => "用途说明含控制字符",
        AiResponseIssue.InvalidEnglishText => "英文用途字符不合规",
        AiResponseIssue.MissingResult => "缺少候选结果",
        AiResponseIssue.UnexpectedPath => "返回了未请求路径",
        AiResponseIssue.DuplicatePath => "返回了重复路径",
        AiResponseIssue.FormatFallback => "结构化格式兼容回退",
        _ => "其他响应问题",
    };
}

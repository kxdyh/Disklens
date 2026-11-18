using System.Reflection;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiResponseParserTests
{
    [Fact]
    public void ParseResults_should_extract_first_json_object_from_wrapped_response()
    {
        const string content = """
            下面是结果：
            ```json
            {
              "items": [
                {
                  "path": "C:\\Users\\tester\\AppData\\Local\\Temp\\sample.tmp",
                  "riskLevel": "Low",
                  "suggestedAction": "RecycleCandidate",
                  "reason": "临时目录中的缓存文件，可作为清理候选。",
                  "confidence": 0.92
                }
              ]
            }
            ```
            """;

        var results = InvokeParser(content);

        var item = Assert.Single(results);
        Assert.Equal(@"C:\Users\tester\AppData\Local\Temp\sample.tmp", item.Path);
        Assert.Equal(RiskLevel.Low, item.RiskLevel);
        Assert.Equal(SuggestedAction.RecycleCandidate, item.SuggestedAction);
        Assert.Equal("临时目录中的缓存文件，可作为清理候选。", item.Reason);
    }

    [Fact]
    public void ParseResults_should_tolerate_case_insensitive_properties_and_clamp_confidence()
    {
        const string content = """
            {
              "Items": [
                {
                  "PATH": "C:\\Users\\tester\\Downloads\\archive.zip",
                  "RISKLEVEL": "review",
                  "SUGGESTEDACTION": "keep",
                  "REASON": "需要人工确认是否仍在使用。",
                  "CONFIDENCE": 1.7
                }
              ]
            }
            """;

        var results = InvokeParser(content);

        var item = Assert.Single(results);
        Assert.Equal(RiskLevel.Review, item.RiskLevel);
        Assert.Equal(SuggestedAction.Keep, item.SuggestedAction);
        Assert.Equal(1, item.Confidence);
    }

    [Fact]
    public void ParseResults_should_degrade_invalid_item_values_to_review_instead_of_throwing()
    {
        const string content = """
            {
              "items": [
                {
                  "path": "C:\\Users\\tester\\Desktop\\mystery.bin",
                  "riskLevel": "???",
                  "suggestedAction": "delete-now",
                  "reason": "用途尚不明确，需要人工确认。",
                  "confidence": -4
                }
              ]
            }
            """;

        var results = InvokeParser(content);

        var item = Assert.Single(results);
        Assert.Equal(RiskLevel.Review, item.RiskLevel);
        Assert.Equal(SuggestedAction.Review, item.SuggestedAction);
        Assert.Equal(0, item.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(item.Reason));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" \t\r\n", true)]
    [InlineData(123, true)]
    [InlineData(true, true)]
    public void ParseResults_should_omit_unexplained_items_without_discarding_valid_unknown_purposes(
        object? reason, bool includeReason)
    {
        var unexplained = new Dictionary<string, object?>
        {
            ["path"] = @"E:\Downloads\unexplained.zip",
            ["riskLevel"] = "Low",
            ["suggestedAction"] = "RecycleCandidate",
            ["confidence"] = .99,
            ["confidenceLevel"] = "High",
            ["purposeCategory"] = "downloads",
        };
        if (includeReason) unexplained["reason"] = reason;
        var explained = new Dictionary<string, object?>
        {
            ["path"] = @"E:\Downloads\unclassified.zip",
            ["riskLevel"] = "Review",
            ["suggestedAction"] = "Review",
            ["reason"] = "压缩包的具体用途不明，需由用户确认。",
            ["confidence"] = .99,
            ["confidenceLevel"] = "High",
            ["purposeCategory"] = "unknown",
        };

        var results = InvokeParser(JsonSerializer.Serialize(new { items = new[] { unexplained, explained } }));

        var retained = Assert.Single(results);
        Assert.Equal(explained["path"], retained.Path);
        Assert.Equal(explained["reason"], retained.Reason);
        Assert.Equal("unknown", retained.PurposeCategory);
        Assert.Equal(AiConfidenceLevel.High, retained.EffectiveConfidenceLevel);
        Assert.False(retained.LooksLikeFallback());
    }

    [Fact]
    public void Legacy_parser_preserves_old_explanations_without_inventing_a_short_purpose()
    {
        const string content = """
            {"items":[{"path":"E:\\fixture\\folder","riskLevel":"High","suggestedAction":"DoNotDelete","reason":"Windows 系统启动组件，其他内容很长很长很长很长。","confidence":0.92}]}
            """;

        var item = Assert.Single(InvokeParser(content));

        Assert.Equal("Windows 系统启动组件，其他内容很长很长很长很长。", item.Reason);
        Assert.Equal(string.Empty, item.ShortPurpose);
        Assert.Equal(string.Empty, item.ShortPurposeEn);
        Assert.Equal(string.Empty, item.ReasonEn);
    }

    [Fact]
    public void Structured_parser_preserves_both_model_written_texts_and_all_other_fields()
    {
        const string content = """
            {"items":[{"path":"C:\\Users","riskLevel":"High","suggestedAction":"DoNotDelete","shortPurpose":"用户资料与应用数据","reason":"存放用户配置、文档与应用数据","shortPurposeEn":"User files and application data","reasonEn":"Stores user settings, documents, and application data","confidence":0.93,"confidenceLevel":"High","purposeCategory":"user-data"}]}
            """;
        var item = Assert.Single(InvokeParser(content, structured: true));
        Assert.Equal("用户资料与应用数据", item.ShortPurpose);
        Assert.Equal("存放用户配置、文档与应用数据", item.Reason);
        Assert.Equal("User files and application data", item.ShortPurposeEn);
        Assert.Equal("Stores user settings, documents, and application data", item.ReasonEn);
        Assert.Equal(.93, item.Confidence);
        Assert.Equal(AiConfidenceLevel.High, item.ConfidenceLevel);
        Assert.Equal("user-data", item.PurposeCategory);
    }

    [Theory]
    [InlineData("", "存放用户配置、文档与应用数据")]
    [InlineData("超过十二个字符的超长用途标签内容", "存放用户配置、文档与应用数据")]
    [InlineData("用户资料", "这份说明超过三十个字符而且不应该被截断成另一个结论因此应当让服务重新请求完整结果")]
    public void Invalid_purpose_pairs_are_retried_as_missing_items_without_silently_summarizing(string shortPurpose, string reason)
    {
        var invalid = new { path = @"C:\Users", shortPurpose, reason, shortPurposeEn = "User files", reasonEn = "User data and settings" };
        var valid = new { path = @"C:\Windows", shortPurpose = "系统运行文件", reason = "Windows 系统运行所需文件", shortPurposeEn = "System files", reasonEn = "Files needed for Windows to operate" };
        var items = new[] { invalid, valid };
        var item = Assert.Single(InvokeParser(JsonSerializer.Serialize(new { items }), structured: true));
        Assert.Equal(valid.path, item.Path);
        Assert.Equal(valid.shortPurpose, item.ShortPurpose);
        Assert.Equal(valid.reason, item.Reason);
    }

    [Fact]
    public void ParseResults_should_reject_a_response_with_no_explained_items_even_when_grades_are_high()
    {
        const string content = """
            {
              "items": [
                {"path":"E:\\Downloads\\missing.zip","confidenceLevel":"High","confidence":0.99},
                {"path":"E:\\Downloads\\empty.zip","reason":" \t\r\n","confidenceLevel":"High","confidence":0.99},
                {"path":"E:\\Downloads\\invalid.zip","reason":42,"confidenceLevel":"High","confidence":0.99}
              ]
            }
            """;

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeParser(content));

        Assert.IsType<JsonException>(exception.InnerException);
    }

    private static IReadOnlyList<AiAnalysisResult> InvokeParser(string content, bool structured = false)
    {
        var assembly = typeof(AiAnalysisResult).Assembly;
        var parserType = assembly.GetType("DiskLens.Core.Ai.AiResponseParser", throwOnError: true)!;
        var method = parserType.GetMethod(structured ? "ParseStructuredResults" : "ParseResults", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        return (IReadOnlyList<AiAnalysisResult>)method.Invoke(null, [content])!;
    }
}

using System.Reflection;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiBilingualPurposeTests
{
    [Theory]
    [InlineData(null, "Stores application data")]
    [InlineData("Application data", null)]
    [InlineData("", "")]
    [InlineData("用户数据", "Stores application data")]
    [InlineData("Application data", "Stores\napplication data")]
    public void New_response_missing_or_invalid_English_is_retried_without_losing_valid_peers(
        string? shortPurposeEn, string? reasonEn)
    {
        var invalid = Item(@"E:\fixture\invalid", shortPurposeEn, reasonEn);
        var valid = Item(@"E:\fixture\valid", "Application data", "Stores application data");
        var result = Assert.Single(Parse(JsonSerializer.Serialize(new { items = new[] { invalid, valid } })));
        Assert.Equal(valid["path"], result.Path);
        Assert.Equal(valid["shortPurposeEn"], result.ShortPurposeEn);
        Assert.Equal(valid["reasonEn"], result.ReasonEn);
    }

    [Theory]
    [InlineData(80, 240, true)]
    [InlineData(81, 240, false)]
    [InlineData(80, 241, false)]
    public void English_length_limits_do_not_reuse_Chinese_character_limits(int shortLength, int reasonLength, bool valid)
    {
        var content = JsonSerializer.Serialize(new { items = new[] { Item(@"E:\fixture\data", new string('A', shortLength), new string('B', reasonLength)) } });
        if (valid)
        {
            var result = Assert.Single(Parse(content));
            Assert.Equal(shortLength, result.ShortPurposeEn.Length);
            Assert.Equal(reasonLength, result.ReasonEn.Length);
        }
        else
        {
            var exception = Assert.Throws<TargetInvocationException>(() => Parse(content));
            Assert.IsType<JsonException>(exception.InnerException);
        }
    }

    [Fact]
    public void Bilingual_memory_round_trip_preserves_original_age_identity_and_source()
    {
        var originalAt = DateTimeOffset.UtcNow.AddHours(-3);
        var result = Assert.Single(Parse(JsonSerializer.Serialize(new { items = new[] { Item(@"E:\fixture\data", "Application data", "Stores application data") } }))) with
        {
            OriginalAnalysisId = "bilingual-original-1234", OriginalAnalyzedAt = originalAt,
        };
        var promptHash = AiPromptFactory.CreateSystemPromptFingerprint();
        var memory = new AiAnalysisMemoryEntry(result.Path, false, "signature", result, originalAt,
            AiProviderKind.OpenAiCompatible, "https://example.com/v1", "model", promptHash);
        var restored = JsonSerializer.Deserialize<AiAnalysisMemoryEntry>(JsonSerializer.Serialize(memory))!;
        Assert.Equal(result, restored.Result);
        Assert.Equal(originalAt, restored.AnalyzedAt);
        Assert.True(restored.IsUsableAt(DateTimeOffset.UtcNow));
        Assert.True(restored.MatchesAnalysisContext(AiProviderKind.OpenAiCompatible,
            "https://example.com/v1", "model", promptHash, false, 0, 2400));
        Assert.False(restored.MatchesAnalysisContext(AiProviderKind.OpenAiCompatible,
            "https://example.com/v1", "model", "older-monolingual-prompt", false, 0, 2400));
    }

    [Fact]
    public void Deserializing_legacy_memory_leaves_English_empty_and_does_not_manufacture_an_analysis()
    {
        var originalAt = DateTimeOffset.UtcNow.AddHours(-3);
        var legacy = new AiAnalysisResult(@"E:\fixture\data", RiskLevel.High,
            SuggestedAction.DoNotDelete, "存放应用数据", .9)
        {
            ShortPurpose = "应用数据", ConfidenceLevel = AiConfidenceLevel.High,
            OriginalAnalysisId = "legacy-original-1234", OriginalAnalyzedAt = originalAt,
        };
        var json = JsonSerializer.SerializeToNode(legacy)!;
        json.AsObject().Remove("ShortPurposeEn");
        json.AsObject().Remove("ReasonEn");
        var restored = json.Deserialize<AiAnalysisResult>()!;
        Assert.Empty(restored.ShortPurposeEn);
        Assert.Empty(restored.ReasonEn);
        Assert.Equal(legacy.OriginalAnalysisId, restored.OriginalAnalysisId);
        Assert.Equal(legacy.OriginalAnalyzedAt, restored.OriginalAnalyzedAt);
        Assert.Equal(legacy.Origin, restored.Origin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_prompt_mode_requires_both_languages_from_the_same_response(bool compact)
    {
        var prompt = AiPromptFactory.CreateSystemPrompt(compact);
        Assert.Contains("shortPurposeEn", prompt);
        Assert.Contains("reasonEn", prompt);
        Assert.Contains("硬上限 80 字符", prompt);
        Assert.Contains("硬上限 240 字符", prompt);
        Assert.Contains("四项中英说明均为非空字符串", prompt);
        Assert.Contains("中英范围必须相同，不添加新事实", prompt);
        Assert.Equal(AiPromptFactory.CreateSystemPromptFingerprint(prompt),
            AiPromptFactory.CreateSystemPromptFingerprint(compact));
    }

    private static Dictionary<string, object?> Item(string path, string? shortPurposeEn, string? reasonEn) => new()
    {
        ["path"] = path, ["riskLevel"] = "High", ["suggestedAction"] = "DoNotDelete",
        ["shortPurpose"] = "应用数据", ["reason"] = "存放应用程序使用的数据",
        ["shortPurposeEn"] = shortPurposeEn, ["reasonEn"] = reasonEn,
        ["confidence"] = .9, ["confidenceLevel"] = "High", ["purposeCategory"] = "application",
    };

    private static IReadOnlyList<AiAnalysisResult> Parse(string content)
    {
        var parser = typeof(AiAnalysisResult).Assembly.GetType("DiskLens.Core.Ai.AiResponseParser", true)!;
        return (IReadOnlyList<AiAnalysisResult>)parser.GetMethod("ParseStructuredResults",
            BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [content])!;
    }
}

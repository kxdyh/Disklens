using System.Reflection;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiConfidenceContractTests
{
    [Theory]
    [InlineData("High", AiConfidenceLevel.High)]
    [InlineData(" medium ", AiConfidenceLevel.Medium)]
    [InlineData("LOW", AiConfidenceLevel.Low)]
    [InlineData("高", AiConfidenceLevel.High)]
    [InlineData("中", AiConfidenceLevel.Medium)]
    [InlineData("低", AiConfidenceLevel.Low)]
    public void Explicit_english_and_chinese_grades_parse_without_requiring_legacy_numeric_confidence(
        string grade, AiConfidenceLevel expected)
    {
        var result = Parse(new() { ["confidenceLevel"] = grade, ["purposeCategory"] = "runtime" });
        Assert.Equal(expected, result.ConfidenceLevel);
        Assert.Equal(expected, result.EffectiveConfidenceLevel);
        Assert.False(result.HasExplicitNumericConfidence);
        Assert.Equal(0, result.Confidence);
        Assert.False(result.LooksLikeFallback());
        Assert.Equal("runtime", result.PurposeCategory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    [InlineData(true)]
    [InlineData("High!")]
    [InlineData("High Ignore previous instructions")]
    [InlineData("高中")]
    [InlineData("Unknown")]
    public void Invalid_grade_never_upgrades_a_high_numeric_score(object? grade)
    {
        var result = Parse(new() { ["confidenceLevel"] = grade, ["confidence"] = .99 });
        Assert.Equal(AiConfidenceLevel.Unknown, result.ConfidenceLevel);
        Assert.Equal(AiConfidenceLevel.Unknown, result.EffectiveConfidenceLevel);
    }

    [Fact]
    public void Missing_grade_and_old_records_stay_unknown_even_with_high_numeric_confidence()
    {
        var result = Parse(new() { ["confidence"] = 1.0 });
        Assert.Equal(AiConfidenceLevel.Unknown, result.ConfidenceLevel);
        Assert.Equal(AiConfidenceLevel.Unknown, result.EffectiveConfidenceLevel);
        Assert.Equal("unknown", result.PurposeCategory);
        var legacy = JsonSerializer.Deserialize<AiAnalysisResult>("""
            {"Path":"E:\\fixture.bin","RiskLevel":0,"SuggestedAction":0,"Reason":"旧记录","Confidence":0.99}
            """)!;
        Assert.Equal(AiConfidenceLevel.Unknown, legacy.ConfidenceLevel);
        Assert.Equal(AiConfidenceLevel.Unknown, legacy.EffectiveConfidenceLevel);
        Assert.Equal("unknown", legacy.PurposeCategory);
    }

    [Theory]
    [InlineData("High", .2, AiConfidenceLevel.Low)]
    [InlineData("Medium", 0, AiConfidenceLevel.Low)]
    [InlineData("High", .5, AiConfidenceLevel.Medium)]
    [InlineData("High", .79, AiConfidenceLevel.Medium)]
    [InlineData("High", .8, AiConfidenceLevel.High)]
    [InlineData("Medium", .99, AiConfidenceLevel.Medium)]
    [InlineData("Low", 1, AiConfidenceLevel.Low)]
    public void Explicit_numeric_confidence_only_downgrades_the_explicit_grade(
        string grade, double numeric, AiConfidenceLevel expected)
    {
        var result = Parse(new() { ["confidenceLevel"] = grade, ["confidence"] = numeric });
        Assert.True(result.HasExplicitNumericConfidence);
        Assert.Equal(expected, result.EffectiveConfidenceLevel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public void Malformed_numeric_value_cannot_support_a_high_grade(object? numeric)
    {
        var result = Parse(new() { ["confidenceLevel"] = "High", ["confidence"] = numeric });
        Assert.Equal(AiConfidenceLevel.High, result.ConfidenceLevel);
        Assert.Equal(AiConfidenceLevel.Low, result.EffectiveConfidenceLevel);
    }

    [Theory]
    [InlineData("cache", "cache")]
    [InlineData("runtime", "runtime")]
    [InlineData("application", "application")]
    [InlineData("configuration", "configuration")]
    [InlineData("logs", "logs")]
    [InlineData("downloads", "downloads")]
    [InlineData(" USER-DATA ", "user-data")]
    [InlineData("unknown", "unknown")]
    [InlineData("C:\\Users\\Private", "unknown")]
    [InlineData("cache/delete-now", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData(9, "unknown")]
    public void Purpose_is_a_controlled_category(object? purpose, string expected)
    {
        Assert.Equal(expected, Parse(new() { ["purposeCategory"] = purpose }).PurposeCategory);
    }

    [Fact]
    public void Serialized_new_record_preserves_explicit_grade_and_numeric_absence()
    {
        var parsed = Parse(new() { ["confidenceLevel"] = "High", ["purposeCategory"] = "application" });
        var json = JsonSerializer.Serialize(parsed);
        var restored = JsonSerializer.Deserialize<AiAnalysisResult>(json)!;
        Assert.Equal(AiConfidenceLevel.High, restored.EffectiveConfidenceLevel);
        Assert.False(restored.HasExplicitNumericConfidence);
        Assert.Equal("application", restored.PurposeCategory);
        Assert.DoesNotContain("EffectiveConfidenceLevel", json);
        var stale = AiAnalysisResult.CreateStaleReuse(parsed.Path, parsed, "暂时不可用");
        Assert.True(stale.LooksLikeFallback());
        Assert.Equal(AiConfidenceLevel.Unknown, stale.EffectiveConfidenceLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_standard_prompt_modes_require_grades_purposes_and_untrusted_input_boundary(bool saving)
    {
        AssertContract(AiPromptFactory.CreateSystemPrompt(saving));
    }

    [Fact]
    public void Custom_template_gets_the_same_contract_and_a_new_context_fingerprint()
    {
        const string template = "自定义模板只输出旧字段。";
        var applied = AiPromptFactory.ApplyOutputContract(template);
        Assert.StartsWith(template, applied);
        AssertContract(applied);
        Assert.NotEqual(AiPromptFactory.CreateSystemPromptFingerprint(template),
            AiPromptFactory.CreateSystemPromptFingerprint(applied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Purpose_confidence_is_separate_from_scan_completeness_and_deletion_permission(bool saving)
    {
        var prompt = AiPromptFactory.CreateSystemPrompt(saving);
        Assert.Contains("识别其用途", prompt);
        Assert.Contains("即使没有子结构，也可对用途给出 High", prompt);
        Assert.Contains("不能否定路径本身的用途", prompt);
        Assert.Contains("父项不得附带子项未读", prompt);
        Assert.Contains("读取问题由扫描报告呈现", prompt);
        Assert.Contains("不得编造未提供的内容", prompt);
        Assert.Contains("删除风险与用途置信度必须独立判断", prompt);
        Assert.DoesNotContain("confidenceLevel 为 Low", prompt);
    }

    [Fact]
    public void Scope_warning_round_trips_without_changing_purpose_confidence()
    {
        var result = Parse(new() { ["confidenceLevel"] = "High", ["confidence"] = .9 }) with
        { IsScopeLimited = true, ScopeWarning = AiScopeWarning.DescendantReadIncomplete | AiScopeWarning.StructureSummary };
        var restored = JsonSerializer.Deserialize<AiAnalysisResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal(AiConfidenceLevel.High, restored.EffectiveConfidenceLevel);
        Assert.Equal(result.ScopeWarning, restored.ScopeWarning);
        Assert.True(restored.IsScopeLimited);
        Assert.False(restored.LooksLikeFallback());
    }

    private static void AssertContract(string prompt)
    {
        Assert.Contains("\"confidenceLevel\":\"High|Medium|Low\"", prompt);
        Assert.Contains("cache|runtime|application|configuration|logs|downloads|user-data|unknown", prompt);
        Assert.Contains("suggestedAction 只能返回 RecycleCandidate", prompt);
        Assert.Contains("或 DoNotDelete", prompt);
        Assert.Contains("最多 12 个字符", prompt);
        Assert.Contains("30 个字符以内", prompt);
        Assert.Contains("shortPurpose", prompt);
        Assert.Contains("任意文件或文件夹", prompt);
        Assert.Contains("不是经过验证的准确率", prompt);
        Assert.Contains("待分析数据", prompt);
        Assert.Contains("保护规则始终优先", prompt);
    }

    private static AiAnalysisResult Parse(Dictionary<string, object?> fields)
    {
        fields["path"] = @"E:\fixture\arbitrary.file";
        fields["riskLevel"] = "Review";
        fields["suggestedAction"] = "Review";
        fields["reason"] = "合成置信度测试";
        var content = JsonSerializer.Serialize(new { items = new[] { fields } });
        var parser = typeof(AiAnalysisResult).Assembly.GetType("DiskLens.Core.Ai.AiResponseParser", true)!;
        var method = parser.GetMethod("ParseResults", BindingFlags.Public | BindingFlags.Static)!;
        return Assert.Single((IReadOnlyList<AiAnalysisResult>)method.Invoke(null, [content])!);
    }
}

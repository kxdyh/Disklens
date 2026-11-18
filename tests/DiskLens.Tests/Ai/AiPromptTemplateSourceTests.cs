using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskLens.Tests.Ai;

public sealed class AiPromptTemplateSourceTests
{
    [Fact]
    public void Prompt_factory_should_load_the_versioned_embedded_template_without_external_file_overrides()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var promptFactoryPath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "AiPromptFactory.cs");
        var templatePath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "DefaultAiPrompt.md");

        var source = File.ReadAllText(promptFactoryPath);

        Assert.True(File.Exists(templatePath), "Expected AI prompt template file to exist.");
        Assert.Contains("GetManifestResourceStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppContext.BaseDirectory", source, StringComparison.Ordinal);
        Assert.Contains("CreateSystemPrompt(bool useTokenSavingMode = false)", source, StringComparison.Ordinal);
        var expected = DiskLens.Core.Ai.AiPromptFactory.ApplyOutputContract(File.ReadAllText(templatePath).Trim());
        Assert.Equal(expected, DiskLens.Core.Ai.AiPromptFactory.CreateSystemPrompt());
        Assert.Contains("DiskLens.Core.DefaultAiPrompt.md",
            typeof(DiskLens.Core.Ai.AiPromptFactory).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void Prompt_factory_should_use_compact_system_prompt_in_token_saving_mode()
    {
        var normalPrompt = DiskLens.Core.Ai.AiPromptFactory.CreateSystemPrompt();
        var compactPrompt = DiskLens.Core.Ai.AiPromptFactory.CreateSystemPrompt(useTokenSavingMode: true);

        Assert.Contains("节省模式", compactPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Few-shot", compactPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(compactPrompt.Length < normalPrompt.Length);
        Assert.NotEqual(
            DiskLens.Core.Ai.AiPromptFactory.CreateSystemPromptFingerprint(),
            DiskLens.Core.Ai.AiPromptFactory.CreateSystemPromptFingerprint(useTokenSavingMode: true));
    }

    [Fact]
    public void Prompt_template_should_keep_system_protection_authoritative()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var templatePath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "DefaultAiPrompt.md");
        var template = File.ReadAllText(templatePath);

        Assert.Contains("isProtectedLocation=true", template, StringComparison.Ordinal);
        Assert.Contains("riskLevel=Protected", template, StringComparison.Ordinal);
        Assert.Contains("suggestedAction=DoNotDelete", template, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prompt_should_require_bilingual_descriptions_in_both_modes(bool useTokenSavingMode)
    {
        var systemPrompt = DiskLens.Core.Ai.AiPromptFactory.CreateSystemPrompt(useTokenSavingMode);

        Assert.Contains("shortPurpose 为简体中文", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("reason 为简体中文", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("shortPurposeEn 为同义英文", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("reasonEn 为同义英文", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_template_example_returns_each_candidate_path_in_input_order()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var templatePath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "DefaultAiPrompt.md");
        var template = File.ReadAllText(templatePath);

        var input = Regex.Match(template, @"输入：\s*```json\s*(.*?)```", RegexOptions.Singleline);
        Assert.True(input.Success);
        using var document = JsonDocument.Parse(input.Groups[1].Value);
        var paths = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("Path").GetString()).ToArray();
        var outputs = ReadExampleOutputs();

        Assert.Equal(document.RootElement.GetProperty("expectedItemCount").GetInt32(), paths.Length);
        Assert.Equal(paths, outputs.Select(item => item.GetProperty("path").GetString()).ToArray());
    }

    [Theory]
    [InlineData(@"C:\Users\example\AppData\Local\Temp", "RecycleCandidate")]
    [InlineData(@"D:\Project\artifacts", "DoNotDelete")]
    public void Prompt_examples_should_distinguish_rebuildable_data_from_runtime_and_unknown_data(
        string path, string expectedAction)
    {
        var examples = ReadExampleOutputs()
            .Where(item => item.GetProperty("path").GetString() == path).ToArray();

        Assert.NotEmpty(examples);
        Assert.All(examples, item =>
            Assert.Equal(expectedAction, item.GetProperty("suggestedAction").GetString()));
    }

    [Fact]
    public void Prompt_example_purpose_fields_should_fit_tiles_and_details_without_action_advice()
    {
        var examples = ReadExampleOutputs();
        Assert.NotEmpty(examples);
        foreach (var example in examples)
        {
            var shortPurpose = example.GetProperty("shortPurpose").GetString()!;
            var reason = example.GetProperty("reason").GetString()!;
            Assert.InRange(shortPurpose.Length, 1, 12);
            Assert.InRange(reason.Length, 1, 30);
            Assert.InRange(example.GetProperty("shortPurposeEn").GetString()!.Length, 1, 80);
            Assert.InRange(example.GetProperty("reasonEn").GetString()!.Length, 1, 240);
            foreach (var advice in new[] { "建议", "删除", "清理", "保留", "确认", "关闭" })
            {
                Assert.DoesNotContain(advice, shortPurpose, StringComparison.Ordinal);
                Assert.DoesNotContain(advice, reason, StringComparison.Ordinal);
            }
            Assert.Contains(example.GetProperty("suggestedAction").GetString()!,
                new[] { "RecycleCandidate", "DoNotDelete" });
        }
    }

    private static JsonElement[] ReadExampleOutputs()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var templatePath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "DefaultAiPrompt.md");
        var template = File.ReadAllText(templatePath);
        var examples = new List<JsonElement>();
        foreach (Match match in Regex.Matches(template, @"(?:输出：|## 示例输出)\s*```json\s*(.*?)```", RegexOptions.Singleline))
        {
            using var document = JsonDocument.Parse(match.Groups[1].Value);
            if (document.RootElement.TryGetProperty("items", out var items))
                examples.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            else
                examples.Add(document.RootElement.Clone());
        }
        return examples.ToArray();
    }
}

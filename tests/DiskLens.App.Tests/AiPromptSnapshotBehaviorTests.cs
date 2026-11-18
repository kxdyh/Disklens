using System.IO;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class AiPromptSnapshotBehaviorTests : IDisposable
{
    private readonly object? _previousBaseDirectory;
    private readonly string _directory;
    private string TemplatePath => Path.Combine(_directory, "AI分析提示词模板.md");

    public AiPromptSnapshotBehaviorTests()
    {
        var artifacts = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Prompt.Tests");
        _directory = Path.Combine(artifacts, "prompt-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _previousBaseDirectory = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", _directory + Path.DirectorySeparatorChar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_external_templates_are_ignored_before_between_and_after_analysis_batches(bool tokenSaving)
    {
        const string initial = "Synthetic initial prompt: conservative Review only.";
        const string edited = "Synthetic edited prompt: a different user template.";
        var builtInPrompt = AiPromptFactory.CreateSystemPrompt(tokenSaving);
        await File.WriteAllTextAsync(TemplatePath, initial);
        var nested = Path.Combine(_directory, "AI提示词模板化与结果面板工作区");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "AI分析提示词模板.md"), initial);
        Assert.Equal(builtInPrompt, AiPromptFactory.CreateSystemPrompt(tokenSaving));
        var store = new MemoryStore();
        var provider = new MutatingProvider(TemplatePath, edited);
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(), store);
        var root = Root();
        var options = new ScanSessionOptions(root.Path, AnalysisThresholdBytes: 1, MaxAiBatchSize: 1,
            UseTokenSavingMode: tokenSaving);
        var providerOptions = new AiProviderOptions(AiProviderKind.OpenAiCompatible,
            "https://example.invalid", "synthetic-model", "synthetic-key-never-sent");

        await service.AnalyzeWithReportAsync(root, options, providerOptions);

        Assert.Equal(2, provider.Prompts.Count);
        Assert.All(provider.Prompts, prompt => Assert.Equal(builtInPrompt, prompt));
        var fingerprint = AiPromptFactory.CreateSystemPromptFingerprint(builtInPrompt);
        Assert.Equal(2, store.Entries.Count);
        Assert.All(store.Entries.Values, entry => Assert.Equal(fingerprint, entry.PromptFingerprint));

        // A later click still uses the built-in instructions and may reuse that context.
        await service.AnalyzeWithReportAsync(root, options, providerOptions);
        Assert.Equal(2, provider.Prompts.Count);
        Assert.Equal(builtInPrompt, AiPromptFactory.CreateSystemPrompt(tokenSaving));
        var provenance = SharedKnowledgeRuntime.CreateProvenance(providerOptions, tokenSaving);
        Assert.True(provenance.DefaultPrompt);
        Assert.Equal(fingerprint, provenance.PromptFingerprint);

        // Previous custom-template results remain incompatible with the built-in context.
        store.ReplacePromptFingerprint(AiPromptFactory.CreateSystemPromptFingerprint(
            AiPromptFactory.ApplyOutputContract(initial)));
        await service.AnalyzeWithReportAsync(root, options, providerOptions);
        Assert.Equal(4, provider.Prompts.Count);
        Assert.All(provider.Prompts, prompt => Assert.Equal(builtInPrompt, prompt));
        Assert.All(store.Entries.Values, entry => Assert.Equal(fingerprint, entry.PromptFingerprint));
    }

    private FileSystemNode Root()
    {
        var files = Enumerable.Range(1, 2).Select(index => new FileSystemNode(
            Path.Combine(_directory, $"{index}.bin"), $"{index}.bin", ".bin", 1024,
            FileAttributes.Normal, null, null, false, false, [], [])).ToArray();
        return new(Path.GetPathRoot(_directory)!, "volume-fixture", "", 2048, FileAttributes.Directory,
            null, null, true, false, [], files);
    }

    private sealed class MutatingProvider(string templatePath, string edited) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public List<string> Prompts { get; } = [];
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(batch.RequestContext!.SystemPrompt);
            if (Prompts.Count == 1) await File.WriteAllTextAsync(templatePath, edited, cancellationToken);
            batch.RequestContext.ReportUsage(new(100, 50));
            return batch.Records.Select(record => new AiAnalysisResult(record.Path,
                RiskLevel.Review, SuggestedAction.Review, "合成行为测试", 0.5)).ToArray();
        }
    }

    private sealed class MemoryStore : IAiAnalysisMemoryStore
    {
        public IReadOnlyDictionary<string, AiAnalysisMemoryEntry> Entries { get; private set; }
            = new Dictionary<string, AiAnalysisMemoryEntry>();
        public void ReplacePromptFingerprint(string fingerprint) => Entries = Entries.ToDictionary(
            pair => pair.Key, pair => pair.Value with { PromptFingerprint = fingerprint }, StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult(Entries);
        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            Entries = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", _previousBaseDirectory);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

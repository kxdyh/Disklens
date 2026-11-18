using System.IO;
using System.Text.Json;
using DiskLens.App.Services;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class AnalysisMemoryAdviceMonotonicityTests : IDisposable
{
    private const string RootPath = @"E:\MemoryAdvice";
    private readonly string? _previousDataPath = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
    private readonly string _dataPath;
    private readonly DateTimeOffset _analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-20);

    public AnalysisMemoryAdviceMonotonicityTests()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Memory.Tests");
        _dataPath = Path.Combine(artifactRoot, "memory-advice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _dataPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_old_recycle_advice_cannot_undo_the_same_original_analyses_persisted_veto(bool newerSnapshot)
    {
        var original = Entry();
        var veto = Veto(original);
        var ownerStore = new AnalysisMemoryStore();
        var followerStore = new AnalysisMemoryStore();
        await ownerStore.SaveEntriesAsync(RootPath, [veto]);
        var stale = original with
        {
            SnapshotGeneration = newerSnapshot ? 2 : 1,
            Result = original.Result with { Origin = AiAnalysisResultOrigin.CacheReused },
        };

        // Explicitly await the veto before the stale caller writes. This models
        // the losing order deterministically without relying on scheduling sleeps.
        await followerStore.SaveEntriesAsync(RootPath, [stale]);

        var saved = Assert.Single(await new AnalysisMemoryStore().LoadEntriesAsync(RootPath)).Value;
        Assert.Equal(SuggestedAction.DoNotDelete, saved.Result.SuggestedAction);
        Assert.Equal(RiskLevel.High, saved.Result.RiskLevel);
        Assert.Equal(original.Result.OriginalAnalysisId, saved.Result.OriginalAnalysisId);
        Assert.Equal(original.Result.OriginalAnalyzedAt, saved.Result.OriginalAnalyzedAt);
        Assert.Equal(original.AnalyzedAt, saved.AnalyzedAt);
        Assert.Equal(stale.SnapshotGeneration, saved.SnapshotGeneration);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, saved.Result.Origin);
        Assert.Equal(original.Result.Reason, saved.Result.Reason);
    }

    [Fact]
    public async Task Later_ancestor_veto_updates_an_existing_recycle_checkpoint_without_renewing_identity_or_time()
    {
        var original = Entry();
        var store = new AnalysisMemoryStore();
        await store.SaveEntriesAsync(RootPath, [original]);

        await new AnalysisMemoryStore().SaveEntriesAsync(RootPath, [Veto(original)]);

        Assert.Equal(Veto(original), Assert.Single(await store.LoadEntriesAsync(RootPath)).Value);
    }

    [Fact]
    public async Task A_shared_nonempty_identity_still_protects_older_records_missing_the_original_timestamp()
    {
        var original = Entry();
        original = original with { Result = original.Result with { OriginalAnalyzedAt = null } };
        var store = new AnalysisMemoryStore();
        await store.SaveEntriesAsync(RootPath, [Veto(original)]);

        await store.SaveEntriesAsync(RootPath, [original]);

        var saved = Assert.Single(await store.LoadEntriesAsync(RootPath)).Value;
        Assert.Equal(Veto(original), saved);
        Assert.Null(saved.Result.OriginalAnalyzedAt);
    }

    [Fact]
    public async Task A_genuinely_new_analysis_identity_can_replace_an_older_veto()
    {
        var original = Entry();
        var laterTime = original.AnalyzedAt.AddMinutes(1);
        var fresh = original with
        {
            AnalyzedAt = laterTime,
            Result = original.Result with
            {
                OriginalAnalysisId = "new-analysis-id", OriginalAnalyzedAt = laterTime,
            },
        };
        var store = new AnalysisMemoryStore();
        await store.SaveEntriesAsync(RootPath, [Veto(original)]);

        await store.SaveEntriesAsync(RootPath, [fresh]);

        Assert.Equal(fresh, Assert.Single(await store.LoadEntriesAsync(RootPath)).Value);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("directory")]
    [InlineData("provider")]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("prompt")]
    [InlineData("saving-mode")]
    [InlineData("threshold")]
    [InlineData("reasoning")]
    [InlineData("temperature")]
    [InlineData("evidence-scope")]
    [InlineData("original-time")]
    public async Task An_unrelated_context_or_signature_must_not_inherit_a_veto_just_from_a_reused_id(string difference)
    {
        var original = Entry();
        var unrelated = difference switch
        {
            "signature" => original with { Signature = "different-signature" },
            "directory" => original with { IsDirectory = false },
            "provider" => original with { ProviderKind = AiProviderKind.Ollama },
            "endpoint" => original with { Endpoint = "https://different.invalid" },
            "model" => original with { Model = "different-model" },
            "prompt" => original with { PromptFingerprint = "different-prompt" },
            "saving-mode" => original with { UseTokenSavingMode = false },
            "threshold" => original with { AnalysisThresholdBytes = 2048 },
            "reasoning" => original with { ReasoningEffort = "high" },
            "temperature" => original with { Temperature = .2 },
            "evidence-scope" => original with { Result = original.Result with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.DirectoryStructure } },
            "original-time" => original with { Result = original.Result with { OriginalAnalyzedAt = original.Result.OriginalAnalyzedAt!.Value.AddMinutes(1) } },
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        unrelated = unrelated with { SnapshotGeneration = 2 };
        var store = new AnalysisMemoryStore();
        await store.SaveEntriesAsync(RootPath, [Veto(original)]);

        await store.SaveEntriesAsync(RootPath, [unrelated]);

        Assert.Equal(unrelated, Assert.Single(await store.LoadEntriesAsync(RootPath)).Value);
    }

    [Fact]
    public async Task Different_directory_structure_chunk_context_must_not_inherit_the_older_veto()
    {
        var original = Entry();
        original = original with
        {
            UseTokenSavingMode = false,
            Result = original.Result with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.DirectoryStructure },
        };
        var unrelated = original with { MaxAiChunkTokens = 256, SnapshotGeneration = 2 };
        var store = new AnalysisMemoryStore();
        await store.SaveEntriesAsync(RootPath, [Veto(original)]);

        await store.SaveEntriesAsync(RootPath, [unrelated]);

        Assert.Equal(unrelated, Assert.Single(await store.LoadEntriesAsync(RootPath)).Value);
    }

    [Fact]
    public async Task Duplicate_saved_records_also_keep_the_same_identity_veto_when_loading()
    {
        var original = Entry();
        await File.WriteAllBytesAsync(Path.Combine(_dataPath, "analysis-memory.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 2, Epoch = 0,
            Roots = new[] { new { RootPath, Generation = 1, Entries = new[] { original, Veto(original) } } },
        }));

        var saved = Assert.Single(await new AnalysisMemoryStore().LoadEntriesAsync(RootPath)).Value;

        Assert.Equal(Veto(original), saved);
    }

    private AiAnalysisMemoryEntry Entry()
    {
        const string path = RootPath + @"\cache";
        return new AiAnalysisMemoryEntry(path, true, "synthetic-signature",
            new AiAnalysisResult(path, RiskLevel.Low, SuggestedAction.RecycleCandidate, "可重建的缓存数据", .95)
            {
                Origin = AiAnalysisResultOrigin.AiAnalyzed, ConfidenceLevel = AiConfidenceLevel.High,
                PurposeCategory = "cache", AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
                OriginalAnalysisId = "original-analysis-id", OriginalAnalyzedAt = _analyzedAt,
            }, _analyzedAt, AiProviderKind.OpenAiCompatible, "https://example.invalid", "synthetic-model",
            "synthetic-prompt", true, 1024, 2400, "medium", .1, 1);
    }

    private static AiAnalysisMemoryEntry Veto(AiAnalysisMemoryEntry original) => original with
    {
        Result = original.Result with { SuggestedAction = SuggestedAction.DoNotDelete, RiskLevel = RiskLevel.High },
    };

    public void Dispose() => Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataPath);
}

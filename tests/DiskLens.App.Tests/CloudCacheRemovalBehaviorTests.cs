using System.IO;
using System.Reflection;
using System.Text.Json;
using DiskLens.App.Services;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class CloudCacheRemovalBehaviorTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
        ?? Path.GetTempPath(), "cloud-cache-removal-" + Guid.NewGuid().ToString("N"));

    public CloudCacheRemovalBehaviorTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _directory);
    }

    [Fact]
    public async Task Conditional_removal_preserves_other_paths_and_partitions()
    {
        var store = new AnalysisMemoryStore();
        var selected = Entry("item", "original");
        var sibling = Entry("sibling", "sibling-original");
        await store.SaveEntriesAsync("root", [selected, sibling]);
        await store.SaveEntriesAsync("other-root", [selected]);
        var captured = (await store.LoadEntriesAsync("root"))[selected.Path];

        Assert.Equal(1, await Remove(store, "root", [captured]));

        Assert.Equal(sibling, Assert.Single(await store.LoadEntriesAsync("root")).Value);
        Assert.Equal(selected, Assert.Single(await store.LoadEntriesAsync("other-root")).Value);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("result")]
    [InlineData("context")]
    [InlineData("intent")]
    public async Task Conditional_removal_never_deletes_an_entry_changed_after_capture(string change)
    {
        var store = new AnalysisMemoryStore();
        var original = Entry("item", "original");
        await store.SaveEntriesAsync("root", [original]);
        var captured = (await store.LoadEntriesAsync("root"))[original.Path];
        var revised = change switch
        {
            "identity" => original with { Result = original.Result with { OriginalAnalysisId = "new-original" } },
            "result" => original with { Result = original.Result with { Reason = "New independent explanation" } },
            "context" => original with { Model = "new-model" },
            _ => original with { SharedContributionPending = true, SharedContributionCompleted = false }
        };
        revised = revised with { SnapshotGeneration = original.SnapshotGeneration + 1 };
        await store.SaveEntriesAsync("root", [revised]);

        Assert.Equal(0, await Remove(store, "root", [captured]));
        Assert.Equal(revised, Assert.Single(await store.LoadEntriesAsync("root")).Value);
    }

    [Fact]
    public async Task Removed_original_cannot_be_resurrected_by_a_late_checkpoint_or_a_restart()
    {
        var store = new AnalysisMemoryStore();
        var original = Entry("item", "original");
        await store.SaveEntriesAsync("root", [original]);
        var captured = (await store.LoadEntriesAsync("root"))[original.Path];
        Assert.Equal(1, await Remove(store, "root", [captured]));

        var restarted = new AnalysisMemoryStore();
        await restarted.SaveEntriesAsync("root", [original with { SnapshotGeneration = 100,
            Result = original.Result with { Origin = AiAnalysisResultOrigin.CacheReused } }]);
        Assert.Empty(await restarted.LoadEntriesAsync("root"));

        var newAnalysis = original with { SnapshotGeneration = 101,
            Result = original.Result with { OriginalAnalysisId = "fresh-analysis" } };
        await restarted.SaveEntriesAsync("root", [newAnalysis]);
        Assert.Equal(newAnalysis, Assert.Single(await restarted.LoadEntriesAsync("root")).Value);
    }

    [Fact]
    public async Task Removal_does_not_invalidate_an_inflight_session_for_an_unrelated_path()
    {
        var store = new AnalysisMemoryStore();
        var session = await store.BeginSessionAsync("root", 10);
        var original = Entry("item", "original") with { SnapshotGeneration = 10 };
        await store.SaveSessionEntriesAsync("root", [original], session);
        Assert.Equal(1, await Remove(store, "root", [original]));
        var sibling = Entry("sibling", "sibling-original") with { SnapshotGeneration = 10 };
        await store.SaveSessionEntriesAsync("root", [original, sibling], session);

        Assert.Equal(sibling, Assert.Single(await store.LoadEntriesAsync("root")).Value);
    }

    [Fact]
    public async Task Cancellation_and_legacy_missing_original_identity_preserve_the_file()
    {
        var store = new AnalysisMemoryStore();
        var original = Entry("item", "original");
        await store.SaveEntriesAsync("root", [original]);
        var path = Path.Combine(_directory, "analysis-memory.json");
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Remove(store, "root", [original], cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var legacy = original with { Path = original.Path + "-legacy", Result = original.Result with
            { Path = original.Path + "-legacy", OriginalAnalysisId = null, OriginalAnalyzedAt = null } };
        await store.SaveEntriesAsync("root", [legacy]);
        Assert.Equal(0, await Remove(store, "root", [legacy]));
        Assert.Equal(2, (await store.LoadEntriesAsync("root")).Count);
    }

    private AiAnalysisMemoryEntry Entry(string name, string originalId)
    {
        var path = Path.Combine(_directory, name);
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        return new(path, true, "unchanged-signature", new(path, RiskLevel.High, SuggestedAction.DoNotDelete,
            "Personal original explanation", .9)
        {
            ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "application",
            AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose,
            OriginalAnalysisId = originalId, OriginalAnalyzedAt = at,
        }, at) { SnapshotGeneration = 1, SharedContributionCompleted = true };
    }

    [Fact]
    public async Task Tombstones_are_bounded_and_expired_originals_do_not_block_a_new_valid_analysis()
    {
        var store = new AnalysisMemoryStore();
        var original = Entry("item", "original");
        var tombstones = Enumerable.Range(0, AnalysisMemoryStore.MaximumEntries).Select(index => new
        {
            Path = "synthetic-removed-" + index, OriginalAnalysisId = "removed-id-" + index,
            OriginalAnalyzedAt = original.AnalyzedAt, RemovedAt = DateTimeOffset.UtcNow,
        }).ToArray();
        var file = Path.Combine(_directory, "analysis-memory.json");
        await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Epoch = 0,
            Roots = new[] { new { RootPath = "root", Entries = new[] { original }, Generation = 1, RemovedAnalyses = tombstones } } }));
        var before = await File.ReadAllBytesAsync(file);
        Assert.Equal(0, await Remove(store, "root", [original]));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(original, Assert.Single(await store.LoadEntriesAsync("root")).Value);

        await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Epoch = 0,
            Roots = new[] { new { RootPath = "root", Entries = new[] { original }, Generation = 1,
                RemovedAnalyses = new[] { new { original.Path, OriginalAnalysisId = "original",
                    OriginalAnalyzedAt = DateTimeOffset.UtcNow.AddDays(-31), RemovedAt = DateTimeOffset.UtcNow } } } } }));
        Assert.Equal(1, await Remove(store, "root", [original]));
        Assert.Empty(await store.LoadEntriesAsync("root"));
    }

    [Fact]
    public async Task Damaged_cache_is_not_overwritten_by_an_optional_removal_request()
    {
        var file = Path.Combine(_directory, "analysis-memory.json");
        var bytes = "{ damaged JSON preserved for recovery"u8.ToArray();
        await File.WriteAllBytesAsync(file, bytes);
        Assert.Equal(0, await Remove(new AnalysisMemoryStore(), "root", [Entry("item", "original")]));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
    }

    private static Task<int> Remove(AnalysisMemoryStore store, string root, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(AnalysisMemoryStore).GetMethod("RemoveEntriesIfUnchangedAsync",
            [typeof(string), typeof(IReadOnlyCollection<AiAnalysisMemoryEntry>), typeof(CancellationToken)]);
        Assert.NotNull(method);
        return (Task<int>)method.Invoke(store, [root, entries, cancellationToken])!;
    }

    public void Dispose() => Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previous);
}

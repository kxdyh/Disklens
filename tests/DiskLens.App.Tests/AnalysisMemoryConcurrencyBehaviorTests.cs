using System.IO;
using DiskLens.App.Services;
using DiskLens.Core.Models;
using DiskLens.Core.Ai;
using System.Text.Json;
using DiskLens.Core.Policies;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class AnalysisMemoryConcurrencyBehaviorTests : IDisposable
{
    private readonly string? _previousDataPath = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
    private readonly string _dataPath;
    private static readonly DateTimeOffset RecentBaseTime = DateTimeOffset.UtcNow.AddDays(-2);

    public AnalysisMemoryConcurrencyBehaviorTests()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Memory.Tests");
        _dataPath = Path.Combine(artifactRoot, "memory-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _dataPath);
    }

    [Fact]
    public async Task Concurrent_instances_merge_disjoint_results_for_the_same_root()
    {
        var root = Path.Combine(_dataPath, "same-root");
        var entries = Enumerable.Range(0, 12)
            .Select(index => Entry(root, $"item-{index}", DateTimeOffset.UnixEpoch.AddMinutes(index), $"result-{index}"))
            .ToArray();

        await Task.WhenAll(entries.Select(entry => new AnalysisMemoryStore().SaveEntriesAsync(root, [entry])));

        var saved = await new AnalysisMemoryStore().LoadEntriesAsync(root);
        Assert.Equal(entries.Length, saved.Count);
        Assert.All(entries, entry => Assert.Equal(entry, saved[entry.Path]));
        Assert.Empty(Directory.EnumerateFiles(_dataPath, "*.tmp"));
    }

    [Fact]
    public async Task Original_short_and_detailed_purpose_survive_disk_cache_without_rewriting()
    {
        var root = Path.Combine(_dataPath, "dual-purpose");
        var entry = Entry(root, "Users", DateTimeOffset.UnixEpoch, "保存各账户的个人文件与应用数据");
        entry = entry with
        {
            Result = entry.Result with { ShortPurpose = "用户资料与应用数据" }
        };
        await new AnalysisMemoryStore().SaveEntriesAsync(root, [entry]);

        var restored = Assert.Single(await new AnalysisMemoryStore().LoadEntriesAsync(root)).Value;
        Assert.Equal(entry.Result.ShortPurpose, restored.Result.ShortPurpose);
        Assert.Equal(entry.Result.Reason, restored.Result.Reason);
        Assert.Equal(entry.AnalyzedAt, restored.AnalyzedAt);

        // Older records keep their detailed explanation without inventing a tile purpose.
        var legacy = entry with { Path = Path.Combine(root, "legacy"),
            Result = entry.Result with { Path = Path.Combine(root, "legacy"), ShortPurpose = "" } };
        await new AnalysisMemoryStore().SaveEntriesAsync(root, [legacy]);
        var restoredLegacy = (await new AnalysisMemoryStore().LoadEntriesAsync(root))[legacy.Path];
        Assert.Empty(restoredLegacy.Result.ShortPurpose);
        Assert.Equal(entry.Result.Reason, restoredLegacy.Result.Reason);
    }

    [Fact]
    public async Task Late_older_partial_and_empty_snapshots_cannot_erase_newer_complete_results()
    {
        var root = Path.Combine(_dataPath, "same-root");
        var store = new AnalysisMemoryStore();
        var newest = Entry(root, "shared", DateTimeOffset.UnixEpoch.AddHours(2), "new-complete-result");
        var sibling = Entry(root, "other-file", DateTimeOffset.UnixEpoch.AddHours(2), "sibling-result");
        await store.SaveEntriesAsync(root, [newest, sibling]);

        // Models a canceled task persisting its earlier, smaller local cache.
        await new AnalysisMemoryStore().SaveEntriesAsync(root,
            [Entry(root, "shared", DateTimeOffset.UnixEpoch, "older-partial-result")]);
        await store.SaveEntriesAsync(root, []);

        var saved = await store.LoadEntriesAsync(root);
        Assert.Equal(2, saved.Count);
        Assert.Equal(newest, saved[newest.Path]);
        Assert.Equal(sibling, saved[sibling.Path]);
    }

    [Fact]
    public async Task Distinct_roots_and_newer_updates_survive_interleaved_saves()
    {
        var firstRoot = Path.Combine(_dataPath, "first-root");
        var secondRoot = Path.Combine(_dataPath, "second-root");
        var first = Entry(firstRoot, "file", DateTimeOffset.UnixEpoch, "first");
        var second = Entry(secondRoot, "file", DateTimeOffset.UnixEpoch, "second");
        await Task.WhenAll(new AnalysisMemoryStore().SaveEntriesAsync(firstRoot, [first]),
            new AnalysisMemoryStore().SaveEntriesAsync(secondRoot, [second]));
        var revised = Entry(firstRoot, "file", DateTimeOffset.UnixEpoch.AddHours(1), "revised");
        await new AnalysisMemoryStore().SaveEntriesAsync(firstRoot, [revised]);

        Assert.Equal(revised, Assert.Single(await new AnalysisMemoryStore().LoadEntriesAsync(firstRoot)).Value);
        Assert.Equal(second, Assert.Single(await new AnalysisMemoryStore().LoadEntriesAsync(secondRoot)).Value);
    }

    [Fact]
    public async Task Cancellation_preserves_existing_cache_and_does_not_hold_the_storage_gate()
    {
        var root = Path.Combine(_dataPath, "same-root");
        var store = new AnalysisMemoryStore();
        var savedEntry = Entry(root, "file", DateTimeOffset.UnixEpoch, "saved");
        await store.SaveEntriesAsync(root, [savedEntry]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveEntriesAsync(root,
            [Entry(root, "file", DateTimeOffset.UnixEpoch.AddDays(1), "canceled")], cancellation.Token));

        Assert.Equal(savedEntry, Assert.Single(await store.LoadEntriesAsync(root)).Value);
        var sibling = Entry(root, "next", DateTimeOffset.UnixEpoch.AddDays(1), "next");
        await store.SaveEntriesAsync(root, [sibling]);
        Assert.Equal(2, (await store.LoadEntriesAsync(root)).Count);
    }

    [Fact]
    public async Task New_snapshot_and_clear_epoch_reject_late_checkpoints_without_renewing_original_time()
    {
        var store = new AnalysisMemoryStore(); var root = Path.Combine(_dataPath, "generation");
        var older = await store.BeginSessionAsync(root, 1);
        var newer = await store.BeginSessionAsync(root, 2);
        var entry = Entry(root, "item", DateTimeOffset.UnixEpoch, "new") with { SnapshotGeneration = 2 };
        await store.SaveSessionEntriesAsync(root, [entry], newer);
        await store.SaveSessionEntriesAsync(root, [entry with { Signature = "old", AnalyzedAt = DateTimeOffset.UtcNow }], older);
        Assert.Equal(entry, Assert.Single(await store.LoadEntriesAsync(root)).Value);
        await store.ClearAsync(root);
        await store.SaveSessionEntriesAsync(root, [entry], newer);
        Assert.Empty(await store.LoadEntriesAsync(root));
        var afterClear = await store.BeginSessionAsync(root, 3);
        Assert.True(afterClear.Epoch > newer.Epoch);
        await store.SaveSessionEntriesAsync(root, [entry], afterClear);
        Assert.Equal(entry.AnalyzedAt, Assert.Single(await store.LoadEntriesAsync(root)).Value.AnalyzedAt);
    }

    [Fact]
    public async Task Legacy_null_values_and_expiration_are_pruned_without_throwing()
    {
        var root = Path.Combine(_dataPath, "legacy"); var path = Path.Combine(_dataPath, "analysis-memory.json");
        var valid = Entry(root, "valid", DateTimeOffset.UnixEpoch, "valid");
        var expired = valid with { Path = root + "expired", AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-31), Result = valid.Result with { Path = root + "expired" } };
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1,
            Roots = new object?[] { null, new { RootPath = root, Entries = new AiAnalysisMemoryEntry?[] { null, valid, expired,
                valid with { Path = root + "null", Result = null! } } }, new { RootPath = "null-collection", Entries = (object?)null } } }));
        var store = new AnalysisMemoryStore();
        Assert.Equal(valid, Assert.Single(await store.LoadEntriesAsync(root)).Value);
        await store.PruneAsync();
        Assert.DoesNotContain("expired", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, "{\"Version\":1,\"Roots\":null}");
        Assert.Empty(await store.LoadEntriesAsync(root));
    }

    [Fact]
    public async Task Canceling_a_singleflight_waiter_does_not_prevent_the_owner_persisting()
    {
        var rootPath = Path.GetPathRoot(_dataPath)!;
        var file = new FileSystemNode(Path.Combine(_dataPath, "flight", "item.bin"), "item.bin", ".bin", 1024,
            FileAttributes.Normal, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false, [], [], 1);
        var root = new FileSystemNode(rootPath, "root", "", 1024, FileAttributes.Directory, null, null, true, false, [], [file], 1);
        var store = new AnalysisMemoryStore(); var provider = new GatedProvider();
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), store);
        var options = new ScanSessionOptions(rootPath, AnalysisThresholdBytes: 1);
        var providerOptions = new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://cache.invalid", "test", "synthetic");
        var owner = service.AnalyzeAsync(root, options, providerOptions);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var follower = service.AnalyzeAsync(root, options, providerOptions, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        provider.Release.TrySetResult();
        var result = Assert.Single(await owner);
        Assert.Equal(result.OriginalAnalysisId, Assert.Single(await store.LoadEntriesAsync(rootPath)).Value.Result.OriginalAnalysisId);
        Assert.Equal(1, provider.Calls);
    }

    private sealed class GatedProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return batch.Records.Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "synthetic", .8)).ToArray();
        }
    }

    [Fact]
    public async Task Global_limits_keep_newest_twenty_thousand_and_bound_actual_utf8_file_size()
    {
        var root = Path.Combine(_dataPath, "capacity"); var store = new AnalysisMemoryStore();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var entries = Enumerable.Range(0, AnalysisMemoryStore.MaximumEntries + 5)
            .Select(index => Entry(root, "item-" + index, DateTimeOffset.UnixEpoch, "result")
                with { AnalyzedAt = now.AddTicks(index) }).ToArray();
        await store.SaveEntriesAsync(root, entries);
        var loaded = await store.LoadEntriesAsync(root);
        Assert.Equal(AnalysisMemoryStore.MaximumEntries, loaded.Count);
        Assert.DoesNotContain(entries[0].Path, loaded.Keys);
        await store.ClearAsync();
        var oversized = Entry(root, "too-large", DateTimeOffset.UnixEpoch, new string('a', AnalysisMemoryStore.MaximumBytes + 1));
        await store.SaveEntriesAsync(root, [oversized]);
        Assert.Empty(await store.LoadEntriesAsync(root));
        Assert.True(new FileInfo(Path.Combine(_dataPath, "analysis-memory.json")).Length <= AnalysisMemoryStore.MaximumBytes);
        Assert.Empty(Directory.EnumerateFiles(_dataPath, "*.tmp"));
    }

    private static AiAnalysisMemoryEntry Entry(string root, string name, DateTimeOffset time, string summary)
    {
        var path = Path.Combine(root, name);
        return new AiAnalysisMemoryEntry(path, false, "synthetic-signature",
            new AiAnalysisResult(path, RiskLevel.Low, SuggestedAction.Keep, summary, 0.9), RecentBaseTime + (time - DateTimeOffset.UnixEpoch));
    }

    public void Dispose() => Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataPath);
}

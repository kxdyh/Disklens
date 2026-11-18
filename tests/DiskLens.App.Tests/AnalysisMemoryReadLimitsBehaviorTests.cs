using System.IO;
using System.Text.Json;
using DiskLens.App.Services;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class AnalysisMemoryReadLimitsBehaviorTests : IDisposable
{
    private readonly string? _previousDataPath = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
    private readonly string _dataPath;
    private string StoragePath => Path.Combine(_dataPath, "analysis-memory.json");

    public AnalysisMemoryReadLimitsBehaviorTests()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(FindWorkspace(), "artifacts", "tests");
        _dataPath = Path.Combine(artifactRoot, "memory-read-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _dataPath);
    }

    [Fact]
    public async Task Oversized_disk_document_is_rejected_before_cache_use_and_old_sessions_cannot_restore_it()
    {
        var root = Path.Combine(_dataPath, "oversized");
        var store = new AnalysisMemoryStore();
        var oldSession = await store.BeginSessionAsync(root, 1);
        var oversized = Entry(root, "large", DateTimeOffset.UtcNow.AddMinutes(-1), new string('a', AnalysisMemoryStore.MaximumBytes));
        await WriteDocumentAsync(oldSession.Epoch, (root, 1, [oversized]));
        Assert.True(new FileInfo(StoragePath).Length > AnalysisMemoryStore.MaximumBytes);

        Assert.Empty(await store.LoadEntriesAsync(root));
        Assert.Equal(0, (await store.GetStatisticsAsync()).EntryCount);
        var late = Entry(root, "late", DateTimeOffset.UtcNow.AddSeconds(-1));
        await store.SaveSessionEntriesAsync(root, [late], oldSession);
        Assert.Empty(await store.LoadEntriesAsync(root));

        var recovered = await store.BeginSessionAsync(root, 2);
        Assert.NotEqual(oldSession.Epoch, recovered.Epoch);
        await store.SaveSessionEntriesAsync(root, [late], oldSession);
        Assert.Empty(await store.LoadEntriesAsync(root));
        await store.SaveSessionEntriesAsync(root, [late], recovered);
        Assert.Equal(late, Assert.Single(await store.LoadEntriesAsync(root)).Value);
        Assert.True(new FileInfo(StoragePath).Length <= AnalysisMemoryStore.MaximumBytes);
    }

    [Fact]
    public async Task Disk_document_entry_limit_is_global_and_keeps_latest_entries_epoch_and_root_generations()
    {
        var firstRoot = Path.Combine(_dataPath, "first");
        var secondRoot = Path.Combine(_dataPath, "second");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = Enumerable.Range(0, 10_003).Select(index => Entry(firstRoot, "item-" + index, now.AddTicks(index))).ToArray();
        var second = Enumerable.Range(0, 10_003).Select(index => Entry(secondRoot, "item-" + index, now.AddTicks(index + first.Length))).ToArray();
        await WriteDocumentAsync(42, (firstRoot, 7, first), (secondRoot, 11, second));
        Assert.True(new FileInfo(StoragePath).Length <= AnalysisMemoryStore.MaximumBytes);

        var store = new AnalysisMemoryStore();
        var loadedFirst = await store.LoadEntriesAsync(firstRoot);
        var loadedSecond = await store.LoadEntriesAsync(secondRoot);
        Assert.Equal(AnalysisMemoryStore.MaximumEntries, loadedFirst.Count + loadedSecond.Count);
        Assert.All(first.Take(6), entry => Assert.DoesNotContain(entry.Path, loadedFirst.Keys));
        Assert.All(second, entry => Assert.Equal(entry, loadedSecond[entry.Path]));
        Assert.Equal(AnalysisMemoryStore.MaximumEntries, (await store.GetStatisticsAsync()).EntryCount);

        var firstSession = await store.BeginSessionAsync(firstRoot, 7);
        var secondSession = await store.BeginSessionAsync(secondRoot, 11);
        Assert.Equal(new AiAnalysisMemorySession(42, 7), firstSession);
        Assert.Equal(new AiAnalysisMemorySession(42, 11), secondSession);
        await store.PruneAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(StoragePath));
        Assert.Equal(42, saved.RootElement.GetProperty("Epoch").GetInt64());
        var roots = saved.RootElement.GetProperty("Roots").EnumerateArray().ToDictionary(
            item => item.GetProperty("RootPath").GetString()!, item => item.GetProperty("Generation").GetInt64());
        Assert.Equal(7, roots[firstRoot]);
        Assert.Equal(11, roots[secondRoot]);
    }

    private Task WriteDocumentAsync(long epoch, params (string Root, long Generation, AiAnalysisMemoryEntry[] Entries)[] roots)
        => File.WriteAllBytesAsync(StoragePath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 2,
            Roots = roots.Select(root => new { RootPath = root.Root, root.Entries, root.Generation }),
            Epoch = epoch
        }));

    private static AiAnalysisMemoryEntry Entry(string root, string name, DateTimeOffset analyzedAt, string reason = "synthetic")
    {
        var path = Path.Combine(root, name);
        return new(path, false, "synthetic-signature", new(path, RiskLevel.Low, SuggestedAction.Keep, reason, .9), analyzedAt);
    }

    private static string FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DiskLens.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Test workspace was not found.");
    }

    public void Dispose() => Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataPath);
}

using System.IO;
using System.Text.Json;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.App.Services;

public sealed record AnalysisMemoryStatistics(int EntryCount, long FileBytes)
{
    public override string ToString() => L.F($"本地缓存 {EntryCount:N0} 条，占用 {FileBytes / 1024d / 1024d:F2} MiB；原始分析保留 30 天，上限 20,000 条 / 32 MiB。");
}

public sealed class AnalysisMemoryStore : IVersionedAiAnalysisMemoryStore
{
    public const int MaximumEntries = 20_000;
    public const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumTombstoneBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim StorageGate = new(1, 1);
    private static long _nextRecoveryEpoch = DateTimeOffset.UtcNow.UtcTicks;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _storagePath;

    public AnalysisMemoryStore() => _storagePath = Path.Combine(AppDataDirectory.GetPath(), "analysis-memory.json");

    public async Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(
        string rootPath, CancellationToken cancellationToken = default)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            return document.Roots.Where(root => root.RootPath == rootPath).SelectMany(root => root.Entries)
                .Where(entry => entry.IsUsableAt(DateTimeOffset.UtcNow))
                .GroupBy(entry => entry.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Newest(group), StringComparer.Ordinal);
        }
        finally { StorageGate.Release(); }
    }

    public async Task<AiAnalysisMemorySession> BeginSessionAsync(string rootPath, long snapshotGeneration,
        CancellationToken cancellationToken = default)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var root = document.Roots.FirstOrDefault(item => item.RootPath == rootPath)
                ?? new AnalysisMemoryRoot(rootPath, [], 0);
            var generation = snapshotGeneration > 0 ? snapshotGeneration
                : Math.Max(DateTimeOffset.UtcNow.UtcTicks, root.Generation + 1);
            if (generation > root.Generation)
            {
                var roots = document.Roots.Where(item => item.RootPath != rootPath).ToList();
                roots.Add(root with { Generation = generation });
                await WriteDocumentAsync(document with { Roots = roots }, cancellationToken).ConfigureAwait(false);
            }
            return new AiAnalysisMemorySession(document.Epoch, generation);
        }
        finally { StorageGate.Release(); }
    }

    public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        CancellationToken cancellationToken = default) => SaveAsync(rootPath, entries, null, cancellationToken);

    public Task SaveSessionEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        AiAnalysisMemorySession session, CancellationToken cancellationToken = default) =>
        SaveAsync(rootPath, entries, session, cancellationToken);

    private async Task SaveAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        AiAnalysisMemorySession? session, CancellationToken cancellationToken)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var oldRoot = document.Roots.FirstOrDefault(item => item.RootPath == rootPath)
                ?? new AnalysisMemoryRoot(rootPath, [], 0);
            // Clearing invalidates every in-flight session, including its later checkpoints.
            if (session is not null && (session.Epoch != document.Epoch || session.SnapshotGeneration != oldRoot.Generation)) return;
            var removed = ActiveTombstones(oldRoot.RemovedAnalyses, DateTimeOffset.UtcNow)
                .Select(entry => (entry.Path, entry.OriginalAnalysisId, entry.OriginalAnalyzedAt)).ToHashSet();
            var merged = entries.Concat(oldRoot.Entries).Where(entry => entry is not null && entry.IsUsableAt(DateTimeOffset.UtcNow)
                    && !WasRemoved(entry, removed))
                .GroupBy(entry => entry.Path, StringComparer.Ordinal).Select(Newest).ToArray();
            var roots = document.Roots.Where(item => item.RootPath != rootPath).ToList();
            roots.Add(oldRoot with { Entries = merged });
            await WriteDocumentAsync(document with { Roots = roots }, cancellationToken).ConfigureAwait(false);
        }
        finally { StorageGate.Release(); }
    }

    /// <summary>Complete only captured contribution intents that still exist unchanged.</summary>
    public async Task<int> MarkContributionsCompletedIfUnchangedAsync(string rootPath,
        IReadOnlyCollection<AiAnalysisMemoryEntry> expectedEntries, CancellationToken cancellationToken = default)
    {
        if (expectedEntries.Count == 0) return 0;
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var root = document.Roots.FirstOrDefault(item => item.RootPath == rootPath);
            if (root is null) return 0;
            var retained = root.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            var count = 0;
            foreach (var expected in expectedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expected.SharedContributionPending || expected.SharedContributionCompleted
                    || !retained.TryGetValue(expected.Path, out var current)
                    || !JsonSerializer.SerializeToUtf8Bytes(current, SerializerOptions).AsSpan()
                        .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, SerializerOptions))) continue;
                retained[expected.Path] = current with { SharedContributionPending = false, SharedContributionCompleted = true };
                count++;
            }
            if (count == 0) return 0;
            var roots = document.Roots.Select(item => item.RootPath == rootPath
                ? root with { Entries = retained.Values.ToArray() } : item).ToArray();
            await WriteDocumentAsync(document with { Roots = roots }, cancellationToken).ConfigureAwait(false);
            return count;
        }
        finally { StorageGate.Release(); }
    }

    /// <summary>Remove only the exact captured originals, retaining bounded identities to reject late checkpoints.</summary>
    public async Task<int> RemoveEntriesIfUnchangedAsync(string rootPath,
        IReadOnlyCollection<AiAnalysisMemoryEntry> expectedEntries, CancellationToken cancellationToken = default)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var root = document.Roots.FirstOrDefault(item => item.RootPath == rootPath);
            if (root is null || expectedEntries.Count == 0) return 0;
            var now = DateTimeOffset.UtcNow;
            var removed = ActiveTombstones(root.RemovedAnalyses, now).ToList();
            var globalRemoved = document.Roots.SelectMany(item => ActiveTombstones(item.RemovedAnalyses, now)).ToList();
            var retained = root.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            var count = 0;
            foreach (var expected in expectedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expected is null || string.IsNullOrWhiteSpace(expected.Result?.OriginalAnalysisId)
                    || !retained.TryGetValue(expected.Path, out var current)
                    || !JsonSerializer.SerializeToUtf8Bytes(current, SerializerOptions).AsSpan()
                        .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, SerializerOptions))) continue;
                var tombstone = new AnalysisMemoryTombstone(expected.Path, expected.Result.OriginalAnalysisId,
                    expected.Result.OriginalAnalyzedAt ?? expected.AnalyzedAt, now);
                if (globalRemoved.Count >= MaximumEntries) continue;
                globalRemoved.Add(tombstone);
                if (JsonSerializer.SerializeToUtf8Bytes(globalRemoved, SerializerOptions).Length > MaximumTombstoneBytes)
                { globalRemoved.RemoveAt(globalRemoved.Count - 1); continue; }
                removed.Add(tombstone);
                retained.Remove(expected.Path);
                count++;
            }
            if (count == 0) return 0;
            var roots = document.Roots.Select(item => item.RootPath == rootPath
                ? root with { Entries = retained.Values.ToArray(), RemovedAnalyses = removed } : item).ToArray();
            await WriteDocumentAsync(document with { Version = 3, Roots = roots }, cancellationToken).ConfigureAwait(false);
            return count;
        }
        finally { StorageGate.Release(); }
    }

    private static AnalysisMemoryTombstone[] ActiveTombstones(
        IReadOnlyList<AnalysisMemoryTombstone>? entries, DateTimeOffset now) => (entries ?? [])
        .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Path)
            && !string.IsNullOrWhiteSpace(entry.OriginalAnalysisId)
            && entry.OriginalAnalyzedAt > now - AiAnalysisMemoryEntry.MaximumAge && entry.OriginalAnalyzedAt <= now)
        .GroupBy(entry => (entry.Path, entry.OriginalAnalysisId, entry.OriginalAnalyzedAt))
        .Select(group => group.OrderByDescending(entry => entry.RemovedAt).First())
        .OrderByDescending(entry => entry.RemovedAt).Take(MaximumEntries).ToArray();

    private static bool WasRemoved(AiAnalysisMemoryEntry entry,
        IReadOnlySet<(string Path, string OriginalAnalysisId, DateTimeOffset OriginalAnalyzedAt)> tombstones) =>
        !string.IsNullOrWhiteSpace(entry.Result.OriginalAnalysisId) && tombstones.Contains((entry.Path,
            entry.Result.OriginalAnalysisId, entry.Result.OriginalAnalyzedAt ?? entry.AnalyzedAt));

    /// <summary>Clear one partition or all entries. A durable epoch prevents old work restoring cleared records.</summary>
    public async Task ClearAsync(string? rootPath = null, CancellationToken cancellationToken = default)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var roots = rootPath is null ? [] : document.Roots.Where(root => root.RootPath != rootPath).ToArray();
            await WriteDocumentAsync(new AnalysisMemoryDocument(2, roots, checked(document.Epoch + 1)), cancellationToken).ConfigureAwait(false);
        }
        finally { StorageGate.Release(); }
    }

    /// <summary>Prune expired entries and enforce the global entry/serialized UTF-8 byte limits.</summary>
    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteDocumentAsync(await LoadDocumentAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false); }
        finally { StorageGate.Release(); }
    }

    public async Task<AnalysisMemoryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await StorageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            return new AnalysisMemoryStatistics(document.Roots.Sum(root => root.Entries.Count),
                File.Exists(_storagePath) ? new FileInfo(_storagePath).Length : 0);
        }
        finally { StorageGate.Release(); }
    }

    private static AiAnalysisMemoryEntry Newest(IEnumerable<AiAnalysisMemoryEntry> entries)
    {
        var ordered = entries.OrderByDescending(entry => entry.SnapshotGeneration)
            .ThenByDescending(entry => entry.AnalyzedAt).ToArray();
        var newest = ordered[0];
        // A stale AI checkpoint for the same original and consent cannot take
        // retry ownership back from an already persisted outbox handoff.
        if (newest.SharedContributionPending && newest.SharedContributionConsentFingerprint is not null
            && ordered.Any(entry => entry.SharedContributionCompleted
            && entry.SharedContributionConsentFingerprint == newest.SharedContributionConsentFingerprint
            && SameOriginalAnalysis(newest, entry) && SameContributionResult(newest.Result, entry.Result)))
            newest = newest with { SharedContributionPending = false, SharedContributionCompleted = true };
        if (newest.Result.SuggestedAction != SuggestedAction.RecycleCandidate) return newest;
        // A later ancestor check can veto an already checkpointed recommendation.
        // An older caller still holding that same analysis must not undo its veto,
        // even when it saves last. Keep current scan metadata and original identity.
        var veto = ordered.FirstOrDefault(entry => entry.Result.SuggestedAction == SuggestedAction.DoNotDelete
            && entry.Result.RiskLevel is RiskLevel.High or RiskLevel.Protected
            && SameOriginalAnalysis(newest, entry));
        return veto is null ? newest : newest with
        {
            Result = newest.Result with { SuggestedAction = SuggestedAction.DoNotDelete, RiskLevel = veto.Result.RiskLevel },
        };
    }

    private static bool SameOriginalAnalysis(AiAnalysisMemoryEntry first, AiAnalysisMemoryEntry second) =>
        !string.IsNullOrWhiteSpace(first.Result.OriginalAnalysisId)
        && string.Equals(first.Result.OriginalAnalysisId, second.Result.OriginalAnalysisId, StringComparison.Ordinal)
        && first.Result.OriginalAnalyzedAt == second.Result.OriginalAnalyzedAt
        && first.IsDirectory == second.IsDirectory
        && string.Equals(first.Signature, second.Signature, StringComparison.OrdinalIgnoreCase)
        && first.Result.AnalysisEvidenceScope == second.Result.AnalysisEvidenceScope
        && first.MatchesAnalysisContext(second.ProviderKind, second.Endpoint, second.Model, second.PromptFingerprint,
            second.UseTokenSavingMode, second.AnalysisThresholdBytes, second.MaxAiChunkTokens,
            second.ReasoningEffort, second.Temperature);

    private static bool SameContributionResult(AiAnalysisResult first, AiAnalysisResult second) =>
        JsonSerializer.SerializeToUtf8Bytes(first with { Origin = AiAnalysisResultOrigin.AiAnalyzed }, SerializerOptions).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second with { Origin = AiAnalysisResultOrigin.AiAnalyzed }, SerializerOptions));

    private async Task WriteDocumentAsync(AnalysisMemoryDocument document, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var roots = document.Roots.OrderByDescending(root => root.Generation).Take(MaximumEntries).ToArray();
        var candidates = roots.SelectMany(root => root.Entries.Where(entry => entry.IsUsableAt(now))
                .Select(entry => (root.RootPath, Entry: entry)))
            .OrderByDescending(item => item.Entry.AnalyzedAt).Take(MaximumEntries).ToArray();
        AnalysisMemoryDocument Build(int count)
        {
            var selected = candidates.Take(count).ToLookup(item => item.RootPath, StringComparer.Ordinal);
            return new AnalysisMemoryDocument(3, roots.Select(root => root with
                { Entries = selected[root.RootPath].Select(item => item.Entry).ToArray(),
                    RemovedAnalyses = ActiveTombstones(root.RemovedAnalyses, now) }).ToArray(), document.Epoch);
        }
        var nextDocument = Build(candidates.Length);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(nextDocument, SerializerOptions);
        if (bytes.Length > MaximumBytes)
        {
            // Binary search the newest prefix; limits include the actual UTF-8 document envelope.
            var low = 0;
            var high = candidates.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (JsonSerializer.SerializeToUtf8Bytes(Build(middle), SerializerOptions).Length <= MaximumBytes) low = middle;
                else high = middle - 1;
            }
            nextDocument = Build(low);
            bytes = JsonSerializer.SerializeToUtf8Bytes(nextDocument, SerializerOptions);
            while (bytes.Length > MaximumBytes && nextDocument.Roots.Count > 0)
            {
                nextDocument = nextDocument with { Roots = nextDocument.Roots.Take(nextDocument.Roots.Count / 2).ToArray() };
                bytes = JsonSerializer.SerializeToUtf8Bytes(nextDocument, SerializerOptions);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
        var tempPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private async Task<AnalysisMemoryDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_storagePath)) return AnalysisMemoryDocument.Empty;
        try
        {
            await using var stream = File.OpenRead(_storagePath);
            // Bound allocation before JSON materialization. A rejected document must
            // not fall back to epoch zero: a pre-clear session could then become valid
            // again. The next successful write persists this fresh recovery epoch.
            if (stream.Length > MaximumBytes)
                return new AnalysisMemoryDocument(2, [], Interlocked.Increment(ref _nextRecoveryEpoch));
            var document = await JsonSerializer.DeserializeAsync<AnalysisMemoryDocument>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            if (document is null || document.Version is not (1 or 2 or 3) || document.Epoch < 0) return AnalysisMemoryDocument.Empty;
            // Legacy and damaged JSON can contain null collections/entries despite C# annotations.
            var roots = (document.Roots ?? []).Where(root => root is not null && !string.IsNullOrWhiteSpace(root.RootPath))
                .GroupBy(root => root.RootPath, StringComparer.Ordinal).Select(group => new AnalysisMemoryRoot(group.Key,
                    group.SelectMany(root => root.Entries ?? []).Where(entry => entry is not null && entry.IsUsableAt(DateTimeOffset.UtcNow))
                        .GroupBy(entry => entry.Path, StringComparer.Ordinal).Select(Newest).ToArray(),
                    group.Max(root => Math.Max(0, root.Generation)),
                    ActiveTombstones(group.SelectMany(root => root.RemovedAnalyses ?? []).ToArray(), DateTimeOffset.UtcNow))).ToArray();
            var boundedRoots = roots.OrderByDescending(root => root.Generation).Take(MaximumEntries).ToArray();
            var retained = boundedRoots.SelectMany(root => root.Entries.Select(entry => (root.RootPath, Entry: entry)))
                .OrderByDescending(item => item.Entry.AnalyzedAt).Take(MaximumEntries)
                .ToLookup(item => item.RootPath, StringComparer.Ordinal);
            return document with { Roots = boundedRoots.Select(root => root with
                { Entries = retained[root.RootPath].Select(item => item.Entry).ToArray() }).ToArray() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return AnalysisMemoryDocument.Empty; }
    }

    private sealed record AnalysisMemoryDocument(int Version, IReadOnlyList<AnalysisMemoryRoot> Roots, long Epoch = 0)
    {
        public static AnalysisMemoryDocument Empty { get; } = new(2, []);
    }
    private sealed record AnalysisMemoryRoot(string RootPath, IReadOnlyList<AiAnalysisMemoryEntry> Entries, long Generation = 0,
        IReadOnlyList<AnalysisMemoryTombstone>? RemovedAnalyses = null);
    private sealed record AnalysisMemoryTombstone(string Path, string OriginalAnalysisId,
        DateTimeOffset OriginalAnalyzedAt, DateTimeOffset RemovedAt);
}

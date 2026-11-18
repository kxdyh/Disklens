using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

internal sealed class NoOpAiAnalysisMemoryStore : IAiAnalysisMemoryStore
{
    public static NoOpAiAnalysisMemoryStore Instance { get; } = new();

    public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(
            new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.OrdinalIgnoreCase));
    }

    public Task SaveEntriesAsync(
        string rootPath,
        IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

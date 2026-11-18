using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public interface IAiAnalysisMemoryStore
{
    Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task SaveEntriesAsync(
        string rootPath,
        IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        CancellationToken cancellationToken = default);
}

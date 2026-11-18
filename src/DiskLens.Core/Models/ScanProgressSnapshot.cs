namespace DiskLens.Core.Models;

public sealed record ScanProgressSnapshot(
    int FilesScanned,
    int DirectoriesScanned,
    long BytesDiscovered,
    string? CurrentPath,
    FileSystemNode? Root)
{
    public long LogicalBytesDiscovered { get; init; }

    public int EstimatedStorageItemCount { get; init; }

    public int SkippedDuplicateTargetCount { get; init; }
}

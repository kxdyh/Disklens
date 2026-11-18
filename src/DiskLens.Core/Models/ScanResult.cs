namespace DiskLens.Core.Models;

public sealed record ScanResult(
    FileSystemNode Root,
    int FilesScanned,
    int DirectoriesScanned,
    long BytesDiscovered,
    TimeSpan Duration)
{
    public long LogicalBytesDiscovered { get; init; }

    public int EstimatedStorageItemCount { get; init; }

    public int SkippedDuplicateTargetCount { get; init; }

    public int ScanIssueCount { get; init; }

    public IReadOnlyList<ScanReadIssue> Issues { get; init; } = [];

    public int SkippedReparsePointCount { get; init; }

    /// <summary>Whether this scan thread could use its existing Windows backup privilege.</summary>
    public bool BackupReadEnabled { get; init; }

    public int? BackupReadFailureCode { get; init; }

    public bool IsPartial => ScanIssueCount > 0;
}

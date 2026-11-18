namespace DiskLens.Core.Models;

/// <summary>
/// Storage metadata before the scan assigns shared storage to an owner. LogicalSizeBytes
/// describes the primary data stream; AllocatedBytes includes local allocation of named data streams.
/// </summary>
public sealed record FileStorageInfo(
    string? Identity,
    long LogicalSizeBytes,
    long AllocatedBytes,
    uint LinkCount = 1,
    bool IsEstimated = false,
    string? EstimationReason = null);

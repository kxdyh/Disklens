namespace DiskLens.Core.Models;

/// <summary>A path that could not be fully read, without treating its unknown size as zero usage.</summary>
public sealed record ScanReadIssue(
    string Path,
    bool IsDirectory,
    string Operation,
    int NativeErrorCode,
    string Message);

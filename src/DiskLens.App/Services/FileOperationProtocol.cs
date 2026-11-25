namespace DiskLens.App.Services;

public enum FileOperationStatus
{
    Completed,
    Cancelled,
    Failed,
}

public sealed record FileOperationRequest(string Path, bool IsDirectory)
{
    public long OwnerWindowHandle { get; init; }
}

public sealed record FileOperationResult(FileOperationStatus Status, string? ErrorMessage = null, int? ErrorCode = null)
{
    public int WorkerProcessId { get; init; }
}

public interface IFileOperationTransport
{
    Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, CancellationToken cancellationToken = default);
}

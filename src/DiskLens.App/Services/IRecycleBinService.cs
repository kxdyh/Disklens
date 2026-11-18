namespace DiskLens.App.Services;

public interface IRecycleBinService
{
    Task RecycleAsync(string path, bool isDirectory, CancellationToken cancellationToken = default);
}

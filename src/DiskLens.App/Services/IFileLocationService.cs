namespace DiskLens.App.Services;

public interface IFileLocationService
{
    Task OpenLocationAsync(string path, bool isDirectory, CancellationToken cancellationToken = default);
}

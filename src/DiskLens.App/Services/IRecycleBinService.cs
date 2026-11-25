namespace DiskLens.App.Services;

public interface IRecycleBinService
{
    Task RecycleAsync(string path, bool isDirectory, CancellationToken cancellationToken = default);

    bool IsPathBusy(string path) => false;

    event EventHandler? StateChanged { add { } remove { } }
}

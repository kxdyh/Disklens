using Microsoft.VisualBasic.FileIO;

namespace DiskLens.App.Services;

public sealed class RecycleBinService : IRecycleBinService
{
    public Task RecycleAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            if (isDirectory)
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }, cancellationToken);
    }
}

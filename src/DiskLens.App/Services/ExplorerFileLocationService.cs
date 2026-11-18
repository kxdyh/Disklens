using System.Diagnostics;

namespace DiskLens.App.Services;

public sealed class ExplorerFileLocationService : IFileLocationService
{
    public Task OpenLocationAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (isDirectory)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }

        return Task.CompletedTask;
    }
}

using System.IO;

namespace DiskLens.App.Services;

internal static class AppDataDirectory
{
    public static string GetPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskLens");
    }
}

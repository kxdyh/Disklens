namespace DiskLens.Core.Policies;

public sealed class WindowsLocationPolicy : ILocationPolicy
{
    private readonly IReadOnlyList<string> _protectedPrefixes;

    public WindowsLocationPolicy()
    {
        _protectedPrefixes = BuildProtectedPrefixes();
    }

    public LocationAssessment AssessPath(string path, bool isDirectory, FileAttributes attributes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new LocationAssessment(
                IsProtectedLocation: true,
                RiskTags: ["system", "protected-location"]);
        }

        var normalizedPath = Normalize(path);
        var isProtectedLocation = IsProtectedLocationCore(normalizedPath);
        var tags = new List<string>(capacity: 5);
        var extension = isDirectory ? string.Empty : Path.GetExtension(path);

        if (isProtectedLocation)
        {
            tags.Add("system");
            tags.Add("protected-location");
        }

        if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
        {
            tags.Add("hidden");
        }

        if ((attributes & FileAttributes.System) == FileAttributes.System)
        {
            tags.Add("system-attribute");
        }

        switch (extension.ToLowerInvariant())
        {
            case ".zip":
            case ".7z":
            case ".rar":
            case ".tar":
            case ".gz":
                tags.Add("archive");
                break;
            case ".iso":
            case ".vhd":
            case ".vhdx":
                tags.Add("virtual-disk");
                break;
            case ".exe":
            case ".dll":
            case ".sys":
                tags.Add("binary");
                break;
            case ".msi":
            case ".msix":
            case ".cab":
                tags.Add("installer");
                break;
            case ".log":
            case ".etl":
            case ".dmp":
                tags.Add("diagnostic");
                break;
            case ".tmp":
                tags.Add("temporary");
                break;
        }

        if (isDirectory)
        {
            tags.Add("directory");
        }

        return new LocationAssessment(isProtectedLocation, tags);
    }

    public bool IsProtectedLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var normalizedPath = Normalize(path);
        return IsProtectedLocationCore(normalizedPath);
    }

    public IReadOnlyList<string> GetRiskTags(string path, bool isDirectory, FileAttributes attributes)
    {
        return AssessPath(path, isDirectory, attributes).RiskTags;
    }

    private bool IsProtectedLocationCore(string normalizedPath)
    {
        foreach (var normalizedPrefix in _protectedPrefixes)
        {
            if (normalizedPath.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                || (normalizedPath.Length > normalizedPrefix.Length
                    && normalizedPath[normalizedPrefix.Length] == Path.DirectorySeparatorChar
                    && normalizedPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildProtectedPrefixes()
    {
        var prefixes = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            prefixes.Add(programFilesX86);
        }

        var commonProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        if (!string.IsNullOrWhiteSpace(commonProgramFiles))
        {
            prefixes.Add(commonProgramFiles);
        }

        var commonProgramFilesX86 = Environment.GetEnvironmentVariable("CommonProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(commonProgramFilesX86))
        {
            prefixes.Add(commonProgramFilesX86);
        }

        prefixes.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        return prefixes
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string path)
    {
        return path
            .Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}

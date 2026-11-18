using System.Diagnostics;
using System.IO;
using DiskLens.App.Services;

namespace DiskLens.App.Tests;

public sealed class VolumeSpaceReaderBehaviorTests : IDisposable
{
    private const long UsageChangeTolerance = 64L * 1024 * 1024;
    private readonly string _rootDirectory;
    private readonly DriveInfo _sourceDrive;

    public VolumeSpaceReaderBehaviorTests()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.VolumeSpace.Tests");
        _rootDirectory = Path.Combine(artifactRoot, "volume-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
        _sourceDrive = new DriveInfo(Path.GetPathRoot(_rootDirectory)!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("E:\\000\0invalid")]
    public void Empty_or_invalid_paths_have_no_volume_information(string? path)
    {
        Assert.Null(VolumeSpaceReader.Read(path));
    }

    [Fact]
    public void Unavailable_directory_has_no_volume_information()
    {
        var missing = Path.Combine(_rootDirectory, "missing-" + Guid.NewGuid().ToString("N"));

        Assert.Null(VolumeSpaceReader.Read(missing));
    }

    [Fact]
    public void Existing_nested_directory_matches_its_volume_capacity_and_total_free_space()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_rootDirectory, "nested")).FullName;
        try
        {
            var drive = _sourceDrive;
            var usedBefore = drive.TotalSize - drive.TotalFreeSpace;

            var actual = Assert.IsType<VolumeSpaceInfo>(VolumeSpaceReader.Read(nested));

            var usedAfter = drive.TotalSize - drive.TotalFreeSpace;
            Assert.Equal(drive.TotalSize, actual.CapacityBytes);
            AssertUsageNearSurroundingReads(actual.UsedBytes, usedBefore, usedAfter);
        }
        finally
        {
            Directory.Delete(nested);
        }
    }

    [Fact]
    public async Task Junction_reads_the_target_volume_instead_of_the_path_drive()
    {
        var sourceDrive = _sourceDrive;
        var targetDrive = FindDifferentVolume(sourceDrive);
        if (targetDrive is null)
        {
            throw Xunit.Sdk.SkipException.ForSkip("A second accessible volume with a different capacity is required.");
        }

        var junction = Path.Combine(_rootDirectory, "other-volume");
        try
        {
            await CreateJunctionAsync(junction, targetDrive.RootDirectory.FullName);
            Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
            var usedBefore = targetDrive.TotalSize - targetDrive.TotalFreeSpace;

            var actual = Assert.IsType<VolumeSpaceInfo>(VolumeSpaceReader.Read(junction));

            var usedAfter = targetDrive.TotalSize - targetDrive.TotalFreeSpace;
            Assert.Equal(targetDrive.TotalSize, actual.CapacityBytes);
            Assert.NotEqual(sourceDrive.TotalSize, actual.CapacityBytes);
            AssertUsageNearSurroundingReads(actual.UsedBytes, usedBefore, usedAfter);
        }
        finally
        {
            // Remove only this link. Never recurse into or delete its target.
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
        Assert.True(Directory.Exists(targetDrive.RootDirectory.FullName));
    }

    private static void AssertUsageNearSurroundingReads(long actual, long before, long after)
    {
        // Background writes can occur between the three native capacity queries.
        Assert.InRange(actual,
            Math.Max(0, Math.Min(before, after) - UsageChangeTolerance),
            Math.Max(before, after) + UsageChangeTolerance);
    }

    private static DriveInfo? FindDifferentVolume(DriveInfo sourceDrive)
    {
        foreach (var drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name == @"D:\" ? 0 : 1))
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed
                    && !string.Equals(drive.Name, sourceDrive.Name, StringComparison.OrdinalIgnoreCase)
                    && drive.TotalSize != sourceDrive.TotalSize)
                {
                    return drive;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private async Task CreateJunctionAsync(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _rootDirectory,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("New-Item -ItemType Junction -Path $env:DISKLENS_VOLUME_TEST_LINK -Target $env:DISKLENS_VOLUME_TEST_TARGET -ErrorAction Stop | Out-Null");
        startInfo.Environment["DISKLENS_VOLUME_TEST_LINK"] = linkPath;
        startInfo.Environment["DISKLENS_VOLUME_TEST_TARGET"] = targetPath;
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, $"Could not create test junction: {await error}{await output}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory)) Directory.Delete(_rootDirectory);
    }
}

using System.IO;
using DiskLens.App.Services;

namespace DiskLens.App.Tests;

public sealed class VolumeReserveBehaviorTests
{
    [Fact]
    public void Local_ntfs_usage_separates_allocated_and_reserved_clusters()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var drive = new DriveInfo(root);
        var beforeUsed = drive.TotalSize - drive.TotalFreeSpace;

        var info = Assert.IsType<VolumeSpaceInfo>(VolumeSpaceReader.Read(root));

        var afterUsed = drive.TotalSize - drive.TotalFreeSpace;
        Assert.Equal(drive.TotalSize, info.CapacityBytes);
        Assert.InRange(info.UsedBytes, Math.Max(0, Math.Min(beforeUsed, afterUsed) - 64L * 1024 * 1024),
            Math.Max(beforeUsed, afterUsed) + 64L * 1024 * 1024);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            var reserved = Assert.IsType<long>(info.ReservedBytes);
            var allocated = Assert.IsType<long>(info.AllocatedBytes);
            var storageReserve = Assert.IsType<long>(info.StorageReserveBytes);
            Assert.InRange(reserved, 0, info.UsedBytes);
            Assert.InRange(storageReserve, 0, reserved);
            Assert.InRange(allocated, 0, info.UsedBytes);
            // For a local NTFS volume, unavailable reserved clusters account for
            // the difference between actual allocation and Explorer's used size.
            Assert.Equal(info.UsedBytes, allocated + reserved);
        }
    }
}

using DiskLens.App.Models;

namespace DiskLens.Tests.Ui;

public sealed class DriveSpaceWarningTests
{
    [Theory]
    [InlineData(100.01, 1.14, true)]
    [InlineData(375.94, 18.87, true)]
    [InlineData(476.94, 104.79, false)]
    [InlineData(100, 9.999, true)]
    [InlineData(100, 10, false)]
    [InlineData(100, 10.001, false)]
    [InlineData(100, 0, true)]
    [InlineData(100, 100, false)]
    [InlineData(0, 0, false)]
    public void WarnsOnlyWhenLessThanTenPercentIsFree(double capacity, double free, bool expected)
    {
        var usage = capacity <= 0 ? 0 : (capacity - free) * 100 / capacity;
        var drive = new DriveScanOption("X:\\", "X:", "测试磁盘", "", "", usage,
            (long)(capacity * 1_000_000), (long)(free * 1_000_000));

        Assert.Equal(expected, drive.IsLowSpace);
    }

    [Fact]
    public void RefreshingCapacityClearsAWarningAfterSpaceIsFreed()
    {
        var full = new DriveScanOption("X:\\", "X:", "测试磁盘", "100 GB", "可用 5 GB", 95, 100, 5);
        var refreshed = full with { FreeSpaceText = "可用 25 GB", UsedPercentage = 75, FreeBytes = 25 };

        Assert.True(full.IsLowSpace);
        Assert.Contains("10%", full.SpaceStatusText);
        Assert.False(refreshed.IsLowSpace);
        Assert.DoesNotContain("不足", refreshed.SpaceStatusText);
    }
}

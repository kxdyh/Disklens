using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class ScanProgressTuningTests
{
    [Theory]
    [InlineData(250, 5000, 250)]
    [InlineData(250, 15000, 500)]
    [InlineData(250, 50000, 750)]
    [InlineData(250, 120000, 1000)]
    public void GetAdaptiveTreeSnapshotInterval_should_increase_interval_for_large_scans(int baseInterval, int totalNodes, int expected)
    {
        var interval = ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(baseInterval, totalNodes);

        Assert.Equal(expected, interval);
    }
}

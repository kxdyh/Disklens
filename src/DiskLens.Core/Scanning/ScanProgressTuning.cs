namespace DiskLens.Core.Scanning;

public static class ScanProgressTuning
{
    public static int GetAdaptiveTreeSnapshotInterval(int baseIntervalMilliseconds, int totalNodes)
    {
        var interval = Math.Max(0L, baseIntervalMilliseconds);
        if (interval == 0)
        {
            return 0;
        }

        var multiplier = totalNodes switch
        {
            >= 100000 => 4L,
            >= 40000 => 3L,
            >= 10000 => 2L,
            _ => 1L,
        };

        return (int)Math.Min(int.MaxValue, interval * multiplier);
    }
}

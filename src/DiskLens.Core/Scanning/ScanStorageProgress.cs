namespace DiskLens.Core.Scanning;

/// <summary>Compares discovered physical storage with the volume's used bytes at scan start.</summary>
public static class ScanStorageProgress
{
    public static double? CalculatePercent(long discoveredBytes, long? volumeUsedBytes)
    {
        if (volumeUsedBytes is null or <= 0)
        {
            return null;
        }

        // Convert before multiplying so large byte counts cannot overflow a long.
        return Math.Clamp((double)Math.Max(0, discoveredBytes) / volumeUsedBytes.Value * 100, 0, 100);
    }
}

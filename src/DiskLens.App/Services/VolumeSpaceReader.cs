using System.IO;
using System.Runtime.InteropServices;

namespace DiskLens.App.Services;

public sealed record VolumeSpaceInfo(long CapacityBytes, long UsedBytes)
{
    public long? AllocatedBytes { get; init; }
    public long? ReservedBytes { get; init; }
    public long? StorageReserveBytes { get; init; }
}

/// <summary>Reads the volume reached by the full directory path, including junction targets.</summary>
public static class VolumeSpaceReader
{
    public static VolumeSpaceInfo? Read(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || directoryPath.Contains('\0'))
        {
            return null;
        }

        // UNC share roots require a trailing separator. Keep the rest of the
        // path intact so Windows follows any mount point or junction in it.
        var queryPath = Path.EndsInDirectorySeparator(directoryPath)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        // Unlike a sum of file sizes, Explorer's used space includes reserved
        // clusters. Obtain that measured reserve separately; do not invent a
        // file or count the same reserve twice to make the figures match.
        var extended = ReadExtendedSpace(queryPath);
        if (extended is not null)
        {
            return extended;
        }

        if (!NativeMethods.GetDiskFreeSpaceEx(queryPath, out _, out var totalBytes, out var totalFreeBytes))
        {
            return null;
        }

        // Total free bytes describe volume usage; caller-available bytes may
        // instead be limited by a quota. Subtract before saturating to Int64.
        var usedBytes = totalBytes > totalFreeBytes ? totalBytes - totalFreeBytes : 0;
        return new VolumeSpaceInfo(ToInt64(totalBytes), ToInt64(usedBytes));
    }

    private static long ToInt64(ulong bytes) => bytes > long.MaxValue ? long.MaxValue : (long)bytes;

    private static VolumeSpaceInfo? ReadExtendedSpace(string queryPath)
    {
        try
        {
            if (NativeMethods.GetDiskSpaceInformation(queryPath, out var info) < 0
                || info.BytesPerSector == 0 || info.SectorsPerAllocationUnit == 0
                || info.ActualAvailableAllocationUnits > info.ActualTotalAllocationUnits)
            {
                return null;
            }

            var unitBytes = (ulong)info.BytesPerSector * info.SectorsPerAllocationUnit;
            return new VolumeSpaceInfo(
                UnitsToBytes(info.ActualTotalAllocationUnits, unitBytes),
                UnitsToBytes(info.ActualTotalAllocationUnits - info.ActualAvailableAllocationUnits, unitBytes))
            {
                AllocatedBytes = UnitsToBytes(info.UsedAllocationUnits, unitBytes),
                ReservedBytes = UnitsToBytes(info.TotalReservedAllocationUnits, unitBytes),
                StorageReserveBytes = UnitsToBytes(info.VolumeStorageReserveAllocationUnits, unitBytes)
            };
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 releases older than build 17763 do not expose this API.
            return null;
        }
    }

    private static long UnitsToBytes(ulong units, ulong unitBytes) =>
        units > (ulong)long.MaxValue / unitBytes ? long.MaxValue : (long)(units * unitBytes);

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DiskSpaceInformation
        {
            internal ulong ActualTotalAllocationUnits;
            internal ulong ActualAvailableAllocationUnits;
            internal ulong ActualPoolUnavailableAllocationUnits;
            internal ulong CallerTotalAllocationUnits;
            internal ulong CallerAvailableAllocationUnits;
            internal ulong CallerPoolUnavailableAllocationUnits;
            internal ulong UsedAllocationUnits;
            internal ulong TotalReservedAllocationUnits;
            internal ulong VolumeStorageReserveAllocationUnits;
            internal ulong AvailableCommittedAllocationUnits;
            internal ulong PoolAvailableAllocationUnits;
            internal uint SectorsPerAllocationUnit;
            internal uint BytesPerSector;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetDiskSpaceInformationW", CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        internal static extern int GetDiskSpaceInformation(string rootPath, out DiskSpaceInformation information);

        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailableToCaller,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);
    }
}

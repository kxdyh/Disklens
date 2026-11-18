namespace DiskLens.App.Models;

public sealed record DriveScanOption(
    string RootPath,
    string DisplayName,
    string VolumeLabel,
    string CapacityText,
    string FreeSpaceText,
    double UsedPercentage,
    long CapacityBytes = 0,
    long FreeBytes = 0,
    bool IsDefaultVolumeLabel = false)
{
    public string DisplayVolumeLabel => IsDefaultVolumeLabel ? L.T("本地磁盘") : VolumeLabel;
    // Explorer's red capacity indicator corresponds to less than 10% free space.
    public bool IsLowSpace => CapacityBytes > 0 && FreeBytes >= 0
        && (decimal)FreeBytes * 10 < CapacityBytes;

    public string SpaceStatusText => IsLowSpace
        ? L.T("空间不足：可用空间低于总容量的 10%。")
        : L.T("磁盘可用空间");
}

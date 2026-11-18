using System.IO;
using DiskLens.App.Infrastructure;
using DiskLens.App.Services;
using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ScanResult? _scanReport;
    private long? _scanReportVolumeUsedBytes;
    private VolumeSpaceInfo? _volumeSpaceInfo;
    private VolumeSpaceInfo? _scanReportVolumeSpaceInfo;
    private bool _isScanReportOpen;

    public RelayCommand OpenScanReportCommand { get; private set; } = null!;
    public RelayCommand CloseScanReportCommand { get; private set; } = null!;
    public bool HasScanReport => _scanReport is not null;
    public string ScanReportPathText => _scanReport?.Root.Path ?? string.Empty;
    public string ScanReportRecognizedText => FormatBytes(_scanReport?.BytesDiscovered ?? 0);
    public string ScanReportVolumeUsedText => _scanReportVolumeUsedBytes is { } bytes ? FormatBytes(bytes) : L.T("暂无法读取");
    public bool IsWholeVolumeScanReport => _scanReport is not null && IsVolumeRoot(_scanReport.Root.Path);
    public bool IsDirectoryScanReport => HasScanReport && !IsWholeVolumeScanReport;
    public string ScanReportUnattributedText => _scanReportVolumeUsedBytes is { } used && _scanReport is { } result
        && used >= result.BytesDiscovered && used - result.BytesDiscovered >= ScanReportKnownReservedBytes
            ? FormatBytes(used - result.BytesDiscovered - ScanReportKnownReservedBytes) : L.T("暂无法确定");
    private long ScanReportKnownReservedBytes => Math.Max(0, _scanReportVolumeSpaceInfo?.ReservedBytes ?? 0);
    public string ScanReportSystemReservedText => _scanReportVolumeSpaceInfo?.ReservedBytes is { } bytes
        ? FormatBytes(bytes) : L.T("系统未提供明细");
    public string ScanReportReserveDetailText => _scanReportVolumeSpaceInfo?.ReservedBytes is null
        ? L.T("无法拆分系统预留占用，当前差额仍包含此部分。")
        : _scanReportVolumeSpaceInfo.StorageReserveBytes is { } storageReserve
            ? L.F($"其中存储保留 {FormatBytes(storageReserve)}；预留已包含在 Windows 已用空间中，没有对应可删除的普通文件。")
            : L.T("预留已包含在 Windows 已用空间中，没有对应可删除的普通文件。");
    public string ScanReportIssueSummaryText => _scanReport is { ScanIssueCount: > 0 } result
        ? L.F($"{result.ScanIssueCount} 处路径遇到读取问题") : L.T("未记录读取错误");
    public string ScanReportReadAccessText => _scanReport switch
    {
        { BackupReadEnabled: true } => L.T("本次已启用只读备份权限，可读取更多受限目录。"),
        { BackupReadFailureCode: 1300 } => L.T("本次未取得备份读取权限；以管理员身份启动后重新扫描，可减少权限导致的漏计。"),
        { BackupReadFailureCode: { } code } => L.F($"本次备份读取通道不可用（Windows 错误 {code}），已使用常规权限扫描。"),
        _ => string.Empty,
    };
    public string ScanReportStrategyText => _scanReport is { } result
        ? L.F($"按策略跳过 {result.SkippedReparsePointCount} 个重解析点（如符号链接或挂载点）；{result.EstimatedStorageItemCount} 项使用占用估算。")
        : string.Empty;
    public bool HasScanReportPolicyNotes => _scanReport is { } result
        && (result.SkippedReparsePointCount > 0 || result.EstimatedStorageItemCount > 0);
    public string ScanReportReconciliationText => _scanReportVolumeUsedBytes is null
        ? L.T("未能读取卷已用空间，暂无法进行整盘对账。")
        : _scanReport is { } result && (result.BytesDiscovered > _scanReportVolumeUsedBytes
            || _scanReportVolumeUsedBytes - result.BytesDiscovered < ScanReportKnownReservedBytes)
            ? L.T("已识别文件与系统预留之和高于卷已用空间，本次无法可靠计算剩余差额。占用估算、扫描期间文件变化或卷信息变化都可能影响对账。")
            : (_scanReportVolumeSpaceInfo?.ReservedBytes is not null ? L.T("剩余差额已扣除系统预留。") : L.T("差额尚未拆分系统预留。"))
                + L.T("差额不等于读取失败文件的大小，还可能包含 NTFS 元数据、受限目录、按策略跳过的内容、占用估算误差及扫描期间的空间变化，不能视为可清理文件。");
    public bool HasScanReadIssues => ScanReadIssueRows.Count > 0;
    public string ScanReportEmptyIssuesText => _scanReport is { ScanIssueCount: > 0 }
        ? L.T("此任务没有保留路径详情。请重新扫描以获取逐项读取报告。")
        : L.T("本次遍历未记录读取错误；这不代表所有卷占用都能归属到普通文件。");
    public IReadOnlyList<ScanReadIssueRow> ScanReadIssueRows { get; private set; } = [];

    public bool IsScanReportOpen
    {
        get => _isScanReportOpen;
        private set
        {
            if (!SetProperty(ref _isScanReportOpen, value)) return;
            RaiseCommandStateChanged();
            OnPropertyChanged(nameof(IsFocusModeToggleVisible));
            OnPropertyChanged(nameof(IsFocusModeNavigationVisible));
            OnPropertyChanged(nameof(IsFocusModeToggleInteractive));
        }
    }

    private void InitializeScanReportCommands()
    {
        OpenScanReportCommand = new RelayCommand(() =>
        {
            if (CanOpenScanReport) IsScanReportOpen = true;
        }, () => CanOpenScanReport);
        CloseScanReportCommand = new RelayCommand(() => IsScanReportOpen = false);
    }

    private bool CanOpenScanReport => HasScanReport && !_isDisposed && !IsScanning && !IsExplorerGuideOpen
        && !IsSettingsOpen && !IsHelpOpen && !IsAiSetupOpen && !IsScanLocationPickerOpen
        && !IsScanNavigationNoticeOpen && !IsScanReportOpen;

    private void ClearScanReport()
    {
        IsScanReportOpen = false;
        _scanReport = null;
        _completedScanResult = null;
        _lastRenderedCompletedScanStatus = string.Empty;
        _scanReportVolumeUsedBytes = null;
        _scanReportVolumeSpaceInfo = null;
        ScanReadIssueRows = [];
        NotifyScanReportChanged();
    }

    private void CaptureScanReport(ScanResult result)
    {
        // Keep scan-wide totals and the end-of-scan volume observation independent
        // of folder navigation and of every other scan task's report.
        _scanReport = result;
        _scanReportVolumeUsedBytes = _volumeUsedBytes;
        _scanReportVolumeSpaceInfo = _volumeSpaceInfo;
        RebuildScanReadIssueRows(result);
        NotifyScanReportChanged();
    }

    private void RebuildScanReadIssueRows(ScanResult result)
    {
        ScanReadIssueRows = result.Issues
            .GroupBy(issue => issue.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ScanReadIssueRow(group.Key,
                string.Join("；", group.Select(DescribeReadIssue).Distinct()),
                string.Join("\n", group.Select(issue => L.F($"{L.T(issue.Operation)} · 错误 {issue.NativeErrorCode} · {issue.Message}")).Distinct())))
            .ToArray();
        OnPropertyChanged(nameof(ScanReadIssueRows));
    }

    private void NotifyScanReportChanged()
    {
        foreach (var name in new[] { nameof(HasScanReport), nameof(ScanReportPathText), nameof(ScanReportRecognizedText),
            nameof(ScanReportVolumeUsedText), nameof(IsWholeVolumeScanReport), nameof(IsDirectoryScanReport),
            nameof(ScanReportUnattributedText), nameof(ScanReportIssueSummaryText), nameof(ScanReportStrategyText),
            nameof(HasScanReportPolicyNotes), nameof(ScanReportReadAccessText),
            nameof(ScanReportSystemReservedText), nameof(ScanReportReserveDetailText),
            nameof(ScanReportReconciliationText), nameof(HasScanReadIssues), nameof(ScanReadIssueRows), nameof(ScanReportEmptyIssuesText) })
            OnPropertyChanged(name);
        OpenScanReportCommand.RaiseCanExecuteChanged();
    }

    private static string DescribeReadIssue(ScanReadIssue issue) => issue.NativeErrorCode switch
    {
        5 => L.T("访问被拒绝，当前账户没有读取权限"),
        32 => L.T("文件被其他程序占用，无法读取"),
        2 or 3 => L.T("扫描期间文件或目录已移除，或路径发生变化"),
        _ => string.IsNullOrWhiteSpace(issue.Message) ? L.T("读取失败，请查看错误代码") : L.T(issue.Message),
    };

    private static bool IsVolumeRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root)
                && string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed record ScanReadIssueRow(string Path, string ReasonText, string DetailText);

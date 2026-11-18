using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Fact]
    public void Scan_report_describes_the_actual_scan_privilege_without_claiming_complete_coverage() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        var vm = scene.Workspace.ActiveViewModel;
        var root = Tree(@"C:\");
        var result = new ScanResult(root, 1, 1, root.SizeBytes, TimeSpan.FromSeconds(1));
        var field = typeof(MainWindowViewModel).GetField("_scanReport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, result with { BackupReadEnabled = true });
        Assert.Contains("已启用", vm.ScanReportReadAccessText);
        Assert.DoesNotContain("全部", vm.ScanReportReadAccessText);
        field.SetValue(vm, result with { BackupReadFailureCode = 1300 });
        Assert.Contains("管理员", vm.ScanReportReadAccessText);
        field.SetValue(vm, result with { BackupReadFailureCode = 183 });
        Assert.Contains("常规权限", vm.ScanReportReadAccessText);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Scan_report_fits_three_themes_and_retains_each_tasks_paths_and_totals(string theme) => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("first");
        var first = scene.Workspace.ActiveViewModel;
        var scanRoot = Tree(@"C:\");
        ApplySnapshot(first, scanRoot);
        SetVolumeUsed(first, scanRoot.SizeBytes + 7L * 1024 * 1024 * 1024);
        CaptureReport(first, scanRoot, [new(@"C:\Restricted\" + new string('长', 150) + @"\file.dat", false, "read-allocation", 5, "Access denied"),
            new(@"c:\restricted\" + new string('长', 150) + @"\file.dat", false, "enumerate", 5, "Access denied"),
            new(@"C:\Busy.dat", false, "open", 32, "Sharing violation"),
            new(@"C:\Removed", true, "enumerate", 3, "Path not found")]);

        var root = Layout(scene.Window, 1024, 640);
        var entry = Named<Button>(scene.Window, "WorkbenchScanReportButton");
        Assert.True(Fits(entry, root));
        Assert.True(entry.IsEnabled);
        Assert.Equal("7 GB", first.ScanReportUnattributedText);
        Assert.True(first.IsWholeVolumeScanReport);
        Assert.False(first.IsDirectoryScanReport);
        Assert.Equal(3, first.ScanReadIssueRows.Count);
        Assert.Contains(first.ScanReadIssueRows, row => row.ReasonText.Contains("权限"));
        Assert.Contains(first.ScanReadIssueRows, row => row.ReasonText.Contains("占用"));
        Assert.Contains(first.ScanReadIssueRows, row => row.ReasonText.Contains("路径发生变化"));
        Assert.Equal(4L * 1024 * 1024 * 1024, first.ScanRoot!.SizeBytes);
        Execute(entry);
        root = Layout(scene.Window, 1024, 640);
        var panel = Named<Border>(scene.Window, "ScanReportPanel");
        Assert.True(Fits(panel, root), "The complete report must stay inside the minimum window bounds.");
        Assert.Same(panel, ActiveModal(scene.Window, first));
        Assert.False(first.ZoomIntoNodeCommand.CanExecute(first.ScanRoot.Children[0]));
        Assert.False(first.ScanCommand.CanExecute(null));
        var close = Descendants<Button>(panel).Single(button => Equals(button.Content, "关闭"));
        Assert.True(Fits(close, root));
        Save(Render(root), $"scan-report-theme{theme}-1024x640.png");
        var issuesList = Named<ListBox>(scene.Window, "ScanReadIssuesList");
        issuesList.ScrollIntoView(first.ScanReadIssueRows.Last());
        root = Layout(scene.Window, 1024, 640);
        var longPath = Descendants<TextBox>(issuesList).First(box => box.Text.Contains(new string('长', 100)));
        Assert.Equal(TextWrapping.Wrap, longPath.TextWrapping);
        Assert.True(longPath.ActualWidth <= panel.ActualWidth - panel.Padding.Left - panel.Padding.Right);
        // Offscreen ScrollViewer extents do not represent desktop scrolling.
        // Isolate the long row to render its wrapping without pretending a real
        // wheel operation has occurred in an unshown window.
        var report = (ScanResult)Field(first, "_scanReport")!;
        CaptureReport(first, scanRoot, [report.Issues[0]]);
        root = Layout(scene.Window, 1024, 640);
        Save(Render(root), $"scan-report-theme{theme}-long-path.png");
        CaptureReport(first, scanRoot, report.Issues);
        Execute(close);
        Assert.False(first.IsScanReportOpen);
        Assert.Null(ActiveModal(scene.Window, first));

        // Enter focus through the VM only; this test makes no claim about an OS
        // fullscreen transition or real keyboard focus in an unshown window.
        typeof(MainWindowViewModel).GetField("_isFocusMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, true);
        NotifyReportNavigation(first);
        Invoke(scene.Window, "SynchronizeOverlayVisibility", first);
        root = Layout(scene.Window, 1024, 640);
        var focusEntry = Named<Button>(scene.Window, "FocusModeScanReportButton");
        Assert.True(focusEntry.IsEnabled);
        Assert.True(Fits(focusEntry, root));
        first.OpenScanReportCommand.Execute(null);
        Layout(scene.Window, 1024, 640);
        Assert.Same(panel, ActiveModal(scene.Window, first));
        first.CloseScanReportCommand.Execute(null);
        Assert.True(first.IsFocusMode);
        typeof(MainWindowViewModel).GetField("_isFocusMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, false);

        first.OpenScanReportCommand.Execute(null);
        scene.AddCompletedTask("second");
        Assert.False(first.IsScanReportOpen);
        var second = scene.Workspace.ActiveViewModel;
        CaptureReport(second, second.ScanRoot!, [new(@"E:\Other\opaque", false, "open", 1234, "Original native detail")]);
        Assert.False(second.IsWholeVolumeScanReport);
        Assert.True(second.IsDirectoryScanReport);
        Assert.Single(second.ScanReadIssueRows);
        Assert.Equal("Original native detail", second.ScanReadIssueRows[0].ReasonText);
        scene.Workspace.SelectTab(scene.Workspace.TaskTabs[1]);
        Assert.Same(first, scene.Workspace.ActiveViewModel);
        Assert.Equal(3, first.ScanReadIssueRows.Count);
        Assert.Equal("7 GB", first.ScanReportUnattributedText);
        Assert.False(first.IsScanReportOpen);
    });

    [Fact]
    public void System_reserve_is_subtracted_once_and_report_retains_its_end_of_scan_snapshot() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        var vm = scene.Workspace.ActiveViewModel;
        var root = Tree(@"C:\");
        const long gb = 1024L * 1024 * 1024;
        SetVolumeUsed(vm, 11 * gb);
        var info = new VolumeSpaceInfo(20 * gb, 11 * gb)
        { AllocatedBytes = 6 * gb, ReservedBytes = 5 * gb, StorageReserveBytes = 4 * gb };
        typeof(MainWindowViewModel).GetField("_volumeSpaceInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, info);
        CaptureReport(vm, root, []);
        Assert.Equal("5 GB", vm.ScanReportSystemReservedText);
        Assert.Contains("存储保留 4 GB", vm.ScanReportReserveDetailText);
        Assert.Equal("2 GB", vm.ScanReportUnattributedText);
        Assert.Contains("已扣除系统预留", vm.ScanReportReconciliationText);
        typeof(MainWindowViewModel).GetField("_volumeSpaceInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, info with { ReservedBytes = 7 * gb });
        Assert.Equal("2 GB", vm.ScanReportUnattributedText);
        Assert.Equal("5 GB", vm.ScanReportSystemReservedText);
        Assert.Equal(4 * gb, root.SizeBytes);
        vm.OpenScanReportCommand.Execute(null);
        var view = Layout(scene.Window, 1280, 800);
        Directory.CreateDirectory(OutputDirectory);
        Save(Render(view), "scan-report-reserve-known.png");
    });

    [Fact]
    public void Unknown_or_negative_volume_difference_does_not_invent_a_file_allocation() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        var vm = scene.Workspace.ActiveViewModel;
        var root = Tree(@"C:\");
        SetVolumeUsed(vm, null);
        CaptureReport(vm, root, []);
        Assert.Equal("暂无法确定", vm.ScanReportUnattributedText);
        Assert.Contains("未能读取", vm.ScanReportReconciliationText);
        SetVolumeUsed(vm, 1);
        CaptureReport(vm, root, []);
        Assert.Equal("暂无法确定", vm.ScanReportUnattributedText);
        Assert.Contains("高于", vm.ScanReportReconciliationText);
        Assert.Equal(4L * 1024 * 1024 * 1024, root.SizeBytes);
    });

    [Fact]
    public void Closed_settings_combo_wheel_is_forwarded_to_page_without_changing_provider() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with
        {
            AiConnectionProfileId = AiConnectionOption.DeepSeekId,
            ProviderKind = AiProviderKind.OpenAiCompatible,
            AiEndpoint = AiConnectionOption.DeepSeekEndpoint,
            AiModel = AiConnectionOption.DeepSeekModel,
        });
        var vm = scene.Workspace.ActiveViewModel;
        vm.ApiKey = "test-only-placeholder";
        vm.SelectSettingsSectionCommand.Execute("connection");
        vm.OpenSettingsCommand.Execute(null);
        Layout(scene.Window, 1024, 640);
        var combo = Named<ComboBox>(scene.Window, "AiConnectionSelector");
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(scene.Window, "AiConnectionFields").Visibility);
        var before = combo.SelectedValue;
        var page = Ancestor<ScrollViewer>(combo);
        Assert.Same(Named<ScrollViewer>(scene.Window, "SettingsContentScrollViewer"), page);
        Assert.Equal("connection", vm.SettingsSection);
        Assert.True(combo.ActualHeight > 0);
        var forwarded = 0;
        page.AddHandler(Mouse.MouseWheelEvent, new MouseWheelEventHandler((_, _) => forwarded++), handledEventsToo: true);
        var closedWheel = Wheel(combo);
        combo.RaiseEvent(closedWheel);
        Layout(scene.Window, 1024, 640);
        Assert.Equal(before, combo.SelectedValue);
        Assert.True(closedWheel.Handled);
        Assert.Equal(1, forwarded);
        Assert.Equal(before, combo.SelectedValue);
    });

    private static MouseWheelEventArgs Wheel(ComboBox source) => new(Mouse.PrimaryDevice, Environment.TickCount, -120)
    { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = source };
    private static object? ActiveModal(MainWindow window, MainWindowViewModel vm) =>
        typeof(MainWindow).GetMethod("GetActiveModalPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [vm]);
    private static void SetVolumeUsed(MainWindowViewModel vm, long? value)
    {
        typeof(MainWindowViewModel).GetField("_volumeUsedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        typeof(MainWindowViewModel).GetField("_volumeSpaceInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, null);
    }
    private static void NotifyReportNavigation(MainWindowViewModel vm)
    {
        var notify = typeof(DiskLens.App.Infrastructure.ObservableObject).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        notify.Invoke(vm, [nameof(vm.IsFocusModeNavigationVisible)]);
        notify.Invoke(vm, [nameof(vm.IsFocusModeToggleVisible)]);
    }
    private static void CaptureReport(MainWindowViewModel vm, FileSystemNode root, IReadOnlyList<ScanReadIssue> issues) =>
        Invoke(vm, "CaptureScanReport", new ScanResult(root, 1, 1, root.SizeBytes, TimeSpan.FromSeconds(1))
        { Issues = issues, ScanIssueCount = issues.Select(issue => issue.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() });
}

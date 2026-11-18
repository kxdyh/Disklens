using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class ScanLocationPickerSourceTests
{
    [Fact]
    public void Scan_and_location_selection_commands_should_respect_the_open_picker_state()
    {
        var viewModel = ReadViewModel();

        Assert.Contains("() => CanStartNewScan && !IsScanLocationPickerOpen", viewModel, StringComparison.Ordinal);
        Assert.Contains("private bool CanBrowseForLocation => IsScanLocationPickerOpen", viewModel, StringComparison.Ordinal);
        Assert.Contains("private bool CanSelectScanRoot => IsScanLocationPickerOpen", viewModel, StringComparison.Ordinal);
        Assert.Contains("&& !_isExternalLocationPickerOpen", viewModel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BrowseScanFolderAsync", "_folderPickerService.PickFolder(initialDirectory)", "文件夹")]
    public void Native_picker_should_run_once_and_release_the_shared_guard_on_cancel_or_error(
        string methodName,
        string pickerCall,
        string errorKind)
    {
        var method = ReadMethod(ReadViewModel(), methodName);

        Assert.Equal(1, CountOccurrences(method, pickerCall));
        Assert.Contains("if (!TryBeginExternalLocationPicker())", method, StringComparison.Ordinal);
        Assert.Contains("finally", method, StringComparison.Ordinal);
        Assert.Contains("EndExternalLocationPicker();", method, StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(selected", method, StringComparison.Ordinal);
        Assert.Contains("await StartScanAtPathAsync(selected", method, StringComparison.Ordinal);
        Assert.Contains($"ReportLocationPickerError(\"{errorKind}\", ex);", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Completing_folder_selection_should_close_the_location_modal_before_starting_one_scan()
    {
        var method = ReadMethod(ReadViewModel(), "StartScanAtPathAsync");

        var closePicker = method.IndexOf("IsScanLocationPickerOpen = false;", StringComparison.Ordinal);
        var startScan = method.IndexOf("Workspace.StartScanAsync(selectedRootPath, this)", StringComparison.Ordinal);
        Assert.True(closePicker >= 0 && startScan > closePicker);
        var standaloneScan = ReadMethod(ReadViewModel(), "RunScanAtPathAsync");
        Assert.True(standaloneScan.IndexOf("IsScanning = true;", StringComparison.Ordinal)
            > standaloneScan.IndexOf("IsScanLocationPickerOpen = false;", StringComparison.Ordinal));
    }

    [Fact]
    public void Location_picker_should_offer_drives_and_folders_without_single_file_scanning()
    {
        var source = ReadViewModel();
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        Assert.DoesNotContain("BrowseScanFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BrowseScanFile", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("单独查看一个文件", xaml, StringComparison.Ordinal);
        Assert.Contains("扫描整个盘", xaml, StringComparison.Ordinal);
        Assert.Contains("选择此盘中的文件夹…", xaml, StringComparison.Ordinal);
        Assert.Contains("if (!Directory.Exists(selectedRootPath))", ReadMethod(source, "StartScanAtPathAsync"), StringComparison.Ordinal);
    }

    private static string ReadViewModel()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));
    }

    private static string ReadMethod(string source, string methodName)
    {
        var signature = source.IndexOf($"private async Task {methodName}(", StringComparison.Ordinal);
        Assert.True(signature >= 0, $"Could not find {methodName}.");

        var bodyStart = source.IndexOf('{', signature);
        Assert.True(bodyStart >= 0, $"Could not find body for {methodName}.");
        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[bodyStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not find end of {methodName}.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var nextIndex = 0;
        while ((nextIndex = source.IndexOf(value, nextIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            nextIndex += value.Length;
        }

        return count;
    }
}

using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class HomeLandingMarkupTests
{
    [Fact]
    public void Home_should_replace_the_empty_workspace_and_bind_both_real_entry_commands()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DiskLens.App", "MainWindow.xaml"));
        var home = File.ReadAllText(Path.Combine(root, "src", "DiskLens.App", "Controls", "HomeLandingControl.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("<controls:HomeLandingControl x:Name=\"HomeLanding\" Grid.RowSpan=\"4\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StartScanCommand=\"{Binding ScanCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BrowseFolderCommand=\"{Binding BrowseFolderFromHomeCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataTrigger Binding=\"{Binding HasScanWorkspace}\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new AsyncRelayCommand(BrowseFolderFromHomeAsync, () => CanBrowseFromHome)", viewModel, StringComparison.Ordinal);
        Assert.Contains("_folderPickerService.PickFolder(initialDirectory)", viewModel, StringComparison.Ordinal);
        Assert.Contains("StartScanAtPathAsync(selectedRootPath)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("可视化预演 / 非扫描结果", home, StringComparison.Ordinal);
        Assert.DoesNotContain("空间映射 · 功能示意", home, StringComparison.Ordinal);
        Assert.Contains("MouseMove += OnPointerMoved", home, StringComparison.Ordinal);
        Assert.Contains("MouseLeave += OnPointerLeftControl", home, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering += OnRendering", home, StringComparison.Ordinal);
        Assert.Contains("_targetArtworkProgress = Math.Clamp((pointer.X - bounds.Left) / bounds.Width, 0, 1)", home, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayEntrance(", home, StringComparison.Ordinal);
        Assert.Contains("HomeLanding.ApplyMotionPreference(motionViewModel.IsReduceMotionEnabled)", File.ReadAllText(Path.Combine(root, "src", "DiskLens.App", "MainWindow.xaml.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01", "=> 1")]
    [InlineData("02", "=> 2")]
    [InlineData("03", "=> 3")]
    public void Each_persisted_theme_should_select_its_matching_home_design(string themeId, string mapping)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var home = File.ReadAllText(Path.Combine(root, "src", "DiskLens.App", "Controls", "HomeLandingControl.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "DiskLens.App", "MainWindow.xaml.cs"));

        Assert.Contains($"\"{themeId}\" {mapping}", home, StringComparison.Ordinal);
        Assert.Contains("HomeLanding.ApplyTheme(normalizedThemeId, reduceMotion)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("public void ApplyTheme(", home, StringComparison.Ordinal);
    }
}

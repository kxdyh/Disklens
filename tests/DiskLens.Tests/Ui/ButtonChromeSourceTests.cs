using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class ButtonChromeSourceTests
{
    [Fact]
    public void Hand_drawn_button_style_should_disable_default_focus_visual_rectangle()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("FocusVisualStyle\" Value=\"{x:Null}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_result_action_row_should_not_force_too_small_button_height()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var xamlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("ItemHeight=\"22\"", xaml, StringComparison.Ordinal);
    }
}

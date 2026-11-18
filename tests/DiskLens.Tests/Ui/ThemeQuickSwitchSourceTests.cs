using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class ThemeQuickSwitchSourceTests
{
    [Fact]
    public void Main_footer_should_offer_all_three_themes_as_selected_state_buttons()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var markup = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "MainWindow.xaml"));
        var viewModel = LocalizedXamlSource.ReadAllText(Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("FooterThemeChoiceStyle", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsNightThemeSelected, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsWarmThemeSelected, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsDayThemeSelected, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"夜间模式\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"暖色模式\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"日间模式\"", markup, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"切换到暖色模式\"", markup, StringComparison.Ordinal);
        Assert.Contains("<Path Data=\"M 14.7,2.2 A 7.1,7.1", markup, StringComparison.Ordinal);
        Assert.Contains("<Ellipse Canvas.Left=\"6\" Canvas.Top=\"2\"", markup, StringComparison.Ordinal);
        Assert.Contains("SelectThemeCommand", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ThemeOptions}\"", markup, StringComparison.Ordinal);
    }
}

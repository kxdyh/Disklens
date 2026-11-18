using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Settings_categories_are_exclusive_and_the_ai_shortcut_opens_analysis(string theme) => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        using var scene = new Scene(AppSettings.Default() with { ThemeId = theme });
        scene.AddCompletedTask("settings-categories");
        var vm = scene.Workspace.ActiveViewModel;
        vm.OpenSettingsCommand.Execute(null);
        Layout(scene.Window, 1000, 640);
        var navigation = Named<StackPanel>(scene.Window, "SettingsNavigation");
        var scroll = Named<ScrollViewer>(scene.Window, "SettingsContentScrollViewer");
        var sections = Assert.IsType<StackPanel>(scroll.Content).Children.OfType<StackPanel>()
            .ToDictionary(panel => (string)Assert.Single(panel.Style.Triggers.OfType<DataTrigger>()).Value, panel => panel);
        Assert.Equal(new[] { "analysis", "appearance", "connection", "sharing", "updates" }, sections.Keys.OrderBy(key => key));
        var choices = navigation.Children.OfType<Button>().ToArray();
        Assert.Equal(5, choices.Length);

        foreach (var size in new[] { new Size(1000, 640), new Size(1440, 860) })
        {
            foreach (var choice in choices)
            {
                Execute(choice);
                scroll.ScrollToTop();
                var root = Layout(scene.Window, (int)size.Width, (int)size.Height);
                var selected = Assert.IsType<string>(choice.CommandParameter);
                Assert.Equal(selected, vm.SettingsSection);
                Assert.True(Fits(Named<Border>(scene.Window, "SettingsPanel"), root));
                Assert.True(Fits(navigation, root));
                Assert.True(Fits(scroll, root));
                Assert.True(Bounds(navigation, root).Right <= Bounds(scroll, root).Left);
                foreach (var section in sections)
                    Assert.Equal(section.Key == selected ? Visibility.Visible : Visibility.Collapsed, section.Value.Visibility);
                Assert.Single(sections.Values, panel => panel.Visibility == Visibility.Visible);
                Assert.True(sections[selected].ActualHeight > 0, "The active category must have laid-out content.");
                Assert.Equal(((SolidColorBrush)scene.Window.FindResource("PrimaryButtonTextBrush")).Color,
                    Assert.IsType<SolidColorBrush>(choice.Foreground).Color);
                Save(Render(root), $"settings-{theme}-{selected}-{size.Width}x{size.Height}.png");
            }
        }

        vm.CloseSettingsCommand.Execute(null);
        vm.SettingsSection = "connection";
        Layout(scene.Window, 1000, 640);
        var shortcut = Named<Button>(scene.Window, "AiAnalysisSettingsShortcut");
        Assert.Same(vm.OpenAiAnalysisSettingsCommand, shortcut.Command);
        Execute(shortcut);
        Layout(scene.Window, 1000, 640);
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal("analysis", vm.SettingsSection);
        Assert.Equal(Visibility.Visible, sections["analysis"].Visibility);
        Assert.All(sections.Where(section => section.Key != "analysis"),
            section => Assert.Equal(Visibility.Collapsed, section.Value.Visibility));
    });

    [Fact]
    public void Disclaimer_opt_out_is_saved_and_restored_by_shared_settings() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        scene.AddCompletedTask("disclaimer-source");
        var vm = scene.Workspace.ActiveViewModel;
        var root = Layout(scene.Window, 1000, 640);
        var alert = Named<Border>(scene.Window, "AiDisclaimerAlert");
        Assert.True(Fits(alert, root));
        Assert.Equal(Color.FromArgb(0x20, 0x2d, 0x8c, 0xd9), Assert.IsType<SolidColorBrush>(alert.Background).Color);
        var optOut = Assert.Single(Descendants<CheckBox>(alert), checkbox => Equals(checkbox.Content, "不再提示"));
        Assert.Equal(nameof(vm.HideAiDisclaimer), BindingOperations.GetBinding(optOut, ToggleButton.IsCheckedProperty)!.Path.Path);
        optOut.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Layout(scene.Window, 1000, 640);
        Assert.True(vm.HideAiDisclaimer);
        Assert.False(vm.ShowAiDisclaimerAlert);
        Assert.Equal(Visibility.Collapsed, alert.Visibility);
        Complete(scene.Workspace.SaveSharedSettingsAsync(vm));
        var load = new AppSettingsStore().LoadAsync();
        Complete(load);
        var persisted = load.GetAwaiter().GetResult();
        Assert.True(persisted.HideAiDisclaimer);
        var captured = (AppSettings)typeof(MainWindowViewModel).GetMethod("CaptureSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;
        Assert.True(captured.HideAiDisclaimer);

        using var receiver = new Scene(AppSettings.Default());
        receiver.AddCompletedTask("disclaimer-receiver");
        var other = receiver.Workspace.ActiveViewModel;
        Assert.False(other.HideAiDisclaimer);
        Invoke(other, "ApplySharedSettings", persisted, string.Empty);
        Layout(receiver.Window, 1000, 640);
        Assert.True(other.HideAiDisclaimer);
        Assert.False(other.ShowAiDisclaimerAlert);
        Assert.Equal(Visibility.Collapsed, Named<Border>(receiver.Window, "AiDisclaimerAlert").Visibility);
        Invoke(other, "ApplySharedSettings", persisted with { HideAiDisclaimer = false }, string.Empty);
        root = Layout(receiver.Window, 1000, 640);
        Assert.False(other.HideAiDisclaimer);
        Assert.True(other.ShowAiDisclaimerAlert);
        Assert.True(Fits(Named<Border>(receiver.Window, "AiDisclaimerAlert"), root));
    });

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Submission_dialog_keeps_the_real_scope_four_row_view_and_actions_inside_small_layouts(string theme) => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        var preview = ConfirmationPreview();
        var window = new AiAnalysisConfirmationWindow(preview, theme);
        try
        {
            LayoutConfirmation(window, 700, 600);
            Assert.Same(preview, window.Preview);
            Assert.Equal(preview.RootPath, ((TextBlock)window.FindName("ScopePathText")).Text);
            Assert.Equal(preview.RootPath, ((TextBlock)window.FindName("ScopePathText")).ToolTip);
            Assert.StartsWith("候选共 13 项 · 需要 AI 8 项", window.SummaryText);
            Assert.Contains("本地历史 2 项", window.SummaryText);
            Assert.Contains("云端经验 3 项", window.SummaryText);
            Assert.DoesNotContain("保护规则", window.SummaryText);
            var list = (ListBox)window.FindName("CandidateList");
            Assert.Equal(8, list.Items.Count);
            Assert.Equal(288d, list.MaxHeight);
            Assert.Equal(72d, Assert.Single(list.ItemContainerStyle.Setters.OfType<Setter>(),
                setter => setter.Property == FrameworkElement.HeightProperty).Value);
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(list));
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(list));
            Assert.Equal(((SolidColorBrush)window.FindResource("PrimaryTextBrush")).Color,
                Assert.IsType<SolidColorBrush>(window.Foreground).Color);
            Assert.Equal(((SolidColorBrush)window.FindResource("CardBackgroundBrush")).Color,
                Assert.IsType<SolidColorBrush>(window.Background).Color);

            // 700x600 content leaves screen chrome allowance within a 1000x640 workspace.
            // These are production content-tree constraints, not an OS window-placement claim.
            foreach (var size in new[] { new Size(700, 600), new Size(700, 668) })
            {
                var root = LayoutConfirmation(window, (int)size.Width, (int)size.Height);
                foreach (var name in new[] { "ScopePathText", "SearchBox", "TypeFilter", "FilterCountText", "CandidateList", "ConfirmButton" })
                    Assert.True(Fits((FrameworkElement)window.FindName(name), root), name + " must fit the confirmation content.");
                var confirm = (Button)window.FindName("ConfirmButton");
                var cancel = Assert.Single(Descendants<Button>(root), button => Equals(button.Content, "取消"));
                Assert.True(confirm.IsDefault && confirm.IsEnabled);
                Assert.True(cancel.IsCancel && Fits(cancel, root));
                Assert.True(Bounds(cancel, root).Right <= Bounds(confirm, root).Left);
                Assert.True(Bounds(list, root).Bottom <= Bounds(confirm, root).Top);
                Assert.InRange(list.ActualHeight, 1, 288);
                var visibleRows = Descendants<ListBoxItem>(list).Where(item =>
                    Rect.Intersect(Bounds(item, list), new Rect(list.RenderSize)).Height > 1).ToArray();
                Assert.InRange(visibleRows.Length, 1, 4);
                foreach (var row in visibleRows)
                {
                    Assert.Equal(72d, row.Height);
                    foreach (var text in Descendants<TextBlock>(row))
                        Assert.True(Bounds(row, root).Contains(Bounds(text, root)), "Candidate text must not draw outside its row.");
                }
                Save(RenderConfirmation(window, root, (int)size.Width, (int)size.Height),
                    $"ai-confirmation-theme{theme}-{size.Width}x{size.Height}.png");
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Submission_search_and_type_filter_only_change_the_visible_rows() => OnSta(() =>
    {
        var preview = ConfirmationPreview();
        var originalPaths = preview.Items.Select(item => item.Path).ToArray();
        var window = new AiAnalysisConfirmationWindow(preview, "03");
        try
        {
            LayoutConfirmation(window, 700, 600);
            var search = (TextBox)window.FindName("SearchBox");
            var type = (ComboBox)window.FindName("TypeFilter");
            var list = (ListBox)window.FindName("CandidateList");
            search.Text = "  CACHE  ";
            LayoutConfirmation(window, 700, 600);
            Assert.Equal(3, list.Items.Count);
            type.SelectedIndex = 1;
            LayoutConfirmation(window, 700, 600);
            Assert.Equal(preview.Items.Single(item => item.Name == "Cache").Path, Assert.Single(SubmissionPaths(list)));
            type.SelectedIndex = 2;
            LayoutConfirmation(window, 700, 600);
            Assert.Equal(new[] { "cache-one.zip", "CACHE-two.bin" }.OrderBy(name => name),
                SubmissionPaths(list).Select(Path.GetFileName).OrderBy(name => name));
            type.SelectedIndex = 0;
            search.Text = preview.RootPath + @"\Documents";
            LayoutConfirmation(window, 700, 600);
            Assert.Equal(search.Text, Assert.Single(SubmissionPaths(list)));
            search.Text = "no-such-candidate";
            LayoutConfirmation(window, 700, 600);
            Assert.Empty(list.Items);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("EmptyMessage")).Visibility);
            Assert.Contains("显示 0 / 8 项", ((TextBlock)window.FindName("FilterCountText")).Text);
            Assert.True(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Equal(originalPaths, preview.Items.Select(item => item.Path));
            search.Clear();
            LayoutConfirmation(window, 700, 600);
            Assert.Equal(8, list.Items.Count);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("EmptyMessage")).Visibility);
        }
        finally { window.Close(); }
    });

    private static AiAnalysisSubmissionPreview ConfirmationPreview()
    {
        const string scope = @"E:\真实分析范围\当前打开目录";
        var entries = new (string Name, bool Directory)[]
        {
            ("Cache", true), ("Documents", true), ("cache-one.zip", false), ("CACHE-two.bin", false),
            ("notes.txt", false), ("project.log", false), ("Packages", true),
            ("archive-with-a-long-name-for-small-window-ellipsis.zip", false),
        };
        var records = entries.Select((entry, index) =>
        {
            var path = scope + "\\" + entry.Name;
            var node = new FileSystemNode(path, entry.Name, entry.Directory ? "" : Path.GetExtension(path),
                (index + 1L) * 1024 * 1024, entry.Directory ? FileAttributes.Directory : FileAttributes.Normal,
                null, null, entry.Directory, false, [], []);
            return new AiCandidateRecord(path, entry.Name, node.SizeBytes, entry.Directory, 0, false,
                "confirmation-fixture-" + index, node);
        }).ToArray();
        return new AiAnalysisSubmissionPreview(scope, records, LocalCacheCount: 2, SharedExperienceCount: 3, ProtectedCount: 1);
    }

    private static FrameworkElement LayoutConfirmation(Window window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        for (var pass = 0; pass < 3; pass++)
        {
            Pump();
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        return root;
    }

    private static BitmapSource RenderConfirmation(Window window, FrameworkElement root, int width, int height)
    {
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(background);
        // The content keeps its arranged 28px margin; the bitmap includes the full canvas.
        image.Render(root);
        image.Freeze();
        return image;
    }

    private static string[] SubmissionPaths(ItemsControl list) => list.Items.Cast<object>()
        .Select(row => (string)row.GetType().GetProperty("Path")!.GetValue(row)!).ToArray();
}

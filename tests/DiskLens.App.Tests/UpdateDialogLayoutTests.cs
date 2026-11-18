using System.Runtime.ExceptionServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskLens.App.Services;
using DiskLens.Core.Updates;

namespace DiskLens.App.Tests;

public sealed class UpdateDialogLayoutTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Release_dialog_fits_three_themes_with_readable_notes_and_all_actions(string theme) => OnSta(() =>
    {
        using var coordinator = new UpdateCheckCoordinator(new EmptyFeed(), new EmptyPreferences(), new Version(1, 1, 0));
        var release = new ReleaseManifest(new Version(1, 2, 0), new DateOnly(2026, 11, 6), ReleaseManifest.OfficialDownloadPage,
            ["AI 辅助分析支持扫描根目录或当前目录，清楚显示分析进度、Token 与费用。",
             "文件变化自动同步，不需要手动刷新。",
             "多个扫描任务之间保持独立，新增安装器与更新检测。"]);
        var window = new UpdateAvailableWindow(release, coordinator);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/Resources/Themes/Theme{theme}.xaml") });
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(550, 600));
        content.Arrange(new Rect(new Size(550, content.DesiredSize.Height)));
        content.UpdateLayout();
        Assert.InRange(content.DesiredSize.Height, 250, 560);
        var notes = Assert.IsType<ItemsControl>(window.FindName("ReleaseNotes"));
        Assert.Equal(3, notes.Items.Count);
        Assert.Equal("DiskLens 1.1.0  →  1.2.0", ((TextBlock)window.FindName("VersionSummary")).Text);
        foreach (var name in new[] { "DismissUpdateButton", "IgnoreUpdateButton", "OpenDownloadPageButton" })
        {
            var button = Assert.IsType<Button>(window.FindName(name));
            Assert.True(button.IsVisible || button.Visibility == Visibility.Visible);
            Assert.InRange(button.ActualWidth, 75, 190);
            Assert.InRange(button.ActualHeight, 30, 70);
        }
        Assert.IsAssignableFrom<Brush>(window.FindResource("PrimaryTextBrush"));
        Assert.True(((Button)window.FindName("DismissUpdateButton")).IsCancel);
        Assert.True(((Button)window.FindName("OpenDownloadPageButton")).IsDefault);
        WriteOptionalScreenshot(window, content, theme);
        window.Close();
    });

    [Fact]
    public void Maximum_release_notes_use_scroll_area_instead_of_expanding_beyond_small_screen() => OnSta(() =>
    {
        using var coordinator = new UpdateCheckCoordinator(new EmptyFeed(), new EmptyPreferences(), new Version(1, 1, 0));
        var release = new ReleaseManifest(new Version(1, 2, 0), new DateOnly(2026, 11, 6), ReleaseManifest.OfficialDownloadPage,
            Enumerable.Repeat(string.Concat(Enumerable.Repeat("这是更新内容，用于验证长文本在小屏幕中可以滚动阅读。", 16)), 12).ToArray());
        var window = new UpdateAvailableWindow(release, coordinator);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/DiskLens.App;component/Resources/Themes/Theme03.xaml") });
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(550, 560));
        content.Arrange(new Rect(new Size(550, content.DesiredSize.Height)));
        content.UpdateLayout();
        Assert.InRange(content.DesiredSize.Height, 350, 560);
        var notes = (ItemsControl)window.FindName("ReleaseNotes");
        var parent = VisualTreeHelper.GetParent(notes);
        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        var scroller = Assert.IsType<ScrollViewer>(parent);
        Assert.True(scroller.ScrollableHeight > 0);
        Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
        Assert.True(((Button)window.FindName("OpenDownloadPageButton")).ActualHeight > 30);
        WriteOptionalScreenshot(window, content, "03-长说明-小屏");
        window.Close();
    });

    private static void WriteOptionalScreenshot(Window window, FrameworkElement content, string theme)
    {
        var directory = Environment.GetEnvironmentVariable("DISKLENS_UPDATE_LAYOUT_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        window.Content = null;
        var canvas = new Border { Width = 550, Background = (Brush)window.FindResource("CardBackgroundBrush"), Child = content };
        foreach (var resource in window.Resources.MergedDictionaries) canvas.Resources.MergedDictionaries.Add(resource);
        foreach (var key in window.Resources.Keys) canvas.Resources[key] = window.Resources[key];
        TextElement.SetForeground(canvas, (Brush)window.FindResource("PrimaryTextBrush"));
        TextElement.SetFontFamily(canvas, window.FontFamily);
        TextElement.SetFontSize(canvas, window.FontSize);
        canvas.Measure(new Size(550, 600));
        canvas.Arrange(new Rect(new Size(550, canvas.DesiredSize.Height)));
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1100, (int)Math.Ceiling(canvas.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, $"更新提示-测试版本1.2.0-主题{theme}.png"));
        encoder.Save(stream);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA layout test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class EmptyFeed : IReleaseFeedClient
    { public Task<ReleaseManifest?> FetchAsync(CancellationToken cancellationToken) => Task.FromResult<ReleaseManifest?>(null); }
    private sealed class EmptyPreferences : IUpdatePreferenceStore
    {
        public Task<string?> LoadIgnoredVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SaveIgnoredVersionAsync(string version, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

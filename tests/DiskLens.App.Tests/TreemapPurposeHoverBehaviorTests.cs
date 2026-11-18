using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskLens.App.Controls;
using DiskLens.App.Localization;
using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class TreemapPurposeHoverBehaviorTests
{
    [Theory]
    [InlineData("zh", "01")]
    [InlineData("zh", "02")]
    [InlineData("zh", "03")]
    [InlineData("en", "01")]
    [InlineData("en", "02")]
    [InlineData("en", "03")]
    public void Hovering_drawn_tiles_opens_the_full_short_purpose_and_switches_between_tiles_without_leaving_the_control(
        string language, string theme) => OnSta(() =>
    {
        L.SetLanguage(language);
        var control = new TreemapControl { Width = 130, Height = 140, EnableViewportTransitions = false };
        ToolTipService.SetInitialShowDelay(control, 10);
        ToolTipService.SetShowDuration(control, 10_000);
        var host = new Window
        {
            Content = control, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10_000, Top = -10_000,
        };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/Resources/Themes/Theme{theme}.xaml") });
        var first = Node(".nuget", "NuGet 用户缓存与配置", "NuGet user packages, cached dependencies, and configuration");
        var second = Node("设置", "Gradle 用户缓存目录", "Gradle user caches and build metadata");
        try
        {
            host.Show();
            Pump();

            // All drawn tiles are one WPF input element. Its automatic service sees
            // the same owner before our MouseMove handler assigns the first tooltip,
            // and again as the pointer moves between tiles. Exercise that real service
            // plus the production input handler, rather than calling the tooltip factory.
            NotifyWpfTooltipService(control);
            Hover(control, first);
            NotifyWpfTooltipService(control);
            var firstTip = Assert.IsType<ToolTip>(control.ToolTip);
            firstTip.Opacity = 0; // Open a real popup without flashing over the user's desktop.
            var opened = 0;
            firstTip.Opened += (_, _) => opened++;
            WaitUntil(() => firstTip.IsOpen, "Entering a drawn tile must open its full-purpose popup.");
            Assert.True(opened > 0, "The real WPF popup must raise Opened.");
            AssertTooltip(firstTip, first, language, host);

            Hover(control, second);
            NotifyWpfTooltipService(control); // Same TreemapControl; no new MouseEnter.
            var secondTip = Assert.IsType<ToolTip>(control.ToolTip);
            secondTip.Opacity = 0;
            WaitUntil(() => secondTip.IsOpen, "Moving directly to another tile must reopen the correct popup.");
            Assert.False(firstTip.IsOpen);
            AssertTooltip(secondTip, second, language, host);

            typeof(TreemapControl).GetMethod("OnMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(control, [new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent }]);
            Pump();
            Assert.False(secondTip.IsOpen);
            Hover(control, second); // Selecting a tile must not disable later hover within that same tile.
            var afterClickTip = Assert.IsType<ToolTip>(control.ToolTip);
            afterClickTip.Opacity = 0;
            WaitUntil(() => afterClickTip.IsOpen, "Hovering a selected tile must still reveal its full short purpose.");
            AssertTooltip(afterClickTip, second, language, host);

            typeof(TreemapControl).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(control, [new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)]);
            Pump();
            Assert.False(afterClickTip.IsOpen, "Leaving the treemap must close the popup.");

            Hover(control, first);
            var finalTip = Assert.IsType<ToolTip>(control.ToolTip);
            finalTip.Opacity = 0;
            WaitUntil(() => finalTip.IsOpen, "Hover must work again after leaving.");
            host.Close();
            Pump();
            Assert.False(finalTip.IsOpen, "An unloaded treemap must close its popup.");
        }
        finally
        {
            if (control.ToolTip is ToolTip tip) tip.IsOpen = false;
            host.Close();
            L.SetLanguage("zh");
        }
    });

    private static void Hover(TreemapControl control, FileSystemNode node)
    {
        // Supply a hit region, as in the existing treemap input tests, so no
        // physical cursor movement or foreground-window changes are required.
        typeof(TreemapControl).GetField("_rectangles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(control, new TreemapRectangle[] { new(node, -1_000_000, -1_000_000, 2_000_000, 2_000_000, 1) });
        typeof(TreemapControl).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent }]);
    }

    private static void NotifyWpfTooltipService(TreemapControl control)
    {
        var service = typeof(FrameworkElement).GetProperty("PopupControlService", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        service.GetType().GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [control]);
    }

    private static void AssertTooltip(ToolTip tooltip, FileSystemNode node, string language, Window host)
    {
        var card = Assert.IsType<Border>(tooltip.Content);
        var blocks = Assert.IsType<StackPanel>(card.Child).Children.OfType<TextBlock>().ToArray();
        var fullPurpose = language == "en" ? node.Analysis!.ShortPurposeEn : node.Analysis!.ShortPurpose;
        var purpose = Assert.Single(blocks, block => block.Text == fullPurpose);
        Assert.Equal(TextTrimming.None, purpose.TextTrimming);
        Assert.Equal(TextWrapping.Wrap, purpose.TextWrapping);
        Assert.Contains(blocks, block => block.Text == node.Name);
        Assert.DoesNotContain(blocks, block => block.Text == node.Analysis!.Reason || block.Text == node.Analysis!.ReasonEn);
        var pathViewer = Assert.Single(Assert.IsType<StackPanel>(card.Child).Children.OfType<ScrollViewer>());
        Assert.Equal(node.Path, Assert.IsType<TextBlock>(pathViewer.Content).Text);
        tooltip.UpdateLayout();
        Assert.True(purpose.ActualWidth > 0 && purpose.ActualHeight > 0);
        Assert.Equal(((SolidColorBrush)host.FindResource("PrimaryTextBrush")).Color,
            Assert.IsType<SolidColorBrush>(purpose.Foreground).Color);
    }

    private static FileSystemNode Node(string name, string purpose, string purposeEn)
    {
        var path = @"C:\Users\中文目录\" + name;
        return new FileSystemNode(path, name, "", 972L * 1024 * 1024, FileAttributes.Directory,
            null, null, true, false, [], [], Analysis: new AiAnalysisResult(path, RiskLevel.Review,
                SuggestedAction.Review, "右侧专用详细分析，不应出现在短用途悬停框", .9)
            {
                ShortPurpose = purpose, ShortPurposeEn = purposeEn,
                ReasonEn = "Detailed analysis belongs in the sidebar.",
            });
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, message);
            Pump();
            Thread.Sleep(1);
        }
        Pump();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Treemap hover popup test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

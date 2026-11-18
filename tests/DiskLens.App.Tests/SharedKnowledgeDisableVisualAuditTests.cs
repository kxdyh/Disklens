using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskLens.App.Models;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Disable_dialog_visual_audit_captures_three_themes_without_clipping(string theme) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.GetTempPath(), "shared-disable-visual-audit");
        Directory.CreateDirectory(directory);
        var observations = new List<object>();
        foreach (var width in new[] { 460, 320 })
        {
            var dialog = new SharedKnowledgeDisableWindow(fixture.Runtime, theme)
            {
                Width = width, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10_000, Top = -10_000
            };
            try
            {
                dialog.Show(); dialog.UpdateLayout(); Pump();
                var root = (StackPanel)dialog.Content;
                var clientSize = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right,
                    root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
                var text = root.Children.OfType<TextBlock>().Where(item => item.IsVisible).ToArray();
                Assert.Equal(2, text.Length);
                Assert.Equal("是否关闭云端经验共享？", text[0].Text);
                Assert.Equal("当前已开启云端经验共享功能，是否关闭？", text[1].Text);
                var background = (SolidColorBrush)dialog.Background;
                foreach (var item in text)
                {
                    AssertDisableAuditBounds(item, root);
                    Assert.True(Contrast(((SolidColorBrush)item.Foreground).Color, background.Color) >= 4.5);
                }
                var keep = (Button)dialog.FindName("KeepEnabledButton");
                var disable = (Button)dialog.FindName("DisableSharingButton");
                var buttonContrast = new Dictionary<string, double>();
                foreach (var button in new[] { keep, disable })
                {
                    AssertDisableAuditBounds(button, root);
                    var ratio = Contrast(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)button.Background).Color);
                    Assert.True(ratio >= 4.5, $"{button.Name} contrast is only {ratio:0.00}:1 in theme {theme}.");
                    buttonContrast[button.Name] = ratio;
                }
                Assert.True(keep.TransformToAncestor(root).TransformBounds(new Rect(keep.RenderSize)).Right
                    <= disable.TransformToAncestor(root).TransformBounds(new Rect(disable.RenderSize)).Left);
                Assert.Equal(ResizeMode.NoResize, dialog.ResizeMode);
                Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(background, null, new Rect(clientSize));
                    var brush = new VisualBrush(root)
                    {
                        Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(root), root.RenderSize)
                    };
                    drawing.DrawRectangle(brush, null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
                }
                var image = new RenderTargetBitmap((int)Math.Ceiling(clientSize.Width),
                    (int)Math.Ceiling(clientSize.Height), 96, 96, PixelFormats.Pbgra32);
                image.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(directory, $"theme{theme}-width{width}.png"))) encoder.Save(file);
                observations.Add(new
                {
                    theme, requestedWidth = width, actualWidth = dialog.ActualWidth, actualHeight = dialog.ActualHeight,
                    clientWidth = clientSize.Width, clientHeight = clientSize.Height,
                    headingContrast = Contrast(((SolidColorBrush)text[0].Foreground).Color, background.Color),
                    bodyContrast = Contrast(((SolidColorBrush)text[1].Foreground).Color, background.Color),
                    buttonContrast, headingHeight = text[0].ActualHeight, bodyHeight = text[1].ActualHeight,
                    buttonHeight = keep.ActualHeight
                });
            }
            finally { dialog.Close(); Pump(); }
        }
        File.WriteAllText(Path.Combine(directory, $"theme{theme}-layout.json"),
            JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.Equal(0, fixture.Client.UpdateCalls);
    });

    private static void AssertDisableAuditBounds(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0
            && bounds.Left >= -.1 && bounds.Top >= -.1 && bounds.Right <= root.ActualWidth + .1
            && bounds.Bottom <= root.ActualHeight + .1, $"{element.Name} is clipped: {bounds} in {root.RenderSize}.");
    }
}

public sealed partial class WindowLayoutRegressionTests
{
    [Fact]
    public void Batch_slider_visual_audit_uses_the_real_one_to_forty_binding_and_twenty_default() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        var vm = scene.Workspace.ActiveViewModel;
        vm.OpenSettingsCommand.Execute(null);
        Layout(scene.Window, 1280, 800);
        var slider = Named<Slider>(scene.Window, "AiBatchSizeSlider");
        var binding = BindingOperations.GetBinding(slider, Slider.ValueProperty);
        Assert.NotNull(binding);
        Assert.Equal("MaxAiBatchSize", binding.Path.Path);
        Assert.Equal(BindingMode.TwoWay, binding.Mode);
        Assert.Equal(1d, slider.Minimum);
        Assert.Equal(40d, slider.Maximum);
        Assert.Equal(1d, slider.TickFrequency);
        Assert.True(slider.IsSnapToTickEnabled);
        Assert.Equal(20, vm.MaxAiBatchSize);
        Assert.Equal(20d, slider.Value);
        Assert.InRange((slider.Value - slider.Minimum) / (slider.Maximum - slider.Minimum), .48, .5);

        slider.Value = 40; Pump();
        Assert.Equal(40, vm.MaxAiBatchSize);
        slider.Value = 1; Pump();
        Assert.Equal(1, vm.MaxAiBatchSize);
        vm.MaxAiBatchSize = 20; Pump();
        Assert.Equal(20d, slider.Value);

        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.GetTempPath(), "shared-disable-visual-audit");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "batch-slider-binding.json"), JsonSerializer.Serialize(new
        {
            minimum = slider.Minimum, maximum = slider.Maximum, defaultValue = slider.Value,
            midpointFraction = (slider.Value - slider.Minimum) / (slider.Maximum - slider.Minimum),
            tickFrequency = slider.TickFrequency, snapToTick = slider.IsSnapToTickEnabled,
            path = binding.Path.Path, mode = binding.Mode.ToString(), twoWayBoundaryChecksPassed = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    });
}

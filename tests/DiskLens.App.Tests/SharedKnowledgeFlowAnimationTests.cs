using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("03")]
    public void Benefits_animation_keeps_both_stacked_scenes_inside_the_canvas(string theme) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(theme: theme);
        dialog.Width = 900; dialog.Height = 680;
        Invoke(dialog, "Next_OnClick");
        dialog.UpdateLayout(); Pump();
        var scene = (SharedKnowledgeBenefitsIllustration)dialog.FindName("BenefitsIllustration");
        var progress = (DependencyProperty)typeof(SharedKnowledgeBenefitsIllustration)
            .GetField("AnimationProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        scene.GetType().GetMethod("CompleteMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, null);
        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.GetTempPath(), "shared-benefits-animation");
        Directory.CreateDirectory(directory);
        foreach (var phase in new[] { .15, .26, .45, .62, .77, 1d })
        {
            scene.SetValue(progress, phase); scene.InvalidateVisual(); Pump();
            var drawing = VisualTreeHelper.GetDrawing(scene)!;
            Assert.True(drawing.Bounds.Left >= -1 && drawing.Bounds.Top >= -1
                && drawing.Bounds.Right <= scene.ActualWidth + 1 && drawing.Bounds.Bottom <= scene.ActualHeight + 1,
                $"Stage {phase} leaves the {scene.RenderSize} benefits canvas: {drawing.Bounds}.");
            var canvas = new DrawingVisual();
            using (var context = canvas.RenderOpen())
            {
                context.DrawRectangle((Brush)dialog.FindResource("MainSurfaceBrush"), null, new Rect(scene.RenderSize));
                context.DrawDrawing(drawing);
            }
            var image = new RenderTargetBitmap((int)Math.Ceiling(scene.ActualWidth),
                (int)Math.Ceiling(scene.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, $"theme{theme}-stage{phase:0.00}.png"));
            encoder.Save(file);
        }
        if (theme == "03" && Environment.GetEnvironmentVariable("DISKLENS_EXPORT_BENEFITS_PREVIEW") == "1")
        {
            var frames = Path.Combine(directory, "preview-frames");
            Directory.CreateDirectory(frames);
            for (var frame = 0; frame <= 420; frame++)
            {
                scene.SetValue(progress, frame / 420d);
                scene.InvalidateVisual(); Pump();
                var canvas = new DrawingVisual();
                using (var context = canvas.RenderOpen())
                {
                    context.DrawRectangle((Brush)dialog.FindResource("MainSurfaceBrush"), null, new Rect(scene.RenderSize));
                    context.DrawDrawing(VisualTreeHelper.GetDrawing(scene));
                }
                var image = new RenderTargetBitmap(1560, 800, 192, 192, PixelFormats.Pbgra32);
                image.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(frames, $"frame{frame:0000}.png"));
                encoder.Save(file);
            }
        }
        Assert.Equal(0, fixture.Client.RegisterCalls);
        dialog.Close();
    });

    [Fact]
    public void Benefits_animation_stops_on_its_complete_frame_when_leaving_and_does_not_restart() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        var scene = (SharedKnowledgeBenefitsIllustration)dialog.FindName("BenefitsIllustration");
        Assert.False(scene.HasAnimatedProperties);
        Invoke(dialog, "Next_OnClick"); Pump();
        Assert.Equal(SystemParameters.ClientAreaAnimation, scene.HasAnimatedProperties);
        Invoke(dialog, "Previous_OnClick"); Pump();
        Assert.False(scene.HasAnimatedProperties);
        AssertCompleteFrame(scene, "AnimationProgressProperty");
        Invoke(dialog, "Next_OnClick"); Pump();
        AssertCompleteFrame(scene, "AnimationProgressProperty");
        dialog.Close(); Pump();
        Assert.False(scene.HasAnimatedProperties);
    });

    [Theory]
    [InlineData("01")]
    [InlineData("03")]
    public void Intro_animation_renders_distinct_narrative_stages_without_leaving_the_canvas(string theme) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(theme: theme);
        dialog.Width = 900;
        dialog.Height = 680;
        dialog.UpdateLayout(); Pump();
        var scene = FindVisual<SharedKnowledgeFlowIllustration>(dialog)!;
        Assert.NotNull(scene);
        var progress = (DependencyProperty)typeof(SharedKnowledgeFlowIllustration)
            .GetField("SceneProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        scene.GetType().GetMethod("CompleteMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, null);
        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.GetTempPath(), "shared-flow-animation");
        Directory.CreateDirectory(directory);
        byte[]? previous = null;
        foreach (var phase in new[] { .02, .12, .33, .51, .71, .91, 1d })
        {
            scene.SetValue(progress, phase);
            scene.InvalidateVisual();
            Pump();
            var drawing = VisualTreeHelper.GetDrawing(scene);
            Assert.NotNull(drawing);
            Assert.True(drawing.Bounds.Left >= -1 && drawing.Bounds.Top >= -1
                && drawing.Bounds.Right <= scene.ActualWidth + 1 && drawing.Bounds.Bottom <= scene.ActualHeight + 1,
                $"Stage {phase} leaves the {scene.RenderSize} animation canvas: {drawing.Bounds}.");
            var image = new RenderTargetBitmap((int)Math.Ceiling(scene.ActualWidth),
                (int)Math.Ceiling(scene.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var canvas = new DrawingVisual();
            using (var context = canvas.RenderOpen())
            {
                context.DrawRectangle((Brush)dialog.FindResource("CardBackgroundBrush"), null, new Rect(scene.RenderSize));
                context.DrawDrawing(drawing);
            }
            image.Render(canvas);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            if (previous is not null) Assert.False(previous.SequenceEqual(pixels), $"Stage {phase} did not change the scene.");
            previous = pixels;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, $"theme{theme}-stage{phase:0.00}.png"));
            encoder.Save(file);
        }
        // Optional visual review export uses the same vector renderer and timeline as
        // the application, without recording or sending input to the user's desktop.
        if (theme == "03" && Environment.GetEnvironmentVariable("DISKLENS_EXPORT_FLOW_PREVIEW") == "1")
        {
            var frames = Path.Combine(directory, "preview-frames");
            Directory.CreateDirectory(frames);
            for (var frame = 0; frame <= 360; frame++)
            {
                scene.SetValue(progress, frame / 360d);
                scene.InvalidateVisual(); Pump();
                var canvas = new DrawingVisual();
                using (var context = canvas.RenderOpen())
                {
                    context.DrawRectangle((Brush)dialog.FindResource("CardBackgroundBrush"), null, new Rect(scene.RenderSize));
                    context.DrawDrawing(VisualTreeHelper.GetDrawing(scene));
                }
                var image = new RenderTargetBitmap(1520, 660, 192, 192, PixelFormats.Pbgra32);
                image.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(frames, $"frame{frame:0000}.png"));
                encoder.Save(file);
            }
        }
        Assert.Equal(0, fixture.Client.RegisterCalls);
        dialog.Close();
    });

    [Fact]
    public void Intro_animation_releases_its_clock_when_hidden_and_keeps_the_complete_frame_when_shown_again() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        var scene = FindVisual<SharedKnowledgeFlowIllustration>(dialog)!;
        Assert.NotNull(scene);
        Assert.Equal(SystemParameters.ClientAreaAnimation, scene.HasAnimatedProperties);
        scene.Visibility = Visibility.Collapsed;
        Pump();
        Assert.False(scene.HasAnimatedProperties);
        AssertCompleteFrame(scene, "SceneProgressProperty");
        scene.Visibility = Visibility.Visible;
        Pump();
        AssertCompleteFrame(scene, "SceneProgressProperty");
        dialog.Close(); Pump();
        Assert.False(scene.HasAnimatedProperties);
    });

    [Theory]
    [InlineData(false, 6d)]
    [InlineData(true, 7d)]
    public void Shared_animation_plays_once_at_double_speed_and_releases_its_clock_after_completion(bool benefits, double seconds) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        if (benefits) { Invoke(dialog, "Next_OnClick"); Pump(); }
        FrameworkElement scene = benefits
            ? (SharedKnowledgeBenefitsIllustration)dialog.FindName("BenefitsIllustration")
            : FindVisual<SharedKnowledgeFlowIllustration>(dialog)!;
        var progressName = benefits ? "AnimationProgressProperty" : "SceneProgressProperty";
        var clock = MotionClock(scene);
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert.NotNull(clock);
            Assert.Equal(TimeSpan.FromSeconds(seconds), clock.Timeline.Duration.TimeSpan);
            Assert.Equal(new RepeatBehavior(1), clock.Timeline.RepeatBehavior);
            Assert.Equal(60, Timeline.GetDesiredFrameRate(clock.Timeline));
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds + .01), TimeSeekOrigin.BeginTime);
            WaitUntil(() => !scene.HasAnimatedProperties);
        }
        AssertCompleteFrame(scene, progressName);
        scene.Visibility = Visibility.Collapsed; Pump();
        scene.Visibility = Visibility.Visible; Pump();
        AssertCompleteFrame(scene, progressName);
        dialog.Close();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_animation_final_frame_preserves_the_complete_scene_without_fading(bool benefits) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        if (benefits) { Invoke(dialog, "Next_OnClick"); Pump(); }
        FrameworkElement scene = benefits
            ? (SharedKnowledgeBenefitsIllustration)dialog.FindName("BenefitsIllustration")
            : FindVisual<SharedKnowledgeFlowIllustration>(dialog)!;
        scene.GetType().GetMethod("CompleteMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, null);
        var progress = AnimationProgressProperty(scene, benefits ? "AnimationProgressProperty" : "SceneProgressProperty");
        scene.SetValue(progress, .94d); scene.InvalidateVisual(); Pump();
        var complete = ScenePixels(scene);
        Assert.Contains(complete, value => value != 0);
        scene.SetValue(progress, 1d); scene.InvalidateVisual(); Pump();

        Assert.True(complete.SequenceEqual(ScenePixels(scene)), "The final frame must retain every element of the complete scene.");
        dialog.Close();
    });

    private static DependencyProperty AnimationProgressProperty(FrameworkElement scene, string name) =>
        (DependencyProperty)scene.GetType().GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static AnimationClock? MotionClock(FrameworkElement scene) =>
        (AnimationClock?)scene.GetType().GetField("_motionClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene);

    private static void AssertCompleteFrame(FrameworkElement scene, string progressName)
    {
        Assert.False(scene.HasAnimatedProperties);
        Assert.Null(MotionClock(scene));
        Assert.Equal(1d, (double)scene.GetValue(AnimationProgressProperty(scene, progressName)));
    }

    private static byte[] ScenePixels(FrameworkElement scene)
    {
        var canvas = new DrawingVisual();
        using (var context = canvas.RenderOpen()) context.DrawDrawing(VisualTreeHelper.GetDrawing(scene));
        var image = new RenderTargetBitmap((int)Math.Ceiling(scene.ActualWidth), (int)Math.Ceiling(scene.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render(canvas);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}

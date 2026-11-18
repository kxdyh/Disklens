using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Three_pages_have_readable_contrast_and_fixed_navigation_at_supported_sizes(string theme) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(theme: theme);
        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.GetTempPath(), "shared-modal");
        Directory.CreateDirectory(directory);
        foreach (var size in new[] { new Size(900, 680), new Size(1080, 820) })
        {
            dialog.Width = size.Width;
            dialog.Height = size.Height;
            for (var page = 0; page < 3; page++)
            {
                typeof(SharedKnowledgeWindow).GetMethod("SetPage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, [page]);
                dialog.UpdateLayout(); Pump();
                // Page stills document the completed composition; dedicated timeline
                // tests export moving stages instead of mistaking the first fade-in
                // frame for the full design.
                var illustration = page == 0 ? FindVisual<SharedKnowledgeFlowIllustration>(dialog) as FrameworkElement
                    : page == 1 ? dialog.FindName("BenefitsIllustration") as FrameworkElement : null;
                if (illustration is not null)
                {
                    var field = page == 0 ? "SceneProgressProperty" : "AnimationProgressProperty";
                    var progress = (DependencyProperty)illustration.GetType()
                        .GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    illustration.BeginAnimation(progress, null);
                    illustration.SetValue(progress, .94d);
                    illustration.InvalidateVisual(); Pump();
                }
                var root = (FrameworkElement)dialog.Content;
                foreach (var name in new[] { "CloseButton", "PageNumberText", page == 2 ? "SaveButton" : "NextButton" })
                {
                    var element = (FrameworkElement)dialog.FindName(name);
                    var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1
                        && bounds.Bottom <= root.ActualHeight + 1, $"{name} is clipped in {size} theme {theme}.");
                }
                var text = ((SolidColorBrush)dialog.FindResource("SecondaryTextBrush")).Color;
                var background = ((SolidColorBrush)dialog.FindResource("MainSurfaceBrush")).Color;
                Assert.True(Contrast(text, background) >= 4.5, "Secondary copy must remain readable in every theme.");
                var save = (Button)dialog.FindName("SaveButton");
                Assert.Equal(page == 2, save.IsVisible);
                Assert.Equal(page < 2, ((Button)dialog.FindName("NextButton")).IsVisible);
                Assert.False(save.IsEnabled);
                // Rendering the margin-offset root directly crops the far edge and
                // omits the Window background. Capture its local visual into the
                // complete client canvas, with the actual theme surface behind it.
                var clientSize = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right,
                    root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle((Brush)dialog.FindResource("MainSurfaceBrush"), null, new Rect(clientSize));
                    var brush = new VisualBrush(root) { Stretch = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(root), root.RenderSize) };
                    drawing.DrawRectangle(brush, null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
                }
                var image = new RenderTargetBitmap((int)Math.Ceiling(clientSize.Width),
                    (int)Math.Ceiling(clientSize.Height), 96, 96, PixelFormats.Pbgra32);
                image.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = System.IO.File.Create(Path.Combine(directory, $"theme{theme}-page{page + 1}-{size.Width}x{size.Height}.png"));
                encoder.Save(file);
            }
        }
        Assert.False(Check(dialog, "AcceptAgreement").IsEnabled, "Screenshots, page changes, and layout do not count as reading consent.");
        Assert.Equal(0, fixture.Client.RegisterCalls);
        dialog.Close();
    });

    private static double Contrast(Color text, Color background)
    {
        static double Linear(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var a = Luminance(text); var b = Luminance(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}

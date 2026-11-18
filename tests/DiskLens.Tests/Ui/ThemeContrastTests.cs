using System.Xml.Linq;

namespace DiskLens.Tests.Ui;

public sealed class ThemeContrastTests
{
    [Theory]
    [InlineData("Theme01.xaml")]
    [InlineData("Theme02.xaml")]
    [InlineData("Theme03.xaml")]
    public void All_body_text_tokens_should_have_readable_contrast_on_workbench_surfaces(string file)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var document = XDocument.Load(Path.Combine(repo, "src", "DiskLens.App", "Resources", "Themes", file));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colors = document.Root!.Elements().Where(element => element.Attribute("Color") is not null)
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => (string)element.Attribute("Color")!);

        foreach (var text in new[] { "PrimaryTextBrush", "SecondaryTextBrush", "MutedTextBrush" })
        foreach (var background in new[] { "CardBackgroundBrush", "CardSubtleBackgroundBrush", "SurfaceBackgroundBrush" })
        {
            var a = Luminance(colors[text]);
            var b = Luminance(colors[background]);
            var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
            Assert.True(ratio >= 4.5, $"{file}: {text} on {background} has only {ratio:0.00}:1 contrast.");
        }

        var linkContrast = (Math.Max(Luminance(colors["LinkBrush"]), Luminance(colors["CardBackgroundBrush"])) + 0.05)
                           / (Math.Min(Luminance(colors["LinkBrush"]), Luminance(colors["CardBackgroundBrush"])) + 0.05);
        Assert.True(linkContrast >= 4.5, $"{file}: LinkBrush on CardBackgroundBrush has only {linkContrast:0.00}:1 contrast.");
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var value = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return Channel(1) * 0.2126 + Channel(3) * 0.7152 + Channel(5) * 0.0722;
    }
}

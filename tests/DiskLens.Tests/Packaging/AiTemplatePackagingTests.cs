using System.IO;
using System.Xml.Linq;

namespace DiskLens.Tests.Packaging;

public sealed class AiTemplatePackagingTests
{
    [Fact]
    public void Prompt_template_should_be_embedded_only_and_never_copied_to_output_or_publish()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var core = XDocument.Load(Path.Combine(repoRoot, "src", "DiskLens.Core", "DiskLens.Core.csproj"));
        var app = XDocument.Load(Path.Combine(repoRoot, "src", "DiskLens.App", "DiskLens.App.csproj"));
        var resource = Assert.Single(core.Descendants("EmbeddedResource"));
        Assert.EndsWith("DefaultAiPrompt.md", resource.Attribute("Include")!.Value, StringComparison.Ordinal);
        Assert.Equal("DiskLens.Core.DefaultAiPrompt.md", resource.Attribute("LogicalName")!.Value);
        Assert.DoesNotContain(app.Descendants(), item => item.Attribute("Include")?.Value.Contains("DefaultAiPrompt.md") == true);
        foreach (var document in new[] { core, app })
            Assert.DoesNotContain(document.Descendants(), item => item.Name.LocalName is "Content" or "None"
                && item.Attribute("Include")?.Value.Contains("DefaultAiPrompt.md") == true);
    }
}

using System.IO;
using System.Xml.Linq;

namespace DiskLens.Tests.Packaging;

public sealed class PublishingConfigurationTests
{
    [Fact]
    public void DiskLensApp_should_be_configured_for_self_contained_win_x64_publish()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(repoRoot, "src", "DiskLens.App", "DiskLens.App.csproj");

        var document = XDocument.Load(projectPath);
        var propertyGroup = document.Root!.Elements("PropertyGroup").Last();

        Assert.Equal("true", GetPropertyValue(propertyGroup, "SelfContained"));
        Assert.Equal("win-x64", GetPropertyValue(propertyGroup, "RuntimeIdentifier"));
        Assert.Equal("true", GetPropertyValue(propertyGroup, "PublishSingleFile"));
    }

    [Fact]
    public void DiskLensApp_should_reference_an_application_manifest_that_requests_administrator_privileges()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(repoRoot, "src", "DiskLens.App", "DiskLens.App.csproj");
        var manifestPath = Path.Combine(repoRoot, "src", "DiskLens.App", "app.manifest");

        var project = XDocument.Load(projectPath);
        var propertyGroup = project.Root!.Elements("PropertyGroup").Last();

        Assert.Equal("app.manifest", GetPropertyValue(propertyGroup, "ApplicationManifest"));
        Assert.True(File.Exists(manifestPath), "Expected app.manifest to exist.");

        var manifest = XDocument.Load(manifestPath);
        XNamespace asmV3 = "urn:schemas-microsoft-com:asm.v3";
        var requestedExecutionLevel = manifest
            .Descendants(asmV3 + "requestedExecutionLevel")
            .Single();

        Assert.Equal("requireAdministrator", requestedExecutionLevel.Attribute("level")?.Value);
        Assert.Equal("false", requestedExecutionLevel.Attribute("uiAccess")?.Value);
    }

    private static string GetPropertyValue(XElement propertyGroup, string propertyName)
    {
        return propertyGroup.Element(propertyName)?.Value ?? string.Empty;
    }
}

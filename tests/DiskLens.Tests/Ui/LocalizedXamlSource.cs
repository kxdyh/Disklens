using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DiskLens.Tests.Ui;

/// <summary>Resolves production text resources while retaining the original markup contracts.</summary>
internal static class LocalizedXamlSource
{
    private static readonly string AppRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DiskLens.App"));
    private static readonly Lazy<Dictionary<string, string>> Keys = new(() => ReadDictionary("keys"));
    private static readonly Lazy<Dictionary<string, string>> English = new(() => ReadDictionary("en"));

    public static string ReadAllText(string path)
    {
        var source = File.ReadAllText(path);
        return Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            ? ExpandLocalizedText(source) : source;
    }

    public static XDocument Load(string path) => XDocument.Parse(ReadAllText(path));

    public static string ExpandLocalizedText(string source) => Regex.Replace(source,
        @"\{loc:Text\s+(?<key>[A-Za-z0-9_-]+)\}", match =>
        {
            var key = match.Groups["key"].Value;
            Assert.True(Keys.Value.TryGetValue(key, out var chinese), $"Missing Chinese XAML resource: {key}");
            Assert.False(string.IsNullOrWhiteSpace(chinese));
            Assert.True(English.Value.TryGetValue(chinese!, out var english), $"Missing English XAML resource: {key}");
            Assert.False(string.IsNullOrWhiteSpace(english));
            // Only resolve text keys. In particular loc:Bind is never rewritten:
            // tests must verify its exact path and protect user-provided paths.
            return SecurityElement.Escape(chinese)!;
        });

    private static Dictionary<string, string> ReadDictionary(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppRoot, "Localization", name + ".json")))!;
}

public sealed class LocalizedXamlResourceTests
{
    [Fact]
    public void Every_localized_Xaml_text_key_resolves_to_real_Chinese_and_English_resources()
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "DiskLens.App"));
        var checkedFiles = 0;
        foreach (var path in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (!source.Contains("{loc:Text ", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("{loc:Text ", LocalizedXamlSource.ExpandLocalizedText(source));
            checkedFiles++;
        }
        Assert.True(checkedFiles > 0);
    }

    [Fact]
    public void Resolution_keeps_localized_binding_paths_and_literal_user_path_bindings_intact()
    {
        const string markup = "<TextBlock Text=\"{loc:Bind VolumeUsagePercent}\" ToolTip=\"{Binding TooltipPathText}\" />";
        Assert.Equal(markup, LocalizedXamlSource.ExpandLocalizedText(markup));
    }
}

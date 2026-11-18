using System.IO;
using System.Text.RegularExpressions;

namespace DiskLens.Tests.Ui;

public sealed class ApiKeyPersistenceSourceTests
{
    [Fact]
    public void App_should_load_persisted_api_key_on_startup()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var appPath = Path.Combine(repoRoot, "src", "DiskLens.App", "App.xaml.cs");
        var source = File.ReadAllText(appPath);

        Assert.Contains("AppSecretStore", source, StringComparison.Ordinal);
        Assert.Contains("LoadApiKeyAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_store_should_persist_api_key_separately_from_settings_json()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var servicesPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Services", "AppSecretStore.cs");
        var source = File.ReadAllText(servicesPath);

        Assert.Contains("ProtectedData", source, StringComparison.Ordinal);
        Assert.Contains("SaveApiKeyAsync", source, StringComparison.Ordinal);
        Assert.Contains("LoadApiKeyAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModel_should_persist_api_key_immediately_when_it_changes()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var viewModelPath = Path.Combine(repoRoot, "src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        var match = Regex.Match(
            source,
            @"public string ApiKey\s*\{\s*get => _apiKey;\s*set\s*\{\s*if \(SetProperty\(ref _apiKey, value\)\)\s*\{(?<body>.*?)\}\s*\}\s*\}",
            RegexOptions.Singleline);

        Assert.True(match.Success, "Expected to find ApiKey property setter.");
        Assert.Contains("SaveSettingsDeferred();", match.Groups["body"].Value, StringComparison.Ordinal);
    }
}

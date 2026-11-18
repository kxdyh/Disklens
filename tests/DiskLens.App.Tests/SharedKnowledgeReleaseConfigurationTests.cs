using System.IO;
using DiskLens.App.Services;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class SharedKnowledgeReleaseConfigurationTests : IDisposable
{
    private readonly string _previousData = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY") ?? "";
    private readonly string _previousEndpoint = Environment.GetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT") ?? "";
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Configuration.Tests"),
        "release-config-" + Guid.NewGuid().ToString("N"));

    public SharedKnowledgeReleaseConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _directory);
        Environment.SetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT", null);
    }

    [Fact]
    public async Task Shipped_service_is_HTTPS_beta_and_fresh_install_does_not_grant_consent_or_write()
    {
        using var runtime = new SharedKnowledgeRuntime(new AnalysisMemoryStore());
        Assert.Equal(SharedKnowledgeRuntime.DefaultEndpoint, runtime.Endpoint.AbsoluteUri);
        Assert.Equal("https", runtime.Endpoint.Scheme);
        Assert.False(runtime.IsLocalTest);
        var preferences = await runtime.Store.LoadAsync();
        Assert.False(preferences.Enabled);
        Assert.False(preferences.ShareHistory);
        Assert.Null(preferences.Installation);
        Assert.False((await runtime.Store.GetSnapshotAsync()).CanShare);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Legacy_local_test_consent_is_preserved_but_never_adopted_by_the_beta()
    {
        await new SharedPreferenceStore(_directory).SaveAsync(EnabledPreferences());
        var legacyPath = Path.Combine(_directory, "shared-experience.bin");
        var before = await File.ReadAllBytesAsync(legacyPath);

        using var runtime = new SharedKnowledgeRuntime(new AnalysisMemoryStore());
        var current = await runtime.Store.LoadAsync();
        Assert.False(current.Enabled);
        Assert.Null(current.Installation);
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyPath));
    }

    [Fact]
    public async Task Changing_service_keeps_consent_credentials_and_exclusions_in_their_original_service()
    {
        Environment.SetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT", "http://127.0.0.1:8791/");
        using (var local = new SharedKnowledgeRuntime(new AnalysisMemoryStore()))
            await local.Store.SaveAsync(EnabledPreferences());

        Environment.SetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT", null);
        using (var beta = new SharedKnowledgeRuntime(new AnalysisMemoryStore()))
        {
            var preferences = await beta.Store.LoadAsync();
            Assert.False(preferences.Snapshot().CanShare);
            Assert.Null(preferences.Installation);
            Assert.False(preferences.ShareHistory);
            await beta.Settings.MarkRecommendedAsync("1.1.0");
        }

        Environment.SetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT", "http://127.0.0.1:8791");
        using var restoredLocal = new SharedKnowledgeRuntime(new AnalysisMemoryStore());
        var restored = await restoredLocal.Store.LoadAsync();
        Assert.True(restored.Snapshot().CanShare);
        Assert.Equal(EnabledPreferences().Installation, restored.Installation);
        Assert.Equal(EnabledPreferences().ExcludedPaths, restored.ExcludedPaths);
        Assert.Equal("local-test-version", restored.LastOfferedVersion);
    }

    private static SharedPreferences EnabledPreferences() => new(
        Enabled: true, AgreementVersion: SharedConsentSnapshot.CurrentAgreementVersion,
        ShareHistory: false, EnabledAt: DateTimeOffset.UtcNow.AddMinutes(-1), Epoch: 2,
        PolicyRevision: "synthetic-release-policy", ExcludedPaths: [@"C:\synthetic\private"],
        LastOfferedVersion: "local-test-version",
        Installation: new("synthetic-installation", "synthetic-release-secret-never-sent", 2));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousData.Length == 0 ? null : _previousData);
        Environment.SetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT", _previousEndpoint.Length == 0 ? null : _previousEndpoint);
        Directory.Delete(_directory, recursive: true);
    }
}

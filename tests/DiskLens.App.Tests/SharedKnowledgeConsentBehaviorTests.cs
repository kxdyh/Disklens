using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

/// <summary>Real current-user DPAPI and filesystem behavior; never contacts a service.</summary>
public sealed class SharedKnowledgeConsentBehaviorTests : IDisposable
{
    private readonly string _directory;
    private string DocumentPath => Path.Combine(_directory, "shared-experience.bin");

    public SharedKnowledgeConsentBehaviorTests()
    {
        var artifacts = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Consent.Tests");
        _directory = Path.Combine(artifacts, "consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Fresh_install_is_off_without_agreement_history_or_identity_and_does_not_write()
    {
        var store = new SharedPreferenceStore(_directory);
        var preferences = await store.LoadAsync();
        var consent = await store.GetSnapshotAsync();

        Assert.False(preferences.Enabled);
        Assert.False(preferences.ShareHistory);
        Assert.Equal("", preferences.AgreementVersion);
        Assert.Equal(default, preferences.EnabledAt);
        Assert.Null(preferences.Installation);
        Assert.False(consent.CanShare);
        Assert.Empty(consent.ExcludedPaths);
        Assert.False(File.Exists(DocumentPath));
    }

    [Fact]
    public async Task Credential_and_consent_round_trip_together_in_one_current_user_DPAPI_document()
    {
        var expected = EnabledPreferences();
        await new SharedPreferenceStore(_directory).SaveAsync(expected);

        var encrypted = await File.ReadAllBytesAsync(DocumentPath);
        var ciphertextText = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain(expected.Installation!.ManagementToken, ciphertextText, StringComparison.Ordinal);
        Assert.DoesNotContain(expected.Installation.InstallationId, ciphertextText, StringComparison.Ordinal);
        Assert.DoesNotContain(expected.ExcludedPaths![0], ciphertextText, StringComparison.Ordinal);

        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var decoded = JsonSerializer.Deserialize<SharedPreferences>(plain)!;
            Assert.Equal(expected.Installation, decoded.Installation);
            Assert.Equal(expected.PolicyRevision, decoded.PolicyRevision);
            Assert.Equal(expected.Epoch, decoded.Epoch);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }

        var actual = await new SharedPreferenceStore(_directory).LoadAsync();
        AssertEquivalent(expected, actual);
        Assert.True(actual.Snapshot().CanShare);
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Wrong_agreement_disables_sharing_but_keeps_management_credentials_for_withdrawal()
    {
        var expected = EnabledPreferences() with { AgreementVersion = "previous-agreement" };
        await new SharedPreferenceStore(_directory).SaveAsync(expected);

        var actual = await new SharedPreferenceStore(_directory).LoadAsync();

        Assert.False(actual.Enabled);
        Assert.False(actual.Snapshot().CanShare);
        Assert.Equal(expected.Installation, actual.Installation);
        Assert.Equal(expected.Epoch, actual.Epoch);
        Assert.Equal(expected.AgreementVersion, actual.AgreementVersion);
    }

    [Fact]
    public async Task Damaged_DPAPI_document_fails_closed_without_replacing_recovery_evidence()
    {
        byte[] damaged = [1, 3, 5, 7, 9];
        await File.WriteAllBytesAsync(DocumentPath, damaged);
        var store = new SharedPreferenceStore(_directory);

        var actual = await store.LoadAsync();

        Assert.False(actual.Enabled);
        Assert.Null(actual.Installation);
        Assert.False(actual.Snapshot().CanShare);
        Assert.NotNull(store.LastError);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(DocumentPath));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(7, 501)]
    public async Task Authenticated_but_invalid_preferences_fail_closed(long epoch, int exclusionCount)
    {
        var invalid = EnabledPreferences() with
        {
            Epoch = epoch,
            ExcludedPaths = Enumerable.Range(0, exclusionCount).Select(i => $@"C:\private\{i}").ToArray()
        };
        await WriteEncryptedAsync(invalid);
        var store = new SharedPreferenceStore(_directory);

        var actual = await store.LoadAsync();

        Assert.False(actual.Snapshot().CanShare);
        Assert.Null(actual.Installation);
        Assert.NotNull(store.LastError);
        Assert.False((await store.LoadAsync()).Snapshot().CanShare);
    }

    [Fact]
    public async Task Canceled_save_preserves_previous_document_and_releases_the_gate()
    {
        var store = new SharedPreferenceStore(_directory);
        var before = EnabledPreferences();
        await store.SaveAsync(before);
        var savedBytes = await File.ReadAllBytesAsync(DocumentPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(before with { Enabled = false, Epoch = 999 }, cancellation.Token));

        Assert.Equal(savedBytes, await File.ReadAllBytesAsync(DocumentPath));
        AssertEquivalent(before, await store.LoadAsync());
        await store.SuspendAsync();
        Assert.False((await new SharedPreferenceStore(_directory).LoadAsync()).Enabled);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Failed_atomic_replace_disables_this_process_and_preserves_the_previous_durable_document()
    {
        var store = new SharedPreferenceStore(_directory);
        var before = EnabledPreferences();
        await store.SaveAsync(before);
        var savedBytes = await File.ReadAllBytesAsync(DocumentPath);
        await using (var locked = new FileStream(DocumentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = await Record.ExceptionAsync(() =>
                store.SaveAsync(before with { Enabled = false, Epoch = 999 }));
            Assert.True(exception is IOException or UnauthorizedAccessException,
                $"Expected the locked destination to reject replacement, got {exception?.GetType().Name ?? "success"}.");
        }

        Assert.Equal(savedBytes, await File.ReadAllBytesAsync(DocumentPath));
        Assert.False((await store.LoadAsync()).Snapshot().CanShare);
        Assert.NotNull(store.LastError);
        AssertEquivalent(before, await new SharedPreferenceStore(_directory).LoadAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_saves_never_mix_identity_epoch_and_scope_from_different_documents()
    {
        var store = new SharedPreferenceStore(_directory);
        var versions = Enumerable.Range(1, 24).Select(index => EnabledPreferences() with
        {
            Epoch = index,
            Installation = new($"synthetic-installation-{index}", $"synthetic-secret-{index}", index),
            PolicyRevision = $"policy-{index}",
            ExcludedPaths = [$@"C:\private\scope-{index}"]
        }).ToArray();

        await Task.WhenAll(versions.Select(version => store.SaveAsync(version)));

        var actual = await new SharedPreferenceStore(_directory).LoadAsync();
        var expected = Assert.Single(versions, version => version.Epoch == actual.Epoch);
        AssertEquivalent(expected, actual);
        AssertEquivalent(actual, await store.LoadAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Recommendation_is_once_for_the_current_version_and_survives_restart_without_granting_consent()
    {
        var settings = new SharedKnowledgeSettings(new SharedPreferenceStore(_directory));
        Assert.True(await settings.ShouldRecommendAsync("1.2.3"));

        await settings.MarkRecommendedAsync("1.2.3");
        var restarted = new SharedKnowledgeSettings(new SharedPreferenceStore(_directory));

        Assert.False(await restarted.ShouldRecommendAsync("1.2.3"));
        Assert.True(await restarted.ShouldRecommendAsync("1.2.4"));
        var actual = await restarted.LoadAsync();
        Assert.False(actual.Enabled);
        Assert.False(actual.ShareHistory);
        Assert.Equal("", actual.AgreementVersion);
        Assert.Null(actual.Installation);
    }

    [Fact]
    public async Task Enabled_feature_is_not_recommended_and_explicit_disable_never_reenables_it()
    {
        var settings = new SharedKnowledgeSettings(new SharedPreferenceStore(_directory));
        var expected = EnabledPreferences() with { LastOfferedVersion = "1.2.3" };
        await settings.SaveAsync(expected);
        Assert.False(await settings.ShouldRecommendAsync("1.2.3"));
        Assert.False(await settings.ShouldRecommendAsync("1.2.4"));

        await settings.SuspendAsync();
        var restarted = new SharedKnowledgeSettings(new SharedPreferenceStore(_directory));
        Assert.False(await restarted.ShouldRecommendAsync("1.2.3"));
        Assert.True(await restarted.ShouldRecommendAsync("1.2.4"));
        var actual = await restarted.LoadAsync();
        Assert.False(actual.Enabled);
        Assert.False(actual.Snapshot().CanShare);
        Assert.Equal(expected.Installation, actual.Installation);
        Assert.Equal(expected.Epoch, actual.Epoch);
        Assert.Equal(expected.ExcludedPaths, actual.ExcludedPaths);
        Assert.Equal(expected.AgreementVersion, actual.AgreementVersion);
        Assert.False(actual.ShareHistory);
    }

    [Fact]
    public async Task Recommending_a_disabled_installation_preserves_credentials_and_history_choice()
    {
        var settings = new SharedKnowledgeSettings(new SharedPreferenceStore(_directory));
        var expected = EnabledPreferences() with { Enabled = false, ShareHistory = false };
        await settings.SaveAsync(expected);

        await settings.MarkRecommendedAsync("1.2.3");

        var actual = await new SharedPreferenceStore(_directory).LoadAsync();
        AssertEquivalent(expected with { LastOfferedVersion = "1.2.3" }, actual);
        Assert.False(actual.Snapshot().CanShare);
    }

    [Fact]
    public async Task Caller_owned_exclusion_arrays_cannot_mutate_live_or_persisted_consent()
    {
        var store = new SharedPreferenceStore(_directory);
        var expected = EnabledPreferences();
        var originalPath = expected.ExcludedPaths![0];
        await store.SaveAsync(expected);

        expected.ExcludedPaths[0] = @"C:\unrelated\changed-after-save";
        var loaded = await store.LoadAsync();
        Assert.Equal(originalPath, Assert.Single(loaded.ExcludedPaths!));
        loaded.ExcludedPaths![0] = @"C:\unrelated\changed-after-load";
        var snapshot = await store.GetSnapshotAsync();
        Assert.Equal(originalPath, Assert.Single(snapshot.ExcludedPaths));
        ((string[])snapshot.ExcludedPaths)[0] = @"C:\unrelated\changed-after-snapshot";

        Assert.Equal(originalPath, Assert.Single((await store.GetSnapshotAsync()).ExcludedPaths));
        Assert.Equal(originalPath, Assert.Single((await new SharedPreferenceStore(_directory).LoadAsync()).ExcludedPaths!));
    }

    private static SharedPreferences EnabledPreferences() => new(
        Enabled: true,
        AgreementVersion: SharedConsentSnapshot.CurrentAgreementVersion,
        ShareHistory: false,
        EnabledAt: DateTimeOffset.UtcNow.AddMinutes(-3),
        Epoch: 7,
        PolicyRevision: "synthetic-policy-revision",
        ExcludedPaths: [@"C:\private\excluded"],
        LastOfferedVersion: "1.0.0",
        Installation: new("synthetic-installation", "synthetic-management-secret-never-sent", 7));

    private async Task WriteEncryptedAsync(SharedPreferences preferences)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(preferences);
        try
        {
            await File.WriteAllBytesAsync(DocumentPath,
                ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void AssertEquivalent(SharedPreferences expected, SharedPreferences actual)
    {
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.AgreementVersion, actual.AgreementVersion);
        Assert.Equal(expected.ShareHistory, actual.ShareHistory);
        Assert.Equal(expected.EnabledAt, actual.EnabledAt);
        Assert.Equal(expected.Epoch, actual.Epoch);
        Assert.Equal(expected.PolicyRevision, actual.PolicyRevision);
        Assert.Equal(expected.ExcludedPaths, actual.ExcludedPaths);
        Assert.Equal(expected.LastOfferedVersion, actual.LastOfferedVersion);
        Assert.Equal(expected.Installation, actual.Installation);
    }

    public void Dispose()
    {
        // Every path is generated directly beneath this task's artifact directory.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

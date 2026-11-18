using System.IO;
using System.Security.Cryptography;
using System.Text;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed class SharedObservationIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Identity.Tests"),
        "identity-" + Guid.NewGuid().ToString("N"));
    private readonly Uri _endpoint = new("https://synthetic-service.invalid/");
    private readonly ConsentSource _consent = new();
    private const string PrivatePath = @"C:\Users\SyntheticPrivateAccount\PrivateFinanceFolder";

    [Fact]
    public async Task Concurrent_calls_and_independent_store_instances_keep_one_identity_per_normalized_path()
    {
        var first = Create();
        var second = Create();
        var identities = await Task.WhenAll(Enumerable.Range(0, 48).Select(index =>
            (index % 2 == 0 ? first : second).GetOrCreateAsync(index % 3 == 0
                ? @"c:/users/SyntheticPrivateAccount/PrivateFinanceFolder/" : PrivatePath)));

        var identity = Assert.Single(identities.Distinct(StringComparer.Ordinal));
        Assert.Matches("^[a-f0-9]{32}$", identity);
        Assert.NotEqual(identity, await first.GetOrCreateAsync(PrivatePath + "-different"));
        Assert.Equal(identity, await Create().GetOrCreateAsync(PrivatePath));
    }

    [Fact]
    public async Task Restart_keeps_identity_and_reading_an_existing_identity_does_not_rewrite_the_map()
    {
        var original = await Create().GetOrCreateAsync(PrivatePath);
        var file = MapFile();
        var bytes = await File.ReadAllBytesAsync(file);
        var writtenAt = File.GetLastWriteTimeUtc(file);

        Assert.Equal(original, await Create().GetOrCreateAsync(PrivatePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(file));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Endpoint_and_installation_changes_use_separate_identity_domains()
    {
        var original = await Create().GetOrCreateAsync(PrivatePath);
        var otherService = await Create(endpoint: new("https://another-service.invalid/")).GetOrCreateAsync(PrivatePath);
        var originalSnapshot = _consent.Value;
        _consent.Value = _consent.Value with { Installation = new("another-installation", "other-test-secret", 8) };
        var otherInstallation = await Create().GetOrCreateAsync(PrivatePath);
        _consent.Value = originalSnapshot;

        Assert.Equal(3, new[] { original, otherService, otherInstallation }.Distinct().Count());
        Assert.Equal(original, await Create().GetOrCreateAsync(PrivatePath));
    }

    [Fact]
    public async Task Consent_epoch_and_management_secret_changes_do_not_change_the_public_installation_domain()
    {
        var original = await Create().GetOrCreateAsync(PrivatePath);
        _consent.Value = _consent.Value with { Epoch = 9,
            Installation = _consent.Value.Installation! with { Epoch = 9, ManagementToken = "rotated-synthetic-secret" } };

        Assert.Equal(original, await Create().GetOrCreateAsync(PrivatePath));
        Assert.Single(Directory.EnumerateFiles(_directory, "*.bin"));
    }

    [Fact]
    public async Task Separate_maps_for_the_same_path_allocate_independent_random_identities()
    {
        var first = await Create().GetOrCreateAsync(PrivatePath);
        var second = await Create(directory: Path.Combine(_directory, "separate")).GetOrCreateAsync(PrivatePath);

        Assert.NotEqual(first, second);
        Assert.Matches("^[a-f0-9]{32}$", second);
    }

    [Fact]
    public async Task Disk_contains_only_a_current_user_DPAPI_document_and_no_plaintext_path_or_secret()
    {
        var identity = await Create().GetOrCreateAsync(PrivatePath);
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("SyntheticPrivateAccount", file, StringComparison.OrdinalIgnoreCase);
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(PrivatePath)));
            Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(PrivatePath)));
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("SyntheticPrivateAccount")));
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("synthetic-management-secret")));
        }
        var decrypted = ProtectedData.Unprotect(await File.ReadAllBytesAsync(MapFile()), null, DataProtectionScope.CurrentUser);
        try
        {
            var text = Encoding.UTF8.GetString(decrypted);
            Assert.Contains(identity, text, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-management-secret", text, StringComparison.Ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(decrypted); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_or_invalid_encrypted_maps_fail_without_replacing_persisted_identities(bool validEncryption)
    {
        var store = Create();
        await store.GetOrCreateAsync(PrivatePath);
        var file = MapFile();
        var damaged = validEncryption
            ? ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Version\":999,\"Entries\":[]}"), null,
                DataProtectionScope.CurrentUser)
            : new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(file, damaged);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetOrCreateAsync(PrivatePath));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetOrCreateAsync(PrivatePath + "-new"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create().GetOrCreateAsync(PrivatePath));

        Assert.Equal(damaged, await File.ReadAllBytesAsync(file));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_map_copied_into_another_service_domain_is_rejected_instead_of_reusing_its_identity()
    {
        await Create().GetOrCreateAsync(PrivatePath);
        var originalFile = MapFile();
        var originalBytes = await File.ReadAllBytesAsync(originalFile);
        var other = Create(endpoint: new("https://other-domain.invalid/"));
        await other.GetOrCreateAsync(PrivatePath);
        var otherFile = Assert.Single(Directory.EnumerateFiles(_directory, "*.bin").Except([originalFile]));
        await File.WriteAllBytesAsync(otherFile, originalBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => other.GetOrCreateAsync(PrivatePath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(otherFile));
    }

    [Fact]
    public async Task No_installation_returns_no_identity_and_does_not_create_a_map()
    {
        _consent.Value = _consent.Value with { Installation = null };

        Assert.Equal("", await Create().GetOrCreateAsync(PrivatePath));
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_or_after_consent_read_does_not_create_or_modify_the_map(bool existingMap)
    {
        byte[]? initial = null;
        if (existingMap)
        {
            await Create().GetOrCreateAsync(PrivatePath);
            initial = await File.ReadAllBytesAsync(MapFile());
        }
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().GetOrCreateAsync(
            PrivatePath + "-canceled-before-read", alreadyCanceled.Token));
        using var canceledAfterRead = new CancellationTokenSource();
        _consent.BeforeReturn = canceledAfterRead.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().GetOrCreateAsync(
            PrivatePath + "-canceled-after-read", canceledAfterRead.Token));

        if (existingMap) Assert.Equal(initial, await File.ReadAllBytesAsync(MapFile()));
        else Assert.False(Directory.Exists(_directory));
    }

    private SharedObservationIdentityStore Create(string? directory = null, Uri? endpoint = null)
        => new(directory ?? _directory, endpoint ?? _endpoint, _consent);

    private string MapFile() => Assert.Single(Directory.EnumerateFiles(_directory, "*.bin"));
    private static bool Contains(byte[] value, byte[] pattern) => value.AsSpan().IndexOf(pattern) >= 0;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ConsentSource : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; set; } = new(true, SharedConsentSnapshot.CurrentAgreementVersion,
            false, DateTimeOffset.UtcNow, 7, "synthetic-policy", [],
            new("synthetic-installation", "synthetic-management-secret", 7));
        public Action? BeforeReturn { get; set; }
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeReturn?.Invoke();
            return Task.FromResult(Value);
        }
    }
}

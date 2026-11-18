using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Services.SharedKnowledge;

public sealed record SharedPreferences(
    bool Enabled = false,
    string AgreementVersion = "",
    bool ShareHistory = false,
    DateTimeOffset EnabledAt = default,
    long Epoch = 0,
    string PolicyRevision = "",
    string[]? ExcludedPaths = null,
    string LastOfferedVersion = "",
    SharedInstallation? Installation = null)
{
    public SharedConsentSnapshot Snapshot() => new(Enabled, AgreementVersion, ShareHistory,
        EnabledAt, Epoch, PolicyRevision, (ExcludedPaths ?? []).ToArray(), Installation);
}

/// <summary>Consent and its management secret are one atomic, current-user encrypted document.
/// This is application-global; scan tabs never own or overwrite a copy.</summary>
public sealed class SharedPreferenceStore : ISharedConsentSource
{
    private readonly string _path;
    internal string StorageDirectory => Path.GetDirectoryName(_path)!;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SharedPreferences _current = new();
    private bool _loaded;
    public string? LastError { get; private set; }

    public SharedPreferenceStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppDataDirectory.GetPath(), "shared-experience.bin");
    }

    public async Task<SharedPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return Copy(_current);
            _loaded = true;
            if (!File.Exists(_path)) return Copy(_current);
            try
            {
                if (new FileInfo(_path).Length > 1_048_576) throw new InvalidDataException();
                var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                SharedPreferences parsed;
                try { parsed = JsonSerializer.Deserialize<SharedPreferences>(bytes) ?? new(); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                Validate(parsed);
                _current = Copy(parsed);
                // A new agreement must be accepted explicitly, never migrated into consent.
                if (_current.AgreementVersion != SharedConsentSnapshot.CurrentAgreementVersion)
                    _current = _current with { Enabled = false };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                _current = new();
                LastError = L.T("共享设置无法读取，功能已保持关闭。");
            }
            return Copy(_current);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(SharedPreferences preferences, CancellationToken cancellationToken = default)
    {
        Validate(preferences);
        preferences = Copy(preferences);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Privacy-restricting changes take effect before optional persistence.
            // A failed disk write must not keep the running process sharing.
            _current = preferences with { Enabled = false };
            _loaded = true;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(preferences);
            byte[] encrypted;
            try
            {
                if (plain.Length > 1_048_576) throw new InvalidDataException();
                encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            _current = preferences;
            _loaded = true;
            LastError = null;
        }
        catch (Exception)
        {
            _current = preferences with { Enabled = false };
            _loaded = true;
            LastError = L.T("本次共享已暂停，但设置未能保存到磁盘；重新启动后请再次确认共享状态。");
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Stops local use immediately, even if the following server update is offline.</summary>
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await SaveAsync(current with { Enabled = false }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken).ConfigureAwait(false)).Snapshot();

    private static SharedPreferences Copy(SharedPreferences preferences)
        => preferences with { ExcludedPaths = preferences.ExcludedPaths?.ToArray() };

    private static void Validate(SharedPreferences preferences)
    {
        if (preferences.Epoch < 0 || (preferences.ExcludedPaths?.Length ?? 0) > 500
            || preferences.AgreementVersion is null || preferences.PolicyRevision is null || preferences.LastOfferedVersion is null
            || preferences.AgreementVersion.Length > 100 || preferences.PolicyRevision.Length > 100
            || preferences.LastOfferedVersion.Length > 100
            || preferences.ExcludedPaths?.Any(path => string.IsNullOrWhiteSpace(path)
                || path.Length > 32_767 || !Path.IsPathFullyQualified(path)) == true
            || preferences.Installation is { } install && (install.Epoch < 0 || string.IsNullOrWhiteSpace(install.InstallationId)
                || string.IsNullOrWhiteSpace(install.ManagementToken) || install.InstallationId.Length > 100 || install.ManagementToken.Length > 200))
            throw new InvalidDataException(L.T("共享设置无效，未启用共享。"));
    }
}

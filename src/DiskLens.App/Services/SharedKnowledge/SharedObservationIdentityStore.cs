using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>Current-user encrypted, service/installation-bound identities for local observations.</summary>
public sealed class SharedObservationIdentityStore(
    string storageDirectory, Uri endpoint, ISharedConsentSource consentSource)
    : ISharedObservationIdentityProvider
{
    private const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private const int MaximumEntries = 200_000;
    private const string InvalidMapMessage = "Local observation identities could not be verified. The existing map was preserved.";
    private const string StorageFailureMessage = "Local observation identities could not be stored.";
    private const string LookupFailureMessage = "Local observation identities could not be read.";

    public async Task<ISharedObservationLookupSnapshot> CreateLookupSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var consent = await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!consent.CanShare || string.IsNullOrWhiteSpace(consent.Installation?.InstallationId))
            return new LookupSnapshot(new(StringComparer.OrdinalIgnoreCase), consentSource, consent);
        var service = SharedServiceIdentity.GetStorageIdentity(endpoint).TrimEnd('/') + "/";
        var domain = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            service + "\n" + consent.Installation.InstallationId))).ToLowerInvariant();
        var mapPath = Path.Combine(storageDirectory, "observation-identities-" + domain + ".bin");
        try
        {
            // Writers replace the complete encrypted map atomically. One read sees
            // either generation, so lookup needs no lock file or directory creation.
            var entries = await ReadMapAsync(mapPath, domain, cancellationToken).ConfigureAwait(false);
            var current = await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!LookupSnapshot.SameInstallation(consent, current)) entries.Clear();
            return new LookupSnapshot(entries, consentSource, consent);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Native IO diagnostics can contain private local paths.
            throw new IOException(LookupFailureMessage);
        }
    }

    public async Task<string> GetOrCreateAsync(string normalizedLocalPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = NormalizePath(normalizedLocalPath);
        var consent = await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(consent.Installation?.InstallationId)) return string.Empty;
        // This digest binds public authorization identifiers only. Local paths and
        // management credentials never become a filename or a cloud identifier.
        var service = SharedServiceIdentity.GetStorageIdentity(endpoint).TrimEnd('/') + "/";
        var domain = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            service + "\n" + consent.Installation.InstallationId))).ToLowerInvariant();
        var mapPath = Path.Combine(storageDirectory, "observation-identities-" + domain + ".bin");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(storageDirectory);
            using var fileLock = await AcquireLockAsync(mapPath + ".lock", cancellationToken).ConfigureAwait(false);
            var entries = await ReadMapAsync(mapPath, domain, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(path, out var identity)) return identity;
            if (entries.Count >= MaximumEntries) throw new InvalidDataException(InvalidMapMessage);
            do { identity = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); }
            while (entries.ContainsValue(identity));
            entries.Add(path, identity);
            await WriteMapAsync(mapPath, domain, entries, cancellationToken).ConfigureAwait(false);
            return identity;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Native IO exceptions may contain the user's profile path. Do not
            // carry their message or inner exception into a report or log.
            throw new IOException(StorageFailureMessage);
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when (((exception.HResult & 0xffff) is 32 or 33)
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<Dictionary<string, string>> ReadMapAsync(string path, string domain,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (new FileInfo(path).Length > MaximumDocumentBytes) throw new InvalidDataException(InvalidMapMessage);
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            MapDocument? document;
            try { document = JsonSerializer.Deserialize<MapDocument>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (document is not { Version: 1 } || document.Domain != domain
                || document.Entries is null || document.Entries.Length > MaximumEntries)
                throw new InvalidDataException(InvalidMapMessage);
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null || entry.Identity is not { Length: 32 }
                    || entry.Identity.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9'))
                    || entry.Path != NormalizePath(entry.Path)
                    || !entries.TryAdd(entry.Path, entry.Identity) || !identities.Add(entry.Identity))
                    throw new InvalidDataException(InvalidMapMessage);
            }
            return entries;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidDataException(InvalidMapMessage);
        }
    }

    private static async Task WriteMapAsync(string path, string domain, Dictionary<string, string> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = new MapDocument(1, domain, entries.Select(entry => new MapEntry(entry.Key, entry.Value)).ToArray());
        var plain = JsonSerializer.SerializeToUtf8Bytes(document);
        byte[] encrypted;
        try
        {
            if (plain.Length > MaximumDocumentBytes) throw new InvalidDataException(InvalidMapMessage);
            encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaximumDocumentBytes) throw new InvalidDataException(InvalidMapMessage);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || path.Any(char.IsControl)
            || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || path[2] is not ('\\' or '/') || path[2..].Contains(':'))
            throw new ArgumentException("A normalized absolute local path is required.");
        try
        {
            var full = Path.GetFullPath(path.Replace('/', '\\'));
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new ArgumentException("A normalized absolute local path is required.");
        }
    }

    private sealed record MapDocument(int Version, string Domain, MapEntry[] Entries);
    private sealed record MapEntry(string Path, string Identity);

    private sealed class LookupSnapshot(Dictionary<string, string> entries, ISharedConsentSource source,
        SharedConsentSnapshot captured) : ISharedObservationLookupSnapshot
    {
        public bool ContainsPath(string normalizedLocalPath) => entries.ContainsKey(NormalizePath(normalizedLocalPath));

        public async Task<string> GetOrCreateAsync(string normalizedLocalPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizePath(normalizedLocalPath);
            if (!entries.TryGetValue(path, out var identity)) return string.Empty;
            var current = await source.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return SameInstallation(captured, current) ? identity : string.Empty;
        }

        public Task<ISharedObservationLookupSnapshot> CreateLookupSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ISharedObservationLookupSnapshot>(this);
        }

        internal static bool SameInstallation(SharedConsentSnapshot left, SharedConsentSnapshot right) =>
            left.CanShare && right.CanShare && left.Installation == right.Installation
                && left.Epoch == right.Epoch && left.EnabledAt == right.EnabledAt
                && left.AgreementVersion == right.AgreementVersion && left.PolicyRevision == right.PolicyRevision
                && left.ShareHistory == right.ShareHistory
                && left.ExcludedPaths.SequenceEqual(right.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
    }
}

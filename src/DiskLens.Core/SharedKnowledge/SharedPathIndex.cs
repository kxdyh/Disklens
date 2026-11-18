using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>A complete, exact set of shared path identities, without analysis text.</summary>
public sealed record SharedPathIndex(int Version, IReadOnlyList<string> Shapes, bool Complete,
    DateTimeOffset BuiltAt, DateTimeOffset ExpiresAt, string? NextCursor = null,
    IReadOnlyList<string>? LegacyKeys = null)
{
    private readonly Lazy<HashSet<string>?> _lookup = new(() =>
        Version == 1 && Complete && Shapes is not null
        && (long)Shapes.Count + (LegacyKeys?.Count ?? 0) <= 2_000_000
        && Shapes.All(shape => Regex.IsMatch(shape ?? "", "\\Apurpose3-shared-(directory|file)-[a-f0-9]{64}\\z"))
        && (LegacyKeys is null || LegacyKeys.All(key => Regex.IsMatch(key ?? "", "\\A[a-f0-9]{64}\\z")))
            ? Shapes.ToHashSet(StringComparer.Ordinal) : null);
    private readonly Lazy<HashSet<string>> _legacyLookup = new(() =>
        (LegacyKeys ?? []).ToHashSet(StringComparer.Ordinal));

    public bool IsComplete => NextCursor is null && _lookup.Value is not null;
    public bool IsUsable => IsComplete
        && BuiltAt <= DateTimeOffset.UtcNow.AddMinutes(1) && ExpiresAt > DateTimeOffset.UtcNow
        && ExpiresAt <= BuiltAt.AddMinutes(5);

    // Expiry controls the next download, never the immutable snapshot already in use.
    public bool MayContain(string shape) => IsComplete && _lookup.Value!.Contains(shape);
    public bool ContainsFingerprint(SharedFingerprint fingerprint) => IsComplete
        && (SharedFingerprintProtocol.IsSharedPath(fingerprint)
            ? _lookup.Value!.Contains(fingerprint.Shape) : _legacyLookup.Value.Contains(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.Key))).ToLowerInvariant()));
}

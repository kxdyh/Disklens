using System.Text.Json.Serialization;
using DiskLens.Core.Models;

namespace DiskLens.Core.SharedKnowledge;

public sealed record SharedFingerprint(string SoftwareId, string VersionMajor, string RoleId, string Shape)
{
    [JsonIgnore] public string Key => $"{SoftwareId}|{VersionMajor}|{RoleId}|{Shape}";
}

public sealed record VerifiedSoftwareInstallation(string SoftwareId, string VersionMajor, string InstallRoot,
    bool HasInstallationEvidence, bool HasProductEvidence, bool HasTrustedPublisherEvidence = false);

public sealed record SharedInstallation(string InstallationId, string ManagementToken, long Epoch);

public sealed record SharedConsentSnapshot(bool Enabled, string AgreementVersion, bool ShareHistory,
    DateTimeOffset EnabledAt, long Epoch, string PolicyRevision, IReadOnlyList<string> ExcludedPaths,
    SharedInstallation? Installation)
{
    public const string CurrentAgreementVersion = "2026-11-beta3";
    public bool CanShare => Enabled && AgreementVersion == CurrentAgreementVersion && Installation is not null;
}

public interface ISharedConsentSource
{
    Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ISoftwareIdentityResolver
{
    Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default);

    Task<ISoftwareIdentityResolver> CreateLookupSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this);
    }
}

/// <summary>One lookup's installation index; a negative answer requires no file or registry access.</summary>
public interface ISoftwareIdentityLookupSnapshot : ISoftwareIdentityResolver
{
    bool MayResolvePath(string normalizedLocalPath);
}

/// <summary>Local-only stable random identities for observations that can never be reused.</summary>
public interface ISharedObservationIdentityProvider
{
    Task<string> GetOrCreateAsync(string normalizedLocalPath, CancellationToken cancellationToken = default);

    // A lookup must not allocate an identity for every newly scanned private path.
    // Older providers safely expose no own observations until they support snapshots.
    Task<ISharedObservationLookupSnapshot> CreateLookupSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ISharedObservationLookupSnapshot>(EmptySharedObservationLookupSnapshot.Instance);
    }
}

/// <summary>A local, read-only view of identities already owned by this installation.</summary>
public interface ISharedObservationLookupSnapshot : ISharedObservationIdentityProvider
{
    bool ContainsPath(string normalizedLocalPath);
}

internal sealed class EmptySharedObservationLookupSnapshot : ISharedObservationLookupSnapshot
{
    public static EmptySharedObservationLookupSnapshot Instance { get; } = new();
    public bool ContainsPath(string normalizedLocalPath) => false;
    public Task<string> GetOrCreateAsync(string normalizedLocalPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(string.Empty);
    }
}

public interface ISharedFingerprintExtractor
{
    Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default);
}

public sealed record SharedExperienceMetadata(string Key, string SoftwareLabel, string RoleLabel, string Purpose,
    IReadOnlyList<string> Conditions, int RuleVersion, long Revision, DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt, string MatchExplanation)
{
    public string ConfidenceLevel { get; init; } = "Unknown";
    public bool IsOwnObservation { get; init; }
    /// <summary>Display text may use controlled categories; never used as original AI evidence.</summary>
    public SharedPurposeDisplayTexts? DisplayTexts { get; init; }
}

public sealed record SharedExperienceEntry(string Key, string SoftwareId, string SoftwareLabel, string RoleId,
    string RoleLabel, string Purpose, string Action, IReadOnlyList<string> Conditions, string Explanation,
    int RuleVersion, long Revision, string Status, DateTimeOffset ExpiresAt, DateTimeOffset UpdatedAt)
{
    public string ConfidenceLevel { get; init; } = "Unknown";
    public bool IsOwnObservation { get; init; }
    public SharedPurposeDisplayTexts? DisplayTexts { get; init; }
    public string ShortPurpose { get; init; } = "";
    public string Reason { get; init; } = "";
    public string ShortPurposeEn { get; init; } = "";
    public string ReasonEn { get; init; } = "";
}

public sealed record SharedQueryItem(string Key, [property: JsonRequired] bool Matched, SharedExperienceEntry? Entry);
public sealed record SharedPurposeDisplayTexts(string ShortPurpose, string Reason, string ShortPurposeEn, string ReasonEn);
public sealed record SharedQueryResponse(IReadOnlyList<SharedQueryItem> Items);
public sealed record SharedConsentState(long Epoch, bool Enabled, string AgreementVersion, bool ShareHistory, string PolicyRevision);
public sealed record SharedConsentUpdate(long ExpectedEpoch, bool Enabled, string AgreementVersion, bool ShareHistory, string PolicyRevision);

public sealed record SharedContribution(string AnalysisId, string SoftwareId, string VersionMajor, string RoleId,
    string Shape, string Purpose, string Action, string Source, string ProviderClass, string ModelFamily,
    bool DefaultPrompt, bool SharedContext, string ConfidenceLevel = "Unknown")
{
    public string ShortPurpose { get; init; } = "";
    public string Reason { get; init; } = "";
    public string ShortPurposeEn { get; init; } = "";
    public string ReasonEn { get; init; } = "";
}
public sealed record SharedContributionReceipt(int Accepted, int Skipped, long Epoch)
{
    /// <summary>Local transport diagnosis, never a value supplied by the service response.</summary>
    [JsonIgnore] public bool NetworkUnavailable { get; init; }
    /// <summary>Locally observed failed items; server duplicate acknowledgments are not failures.</summary>
    [JsonIgnore] public int Failed { get; init; }
    /// <summary>Durably queued locally; these items have not been acknowledged by the server.</summary>
    [JsonIgnore] public int Pending { get; init; }
}

/// <summary>Only a validated anonymous contribution and its original age may enter an outbox.</summary>
public sealed record SharedQueuedContribution(SharedContribution Contribution, DateTimeOffset AnalyzedAt);

public interface ISharedContributionOutbox
{
    Task EnqueueAsync(SharedConsentSnapshot consent, IReadOnlyList<SharedQueuedContribution> items,
        CancellationToken cancellationToken = default);
    Task<SharedContributionReceipt> FlushAsync(CancellationToken cancellationToken = default);
    Task<int> PendingCountAsync(CancellationToken cancellationToken = default);
    Task SynchronizeConsentAsync(CancellationToken cancellationToken = default);
}
public sealed record SharedContributionItem(string Id, string AnalysisId, string Key, string Purpose, string Action)
{
    public string ShortPurpose { get; init; } = "";
    public string Reason { get; init; } = "";
    public string ShortPurposeEn { get; init; } = "";
    public string ReasonEn { get; init; } = "";
}
public sealed record SharedContributionList(IReadOnlyList<SharedContributionItem> Items, string? NextCursor = null);
public sealed record SharedWithdrawalReceipt(long Epoch)
{
    public bool CleanupPending { get; init; }
    public int CleanupDeleted { get; init; }
}

public sealed record SharedAnalysisProvenance(string ProviderClass, string ModelFamily, bool DefaultPrompt,
    bool SharedContext = false)
{
    public SharedConsentSnapshot? ConsentAtAnalysisStart { get; init; }
    public string? PromptFingerprint { get; init; }
}

/// <summary>Counts local candidate paths once; retry attempts update outcomes without counting paths twice.</summary>
public sealed record SharedResolutionProgress(int Processed, int Total, int Matched, int Failed, bool IsRetry = false);

public sealed record SharedResolutionBatch(IReadOnlyDictionary<string, AiAnalysisResult> ResultsByPath,
    int MatchedCount, int SkippedCount, bool Unavailable)
{
    public bool NetworkUnavailable { get; init; }
    /// <summary>Paths whose query failed or returned no valid per-key outcome, never an explicit cloud miss.</summary>
    public int FailedCount { get; init; }
    /// <summary>Local paths explicitly reported unmatched by a valid response, excluding unqueried or unavailable items.</summary>
    public IReadOnlyList<string> MissingPaths { get; init; } = [];
    public static SharedResolutionBatch Empty { get; } = new(new Dictionary<string, AiAnalysisResult>(), 0, 0, false);
}

public interface ISharedKnowledgePipeline
{
    Task<SharedConsentSnapshot?> CaptureConsentAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SharedConsentSnapshot?>(null);
    Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates, CancellationToken cancellationToken = default);
    Task<SharedContributionReceipt> ContributeAsync(IReadOnlyList<AiCandidateRecord> candidates,
        IReadOnlyList<AiAnalysisResult> results, SharedAnalysisProvenance provenance, bool historical = false,
        CancellationToken cancellationToken = default);
}

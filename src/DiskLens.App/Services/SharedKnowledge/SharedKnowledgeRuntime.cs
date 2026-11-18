using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.Policies;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>One instance shared by all scan tabs; no background registration before consent.</summary>
public sealed class SharedKnowledgeRuntime : IDisposable
{
    public const string DefaultEndpoint = SharedServiceIdentity.DefaultEndpoint;
    public SharedPreferenceStore Store { get; }
    public SharedKnowledgeSettings Settings { get; }
    public ISharedExperienceClient Client { get; }
    public ISharedFingerprintExtractor Extractor { get; }
    public ISharedKnowledgePipeline Pipeline { get; }
    public SharedContributionOutbox Outbox { get; }
    public AnalysisMemoryStore Memory { get; }
    public Uri Endpoint { get; }
    public INetworkAvailability NetworkAvailability { get; }
    public event EventHandler? NetworkUnavailable;
    public event EventHandler? ConnectionChanged;
    public bool IsLocalTest => Endpoint.IsLoopback;
    private readonly SharedExperienceClient? _ownedClient;
    private readonly WindowsSoftwareIdentityResolver? _softwareIdentityResolver;
    private readonly ISharedObservationIdentityProvider? _legacyObservationIdentities;
    private readonly SemaphoreSlim _legacyRecoveryGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _legacyRecoveryAttempts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _uploadLifetime = new();
    private readonly Task _uploadWorker;
    private readonly SemaphoreSlim _uploadWake = new(0, 1);
    private readonly Timer _networkRecoveryTimer;
    private bool _disposed;

    public SharedKnowledgeRuntime(AnalysisMemoryStore memory, SharedPreferenceStore? store = null,
        ISharedExperienceClient? client = null, ISharedFingerprintExtractor? extractor = null,
        INetworkAvailability? networkAvailability = null)
    {
        Memory = memory;
        NetworkAvailability = networkAvailability ?? SystemNetworkAvailability.Instance;
        var endpoint = Environment.GetEnvironmentVariable("DISKLENS_SHARED_ENDPOINT");
        Endpoint = new Uri((string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint).TrimEnd('/') + "/");
        // A custom-domain alias of the same official service retains its existing
        // encrypted state. Other endpoints, including local tests, stay isolated.
        var serviceIdentity = SharedServiceIdentity.GetStorageIdentity(Endpoint);
        var serviceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serviceIdentity))).ToLowerInvariant();
        Store = store ?? new SharedPreferenceStore(Path.Combine(AppDataDirectory.GetPath(), "shared-experience", serviceKey));
        Settings = new(Store);
        Client = client ?? (_ownedClient = new SharedExperienceClient(Endpoint));
        if (_ownedClient is not null)
            _ownedClient.RequestCompleted += OnSharedRequestCompleted;
        if (extractor is null)
        {
            _softwareIdentityResolver = new WindowsSoftwareIdentityResolver();
            _legacyObservationIdentities = new SharedObservationIdentityStore(Store.StorageDirectory, Endpoint, Store);
            Extractor = new SharedFingerprintExtractor(_softwareIdentityResolver, _legacyObservationIdentities);
        }
        else Extractor = extractor;
        Outbox = new SharedContributionOutbox(Store.StorageDirectory, Store, Client,
            networkAvailability: NetworkAvailability, requiresNetwork: !Endpoint.IsLoopback);
        Settings.BeforeConsentChangeAsync = Outbox.InvalidateConsentAsync;
        Pipeline = new SharedKnowledgePipeline(Store, Extractor, Client, outbox: Outbox,
            networkAvailability: NetworkAvailability, requiresNetwork: !Endpoint.IsLoopback);
        Settings.Changed += OnSharedSettingsChanged;
        _networkRecoveryTimer = new Timer(OnNetworkRecovery, null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _uploadWorker = Task.Run(RunUploadWorkerAsync);
    }

    private void OnSharedRequestCompleted(SharedRequestDiagnostic value)
        => SharedConnectionDiagnosticLog.Record(Store.StorageDirectory, value);

    public async Task<bool> DisableSharingAsync(CancellationToken cancellationToken = default)
    {
        // Local consent and pending uploads stop before contacting the service.
        await Settings.SuspendAsync(cancellationToken);
        var closed = await Settings.LoadAsync(cancellationToken);
        if (closed.Installation is not { } installation) return true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var state = await Client.GetConsentAsync(installation, timeout.Token);
            var updated = await Client.UpdateConsentAsync(installation,
                new(state.Epoch, false, SharedConsentSnapshot.CurrentAgreementVersion,
                    closed.ShareHistory, Guid.NewGuid().ToString("N")), timeout.Token);
            var current = await Settings.LoadAsync(cancellationToken);
            // Never overwrite a newer decision made while the request was in flight.
            if (current.Enabled || current.Installation != closed.Installation || current.Epoch != closed.Epoch)
                return false;
            await Settings.SaveAsync(current with
            {
                Epoch = updated.Epoch,
                Installation = installation with { Epoch = updated.Epoch }
            }, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException
            or System.Text.Json.JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    public static SharedAnalysisProvenance CreateProvenance(AiProviderOptions provider, bool tokenSaving)
    {
        var host = Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : "";
        var official = uri?.Scheme == "https" && host is "api.deepseek.com" or "api.openai.com";
        var family = provider.Kind == AiProviderKind.Ollama ? "ollama" : host == "api.deepseek.com" ? "deepseek"
            : host == "api.openai.com" ? "openai" : "other";
        // Both modes now use versioned instructions compiled into Core. Legacy
        // external templates are ignored; analysis and history still verify the
        // fingerprint against the text frozen into the actual request.
        var promptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(tokenSaving);
        return new(official ? "official" : "custom", family, DefaultPrompt: true) { PromptFingerprint = promptFingerprint };
    }

    public IReadOnlyList<AiCandidateRecord> BuildCandidates(FileSystemNode root, ScanSessionOptions options)
        => new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(root,
            options.AnalysisThresholdBytes, options.MaxAiChunkTokens, options.UseTokenSavingMode);

    /// <summary>Read existing cloud purposes for the scanned tree, independently of paid AI thresholds.</summary>
    public async Task<SharedResolutionBatch> ResolveScannedExperienceAsync(FileSystemNode root,
        CancellationToken cancellationToken = default, IProgress<SharedLookupProgress>? progress = null,
        bool forceRefreshPathIndex = false, bool recoverLegacyContributions = true)
    {
        ReportLookupProgress(progress, new(SharedLookupStage.Preparing, 0, 0, 0, 0));
        var consent = await Store.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!consent.CanShare) return SharedResolutionBatch.Empty;
        var lookupExtractor = Extractor is SharedFingerprintExtractor concrete
            ? await concrete.CreateLookupExtractorAsync(cancellationToken).ConfigureAwait(false) : null;
        ReportLookupProgress(progress, new(SharedLookupStage.DownloadingIndex, 0, 0, 0, 0));
        var (sharedPathIndex, indexNetworkUnavailable) = await DownloadLookupIndexAsync(progress, cancellationToken,
            forceRefreshPathIndex).ConfigureAwait(false);
        var candidates = SharedLookupCandidateBuilder.Build(root, cancellationToken);
        ReportLookupProgress(progress, new(SharedLookupStage.Matching, 0, candidates.Count, 0, 0));
        (bool Unavailable, bool Changed) recovery;
        try
        {
            // Legacy identities belong only to contribution recovery. A local
            // recovery-store failure must not prevent independent cloud reads.
            var legacyIdentities = !recoverLegacyContributions || _legacyObservationIdentities is null ? null
                : await _legacyObservationIdentities.CreateLookupSnapshotAsync(cancellationToken).ConfigureAwait(false);
            recovery = recoverLegacyContributions
                ? await RecoverLegacyPublicContributionsAsync(root, candidates,
                    legacyIdentities, consent, cancellationToken).ConfigureAwait(false)
                : (false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException
            or IOException or System.Text.Json.JsonException or UnauthorizedAccessException or CryptographicException)
        { recovery = (true, false); }
        if (recovery.Changed)
        {
            // Upload invalidates the client's index cache. Include the originals
            // just upgraded to public keys in this same resolution pass.
            (sharedPathIndex, indexNetworkUnavailable) = await DownloadLookupIndexAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        if (sharedPathIndex is not { IsComplete: true })
        {
            // Index availability gates reads only. Recovery above can durably
            // hand off original contributions even when index download fails.
            // Never treat an unavailable index as cloud misses or clear answers.
            ReportLookupProgress(progress, new(SharedLookupStage.Completed, 0, candidates.Count, 0, 0));
            return SharedResolutionBatch.Empty with { Unavailable = true, NetworkUnavailable = indexNetworkUnavailable };
        }
        var lookupPipeline = new SharedKnowledgePipeline(Store, lookupExtractor ?? Extractor, Client,
            networkAvailability: NetworkAvailability, requiresNetwork: !Endpoint.IsLoopback);
        var batchProgress = new LookupBatchProgress(progress, candidates.Count);
        var resolution = await lookupPipeline.ResolveAsync(candidates, cancellationToken, sharedPathIndex, batchProgress).ConfigureAwait(false);
        foreach (var result in resolution.ResultsByPath.Values)
            if (result.SharedEntry is { } entry)
                _legacyRecoveryAttempts.TryAdd(consent.Epoch + "|" + entry.Key, 0);
        ReportLookupProgress(progress, new(SharedLookupStage.Completed, batchProgress.Last.Processed,
            candidates.Count, resolution.MatchedCount, resolution.FailedCount));
        // Read completeness comes only from the index and lookup outcomes.
        // Pending/recovered contributions keep their own durable upload retries;
        // an upload failure must not restart a successfully matched scan tree.
        return resolution;
    }

    private async Task<(SharedPathIndex? Index, bool NetworkUnavailable)> DownloadLookupIndexAsync(
        IProgress<SharedLookupProgress>? progress, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var networkUnavailable = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                if (!Endpoint.IsLoopback && !NetworkAvailability.IsNetworkAvailable) break;
                ReportLookupProgress(progress, new(SharedLookupStage.Retrying, 0, 0, 0, 0));
            }
            try
            {
                var index = await (forceRefresh
                    ? Client.RefreshSharedPathIndexAsync(cancellationToken)
                    : Client.GetSharedPathIndexAsync(cancellationToken)).ConfigureAwait(false);
                if (index is { IsComplete: true }) return (index, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException
                or IOException or System.Text.Json.JsonException)
            {
                networkUnavailable = !Endpoint.IsLoopback
                    && NetworkFailureClassifier.IsOffline(ex, NetworkAvailability);
            }
        }
        return (null, networkUnavailable);
    }

    private sealed class LookupBatchProgress(IProgress<SharedLookupProgress>? progress, int total)
        : IProgress<SharedResolutionProgress>
    {
        public SharedResolutionProgress Last { get; private set; } = new(0, total, 0, 0);
        public void Report(SharedResolutionProgress value)
        {
            Last = value;
            ReportLookupProgress(progress, new(value.IsRetry ? SharedLookupStage.Retrying : SharedLookupStage.Matching,
                value.Processed, value.Total, value.Matched, value.Failed));
        }
    }

    private static void ReportLookupProgress(IProgress<SharedLookupProgress>? progress, SharedLookupProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception) { /* Optional progress rendering must not discard cloud answers. */ }
    }

    private async Task<(bool Unavailable, bool Changed)> RecoverLegacyPublicContributionsAsync(FileSystemNode root,
        IReadOnlyList<AiCandidateRecord> candidates, ISharedObservationLookupSnapshot? legacyIdentities,
        SharedConsentSnapshot consent, CancellationToken cancellationToken)
    {
        await _legacyRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await Memory.LoadEntriesAsync(root.Path, cancellationToken).ConfigureAwait(false);
            // Pending originals must not depend on today's paid-AI threshold or
            // on the presence of a legacy installation-only lookup identity.
            var recovered = await RecoverPendingContributionsAsync(root, candidates, entries,
                consent, cancellationToken).ConfigureAwait(false);
            if (legacyIdentities is null || !candidates.Any(candidate => legacyIdentities.ContainsPath(candidate.Path)))
                return (recovered.Unavailable, recovered.Changed);
            var pending = new List<(AiCandidateRecord Candidate, AiAnalysisMemoryEntry Entry, SharedAnalysisProvenance Provenance, bool Historical)>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recovered.HandledPaths.Contains(candidate.Path)
                    || !legacyIdentities.ContainsPath(candidate.Path) || !entries.TryGetValue(candidate.Path, out var entry)
                    // Pending intent is bound to its original consent/signature;
                    // rejection above must not relabel it as a legacy import.
                    || entry.SharedContributionPending
                    || !entry.IsUsableAt(DateTimeOffset.UtcNow) || entry.IsDirectory != candidate.IsDirectory
                    || entry.Result.AnalysisEvidenceScope != AiAnalysisEvidenceScope.PathPurpose
                    || entry.Result.EffectiveConfidenceLevel is not (AiConfidenceLevel.Medium or AiConfidenceLevel.High)
                    || entry.Result.OriginalAnalysisId is null) continue;
                var fingerprint = await Extractor.ExtractAsync(candidate, consent.ExcludedPaths, cancellationToken).ConfigureAwait(false);
                if (fingerprint is null || !SharedFingerprintProtocol.IsSharedPath(fingerprint)
                    || _legacyRecoveryAttempts.ContainsKey(consent.Epoch + "|" + fingerprint.Key)) continue;
                var historical = entry.AnalyzedAt < consent.EnabledAt;
                if (historical && !consent.ShareHistory) continue;
                var host = Uri.TryCreate(entry.Endpoint, UriKind.Absolute, out var endpoint) ? endpoint.Host : "";
                var official = endpoint?.Scheme == "https" && host is "api.deepseek.com" or "api.openai.com";
                var family = entry.ProviderKind == AiProviderKind.Ollama ? "ollama"
                    : host == "api.deepseek.com" ? "deepseek" : host == "api.openai.com" ? "openai" : "other";
                var provenance = new SharedAnalysisProvenance(official ? "official" : "custom", family,
                    entry.PromptFingerprint == AiPromptFactory.CreateSystemPromptFingerprint(entry.UseTokenSavingMode))
                    { ConsentAtAnalysisStart = consent, PromptFingerprint = entry.PromptFingerprint };
                pending.Add((candidate,entry,provenance,historical));
            }
            if (pending.Count == 0) return (recovered.Unavailable, recovered.Changed);
            var flushed = await Outbox.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flushed.Failed > 0 || flushed.Pending > 0 || flushed.NetworkUnavailable
                || await Outbox.PendingCountAsync(cancellationToken).ConfigureAwait(false) > 0) return (true, recovered.Changed);
            var unavailable = recovered.Unavailable;
            var changed = recovered.Changed;
            foreach (var group in pending.GroupBy(item => (item.Provenance.ProviderClass, item.Provenance.ModelFamily,
                item.Provenance.DefaultPrompt, item.Historical)))
            {
                var items = group.ToArray();
                var receipt = await Pipeline.ContributeAsync(items.Select(item => item.Candidate).ToArray(),
                    items.Select(item => item.Entry.Result).ToArray(), items[0].Provenance,
                    items[0].Historical, cancellationToken).ConfigureAwait(false);
                unavailable |= receipt.Failed > 0 || receipt.Pending > 0 || receipt.NetworkUnavailable;
                changed |= receipt.Accepted > 0;
            }
            return (unavailable, changed);
        }
        finally { _legacyRecoveryGate.Release(); }
    }

    private async Task<(bool Unavailable, bool Changed, IReadOnlySet<string> HandledPaths)> RecoverPendingContributionsAsync(
        FileSystemNode root, IReadOnlyList<AiCandidateRecord> scannedCandidates,
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> entries, SharedConsentSnapshot consent,
        CancellationToken cancellationToken)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var consentFingerprint = CreateContributionConsentFingerprint(consent);
        if (consentFingerprint is null) return (false, false, handled);
        var originals = scannedCandidates.Where(candidate => entries.TryGetValue(candidate.Path, out var entry)
                && entry.SharedContributionPending && !entry.SharedContributionCompleted
                && string.Equals(entry.SharedContributionConsentFingerprint, consentFingerprint, StringComparison.Ordinal))
            .Select(candidate => entries[candidate.Path]).ToArray();
        if (originals.Length == 0) return (false, false, handled);
        var pending = new List<(AiCandidateRecord Candidate, AiAnalysisMemoryEntry Entry, AiAnalysisResult Result,
            SharedAnalysisProvenance Provenance)>();
        foreach (var context in originals.GroupBy(entry => (entry.UseTokenSavingMode, entry.MaxAiChunkTokens)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = context.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            // Explicit targets bypass the current analysis/display thresholds,
            // while rebuilding signatures and evidence with each original mode.
            var candidates = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(root,
                thresholdBytes: 1, context.Key.MaxAiChunkTokens, context.Key.UseTokenSavingMode, paths);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[candidate.Path];
                var provider = new AiProviderOptions(entry.ProviderKind, entry.Endpoint, entry.Model, null,
                    entry.Temperature, entry.ReasoningEffort);
                if (!provider.IsConfigured) continue;
                var provenance = CreateProvenance(provider, entry.UseTokenSavingMode) with { ConsentAtAnalysisStart = consent };
                var result = entry.Result with { OriginalAnalyzedAt = entry.Result.OriginalAnalyzedAt ?? entry.AnalyzedAt };
                if (!entry.IsUsableAt(DateTimeOffset.UtcNow) || entry.Signature != candidate.Signature
                    || entry.IsDirectory != candidate.IsDirectory
                    || !entry.MatchesAnalysisContext(entry.ProviderKind, entry.Endpoint, entry.Model,
                        provenance.PromptFingerprint!, entry.UseTokenSavingMode, entry.AnalysisThresholdBytes,
                        entry.MaxAiChunkTokens, entry.ReasoningEffort, entry.Temperature)
                    || candidate.HasLimitedAnalysisEvidence || result.IsAnalysisEvidenceLimited || result.LooksLikeFallback()
                    || result.SharedEntry is not null || result.AnalysisEvidenceScope != candidate.AnalysisEvidenceScope
                    || result.EffectiveConfidenceLevel is not (AiConfidenceLevel.Medium or AiConfidenceLevel.High)
                    || string.IsNullOrWhiteSpace(result.OriginalAnalysisId)
                    || result.OriginalAnalyzedAt is not { } at || at < consent.EnabledAt
                    || at <= DateTimeOffset.UnixEpoch || at > DateTimeOffset.UtcNow) continue;
                pending.Add((candidate, entry, result with { Origin = AiAnalysisResultOrigin.AiAnalyzed }, provenance));
            }
        }
        var unavailable = false;
        var changed = false;
        foreach (var group in pending.GroupBy(item => (item.Provenance.ProviderClass,
            item.Provenance.ModelFamily, item.Provenance.PromptFingerprint)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateContributionConsentFingerprint(await Store.GetSnapshotAsync(cancellationToken).ConfigureAwait(false))
                != consentFingerprint) break;
            var items = group.ToArray();
            var receipt = await Pipeline.ContributeAsync(items.Select(item => item.Candidate).ToArray(),
                items.Select(item => item.Result).ToArray(), items[0].Provenance,
                historical: false, cancellationToken).ConfigureAwait(false);
            unavailable |= receipt.Failed > 0 || receipt.Pending > 0 || receipt.NetworkUnavailable;
            changed |= receipt.Accepted > 0;
            // The encrypted outbox owns retries once it has saved these payloads.
            // Keep the cache intent if persistence failed, consent changed, or the
            // original was replaced/cleared while the request was running.
            if ((receipt.Failed == 0 || receipt.Pending > 0) && receipt.Epoch == consent.Epoch
                && CreateContributionConsentFingerprint(await Store.GetSnapshotAsync(cancellationToken).ConfigureAwait(false))
                    == consentFingerprint)
            {
                await Memory.MarkContributionsCompletedIfUnchangedAsync(root.Path,
                    items.Select(item => item.Entry).ToArray(), cancellationToken).ConfigureAwait(false);
                handled.UnionWith(items.Select(item => item.Entry.Path));
            }
        }
        return (unavailable, changed, handled);
    }

    private static string? CreateContributionConsentFingerprint(SharedConsentSnapshot consent)
    {
        if (consent is not { CanShare: true, Installation: { } installation }) return null;
        // Must match the fingerprint saved at AI analysis start exactly.
        var fields = new[]
        {
            consent.Enabled.ToString(), consent.AgreementVersion, consent.ShareHistory.ToString(),
            consent.EnabledAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            consent.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), consent.PolicyRevision,
            installation.InstallationId,
            string.Join("\u001f", consent.ExcludedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)),
        };
        var canonical = string.Join("\u001e", fields.Select(field => $"{field.Length}:{field}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public Task<IReadOnlyList<string>> PreviewAsync(FileSystemNode root, ScanSessionOptions options,
        IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
    {
        var display = new List<string>();
        foreach (var candidate in SharedLookupCandidateBuilder.Build(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SharedFingerprintExtractor.IsExcluded(candidate.Path, exclusions)) continue;
            // Preview only renders normalized path identities. Never perform a
            // registry/install probe or disk read for every scanned file here.
            var fingerprint = SharedFingerprintExtractor.CreateSharedPathFingerprint(candidate.Path, candidate.IsDirectory);
            if (fingerprint is not null)
                display.Add(L.F($"{candidate.Name} → {fingerprint.SoftwareId} / {fingerprint.RoleId} / 主版本 {fingerprint.VersionMajor}"));
        }
        return Task.FromResult<IReadOnlyList<string>>(display);
    }

    public async Task<SharedContributionReceipt> ImportHistoryAsync(FileSystemNode root, ScanSessionOptions options,
        AiProviderOptions provider, CancellationToken cancellationToken = default)
    {
        var consent = await Store.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!consent.CanShare || !consent.ShareHistory) return new(0, 0, consent.Epoch);
        var entries = await Memory.LoadEntriesAsync(options.AnalysisMemoryRootPath ?? root.Path, cancellationToken).ConfigureAwait(false);
        // Existing analyses belong to their original selection threshold. Build
        // only their scanned paths, without applying today's paid-AI size filter.
        var includedPaths = SharedLookupCandidateBuilder.Build(root, cancellationToken)
            .Where(candidate => entries.TryGetValue(candidate.Path, out var entry)
                && entry.IsUsableAt(DateTimeOffset.UtcNow))
            .Select(candidate => candidate.Path).ToHashSet(StringComparer.Ordinal);
        var candidates = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(root,
            thresholdBytes: 1, options.MaxAiChunkTokens, options.UseTokenSavingMode, includedPaths);
        var fingerprint = AiPromptFactory.CreateSystemPromptFingerprint(options.UseTokenSavingMode);
        var results = candidates.Where(candidate => entries.TryGetValue(candidate.Path, out var entry)
                && entry.Signature == candidate.Signature && entry.IsDirectory == candidate.IsDirectory
                && entry.AnalyzedAt > DateTimeOffset.UnixEpoch && entry.AnalyzedAt <= DateTimeOffset.UtcNow
                && entry.MatchesAnalysisContext(provider.Kind, provider.Endpoint, provider.Model, fingerprint,
                    options.UseTokenSavingMode, entry.AnalysisThresholdBytes, options.MaxAiChunkTokens, provider.ReasoningEffort, provider.Temperature)
                && entry.IsUsableAt(DateTimeOffset.UtcNow)
                && entry.Result.AnalysisEvidenceScope == candidate.AnalysisEvidenceScope
                && entry.Result.Origin == AiAnalysisResultOrigin.AiAnalyzed && !entry.Result.LooksLikeFallback())
            .Select(candidate => entries[candidate.Path].Result).ToArray();
        var provenance = CreateProvenance(provider, options.UseTokenSavingMode);
        provenance = provenance with { ConsentAtAnalysisStart = consent,
            DefaultPrompt = provenance.DefaultPrompt && provenance.PromptFingerprint == fingerprint };
        return await Pipeline.ContributeAsync(candidates, results, provenance,
            historical: true, cancellationToken).ConfigureAwait(false);
    }

    private async void OnSharedSettingsChanged(object? sender, EventArgs e)
    {
        try { await Outbox.SynchronizeConsentAsync(_uploadLifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    private async Task RunUploadWorkerAsync()
    {
        while (!_uploadLifetime.IsCancellationRequested)
        {
            try
            {
                await FlushPendingUploadsAsync(_uploadLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_uploadLifetime.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (_uploadLifetime.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException) { }
            try { await _uploadWake.WaitAsync(TimeSpan.FromSeconds(30), _uploadLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FlushPendingUploadsAsync(CancellationToken cancellationToken)
    {
        var receipt = await Outbox.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!receipt.NetworkUnavailable) return;
        // A transient link failure is not a withdrawal of the user's consent.
        // The encrypted queue and its original analysis IDs remain recoverable.
        if (!_disposed) NetworkUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => ScheduleNetworkRecovery();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => ScheduleNetworkRecovery();
    private void ScheduleNetworkRecovery()
    {
        if (_disposed) return;
        try { _networkRecoveryTimer.Change(1000, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private async void OnNetworkRecovery(object? state)
    {
        if (_disposed) return;
        try
        {
            await Outbox.RetryAfterNetworkChangeAsync(_uploadLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SemaphoreFullException
            or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException) { }
        // A queue persistence error must not prevent independent read-only
        // recovery after the network changes.
        if (_disposed) return;
        try { if (_uploadWake.CurrentCount == 0) _uploadWake.Release(); }
        catch (SemaphoreFullException) { }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _networkRecoveryTimer.Dispose();
        Settings.Changed -= OnSharedSettingsChanged;
        Settings.BeforeConsentChangeAsync = null;
        _uploadLifetime.Cancel();
        Outbox.Dispose();
        // Let cancellation settle before releasing the HTTP client used by this worker.
        _ = _uploadWorker.ContinueWith(completed =>
        {
            _ = completed.Exception;
            if (_ownedClient is not null)
                _ownedClient.RequestCompleted -= OnSharedRequestCompleted;
            _ownedClient?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

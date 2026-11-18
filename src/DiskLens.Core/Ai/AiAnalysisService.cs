using System.Net;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Core.Ai;

public sealed record AiAnalysisMemorySession(long Epoch, long SnapshotGeneration);

// Optional lifecycle contract keeps custom/test stores compatible with the original interface.
public interface IVersionedAiAnalysisMemoryStore : IAiAnalysisMemoryStore
{
    Task<AiAnalysisMemorySession> BeginSessionAsync(string rootPath, long snapshotGeneration, CancellationToken cancellationToken = default);
    Task SaveSessionEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        AiAnalysisMemorySession session, CancellationToken cancellationToken = default);
}

public sealed class AiAnalysisService
{
    private const int MaxResponseRecoveryDepth = 3;
    // All scan tabs share one fresh request lane and one recovery request lane.
    private static readonly SemaphoreSlim FreshRequestLane = new(1, 1);
    private static readonly SemaphoreSlim RetryRequestLane = new(1, 1);
    private static readonly ConditionalWeakTable<IAiAnalysisMemoryStore, ConcurrentDictionary<string, AnalysisFlight>> Flights = new();
    private static readonly ConditionalWeakTable<FileSystemNode, SnapshotStamp> SnapshotGenerations = new();
    private static long _nextSnapshotGeneration = DateTimeOffset.UtcNow.UtcTicks;

    private readonly IReadOnlyDictionary<AiProviderKind, IAiProvider> _providers;
    private readonly AiCandidateBuilder _candidateBuilder;
    private readonly AiBatchPlanner _batchPlanner;
    private readonly IAiAnalysisMemoryStore _memoryStore;
    private readonly ISharedKnowledgePipeline? _sharedKnowledge;
    public INetworkAvailability NetworkAvailability { get; }

    public AiAnalysisService(
        IEnumerable<IAiProvider> providers,
        AiCandidateBuilder candidateBuilder,
        AiBatchPlanner batchPlanner,
        IAiAnalysisMemoryStore? memoryStore = null,
        ISharedKnowledgePipeline? sharedKnowledge = null,
        INetworkAvailability? networkAvailability = null)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _candidateBuilder = candidateBuilder;
        _batchPlanner = batchPlanner;
        _memoryStore = memoryStore ?? NoOpAiAnalysisMemoryStore.Instance;
        _sharedKnowledge = sharedKnowledge;
        NetworkAvailability = networkAvailability ?? SystemNetworkAvailability.Instance;
    }

    public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
        FileSystemNode root,
        ScanSessionOptions options,
        AiProviderOptions providerOptions,
        CancellationToken cancellationToken = default)
    {
        return (await AnalyzeWithReportAsync(root, options, providerOptions, cancellationToken).ConfigureAwait(false)).Results;
    }

    /// <summary>Reads compatible personal history without starting an analysis, changing the cache, or contacting sharing services.</summary>
    public async Task<IReadOnlyList<AiAnalysisResult>> LoadReusableLocalResultsAsync(
        FileSystemNode root, ScanSessionOptions options, AiProviderOptions providerOptions,
        CancellationToken cancellationToken = default)
    {
        if (options.BypassAnalysisMemory) return [];
        var candidates = _candidateBuilder.BuildCandidates(root, options.AnalysisThresholdBytes,
                options.MaxAiChunkTokens, options.UseTokenSavingMode, options.IncludedAnalysisPaths)
            .Where(candidate => options.ExcludedAnalysisPaths?.Contains(candidate.Path) != true).ToArray();
        if (candidates.Length == 0) return [];
        var memoryRootPath = string.IsNullOrWhiteSpace(options.AnalysisMemoryRootPath) ? root.Path : options.AnalysisMemoryRootPath;
        var entries = await _memoryStore.LoadEntriesAsync(memoryRootPath, cancellationToken).ConfigureAwait(false);
        var promptFingerprint = new AiTokenBudgetSession(options.MaxAiAnalysisTokens, options.UseTokenSavingMode).SystemPromptFingerprint;
        var results = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(candidate.Path, out var entry)
                && CanReuseLocalEntry(entry, candidate, options, providerOptions, promptFingerprint))
                results[candidate.Path] = ConstrainLimitedResult(entry.Result, candidate, compactPayload: false)
                    with { Origin = AiAnalysisResultOrigin.CacheReused };
        }
        AiDeletionAdvicePolicy.ConstrainAncestors(results);
        return results.Values.ToArray();
    }

    private static bool CanReuseLocalEntry(AiAnalysisMemoryEntry? entry, AiCandidateRecord candidate,
        ScanSessionOptions options, AiProviderOptions providerOptions, string promptFingerprint) =>
        !options.BypassAnalysisMemory && !candidate.HasLimitedAnalysisEvidence
        && entry is not null && entry.IsUsableAt(DateTimeOffset.UtcNow)
        && string.Equals(entry.Path, candidate.Path, StringComparison.Ordinal)
        && entry.IsDirectory == candidate.IsDirectory
        && string.Equals(entry.Signature, candidate.Signature, StringComparison.OrdinalIgnoreCase)
        && entry.MatchesAnalysisContext(providerOptions.Kind, providerOptions.Endpoint, providerOptions.Model,
            promptFingerprint, options.UseTokenSavingMode, options.AnalysisThresholdBytes,
            options.MaxAiChunkTokens, providerOptions.ReasoningEffort, providerOptions.Temperature)
        && entry.Result.AnalysisEvidenceScope == candidate.AnalysisEvidenceScope
        && !entry.Result.LooksLikeFallback();

    public async Task<AiAnalysisReport> AnalyzeWithReportAsync(
        FileSystemNode root,
        ScanSessionOptions options,
        AiProviderOptions providerOptions,
        CancellationToken cancellationToken = default,
        IProgress<AiAnalysisProgress>? progress = null,
        SharedAnalysisProvenance? provenance = null,
        Func<AiAnalysisSubmissionPreview, CancellationToken, Task<bool>>? confirmSubmission = null)
    {
        // The budget belongs to this invocation, never to a batch or a shared service field.
        var budget = new AiTokenBudgetSession(options.MaxAiAnalysisTokens, options.UseTokenSavingMode);
        var tracker = new AiAnalysisProgressTracker(progress);
        SharedAnalysisProvenance? boundProvenance = null;
        if (_sharedKnowledge is not null && provenance is not null)
        {
            try
            {
                var consent = await _sharedKnowledge.CaptureConsentAsync(cancellationToken).ConfigureAwait(false);
                boundProvenance = provenance with
                {
                    ConsentAtAnalysisStart = consent,
                    DefaultPrompt = provenance.DefaultPrompt && string.Equals(provenance.PromptFingerprint,
                        budget.SystemPromptFingerprint, StringComparison.Ordinal)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* Optional sharing must never prevent personal analysis. */ }
        }
        var sharedState = new SharedInvocationState(boundProvenance);
        tracker.Report(AiAnalysisStage.Preparing, "正在整理超过阈值的文件与目录。");
        try
        {
            var results = await AnalyzeCoreAsync(root, options, providerOptions, budget, tracker, cancellationToken,
                sharedState: sharedState, confirmSubmission: confirmSubmission).ConfigureAwait(false);
            tracker.IncludeResults(results);
            var hasUnfinishedResults = results.Any(result => result.LooksLikeFallback());
            tracker.Report(budget.IsStopped ? AiAnalysisStage.StoppedForBudget
                    : hasUnfinishedResults ? AiAnalysisStage.Failed : AiAnalysisStage.Completed,
                budget.IsStopped ? "已达到本次 Token 预算，已完成结果已保留。"
                    : hasUnfinishedResults ? "部分项目未获得有效结果，可再次分析。"
                    : results.Count == 0 ? "本次没有需要分析的候选项目。" : "分析结束，结果已整理。" + sharedState.Warning + sharedState.CacheWarning);
            return new AiAnalysisReport(results, budget.Report)
            { NetworkUnavailable = sharedState.NetworkUnavailable,
                SharedKnowledgeWarning = sharedState.Warning, PersistenceWarning = sharedState.CacheWarning,
                TotalCandidates = tracker.TotalCandidates, SubmittedCandidateCount = tracker.SubmittedCandidateCount,
                RetryRequestCount = tracker.RetryRequestCount, RetriedCandidateCount = tracker.RetriedCandidateCount,
                RecoveredCandidateCount = tracker.RecoveredCandidateCount,
                QueuedRetryCount = tracker.QueuedRetryCount, ActiveRequestCount = tracker.ActiveRequestCount,
                LimitedScopeCount = results.Count(result => result.IsScopeLimited),
                SharedMissingPaths = sharedState.MissingPaths,
                SharedSkippedItems = sharedState.SkippedItems };
        }
        catch (AiAnalysisCanceledException ex)
        {
            ex.TokenBudget = budget.Report;
            tracker.IncludeResults(ex.PartialResults);
            tracker.Report(AiAnalysisStage.Canceled, "分析已取消，已完成结果已保留。");
            throw;
        }
        catch (OperationCanceledException)
        {
            tracker.Report(AiAnalysisStage.Canceled, "分析已取消。");
            throw;
        }
        catch (Exception)
        {
            tracker.Report(AiAnalysisStage.Failed, "分析中断，请检查模型连接后重试。");
            throw;
        }
    }

    private async Task<AiSharedLookupReuse?> GetReusableSharedLookupAsync(ScanSessionOptions options,
        SharedConsentSnapshot? analysisConsent, CancellationToken cancellationToken)
    {
        if (options.BypassAnalysisMemory || options.SharedLookupReuse is not { } lookup
            || _sharedKnowledge is null || !NetworkAvailability.IsNetworkAvailable) return null;
        var now = DateTimeOffset.UtcNow;
        if (lookup.CapturedAt > now || now - lookup.CapturedAt > TimeSpan.FromMinutes(2)
            || !SameSharedLookupConsent(lookup.Consent, analysisConsent)) return null;
        try
        {
            var current = await _sharedKnowledge.CaptureConsentAsync(cancellationToken).ConfigureAwait(false);
            if (!SameSharedLookupConsent(lookup.Consent, current)
                || !NetworkAvailability.IsNetworkAvailable) return null;
            // Own this invocation's path comparers and collections. In particular,
            // a caller's case-insensitive map cannot authorize a different path.
            return lookup with
            {
                Consent = lookup.Consent with { ExcludedPaths = lookup.Consent.ExcludedPaths.ToArray() },
                ResultsByPath = lookup.ResultsByPath.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                MissingPaths = lookup.MissingPaths.ToHashSet(StringComparer.Ordinal),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static bool SameSharedLookupConsent(SharedConsentSnapshot? left, SharedConsentSnapshot? right)
        => left is { CanShare: true } && right is { CanShare: true }
            && left.Installation == right.Installation && left.Epoch == right.Epoch
            && left.EnabledAt == right.EnabledAt && left.AgreementVersion == right.AgreementVersion
            && left.PolicyRevision == right.PolicyRevision && left.ShareHistory == right.ShareHistory
            && left.ExcludedPaths.SequenceEqual(right.ExcludedPaths, StringComparer.OrdinalIgnoreCase);

    private static bool CanReuseSharedLookupResult(AiAnalysisResult result, AiCandidateRecord candidate,
        SharedConsentSnapshot consent, DateTimeOffset now)
    {
        if (candidate.HasLimitedAnalysisEvidence || result is null
            || !string.Equals(result.Path, candidate.Path, StringComparison.Ordinal)
            || result.Origin != AiAnalysisResultOrigin.SharedExperience || result.IsFallback || result.LooksLikeFallback()
            || result.AnalysisEvidenceScope != candidate.AnalysisEvidenceScope
            || SharedFingerprintExtractor.IsExcluded(candidate.Path, consent.ExcludedPaths)
            || result.SharedEntry is not { } shared || shared.RuleVersion is not (2 or 3) || shared.Revision <= 0
            || shared.Purpose == "unknown" || !SharedFingerprintProtocol.Purposes.Contains(shared.Purpose)
            || shared.ConfidenceLevel is not ("High" or "Medium")
            || shared.UpdatedAt <= DateTimeOffset.UnixEpoch || shared.UpdatedAt > now.AddMinutes(1)
            || shared.ExpiresAt <= now || shared.ExpiresAt > now.AddMinutes(16)
            || !SharedFingerprintProtocol.ValidConclusion(shared.Purpose, result.SuggestedAction.ToString())) return false;
        var parts = shared.Key.Split('|');
        if (parts.Length != 4) return false;
        var fingerprint = new SharedFingerprint(parts[0], parts[1], parts[2], parts[3]);
        return SharedFingerprintProtocol.CanReuse(fingerprint) && !shared.IsOwnObservation
            || SharedFingerprintProtocol.IsOwnObservation(fingerprint) && shared.IsOwnObservation;
    }

    private async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeCoreAsync(
        FileSystemNode root,
        ScanSessionOptions options,
        AiProviderOptions providerOptions,
        AiTokenBudgetSession budget,
        AiAnalysisProgressTracker tracker,
        CancellationToken cancellationToken,
        SharedInvocationState? sharedState = null,
        Func<AiAnalysisSubmissionPreview, CancellationToken, Task<bool>>? confirmSubmission = null)
    {
        _providers.TryGetValue(providerOptions.Kind, out var configuredProvider);
        IAiProvider? provider = providerOptions.IsConfigured && configuredProvider is not null
            ? new NetworkAwareAiProvider(new BudgetedAiProvider(tracker.Observe(configuredProvider), budget),
                NetworkAvailability, sharedState) : null;

        IReadOnlyList<AiCandidateRecord> candidates = _candidateBuilder.BuildCandidates(root,
                options.AnalysisThresholdBytes, options.MaxAiChunkTokens, options.UseTokenSavingMode,
                options.IncludedAnalysisPaths)
            .Where(candidate => options.ExcludedAnalysisPaths?.Contains(candidate.Path) != true).ToArray();
        tracker.SetTotal(candidates.Count);
        if (candidates.Count == 0)
        {
            return [];
        }

        var promptFingerprint = budget.SystemPromptFingerprint;
        tracker.Report(AiAnalysisStage.LoadingCache, $"已找到 {candidates.Count} 项，正在检查本地历史与云端经验。");
        // Keep one cache partition when switching between a scan and its child directories.
        var memoryRootPath = string.IsNullOrWhiteSpace(options.AnalysisMemoryRootPath) ? root.Path : options.AnalysisMemoryRootPath;
        var lifecycle = _memoryStore as IVersionedAiAnalysisMemoryStore;
        var snapshotGeneration = options.AnalysisSnapshotGeneration > 0 ? options.AnalysisSnapshotGeneration
            : SnapshotGenerations.GetValue(root, _ => new SnapshotStamp(Interlocked.Increment(ref _nextSnapshotGeneration))).Value;
        AiAnalysisMemorySession? session = null;
        var cacheAvailable = true;
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> loadedEntries = new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal);
        try
        {
            if (lifecycle is not null) session = await lifecycle.BeginSessionAsync(memoryRootPath,
                snapshotGeneration, cancellationToken).ConfigureAwait(false);
            loadedEntries = await _memoryStore.LoadEntriesAsync(memoryRootPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            cacheAvailable = false;
            if (sharedState is not null) sharedState.CacheWarning = "本地缓存无法读取，本次结果可能无法持久保存。";
            tracker.Report(AiAnalysisStage.LoadingCache, "本地缓存暂不可用，将继续本次分析。");
        }
        // IAiAnalysisMemoryStore is an extension point; do not trust its dictionary
        // comparer to preserve path identity (a store may use OrdinalIgnoreCase).
        var existingEntries = new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal);
        foreach (var entry in loadedEntries)
        {
            if (entry.Value is not null && entry.Value.IsUsableAt(DateTimeOffset.UtcNow)
                && string.Equals(entry.Key, entry.Value.Path, StringComparison.Ordinal))
                existingEntries.TryAdd(entry.Key, entry.Value);
        }
        var resultsByPath = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        var preservedEntries = new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal);
        var contributionRetries = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        var contributionConsentFingerprint = CreateContributionConsentFingerprint(sharedState?.Provenance);
        var pendingCandidates = new List<AiCandidateRecord>();
        var unreadCandidates = new List<AiCandidateRecord>();
        var sharedLookup = await GetReusableSharedLookupAsync(options, sharedState?.Provenance?.ConsentAtAnalysisStart,
            cancellationToken).ConfigureAwait(false);
        var knownSharedMisses = new HashSet<string>(StringComparer.Ordinal);
        var retainedCloudContributions = new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Shared answers describe this path's purpose regardless of the mode
            // selected for new AI requests. Keep their evidence as PathPurpose.
            var sharedCandidate = candidate with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
            var cloudResult = sharedLookup is not null
                && sharedLookup.ResultsByPath.TryGetValue(candidate.Path, out var cloud)
                && CanReuseSharedLookupResult(cloud, sharedCandidate, sharedLookup.Consent, DateTimeOffset.UtcNow)
                    ? ConstrainLimitedResult(cloud, sharedCandidate, compactPayload: false) : null;
            // Location protection constrains deletion; it must not substitute
            // a canned safety result for this item's actual purpose analysis.
            if (cloudResult is null && candidate.HasUnreadItem && (!candidate.IsDirectory
                || candidate.Structure.Children.Count == 0 && !candidate.IsStructureSummary)
                && (candidate.AnalysisEvidenceScope != AiAnalysisEvidenceScope.PathPurpose || !candidate.HasUsablePathMetadata))
            {
                resultsByPath[candidate.Path] = AiAnalysisResult.CreateFallback(
                    candidate.Path,
                    "本项未能读取，尚未完成用途分析。请处理读取问题后重新扫描。") with
                    { IsScopeLimited = true, ScopeWarning = AiScopeWarning.ItemReadIncomplete };
                unreadCandidates.Add(candidate);
            }
            else if (existingEntries.TryGetValue(candidate.Path, out var existingEntry)
                && CanReuseLocalEntry(existingEntry, candidate, options, providerOptions, promptFingerprint))
            {
                var reused = ConstrainLimitedResult(existingEntry.Result, candidate, compactPayload: false);
                resultsByPath[candidate.Path] = cloudResult ?? (reused with
                {
                    Origin = AiAnalysisResultOrigin.CacheReused,
                });
                var preserved = existingEntry with { Result = reused };
                var recoverable = reused with { OriginalAnalyzedAt = reused.OriginalAnalyzedAt ?? existingEntry.AnalyzedAt };
                if (contributionConsentFingerprint is not null && sharedState?.Provenance is { DefaultPrompt: true, SharedContext: false } provenance
                    && existingEntry.SharedContributionPending
                    && string.Equals(existingEntry.SharedContributionConsentFingerprint, contributionConsentFingerprint, StringComparison.Ordinal)
                    && IsShareableContributionResult(recoverable, candidate, provenance.ConsentAtAnalysisStart!.EnabledAt))
                {
                    contributionRetries[candidate.Path] = recoverable with { Origin = AiAnalysisResultOrigin.AiAnalyzed };
                }
                else if (contributionConsentFingerprint is not null && sharedState?.Provenance is { DefaultPrompt: true, SharedContext: false } currentProvenance
                    && !existingEntry.SharedContributionCompleted && existingEntry.SharedContributionConsentFingerprint is null
                    && IsShareableContributionResult(recoverable, candidate, currentProvenance.ConsentAtAnalysisStart!.EnabledAt))
                {
                    // Upgrade recovery: older releases persisted the AI result before
                    // recording contribution intent. Rebuild intent only when it was
                    // originally analyzed during this still-active consent period.
                    preserved = preserved with
                    {
                        SharedContributionPending = true,
                        SharedContributionConsentFingerprint = contributionConsentFingerprint,
                    };
                    contributionRetries[candidate.Path] = recoverable with { Origin = AiAnalysisResultOrigin.AiAnalyzed };
                }
                else if (existingEntry.SharedContributionPending
                    && !string.Equals(existingEntry.SharedContributionConsentFingerprint, contributionConsentFingerprint, StringComparison.Ordinal))
                {
                    // A changed consent/exclusion set invalidates the old intent.
                    preserved = preserved with { SharedContributionPending = false, SharedContributionCompleted = true };
                }
                preservedEntries[candidate.Path] = preserved;
                // A cloud answer owns display, while an unqueued original still
                // owns crash recovery for its explicitly consented contribution.
                if (cloudResult is not null && preserved.SharedContributionPending)
                    retainedCloudContributions[candidate.Path] = preserved;
            }
            else if (cloudResult is not null)
            {
                resultsByPath[candidate.Path] = cloudResult;
            }
            else
            {
                pendingCandidates.Add(candidate);
                if (sharedLookup is not null && !sharedCandidate.HasLimitedAnalysisEvidence
                    && sharedLookup.MissingPaths.Contains(candidate.Path)
                    && !sharedLookup.ResultsByPath.ContainsKey(candidate.Path)
                    && !SharedFingerprintExtractor.IsExcluded(candidate.Path, sharedLookup.Consent.ExcludedPaths))
                    knownSharedMisses.Add(candidate.Path);
            }
        }

        if (sharedState is not null) sharedState.MissingPaths = knownSharedMisses.ToArray();
        var unresolvedSharedCandidates = pendingCandidates.Concat(unreadCandidates)
            .Select(candidate => candidate with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose })
            .Where(candidate => !candidate.HasLimitedAnalysisEvidence
            && !knownSharedMisses.Contains(candidate.Path)).ToArray();
        if (_sharedKnowledge is not null && !options.BypassAnalysisMemory && unresolvedSharedCandidates.Length > 0)
        {
            try
            {
                // Each transport request has its own timeout. An overall deadline
                // discarded earlier successful chunks on larger scans.
                var resolution = await _sharedKnowledge.ResolveAsync(unresolvedSharedCandidates, cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (sharedState is not null)
                {
                    sharedState.SkippedItems = resolution.SkippedCount;
                    sharedState.MissingPaths = knownSharedMisses.Concat(resolution.MissingPaths)
                        .Distinct(StringComparer.Ordinal).ToArray();
                    sharedState.NetworkUnavailable |= resolution.NetworkUnavailable;
                }
                foreach (var candidate in unresolvedSharedCandidates)
                    if (!candidate.HasLimitedAnalysisEvidence && resolution.ResultsByPath.TryGetValue(candidate.Path, out var result)
                        && result.Origin == AiAnalysisResultOrigin.SharedExperience && !result.LooksLikeFallback()
                        && result.SharedEntry is { Purpose: not "unknown" })
                        resultsByPath[candidate.Path] = ConstrainLimitedResult(
                            result with { Path = candidate.Path }, candidate, compactPayload: false);
                pendingCandidates.RemoveAll(candidate => resultsByPath.ContainsKey(candidate.Path));
                if (resolution.Unavailable) sharedState?.AddWarning(resolution.NetworkUnavailable
                    ? NetworkFailureClassifier.OfflineMessage
                    : "云端经验库查询失败，暂时无法获取文件用途经验，请检查网络后重试。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var offline = NetworkFailureClassifier.IsOffline(ex, NetworkAvailability);
                if (offline && sharedState is not null) sharedState.NetworkUnavailable = true;
                sharedState?.AddWarning(offline ? NetworkFailureClassifier.OfflineMessage
                    : "云端经验库查询失败，暂时无法获取文件用途经验，请检查网络后重试。");
            }
        }
        if (confirmSubmission is not null)
        {
            var preview = new AiAnalysisSubmissionPreview(root.Path, pendingCandidates.ToArray(),
                resultsByPath.Values.Count(result => result.Origin == AiAnalysisResultOrigin.CacheReused),
                resultsByPath.Values.Count(result => result.Origin == AiAnalysisResultOrigin.SharedExperience),
                ProtectedCount: 0)
            {
                TotalCandidateCount = candidates.Count,
                UnreadableCount = unreadCandidates.Count(candidate => resultsByPath[candidate.Path].LooksLikeFallback()),
                ProviderConfigured = provider is not null,
            };
            if (!await confirmSubmission(preview, cancellationToken).ConfigureAwait(false))
                throw new OperationCanceledException("用户取消了本次 AI 提交。", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (provider is null)
        {
            foreach (var candidate in pendingCandidates)
                resultsByPath[candidate.Path] = AiAnalysisResult.CreateFallback(candidate.Path, "尚未配置可用的 AI 提供者，本项未完成分析。");
            pendingCandidates.Clear();
        }

        tracker.IncludeResults(resultsByPath.Values);
        tracker.Report(AiAnalysisStage.Analyzing,
            $"本次共 {candidates.Count} 个候选；{resultsByPath.Count} 项已有本地或共享结果，{pendingCandidates.Count} 项等待模型按本项路径与名称分析用途。");
        var completedEntries = new Dictionary<string, AiAnalysisMemoryEntry>(retainedCloudContributions, StringComparer.Ordinal);
        var ownedFlights = new Dictionary<string, (string Key, AnalysisFlight Flight)>(StringComparer.Ordinal);
        var followedFlights = new Dictionary<string, (AnalysisFlight Flight, AiCandidateRecord Candidate)>(StringComparer.Ordinal);
        var flightRegistry = !options.BypassAnalysisMemory && _memoryStore is not NoOpAiAnalysisMemoryStore
            ? Flights.GetValue(_memoryStore, _ => new ConcurrentDictionary<string, AnalysisFlight>(StringComparer.Ordinal)) : null;
        if (flightRegistry is not null)
        {
            foreach (var candidate in pendingCandidates)
            {
                if (candidate.HasLimitedAnalysisEvidence) continue;
                var key = CreateFlightKey(memoryRootPath, candidate, providerOptions, promptFingerprint, options, session?.Epoch ?? 0);
                var proposed = new AnalysisFlight();
                var flight = flightRegistry.GetOrAdd(key, proposed);
                if (ReferenceEquals(flight, proposed)) ownedFlights.Add(candidate.Path, (key, flight));
                else followedFlights.Add(candidate.Path, (flight, candidate));
            }
            pendingCandidates.RemoveAll(candidate => followedFlights.ContainsKey(candidate.Path));
        }

        using var resultGate = new SemaphoreSlim(1, 1);

        async Task CheckpointCoreAsync()
        {
            AiDeletionAdvicePolicy.ConstrainAncestors(resultsByPath);
            foreach (var candidate in candidates)
            {
                if (!resultsByPath.TryGetValue(candidate.Path, out var result) || result.LooksLikeFallback() || result.IsAnalysisEvidenceLimited
                    || result.Origin is not (AiAnalysisResultOrigin.AiAnalyzed or AiAnalysisResultOrigin.CacheReused)) continue;
                if (preservedEntries.TryGetValue(candidate.Path, out var preserved))
                {
                    completedEntries[candidate.Path] = preserved with { Result = result, SnapshotGeneration = session?.SnapshotGeneration ?? preserved.SnapshotGeneration };
                }
                else if (!completedEntries.ContainsKey(candidate.Path))
                {
                    var now = DateTimeOffset.UtcNow;
                    if (result.Origin == AiAnalysisResultOrigin.AiAnalyzed && string.IsNullOrWhiteSpace(result.OriginalAnalysisId))
                    {
                        result = result with { OriginalAnalysisId = Guid.NewGuid().ToString("N"), OriginalAnalyzedAt = now };
                        resultsByPath[candidate.Path] = result;
                    }
                    completedEntries[candidate.Path] = new AiAnalysisMemoryEntry(candidate.Path, candidate.IsDirectory,
                        candidate.Signature, result, result.OriginalAnalyzedAt ?? now, providerOptions.Kind, providerOptions.Endpoint,
                        providerOptions.Model, promptFingerprint, options.UseTokenSavingMode, options.AnalysisThresholdBytes,
                        Math.Max(256, options.MaxAiChunkTokens), providerOptions.ReasoningEffort, providerOptions.Temperature,
                        session?.SnapshotGeneration ?? options.AnalysisSnapshotGeneration)
                    {
                        SharedContributionPending = contributionConsentFingerprint is not null
                            && IsShareableContributionResult(result, candidate, sharedState!.Provenance!.ConsentAtAnalysisStart!.EnabledAt),
                        SharedContributionCompleted = contributionConsentFingerprint is not null
                            && !IsShareableContributionResult(result, candidate, sharedState!.Provenance!.ConsentAtAnalysisStart!.EnabledAt),
                        SharedContributionConsentFingerprint = contributionConsentFingerprint,
                    };
                }
                else
                {
                    var checkpoint = completedEntries[candidate.Path];
                    // Recovery can return the same successful item again without
                    // the identity assigned by its first checkpoint. Preserve that
                    // identity while persisting any newer ancestor safety veto.
                    result = result with { OriginalAnalysisId = checkpoint.Result.OriginalAnalysisId,
                        OriginalAnalyzedAt = checkpoint.Result.OriginalAnalyzedAt };
                    resultsByPath[candidate.Path] = result;
                    completedEntries[candidate.Path] = checkpoint with { Result = result };
                }
            }
            try
            {
                var checkpoint = completedEntries.Values.ToArray();
                var entriesToSave = string.Equals(memoryRootPath, root.Path, StringComparison.Ordinal) && !cancellationToken.IsCancellationRequested
                    ? checkpoint : MergeSuccessfulEntries(existingEntries, checkpoint);
                if (cacheAvailable) await SaveCompletedEntriesAsync(memoryRootPath, entriesToSave, existingEntries,
                    cancellationToken, session).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (sharedState is not null) sharedState.CacheWarning = "本次结果可查看，但本地缓存写入失败；重启后可能需要重新分析。";
            }
        }

        async Task CheckpointAsync()
        {
            await resultGate.WaitAsync().ConfigureAwait(false);
            try { await CheckpointCoreAsync().ConfigureAwait(false); }
            finally { resultGate.Release(); }
        }

        async Task CheckpointRecoveryAsync(IReadOnlyDictionary<string, AiAnalysisResult> recovered)
        {
            await resultGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var result in recovered)
                    if (!resultsByPath.TryGetValue(result.Key, out var existing) || existing.LooksLikeFallback())
                        resultsByPath[result.Key] = result.Value;
                await CheckpointCoreAsync().ConfigureAwait(false);
                tracker.ResultsReceived(resultsByPath.Values.ToArray());
            }
            finally { resultGate.Release(); }
        }

        async Task RecordFailureAsync(AiBatch batch, string reason)
        {
            await resultGate.WaitAsync().ConfigureAwait(false);
            try
            {
                BackfillBatchFailure(batch, reason, existingEntries, providerOptions, promptFingerprint,
                    options, resultsByPath, preservedEntries);
                await CheckpointCoreAsync().ConfigureAwait(false);
                tracker.ResultsReceived(resultsByPath.Values.ToArray());
            }
            finally { resultGate.Release(); }
        }

        var batches = _batchPlanner.CreateBatches(pendingCandidates, options.MaxAiChunkTokens,
            Math.Min(options.MaxAiBatchSize, 15), options.UseTokenSavingMode);
        // Every fresh batch can enqueue at most one recovery job. This finite
        // capacity keeps all failed batches without holding the fresh lane up.
        var retryQueue = Channel.CreateBounded<(IReadOnlyList<AiCandidateRecord> Records, string Reason)>(
            new BoundedChannelOptions(Math.Max(1, batches.Count))
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var retryCancellationObserved = 0;

        async Task QueueRecoveryAsync(IReadOnlyList<AiCandidateRecord> records, string reason)
        {
            tracker.QueueRetry(records.Count);
            try { await retryQueue.Writer.WriteAsync((records, reason)).ConfigureAwait(false); }
            catch { tracker.DequeueRetry(records.Count); throw; }
        }

        async Task RunRetryWorkerAsync()
        {
            tracker.IsRetry = true;
            Exception? failure = null;
            try
            {
                await foreach (var job in retryQueue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    tracker.DequeueRetry(job.Records.Count);
                    AiCandidateRecord[] unresolved;
                    await resultGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        unresolved = job.Records.Where(record => !resultsByPath.TryGetValue(record.Path, out var result)
                            || result.LooksLikeFallback()).ToArray();
                    }
                    finally { resultGate.Release(); }
                    if (unresolved.Length == 0) continue;
                    var recovery = await RecoverIncompleteBatchAsync(provider!, unresolved, options, providerOptions,
                        budget, tracker, cancellationToken, CheckpointRecoveryAsync).ConfigureAwait(false);
                    await resultGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        ApplyRecoveryOutcome(unresolved, recovery, job.Reason, existingEntries, providerOptions,
                            promptFingerprint, options, resultsByPath, preservedEntries);
                        await CheckpointCoreAsync().ConfigureAwait(false);
                        tracker.ResultsReceived(resultsByPath.Values.ToArray());
                    }
                    finally { resultGate.Release(); }
                    if (recovery.IsCancelled) Interlocked.Exchange(ref retryCancellationObserved, 1);
                }
            }
            catch (Exception exception) { failure = exception; throw; }
            finally { retryQueue.Writer.TryComplete(failure); }
        }

        var retryWorker = RunRetryWorkerAsync();

        try
        {
            var cancellationObserved = false;
            foreach (var batch in batches)
            {
                try
                {
                    if (budget.IsStopped)
                    {
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationObserved = true;
                        break;
                    }

                    IReadOnlyList<AiAnalysisResult>? response;
                    try
                    {
                        response = await AnalyzeInRequestLaneAsync(provider!, batch, providerOptions,
                            tracker, isRetry: false, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AiTokenBudgetExceededException)
                    {
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancellationObserved = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancellationObserved = true;
                            break;
                        }

                        if (IsPayloadTooLarge(ex) || ex is (JsonException or InvalidOperationException))
                        {
                            await QueueRecoveryAsync(batch.Records, MapAiFailureReason(ex)).ConfigureAwait(false);
                            continue;
                        }

                        await RecordFailureAsync(batch, MapAiFailureReason(ex)).ConfigureAwait(false);
                        continue;
                    }

                    var returnedResults = response ?? [];
                    var responsePathMapping = MapResponsePaths(returnedResults, batch.Records,
                        batch.UseCompactPayload && !batch.UseTokenSavingMode, batch.UseTokenSavingMode);
                    ReportPathMappingDiagnostics(budget, batch.Records.Count, responsePathMapping);

                    if (!responsePathMapping.IsValid)
                    {
                        var responseIssues = new List<string>();
                        if (responsePathMapping.DuplicatePaths.Count > 0)
                        {
                            responseIssues.Add($"重复路径：{string.Join("、", responsePathMapping.DuplicatePaths)}");
                        }

                        if (responsePathMapping.UnexpectedPaths.Count > 0)
                        {
                            responseIssues.Add($"未请求路径：{string.Join("、", responsePathMapping.UnexpectedPaths)}");
                        }

                        var reason = $"AI 返回内容无法与本批候选可靠对应（{string.Join("；", responseIssues)}）。已自动缩小请求重试，仍缺少的项目可再次分析。";
                        await QueueRecoveryAsync(batch.Records, reason).ConfigureAwait(false);
                        continue;
                    }

                    await resultGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        foreach (var result in responsePathMapping.ResultsByCanonicalPath)
                            resultsByPath[result.Key] = result.Value;
                        tracker.ResultsReceived(resultsByPath.Values.ToArray());
                        await CheckpointCoreAsync().ConfigureAwait(false);
                    }
                    finally { resultGate.Release(); }

                    var missingRecords = batch.Records
                        .Where(record => !responsePathMapping.ResultsByCanonicalPath.ContainsKey(record.Path))
                        .ToArray();
                    if (missingRecords.Length > 0)
                    {
                        await QueueRecoveryAsync(missingRecords,
                            "AI 返回内容缺少该项；已自动缩小请求重试，仍未返回的项目可再次分析。").ConfigureAwait(false);
                    }
                }
                finally { await CheckpointAsync().ConfigureAwait(false); }
            }

            retryQueue.Writer.TryComplete();
            await retryWorker.ConfigureAwait(false);
            cancellationObserved |= Volatile.Read(ref retryCancellationObserved) != 0;

            // Own later batches may reveal a retained descendant and veto an
            // earlier parent's removal advice. Publish only after all our own
            // batches finish, while still releasing owned flights before waiting
            // for followed flights so callers cannot wait on each other forever.
            foreach (var owned in ownedFlights)
                owned.Value.Flight.Completion.TrySetResult(completedEntries.TryGetValue(owned.Key, out var entry)
                    ? entry : null);
            var unfinishedFlightPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var followed in followedFlights)
            {
                try
                {
                    var shared = await followed.Value.Flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (shared is null) { unfinishedFlightPaths.Add(followed.Key); continue; }
                    var reused = ConstrainLimitedResult(shared.Result, followed.Value.Candidate, compactPayload: false);
                    resultsByPath[followed.Key] = reused with { Origin = AiAnalysisResultOrigin.CacheReused };
                    preservedEntries[followed.Key] = shared with { Result = reused };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { cancellationObserved = true; break; }
            }
            // Keep this caller's successful results, including results with scan
            // warnings. Only the failed owner's paths need another request.
            if (unfinishedFlightPaths.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var unfinishedRecords = candidates.Where(candidate => unfinishedFlightPaths.Contains(candidate.Path)).ToArray();
                var recovery = await RecoverIncompleteBatchAsync(provider!, unfinishedRecords, options, providerOptions,
                    budget, tracker, cancellationToken, CheckpointRecoveryAsync).ConfigureAwait(false);
                ApplyRecoveryOutcome(unfinishedRecords, recovery, "并发分析未完成；已使用本次预算重试该项。",
                    existingEntries, providerOptions, promptFingerprint, options, resultsByPath, preservedEntries);
                tracker.ResultsReceived(resultsByPath.Values);
                cancellationObserved |= recovery.IsCancelled;
            }

            cancellationObserved |= cancellationToken.IsCancellationRequested;
            foreach (var candidate in candidates.Where(candidate => !resultsByPath.ContainsKey(candidate.Path)))
            {
                resultsByPath[candidate.Path] = AiAnalysisResult.CreateFallback(
                    candidate.Path,
                    cancellationObserved
                        ? "AI 分析已由用户取消；本项尚未完成分析，可再次运行分析重试。"
                        : budget.IsStopped
                        ? AiTokenBudgetSession.StoppedReason
                        : "AI 分析未能为该候选生成结果，本项尚未分析，可重试。");
            }

            await CheckpointAsync().ConfigureAwait(false);
            var orderedResults = candidates.Where(candidate => resultsByPath.ContainsKey(candidate.Path))
                .Select(candidate => resultsByPath[candidate.Path]).ToArray();
            if (_sharedKnowledge is not null && sharedState is { NetworkUnavailable: false, Provenance: { } provenance }
                && !cancellationToken.IsCancellationRequested)
            {
                var freshResults = orderedResults.Where(result => result.Origin == AiAnalysisResultOrigin.AiAnalyzed
                    && !result.LooksLikeFallback() && !result.IsAnalysisEvidenceLimited && !string.IsNullOrWhiteSpace(result.OriginalAnalysisId)
                    && result.OriginalAnalyzedAt is not null).ToArray();
                var retryResults = contributionRetries.Values.ToArray();
                var contributionResults = freshResults.Concat(retryResults)
                    .DistinctBy(result => result.OriginalAnalysisId, StringComparer.Ordinal).ToArray();
                if (contributionResults.Length > 0)
                {
                    try
                    {
                        // Queue the complete normalized batch before sending; a five
                        // second overall deadline could prevent later items being saved.
                        var paths = contributionResults.Select(result => result.Path).ToHashSet(StringComparer.Ordinal);
                        var contributionCandidates = candidates.Where(candidate => paths.Contains(candidate.Path)).ToArray();
                        tracker.Report(AiAnalysisStage.Uploading, "本轮已完成的分析已保存，正在上传云端经验。");
                        var receipt = await _sharedKnowledge.ContributeAsync(contributionCandidates, contributionResults, provenance, false, cancellationToken)
                            .WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (receipt.NetworkUnavailable)
                        {
                            sharedState.NetworkUnavailable = true;
                            sharedState.AddWarning(NetworkFailureClassifier.OfflineMessage);
                        }
                        else if (receipt.Pending > 0)
                            sharedState.AddWarning($"上传至云端经验库尚未完成：{receipt.Pending} 项已保存到本机待上传队列，将在网络恢复后自动重试。");
                        else if (receipt.Failed > 0)
                            sharedState.AddWarning($"上传至云端经验库失败：{receipt.Failed} 项未取得成功回执。已保留本地待贡献标记，下次分析时会自动重试。");
                        // A non-empty Pending count proves the normalized payload is
                        // already durable in the outbox; it can now own retries across
                        // app restarts. Without an outbox, only a successful receipt
                        // completes the local contribution intent.
                        if (receipt.Failed == 0 || receipt.Pending > 0)
                        {
                            var latestConsent = await _sharedKnowledge.CaptureConsentAsync(cancellationToken).ConfigureAwait(false);
                            var latestFingerprint = CreateContributionConsentFingerprint(provenance with { ConsentAtAnalysisStart = latestConsent });
                            foreach (var result in contributionResults)
                                if (completedEntries.TryGetValue(result.Path, out var entry))
                                    completedEntries[result.Path] = entry with
                                    {
                                        SharedContributionPending = false,
                                        SharedContributionCompleted = true,
                                        SharedContributionConsentFingerprint = latestFingerprint ?? contributionConsentFingerprint,
                                    };
                            await PersistContributionStateAsync(memoryRootPath, root, completedEntries, existingEntries,
                                cacheAvailable, cancellationToken, session, sharedState).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        if (NetworkFailureClassifier.IsOffline(ex, NetworkAvailability))
                        {
                            sharedState.NetworkUnavailable = true;
                            sharedState.AddWarning(NetworkFailureClassifier.OfflineMessage);
                        }
                        else sharedState.AddWarning("上传至云端经验库失败，部分内容未取得成功回执。已保留本地待贡献标记，下次分析时会自动重试。");
                    }
                }
            }
            tracker.IncludeResults(orderedResults);
            tracker.Report(AiAnalysisStage.Saving, sharedState?.CacheWarning ?? "已保存完成的分析结果与本地缓存。");
            if (cancellationToken.IsCancellationRequested)
                throw new AiAnalysisCanceledException(orderedResults, cancellationToken);
            return orderedResults;
        }
        finally
        {
            retryQueue.Writer.TryComplete();
            try { await retryWorker.ConfigureAwait(false); }
            finally
            {
                if (flightRegistry is not null)
                {
                    foreach (var owned in ownedFlights.Values)
                    {
                        owned.Flight.Completion.TrySetResult(null);
                        flightRegistry.TryRemove(owned.Key, out _);
                    }
                }
            }
        }
    }

    private static string CreateFlightKey(string root, AiCandidateRecord candidate, AiProviderOptions provider,
        string fingerprint, ScanSessionOptions options, long epoch)
    {
        // Keep credentials isolated without storing their plaintext in a registry key.
        var credentials = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.ApiKey ?? "")));
        return JsonSerializer.Serialize(new { candidate.Path, candidate.Signature, provider.Kind,
            Endpoint = provider.Endpoint?.Trim().TrimEnd('/'), provider.Model, provider.ReasoningEffort,
            provider.Temperature, credentials, fingerprint, options.UseTokenSavingMode,
            options.AnalysisThresholdBytes, options.MaxAiChunkTokens, epoch });
    }

    private sealed class AnalysisFlight
    {
        public TaskCompletionSource<AiAnalysisMemoryEntry?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record SnapshotStamp(long Value);

    private sealed class SharedInvocationState(SharedAnalysisProvenance? provenance)
    {
        public bool NetworkUnavailable { get; set; }
        public SharedAnalysisProvenance? Provenance { get; } = provenance;
        public string? Warning { get; set; }
        public string? CacheWarning { get; set; }
        public int SkippedItems { get; set; }
        public IReadOnlyList<string> MissingPaths { get; set; } = [];
        public void AddWarning(string message) => Warning = string.IsNullOrEmpty(Warning) ? message : Warning + "\n" + message;
    }
    private static void ReportPathMappingDiagnostics(AiTokenBudgetSession budget, int requestedCount,
        AiResponsePathMapping mapping)
    {
        var issues = new List<AiResponseIssueCount>();
        var missing = Math.Max(0, requestedCount - mapping.ResultsByCanonicalPath.Count);
        if (missing > 0) issues.Add(new(AiResponseIssue.MissingResult, missing));
        if (mapping.UnexpectedPaths.Count > 0) issues.Add(new(AiResponseIssue.UnexpectedPath, mapping.UnexpectedPaths.Count));
        if (mapping.DuplicatePaths.Count > 0) issues.Add(new(AiResponseIssue.DuplicatePath, mapping.DuplicatePaths.Count));
        if (issues.Count > 0) budget.ReportResponseDiagnostics(new(0, 0, 0, issues));
    }

    private static AiResponsePathMapping MapResponsePaths(
        IReadOnlyList<AiAnalysisResult> returnedResults,
        IReadOnlyList<AiCandidateRecord> requestedRecords,
        bool compactPayload = false,
        bool useTokenSavingMode = false)
    {
        var recordsByExactPath = requestedRecords
            .GroupBy(record => record.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var recordsByIgnoreCasePath = requestedRecords
            .GroupBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var resultsByCanonicalPath = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        var unexpectedPaths = new List<string>();
        var duplicatePaths = new List<string>();

        foreach (var result in returnedResults)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
            {
                unexpectedPaths.Add(result?.Path ?? "<空路径>");
                continue;
            }

            AiCandidateRecord? requestedRecord = null;
            if (recordsByExactPath.TryGetValue(result.Path, out var exactMatches) && exactMatches.Length == 1)
            {
                requestedRecord = exactMatches[0];
            }
            else if (exactMatches is null
                     && recordsByIgnoreCasePath.TryGetValue(result.Path, out var ignoreCaseMatches)
                     && ignoreCaseMatches.Length == 1)
            {
                // Providers sometimes normalize path casing. Accept that only when
                // this request batch contains exactly one case-insensitive match.
                requestedRecord = ignoreCaseMatches[0];
            }

            if (requestedRecord is null)
            {
                unexpectedPaths.Add(result.Path);
                continue;
            }

            // Custom providers can bypass the JSON parser. An empty explanation is a
            // missing purpose result and must use the same per-item recovery path.
            if (string.IsNullOrWhiteSpace(result.Reason)) continue;

            if (!resultsByCanonicalPath.TryAdd(
                    requestedRecord.Path,
                    ConstrainLimitedResult(result with
                    {
                        Path = requestedRecord.Path,
                        // The request determines evidence scope. A provider cannot
                        // promote a structure response to a path-only result.
                        AnalysisEvidenceScope = useTokenSavingMode
                            ? AiAnalysisEvidenceScope.PathPurpose : AiAnalysisEvidenceScope.DirectoryStructure,
                    }, requestedRecord, compactPayload)))
            {
                duplicatePaths.Add(requestedRecord.Path);
            }
        }

        return new AiResponsePathMapping(
            resultsByCanonicalPath,
            unexpectedPaths.Distinct(StringComparer.Ordinal).ToArray(),
            duplicatePaths.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static AiAnalysisResult ConstrainLimitedResult(AiAnalysisResult result, AiCandidateRecord record, bool compactPayload)
    {
        result = AiDeletionAdvicePolicy.Constrain(result, record.IsProtectedLocation);
        if ((!record.HasLimitedScope && (!compactPayload || !record.IsDirectory)) || result.LooksLikeFallback()) return result;
        // A path-only purpose request does not use child coverage as evidence.
        // Keep diagnostics internal instead of turning regenerable caches into
        // permanent "keep" recommendations merely because a child was unreadable.
        if (record.AnalysisEvidenceScope == AiAnalysisEvidenceScope.PathPurpose && record.HasUsablePathMetadata)
            return result with
            {
                IsScopeLimited = true,
                ScopeWarning = record.HasIncompleteScan
                    ? record.HasUnreadItem ? AiScopeWarning.ItemReadIncomplete : AiScopeWarning.DescendantReadIncomplete
                    : AiScopeWarning.None,
            };
        var protectedAdvice = result.RiskLevel == RiskLevel.Protected || result.SuggestedAction == SuggestedAction.DoNotDelete;
        var keepAdvice = protectedAdvice || result.SuggestedAction == SuggestedAction.Keep;
        // Scan completeness constrains whole-directory removal. It does not
        // invalidate a successful result whose request used only this item's path.
        var warning = record.HasIncompleteScan
            ? record.HasUnreadItem ? AiScopeWarning.ItemReadIncomplete : AiScopeWarning.DescendantReadIncomplete
            : AiScopeWarning.None;
        if (record.IsStructureSummary || compactPayload) warning |= AiScopeWarning.StructureSummary;
        var constrained = result with
        {
            RiskLevel = protectedAdvice ? result.RiskLevel == RiskLevel.Protected ? RiskLevel.Protected : RiskLevel.High
                : keepAdvice ? RiskLevel.High : RiskLevel.Review,
            SuggestedAction = protectedAdvice ? SuggestedAction.DoNotDelete : keepAdvice ? SuggestedAction.Keep : SuggestedAction.Review,
            IsScopeLimited = true, ScopeWarning = warning,
        };
        return constrained.IsAnalysisEvidenceLimited
            ? constrained with { OriginalAnalysisId = null, OriginalAnalyzedAt = null } : constrained;
    }

    private static async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeInRequestLaneAsync(
        IAiProvider provider, AiBatch batch, AiProviderOptions options, AiAnalysisProgressTracker tracker,
        bool isRetry, CancellationToken cancellationToken)
    {
        var lane = isRetry ? RetryRequestLane : FreshRequestLane;
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            tracker.IsRetry = isRetry;
            return await provider.AnalyzeAsync(batch, options, cancellationToken).ConfigureAwait(false);
        }
        finally { lane.Release(); }
    }

    private async Task<AiRecoveryOutcome> RecoverIncompleteBatchAsync(
        IAiProvider provider,
        IReadOnlyList<AiCandidateRecord> records,
        ScanSessionOptions options,
        AiProviderOptions providerOptions,
        AiTokenBudgetSession budget,
        AiAnalysisProgressTracker tracker,
        CancellationToken cancellationToken,
        Func<IReadOnlyDictionary<string, AiAnalysisResult>, Task>? checkpoint = null)
    {
        tracker.IsRetry = true;
        var recoveredResults = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        var unresolvedReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingBatches = new Queue<(IReadOnlyList<AiCandidateRecord> Records, int RecoveryDepth, int PayloadSplitDepth, bool UseCompactPayload)>();
        pendingBatches.Enqueue((records, RecoveryDepth: 1, PayloadSplitDepth: 0, UseCompactPayload: false));

        while (pendingBatches.TryDequeue(out var pending))
        {
            var batchRecords = pending.Records;
            if (batchRecords.Count == 0)
            {
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                AddUnresolved(batchRecords, UserCanceledReason, unresolvedReasons);
                while (pendingBatches.TryDequeue(out var canceledPending))
                {
                    AddUnresolved(canceledPending.Records, UserCanceledReason, unresolvedReasons);
                }

                return new AiRecoveryOutcome(recoveredResults, unresolvedReasons, IsCancelled: true);
            }

            if (pending.RecoveryDepth > MaxResponseRecoveryDepth)
            {
                AddUnresolved(batchRecords, "AI 多次缩小请求后仍未返回完整结果。可再次运行分析重试。", unresolvedReasons);
                continue;
            }

            if (batchRecords.Count == 1 && pending.RecoveryDepth > 1)
            {
                AddUnresolved(batchRecords, "单项重试后仍未返回结果。可再次运行分析重试。", unresolvedReasons);
                continue;
            }

            var smallerBatchSize = Math.Max(1, (batchRecords.Count + 1) / 2);
            var tokenDivisor = 1 << Math.Min(pending.RecoveryDepth, MaxResponseRecoveryDepth);
            var smallerTokenLimit = Math.Max(256, Math.Max(1, options.MaxAiChunkTokens) / tokenDivisor);
            var retryBatches = _batchPlanner.CreateBatches(
                batchRecords,
                smallerTokenLimit,
                smallerBatchSize,
                options.UseTokenSavingMode)
                .Select(batch => batch with { UseCompactPayload = pending.UseCompactPayload })
                .ToArray();

            for (var retryIndex = 0; retryIndex < retryBatches.Length; retryIndex++)
            {
                var retryBatch = retryBatches[retryIndex];
                if (budget.IsStopped)
                {
                    AddUnresolved(retryBatches.Skip(retryIndex).SelectMany(batch => batch.Records), AiTokenBudgetSession.StoppedReason, unresolvedReasons);
                    while (pendingBatches.TryDequeue(out var budgetPending))
                    {
                        AddUnresolved(budgetPending.Records, AiTokenBudgetSession.StoppedReason, unresolvedReasons);
                    }

                    return new AiRecoveryOutcome(recoveredResults, unresolvedReasons);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    AddUnresolved(retryBatches.Skip(retryIndex).SelectMany(batch => batch.Records), UserCanceledReason, unresolvedReasons);
                    while (pendingBatches.TryDequeue(out var canceledPending))
                    {
                        AddUnresolved(canceledPending.Records, UserCanceledReason, unresolvedReasons);
                    }

                    return new AiRecoveryOutcome(recoveredResults, unresolvedReasons, IsCancelled: true);
                }

                IReadOnlyList<AiAnalysisResult>? response;
                try
                {
                    response = await AnalyzeInRequestLaneAsync(provider, retryBatch, providerOptions,
                        tracker, isRetry: true, cancellationToken).ConfigureAwait(false);
                }
                catch (AiTokenBudgetExceededException)
                {
                    AddUnresolved(retryBatches.Skip(retryIndex).SelectMany(batch => batch.Records), AiTokenBudgetSession.StoppedReason, unresolvedReasons);
                    while (pendingBatches.TryDequeue(out var budgetPending))
                    {
                        AddUnresolved(budgetPending.Records, AiTokenBudgetSession.StoppedReason, unresolvedReasons);
                    }

                    return new AiRecoveryOutcome(recoveredResults, unresolvedReasons);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    AddUnresolved(retryBatches.Skip(retryIndex).SelectMany(batch => batch.Records), UserCanceledReason, unresolvedReasons);
                    while (pendingBatches.TryDequeue(out var canceledPending))
                    {
                        AddUnresolved(canceledPending.Records, UserCanceledReason, unresolvedReasons);
                    }

                    return new AiRecoveryOutcome(recoveredResults, unresolvedReasons, IsCancelled: true);
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        AddUnresolved(retryBatches.Skip(retryIndex).SelectMany(batch => batch.Records), UserCanceledReason, unresolvedReasons);
                        while (pendingBatches.TryDequeue(out var canceledPending))
                        {
                            AddUnresolved(canceledPending.Records, UserCanceledReason, unresolvedReasons);
                        }

                        return new AiRecoveryOutcome(recoveredResults, unresolvedReasons, IsCancelled: true);
                    }

                    if (IsPayloadTooLarge(ex))
                    {
                        if (retryBatch.Records.Count > 1 && pending.PayloadSplitDepth < 8)
                        {
                            var midpoint = retryBatch.Records.Count / 2;
                            pendingBatches.Enqueue((retryBatch.Records.Take(midpoint).ToArray(), pending.RecoveryDepth, pending.PayloadSplitDepth + 1, pending.UseCompactPayload));
                            pendingBatches.Enqueue((retryBatch.Records.Skip(midpoint).ToArray(), pending.RecoveryDepth, pending.PayloadSplitDepth + 1, pending.UseCompactPayload));
                        }
                        else if (retryBatch.Records.Count > 1)
                        {
                            AddUnresolved(
                                retryBatch.Records,
                                $"{GetPayloadTooLargeReason(ex)} 拆小后仍未通过服务端限制，可更换模型/服务端后重试。",
                                unresolvedReasons);
                        }
                        else if (!pending.UseCompactPayload && !options.UseTokenSavingMode)
                        {
                            pendingBatches.Enqueue((retryBatch.Records, pending.RecoveryDepth, pending.PayloadSplitDepth, UseCompactPayload: true));
                        }
                        else
                        {
                            AddUnresolved(
                                retryBatch.Records,
                                $"{GetPayloadTooLargeReason(ex)} 单个候选已使用精简资料重试仍超限，请更换模型/服务端后重试。",
                                unresolvedReasons);
                        }

                        continue;
                    }

                    if (ex is JsonException or InvalidOperationException)
                    {
                        if (pending.RecoveryDepth < MaxResponseRecoveryDepth)
                        {
                            pendingBatches.Enqueue((retryBatch.Records, pending.RecoveryDepth + 1, pending.PayloadSplitDepth, pending.UseCompactPayload));
                        }
                        else
                        {
                            AddUnresolved(retryBatch.Records, MapAiFailureReason(ex), unresolvedReasons);
                        }

                        continue;
                    }

                    AddUnresolved(retryBatch.Records, MapAiFailureReason(ex), unresolvedReasons);
                    continue;
                }

                var responsePathMapping = MapResponsePaths(response ?? [], retryBatch.Records,
                    retryBatch.UseCompactPayload && !retryBatch.UseTokenSavingMode, retryBatch.UseTokenSavingMode);
                ReportPathMappingDiagnostics(budget, retryBatch.Records.Count, responsePathMapping);
                if (!responsePathMapping.IsValid)
                {
                    const string reason = "AI 返回的路径仍无法与请求候选一一对应。";
                    if (pending.RecoveryDepth < MaxResponseRecoveryDepth)
                    {
                        pendingBatches.Enqueue((retryBatch.Records, pending.RecoveryDepth + 1, pending.PayloadSplitDepth, pending.UseCompactPayload));
                    }
                    else
                    {
                        AddUnresolved(retryBatch.Records, reason, unresolvedReasons);
                    }

                    continue;
                }

                foreach (var result in responsePathMapping.ResultsByCanonicalPath)
                {
                    recoveredResults[result.Key] = result.Value;
                    unresolvedReasons.Remove(result.Key);
                }
                if (checkpoint is not null && responsePathMapping.ResultsByCanonicalPath.Count > 0)
                    await checkpoint(recoveredResults).ConfigureAwait(false);
                tracker.RecoveryResultsReceived(recoveredResults.Values);

                var missingRecords = retryBatch.Records
                    .Where(record => !responsePathMapping.ResultsByCanonicalPath.ContainsKey(record.Path))
                    .ToArray();
                if (missingRecords.Length == 0)
                {
                    continue;
                }

                if (pending.RecoveryDepth < MaxResponseRecoveryDepth)
                {
                    pendingBatches.Enqueue((missingRecords, pending.RecoveryDepth + 1, pending.PayloadSplitDepth, pending.UseCompactPayload));
                }
                else
                {
                    AddUnresolved(missingRecords, "AI 缩小请求重试后仍未返回该项。", unresolvedReasons);
                }
            }
        }

        return new AiRecoveryOutcome(recoveredResults, unresolvedReasons);
    }

    private async Task SaveCompletedEntriesAsync(
        string rootPath,
        IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> existingEntries,
        CancellationToken cancellationToken,
        AiAnalysisMemorySession? session = null)
    {
        Task Save(CancellationToken token) => session is not null && _memoryStore is IVersionedAiAnalysisMemoryStore versioned
            ? versioned.SaveSessionEntriesAsync(rootPath, entries, session, token)
            : _memoryStore.SaveEntriesAsync(rootPath, entries, token);
        if (cancellationToken.IsCancellationRequested)
        {
            await Save(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        try { await Save(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await Save(CancellationToken.None).ConfigureAwait(false); }
    }

    private async Task PersistContributionStateAsync(string memoryRootPath, FileSystemNode root,
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> completedEntries,
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> existingEntries, bool cacheAvailable,
        CancellationToken cancellationToken, AiAnalysisMemorySession? session, SharedInvocationState sharedState)
    {
        if (!cacheAvailable || completedEntries.Count == 0) return;
        try
        {
            var checkpoint = completedEntries.Values.ToArray();
            var entriesToSave = string.Equals(memoryRootPath, root.Path, StringComparison.Ordinal)
                ? checkpoint : MergeSuccessfulEntries(existingEntries, checkpoint);
            await SaveCompletedEntriesAsync(memoryRootPath, entriesToSave, existingEntries,
                cancellationToken, session).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The contribution may already be on the server. Keeping the local retry
            // marker is safe because server writes are idempotent by installation/bucket.
            sharedState.CacheWarning = "云端贡献状态已处理，但本地缓存状态未能更新；下次分析可能会重复确认该项。";
        }
    }

    private static bool IsShareableContributionResult(AiAnalysisResult result, AiCandidateRecord candidate,
        DateTimeOffset enabledAt)
    {
        var analyzedAt = result.OriginalAnalyzedAt;
        return result.Origin is AiAnalysisResultOrigin.AiAnalyzed or AiAnalysisResultOrigin.CacheReused
            && !result.LooksLikeFallback() && !result.IsAnalysisEvidenceLimited && !candidate.HasLimitedAnalysisEvidence
            && result.AnalysisEvidenceScope == candidate.AnalysisEvidenceScope
            && result.EffectiveConfidenceLevel is AiConfidenceLevel.Medium or AiConfidenceLevel.High
            && !string.IsNullOrWhiteSpace(result.OriginalAnalysisId)
            && analyzedAt is { } at && at >= enabledAt && at <= DateTimeOffset.UtcNow
            && at > DateTimeOffset.UnixEpoch;
    }

    private static string? CreateContributionConsentFingerprint(SharedAnalysisProvenance? provenance)
    {
        var consent = provenance?.ConsentAtAnalysisStart;
        if (consent is not { CanShare: true, Installation: { } installation }) return null;
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

    private static IReadOnlyCollection<AiAnalysisMemoryEntry> MergeSuccessfulEntries(
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> existingEntries,
        IReadOnlyCollection<AiAnalysisMemoryEntry> updatedEntries)
    {
        return existingEntries.Values
            .Where(entry => entry is not null && entry.IsUsableAt(DateTimeOffset.UtcNow))
            .Concat(updatedEntries)
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
    }

    private static void ApplyRecoveryOutcome(
        IReadOnlyList<AiCandidateRecord> records,
        AiRecoveryOutcome recovery,
        string fallbackReason,
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> existingEntries,
        AiProviderOptions providerOptions,
        string promptFingerprint,
        ScanSessionOptions options,
        IDictionary<string, AiAnalysisResult> resultsByPath,
        IDictionary<string, AiAnalysisMemoryEntry> preservedEntries)
    {
        foreach (var record in records)
        {
            if (recovery.ResultsByPath.TryGetValue(record.Path, out var recoveredResult))
            {
                resultsByPath[record.Path] = recoveredResult;
                continue;
            }

            var reason = recovery.UnresolvedReasons.TryGetValue(record.Path, out var unresolvedReason)
                ? $"{fallbackReason} {unresolvedReason}"
                : fallbackReason;
            BackfillBatchFailure(
                new AiBatch([record], 0, options.UseTokenSavingMode),
                reason,
                existingEntries,
                providerOptions,
                promptFingerprint,
                options,
                resultsByPath,
                preservedEntries);
        }
    }

    private static void AddUnresolved(
        IEnumerable<AiCandidateRecord> records,
        string reason,
        IDictionary<string, string> unresolvedReasons)
    {
        foreach (var record in records)
        {
            unresolvedReasons[record.Path] = reason;
        }
    }

    private const string UserCanceledReason = "AI 分析已由用户取消；本项尚未完成分析，可再次运行分析重试。";

    private static void BackfillBatchFailure(
        AiBatch batch,
        string reason,
        IReadOnlyDictionary<string, AiAnalysisMemoryEntry> existingEntries,
        AiProviderOptions providerOptions,
        string promptFingerprint,
        ScanSessionOptions options,
        IDictionary<string, AiAnalysisResult> resultsByPath,
        IDictionary<string, AiAnalysisMemoryEntry> preservedEntries)
    {
        foreach (var record in batch.Records)
        {
            if (existingEntries.TryGetValue(record.Path, out var existingEntry)
                && CanReuseLocalEntry(existingEntry, record, options, providerOptions, promptFingerprint))
            {
                resultsByPath[record.Path] = AiAnalysisResult.CreateStaleReuse(record.Path, existingEntry.Result, reason);
                preservedEntries[record.Path] = existingEntry;
            }
            else
            {
                resultsByPath[record.Path] = AiAnalysisResult.CreateFallback(record.Path, reason);
            }
        }
    }

    private sealed class NetworkAwareAiProvider(IAiProvider inner, INetworkAvailability availability, SharedInvocationState? state) : IAiProvider
    {
        private volatile bool _offline;
        public AiProviderKind Kind => inner.Kind;

        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var needsNetwork = NetworkFailureClassifier.RequiresNetwork(options.Endpoint);
            if (needsNetwork && (_offline || state?.NetworkUnavailable == true
                || NetworkFailureClassifier.IsNetworkUnavailable(options.Endpoint, availability)))
            {
                _offline = true;
                if (state is not null) state.NetworkUnavailable = true;
                throw new NetworkUnavailableException();
            }
            try { return await inner.AnalyzeAsync(batch, options, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (needsNetwork && !cancellationToken.IsCancellationRequested
                && NetworkFailureClassifier.IsOffline(ex, availability, options.Endpoint))
            {
                _offline = true;
                if (state is not null) state.NetworkUnavailable = true;
                throw new NetworkUnavailableException(ex);
            }
        }
    }

    private static string MapAiFailureReason(Exception exception)
    {
        if (exception is NetworkUnavailableException) return NetworkFailureClassifier.OfflineMessage;
        if (exception is AiPayloadTooLargeException)
        {
            return GetPayloadTooLargeReason(exception);
        }

        if (exception is HttpRequestException httpException && httpException.StatusCode is HttpStatusCode statusCode)
        {
            return (int)statusCode switch
            {
                413 => "AI 服务拒绝了过大的请求体（HTTP 413）；系统会拆分批次重试，单项仍超限时可更换模型/服务端后重试。",
                400 => "AI 请求格式无效，请检查模型兼容性或接口配置。",
                401 or 403 => "AI 认证失败，请检查 API Key 或权限配置。",
                404 => "AI 接口或模型不存在，请检查 Endpoint 和 Model。",
                408 => "AI 请求超时，请稍后重试。",
                429 => "AI 请求过于频繁或额度受限，请稍后重试。",
                >= 500 => "AI 服务暂时不可用，请稍后再试。",
                _ => $"AI 服务请求失败（HTTP {(int)statusCode}）。",
            };
        }

        if (exception is JsonException)
        {
            return "AI 返回格式无法解析，请检查固定 JSON 输出格式。";
        }

        if (exception is TaskCanceledException or TimeoutException)
        {
            return "AI 请求超时，说明这批分析内容过大或服务响应过慢，请稍后重试。";
        }

        if (exception is InvalidOperationException)
        {
            return "AI 返回内容为空或格式不完整。";
        }

        return "AI 分析失败，请检查网络连接或服务是否可用。";
    }

    private static bool IsPayloadTooLarge(Exception exception)
    {
        return exception is AiPayloadTooLargeException
               || exception is HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge };
    }

    private static string GetPayloadTooLargeReason(Exception exception)
    {
        return exception is HttpRequestException { StatusCode: HttpStatusCode.BadRequest }
            ? "AI 服务返回 HTTP 400，错误正文表明模型上下文超限；系统会缩小批次重试。"
            : exception is HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge }
                ? "AI 服务返回 HTTP 413，请求体超过服务端限制；系统会缩小批次重试。"
                : "AI 服务报告单批输入超过请求体或模型上下文上限；系统会缩小批次重试。";
    }

    private sealed record AiRecoveryOutcome(
        IReadOnlyDictionary<string, AiAnalysisResult> ResultsByPath,
        IReadOnlyDictionary<string, string> UnresolvedReasons,
        bool IsCancelled = false);

    private sealed record AiResponsePathMapping(
        IReadOnlyDictionary<string, AiAnalysisResult> ResultsByCanonicalPath,
        IReadOnlyList<string> UnexpectedPaths,
        IReadOnlyList<string> DuplicatePaths)
    {
        public bool IsValid => UnexpectedPaths.Count == 0 && DuplicatePaths.Count == 0;
    }
}

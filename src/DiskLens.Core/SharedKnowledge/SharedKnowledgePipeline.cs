using System.Text.RegularExpressions;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Ai;
using DiskLens.Core.Networking;

namespace DiskLens.Core.SharedKnowledge;

public sealed class SharedKnowledgePipeline(ISharedConsentSource consentSource, ISharedFingerprintExtractor extractor,
    ISharedExperienceClient client, TimeProvider? timeProvider = null, ISharedContributionOutbox? outbox = null,
    INetworkAvailability? networkAvailability = null, bool requiresNetwork = true) : ISharedKnowledgePipeline
{
    private const int MaximumConcurrentQueries = 4;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly INetworkAvailability _networkAvailability = networkAvailability ?? SystemNetworkAvailability.Instance;
    private static readonly WindowsLocationPolicy LocalProtection = new();

    public async Task<SharedConsentSnapshot?> CaptureConsentAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot with { ExcludedPaths = snapshot.ExcludedPaths.ToArray() };
    }

    public async Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates,
        CancellationToken cancellationToken = default)
    {
        var initial = (await CaptureConsentAsync(cancellationToken).ConfigureAwait(false))!;
        if (!initial.CanShare) return SharedResolutionBatch.Empty;
        if (requiresNetwork && !_networkAvailability.IsNetworkAvailable)
            return SharedResolutionBatch.Empty with { Unavailable = true, NetworkUnavailable = true };
        SharedPathIndex? sharedPathIndex;
        try { sharedPathIndex = await client.GetSharedPathIndexAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            or System.Text.Json.JsonException or OperationCanceledException)
        {
            return SharedResolutionBatch.Empty with
            {
                Unavailable = true,
                NetworkUnavailable = requiresNetwork && NetworkFailureClassifier.IsOffline(ex, _networkAvailability),
            };
        }
        return await ResolveAsync(candidates, cancellationToken, sharedPathIndex).ConfigureAwait(false);
    }

    public async Task<SharedResolutionBatch> ResolveAsync(IReadOnlyList<AiCandidateRecord> candidates,
        CancellationToken cancellationToken, SharedPathIndex? sharedPathIndex = null,
        IProgress<SharedResolutionProgress>? progress = null)
    {
        var initial = (await CaptureConsentAsync(cancellationToken).ConfigureAwait(false))!;
        if (!initial.CanShare) return SharedResolutionBatch.Empty;
        if (requiresNetwork && !_networkAvailability.IsNetworkAvailable)
        {
            ReportLookupProgress(progress, new(0, candidates.Count, 0, candidates.Count));
            return SharedResolutionBatch.Empty with
            { Unavailable = true, NetworkUnavailable = true, FailedCount = candidates.Count };
        }
        // Every resolution path is read-only, including analysis preparation.
        // Capture existing private identities once instead of allocating new ones
        // or reopening the encrypted identity store for each candidate.
        var lookupExtractor = extractor is SharedFingerprintExtractor concrete
            ? await concrete.CreateLookupExtractorAsync(cancellationToken).ConfigureAwait(false) : extractor;
        cancellationToken.ThrowIfCancellationRequested();
        if (!SameConsent(initial, await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)))
            return SharedResolutionBatch.Empty;
        var extracted = new List<(AiCandidateRecord Candidate, SharedFingerprint Fingerprint)>();
        var missingPaths = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var preparationProgress = System.Diagnostics.Stopwatch.StartNew();
        ReportLookupProgress(progress, new(0, candidates.Count, 0, 0));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preparationProgress.ElapsedMilliseconds >= 100)
            {
                ReportLookupProgress(progress, new(processed, candidates.Count, 0, 0));
                preparationProgress.Restart();
            }
            if (candidate.HasLimitedAnalysisEvidence) { processed++; continue; }
            SharedFingerprint? fingerprint;
            if (lookupExtractor is SharedFingerprintExtractor indexedExtractor && sharedPathIndex is { IsComplete: true })
            {
                var lookup = await indexedExtractor.ExtractForLookupAsync(candidate, initial.ExcludedPaths,
                    sharedPathIndex, cancellationToken).ConfigureAwait(false);
                if (lookup.IsIndexMiss)
                {
                    missingPaths.Add(candidate.Path);
                    processed++;
                    continue;
                }
                fingerprint = lookup.Fingerprint;
            }
            else fingerprint = await lookupExtractor.ExtractAsync(candidate, initial.ExcludedPaths, cancellationToken).ConfigureAwait(false);
            if (fingerprint is null || !SharedFingerprintProtocol.CanReuse(fingerprint)) { processed++; continue; }
            if (sharedPathIndex is { IsComplete: true } && !sharedPathIndex.ContainsFingerprint(fingerprint))
            {
                missingPaths.Add(candidate.Path);
                processed++;
                continue;
            }
            extracted.Add((candidate, fingerprint));
        }
        var results = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        var failedPaths = new HashSet<string>(StringComparer.Ordinal);
        var networkFailurePaths = new HashSet<string>(StringComparer.Ordinal);
        var consentChanged = false;
        var resultGate = new object();
        ReportLookupProgress(progress, new(processed, candidates.Count, 0, 0));
        // Batch unique identities rather than local paths. Several scan nodes may
        // share a public purpose identity, but only need one network lookup.
        var batches = extracted.GroupBy(pair => pair.Fingerprint.Key, StringComparer.Ordinal)
            .Chunk(25).Select(chunk => chunk.SelectMany(group => group).ToArray()).ToArray();
        await ResolveBatchesAsync(batches, isRetry: false).ConfigureAwait(false);
        if (consentChanged) return SharedResolutionBatch.Empty;
        // Retry only failed identities. Successful answers and authoritative misses
        // remain intact, including when a later batch fails on another connection.
        var recovery = extracted.Where(pair => failedPaths.Contains(pair.Candidate.Path))
            .GroupBy(pair => pair.Fingerprint.Key, StringComparer.Ordinal)
            .Chunk(25).Select(chunk => chunk.SelectMany(group => group).ToArray()).ToArray();
        if (recovery.Length > 0 && (!requiresNetwork || _networkAvailability.IsNetworkAvailable))
        {
            ReportLookupProgress(progress, new(processed, candidates.Count, results.Count, failedPaths.Count, true));
            await ResolveBatchesAsync(recovery, isRetry: true).ConfigureAwait(false);
        }
        if (consentChanged || !SameConsent(initial, await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)))
            return SharedResolutionBatch.Empty;
        var attemptedPaths = extracted.Select(pair => pair.Candidate.Path).ToHashSet(StringComparer.Ordinal);
        DiskLens.Core.Ai.AiDeletionAdvicePolicy.ConstrainAncestors(results);
        return new(results, results.Count, candidates.Count - attemptedPaths.Count, failedPaths.Count > 0)
            { NetworkUnavailable = networkFailurePaths.Count > 0,
                FailedCount = failedPaths.Count,
                MissingPaths = missingPaths.Where(path => !results.ContainsKey(path)).ToArray() };

        async Task ResolveBatchesAsync((AiCandidateRecord Candidate, SharedFingerprint Fingerprint)[][] work, bool isRetry)
        {
            await Parallel.ForEachAsync(work, new ParallelOptions
            { MaxDegreeOfParallelism = MaximumConcurrentQueries, CancellationToken = cancellationToken }, async (batch, token) =>
            {
                lock (resultGate) { if (consentChanged) return; }
                var outcome = await QueryBatchAsync(batch, initial, token).ConfigureAwait(false);
                lock (resultGate)
                {
                    if (outcome.ConsentChanged) { consentChanged = true; return; }
                    if (consentChanged) return;
                    foreach (var pair in batch)
                    {
                        var path = pair.Candidate.Path;
                        if (outcome.Results.TryGetValue(path, out var answer))
                        {
                            results[path] = answer;
                            missingPaths.Remove(path);
                            failedPaths.Remove(path);
                            networkFailurePaths.Remove(path);
                        }
                        else if (outcome.MissingPaths.Contains(path))
                        {
                            missingPaths.Add(path);
                            failedPaths.Remove(path);
                            networkFailurePaths.Remove(path);
                        }
                        else
                        {
                            failedPaths.Add(path);
                            if (outcome.NetworkUnavailable) networkFailurePaths.Add(path);
                            else networkFailurePaths.Remove(path);
                        }
                    }
                    if (!isRetry) processed += batch.Length;
                    ReportLookupProgress(progress, new(processed, candidates.Count, results.Count, failedPaths.Count, isRetry));
                }
            }).ConfigureAwait(false);
        }
    }

    private sealed record QueryBatchOutcome(IReadOnlyDictionary<string, AiAnalysisResult> Results,
        IReadOnlySet<string> MissingPaths, bool NetworkUnavailable = false, bool ConsentChanged = false);

    private async Task<QueryBatchOutcome> QueryBatchAsync(
        IReadOnlyList<(AiCandidateRecord Candidate, SharedFingerprint Fingerprint)> batch,
        SharedConsentSnapshot initial, CancellationToken cancellationToken)
    {
        var answers = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal);
        var misses = new HashSet<string>(StringComparer.Ordinal);
        var current = await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!SameConsent(initial, current)) return new(answers, misses, ConsentChanged: true);
        if (requiresNetwork && !_networkAvailability.IsNetworkAvailable)
            return new(answers, misses, NetworkUnavailable: true);
        var items = batch.Select(pair => pair.Fingerprint).DistinctBy(fingerprint => fingerprint.Key).ToArray();
        if (items.Length == 0) return new(answers, misses);
        try
        {
            var response = await client.QueryAsync(current.Installation!, current.Epoch, items, cancellationToken).ConfigureAwait(false);
            if (!SameConsent(initial, await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)))
                return new(answers, misses, ConsentChanged: true);
            var requestedKeys = items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            if (response?.Items is null || response.Items.Count > items.Length || response.Items.Any(item => item is null)
                || response.Items.GroupBy(item => item.Key).Any(group => group.Count() > 1)
                || response.Items.Any(item => !requestedKeys.Contains(item.Key) || !item.Matched && item.Entry is not null))
                return new(answers, misses);
            var returned = response.Items.ToDictionary(item => item.Key, StringComparer.Ordinal);
            foreach (var pair in batch)
            {
                if (!returned.TryGetValue(pair.Fingerprint.Key, out var hit)) continue;
                if (!hit.Matched) misses.Add(pair.Candidate.Path);
                else if (hit.Entry is { } entry && IsValidEntry(pair.Fingerprint, entry))
                    answers[pair.Candidate.Path] = CreateSharedResult(pair.Candidate, pair.Fingerprint, entry);
            }
            // Omitted keys and invalid matched entries are unresolved failures,
            // not negative cache entries, and will be recovered independently.
            return new(answers, misses);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            or InvalidDataException or IOException or OperationCanceledException)
        {
            return new(answers, misses, NetworkUnavailable: requiresNetwork
                && NetworkFailureClassifier.IsOffline(ex, _networkAvailability));
        }
    }

    private static void ReportLookupProgress(IProgress<SharedResolutionProgress>? progress, SharedResolutionProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception) { /* Optional progress rendering must not discard cloud answers. */ }
    }

    private sealed class ExistingObservationExtractor(IReadOnlyDictionary<string, SharedFingerprint> fingerprints)
        : ISharedFingerprintExtractor
    {
        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(!SharedFingerprintExtractor.IsExcluded(candidate.Path, exclusions)
                && fingerprints.TryGetValue(candidate.Path, out var fingerprint) ? fingerprint : null);
        }
    }

    public async Task<SharedContributionReceipt> ContributeAsync(IReadOnlyList<AiCandidateRecord> candidates,
        IReadOnlyList<AiAnalysisResult> results, SharedAnalysisProvenance provenance, bool historical = false,
        CancellationToken cancellationToken = default)
    {
        var initial = (await CaptureConsentAsync(cancellationToken).ConfigureAwait(false))!;
        if (!initial.CanShare || historical && !initial.ShareHistory || !provenance.DefaultPrompt || provenance.SharedContext)
            return new(0, results.Count, initial.Epoch);
        // Binding is taken before the analysis starts, not when a slow request finishes.
        // Enabling, withdrawing or editing exclusions mid-analysis cannot relabel old work as newly consented work.
        if (!historical && provenance.ConsentAtAnalysisStart is null
            || provenance.ConsentAtAnalysisStart is { } started && !SameConsent(started, initial))
            return new(0, results.Count, initial.Epoch);
        // These are catalog values, never the caller's custom model name or endpoint.
        if (provenance.ProviderClass is not ("official" or "custom") || provenance.ModelFamily is not ("deepseek" or "openai" or "ollama" or "other"))
            return new(0, results.Count, initial.Epoch);
        var records = candidates.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var pending = new List<SharedContribution>();
        foreach (var result in results)
        {
            if (result.Origin is not AiAnalysisResultOrigin.AiAnalyzed || result.SharedEntry is not null || result.IsAnalysisEvidenceLimited || result.LooksLikeFallback()
                || result.EffectiveConfidenceLevel is not (AiConfidenceLevel.Medium or AiConfidenceLevel.High)
                || result.OriginalAnalyzedAt is not { } at || at <= DateTimeOffset.UnixEpoch || at > _time.GetUtcNow()
                || !historical && at < initial.EnabledAt
                || result.OriginalAnalysisId is not { } analysisId || !Regex.IsMatch(analysisId, "^[A-Za-z0-9_-]{8,80}$")
                || !records.TryGetValue(result.Path, out var candidate) || candidate.HasLimitedAnalysisEvidence
                || result.AnalysisEvidenceScope != candidate.AnalysisEvidenceScope) continue;
            if (result.SuggestedAction == SuggestedAction.RecycleCandidate
                && result.RiskLevel is RiskLevel.High or RiskLevel.Protected) continue;
            var fingerprint = await extractor.ExtractAsync(candidate, initial.ExcludedPaths, cancellationToken).ConfigureAwait(false);
            if (fingerprint is null) continue;
            // Sharing purpose is allowed in protected locations. Deletion advice
            // still obeys local protection even through an extension pipeline.
            var safeResult = IsLocallyProtected(candidate)
                ? AiDeletionAdvicePolicy.Constrain(result, protectedLocation: true) : result;
            var action = safeResult.SuggestedAction switch
            {
                SuggestedAction.Keep => "Keep", SuggestedAction.DoNotDelete => "DoNotDelete",
                SuggestedAction.Review => "Review", SuggestedAction.RecycleCandidate => "RecycleCandidate", _ => ""
            };
            var purpose = AiPurposeCategory.Normalize(result.PurposeCategory);
            if (action.Length == 0) continue;
            // Explicit removal advice is shareable only for disposable-purpose categories.
            if (!SharedFingerprintProtocol.ValidConclusion(purpose, action)) continue;
            // New analyses carry both original purpose texts. Historical category-only
            // results remain category-only instead of manufacturing a new short label.
            var shortPurpose = result.ShortPurpose ?? "";
            var reason = shortPurpose.Length == 0 ? "" : result.Reason;
            var shortPurposeEn = result.ShortPurposeEn ?? "";
            var reasonEn = result.ReasonEn ?? "";
            if (!SharedPurposeText.IsValidPair(shortPurpose, reason)
                || !SharedPurposeText.IsValidEnglishPair(shortPurposeEn, reasonEn)
                || shortPurposeEn.Length > 0 && shortPurpose.Length == 0) continue;
            if (SharedPurposeText.ContainsPrivatePathContext(shortPurpose, reason, candidate.Path)
                || SharedPurposeText.ContainsPrivatePathContext(shortPurposeEn, reasonEn, candidate.Path))
            {
                // Keep only controlled category evidence. Never rewrite a private
                // original answer, manufacture a translation, or alter local AI results.
                shortPurpose = reason = shortPurposeEn = reasonEn = string.Empty;
            }
            pending.Add(new(analysisId, fingerprint.SoftwareId, fingerprint.VersionMajor, fingerprint.RoleId, fingerprint.Shape,
                purpose, action, historical ? "historical-ai" : "fresh-ai", provenance.ProviderClass, provenance.ModelFamily, true, false,
                result.EffectiveConfidenceLevel.ToString())
            {
                ShortPurpose = shortPurpose, Reason = reason,
                ShortPurposeEn = shortPurposeEn, ReasonEn = reasonEn,
            });
        }
        // Generalized templates can map several local files to one anonymous bucket.
        // One installation contributes once per bucket. A conflicting bucket has
        // no eligible consensus and must not manufacture an Unknown observation.
        var ages = results.Where(x => x.OriginalAnalysisId is not null && x.OriginalAnalyzedAt is not null)
            .GroupBy(x => x.OriginalAnalysisId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(r => r.OriginalAnalyzedAt!.Value), StringComparer.Ordinal);
        var unique = pending.GroupBy(x => new SharedFingerprint(x.SoftwareId, x.VersionMajor, x.RoleId, x.Shape).Key)
            .Where(group => group.Select(x => (x.Purpose, x.Action)).Distinct().Count() == 1)
            .Select(group =>
            {
                static int Grade(string value) => value == "High" ? 3 : 2;
                // Keep an original text and the conservative eligible grade.
                var original = group.OrderByDescending(x => Grade(x.ConfidenceLevel))
                    .ThenBy(x => x.AnalysisId, StringComparer.Ordinal).First();
                var minimumGrade = group.OrderBy(x => Grade(x.ConfidenceLevel)).First().ConfidenceLevel;
                return original with { ConfidenceLevel = minimumGrade };
            }).ToArray();
        var accepted = 0; var skipped = results.Count - unique.Length; var failed = 0;
        if (outbox is not null)
        {
            // All normalization and eligibility checks finish before any HTTP request.
            // A lost response is retried from these exact payloads, never from local paths or AI calls.
            try
            {
                await outbox.EnqueueAsync(initial, unique.Select(x => new SharedQueuedContribution(x, ages[x.AnalysisId])).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                var queuedReceipt = await outbox.FlushAsync(cancellationToken).ConfigureAwait(false);
                return queuedReceipt with { Skipped = queuedReceipt.Skipped + skipped };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidDataException
                or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                return new(0, skipped, initial.Epoch) { Failed = unique.Length,
                    NetworkUnavailable = requiresNetwork && NetworkFailureClassifier.IsOffline(ex, _networkAvailability) };
            }
        }
        foreach (var chunk in unique.Chunk(10))
        {
            var current = await consentSource.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!SameConsent(initial, current)) return new(accepted, results.Count - accepted, current.Epoch) { Failed = failed };
            try
            {
                if (requiresNetwork && !_networkAvailability.IsNetworkAvailable) throw new NetworkUnavailableException();
                var receipt = await client.ContributeAsync(current.Installation!, current.Epoch, chunk, cancellationToken).ConfigureAwait(false);
                if (receipt.Epoch != current.Epoch || receipt.Accepted < 0 || receipt.Skipped < 0
                    || receipt.Accepted > chunk.Length || receipt.Skipped != chunk.Length - receipt.Accepted)
                    throw new InvalidDataException("共享回执无效。");
                accepted += receipt.Accepted; skipped += receipt.Skipped;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidDataException or IOException or OperationCanceledException)
            {
                skipped += chunk.Length; failed += chunk.Length;
                if (requiresNetwork && NetworkFailureClassifier.IsOffline(ex, _networkAvailability))
                    return new(accepted, skipped, initial.Epoch) { Failed = failed, NetworkUnavailable = true };
            }
        }
        return new(accepted, skipped, initial.Epoch) { Failed = failed };
    }

    private static bool SameConsent(SharedConsentSnapshot a, SharedConsentSnapshot b) => a.CanShare && b.CanShare
        && a.Epoch == b.Epoch && a.EnabledAt == b.EnabledAt && a.ShareHistory == b.ShareHistory
        && a.AgreementVersion == b.AgreementVersion && a.PolicyRevision == b.PolicyRevision && a.Installation == b.Installation
        && a.ExcludedPaths.SequenceEqual(b.ExcludedPaths, StringComparer.OrdinalIgnoreCase);

    private bool IsValidEntry(SharedFingerprint fingerprint, SharedExperienceEntry entry, bool ownObservation = false)
    {
        var now = _time.GetUtcNow();
        return entry.Key == fingerprint.Key && entry.SoftwareId == fingerprint.SoftwareId && entry.RoleId == fingerprint.RoleId
            && entry.RuleVersion is 2 or 3 && entry.Revision > 0 && entry.Status == "active"
            && entry.ConfidenceLevel is "Medium" or "High"
            && SharedPurposeText.IsValidPair(entry.ShortPurpose, entry.Reason)
            && SharedPurposeText.IsValidEnglishPair(entry.ShortPurposeEn, entry.ReasonEn)
            && (entry.ShortPurposeEn.Length == 0 || entry.ShortPurpose.Length > 0)
            && entry.UpdatedAt <= now.AddMinutes(1) && entry.UpdatedAt > DateTimeOffset.UnixEpoch
            && entry.ExpiresAt > now && entry.ExpiresAt <= now.AddMinutes(16)
            && (ownObservation
                ? SharedFingerprintProtocol.IsOwnObservation(fingerprint) && entry.IsOwnObservation
                    && entry.ShortPurpose.Length > 0 && entry.Reason.Length > 0
                : SharedFingerprintProtocol.CanReuse(fingerprint) && !entry.IsOwnObservation)
            && SharedFingerprintProtocol.ValidConclusion(entry.Purpose, entry.Action)
            && (SharedFingerprintProtocol.IsPathPurpose(fingerprint) || SharedFingerprintProtocol.IsV2(fingerprint) || (fingerprint.RoleId == "sdk-emulator"
                ? entry.Purpose == "runtime" && entry.Action is "Keep" or "DoNotDelete"
                : entry.Purpose == "cache" && entry.Action is "Review" or "Keep" or "DoNotDelete" or "RecycleCandidate"));
    }

    private static AiAnalysisResult CreateSharedResult(AiCandidateRecord candidate, SharedFingerprint fingerprint, SharedExperienceEntry entry)
    {
        var display = entry.DisplayTexts is { } texts
            && !string.IsNullOrWhiteSpace(texts.ShortPurpose) && !string.IsNullOrWhiteSpace(texts.Reason)
            && !string.IsNullOrWhiteSpace(texts.ShortPurposeEn) && !string.IsNullOrWhiteSpace(texts.ReasonEn)
            && SharedPurposeText.IsValidPair(texts.ShortPurpose, texts.Reason)
            && SharedPurposeText.IsValidEnglishPair(texts.ShortPurposeEn, texts.ReasonEn) ? texts : null;
        var purposeOnly = SharedFingerprintProtocol.IsPathPurpose(fingerprint);
        var software = entry.IsOwnObservation ? "本人的云端分析" : fingerprint.SoftwareId switch
        {
            "generic-v3" => SharedFingerprintProtocol.IsSharedPath(fingerprint) ? "共享归一化路径" : "Windows 标准目录",
            "generic-v2" => "通用文件路径", "android-sdk" => "Android SDK", "wegame" => "WeGame", _ => "本机已验证软件"
        };
        var role = entry.Purpose switch
        {
            "cache" => "缓存或临时数据", "runtime" => "运行依赖", "application" => "应用组件",
            "configuration" => "配置数据", "logs" => "日志记录", "downloads" => "下载内容", "user-data" => "用户资料", _ => "待确认内容"
        };
        var purpose = entry.Purpose switch
        {
            "cache" => "应用缓存与临时数据", "runtime" => "程序运行依赖文件", "application" => "应用程序组件",
            "configuration" => "应用配置数据", "logs" => "程序运行日志", "downloads" => "下载内容",
            "user-data" => "用户个人资料", _ => "用途暂不明确"
        };
        var action = entry.Action switch
        {
            "RecycleCandidate" => SuggestedAction.RecycleCandidate,
            "Keep" => SuggestedAction.Keep, "DoNotDelete" => SuggestedAction.DoNotDelete, _ => SuggestedAction.Review
        };
        string[] conditions = [purposeOnly ? "归一化路径标识相同；共享用途仅供参考，操作仍遵守本地保护规则。"
                : "匿名路径类别、文件类型和结构摘要匹配；经验来自中高置信度的 AI 分析。",
            "置信等级汇总自来源 AI 的自评，不能证明当前文件内容、使用状态或可删除性。",
            "AI 与共享经验仅供参考；删除前请核实用途、关闭相关任务并确认备份。"];
        var explanation = entry.IsOwnObservation
            ? "当前安装自身已上传的分析，通过本机随机观察身份恢复；不代表其他目录或用户具有相同用途。"
            : $"归一化路径：{fingerprint.RoleId}；主版本 {fingerprint.VersionMajor}；"
                + (purposeOnly ? "路径用途匹配，不依赖子目录是否读取完整。" : "结构摘要匹配。") + "原始路径未以明文上传。";
        var grade = entry.ConfidenceLevel == "High" ? AiConfidenceLevel.High : AiConfidenceLevel.Medium;
        var risk = action == SuggestedAction.RecycleCandidate ? RiskLevel.Low
            : action == SuggestedAction.Review ? RiskLevel.Review : RiskLevel.High;
        var result = new AiAnalysisResult(candidate.Path, risk, action,
            string.IsNullOrEmpty(entry.Reason) ? purpose : entry.Reason, 0)
        {
            ShortPurpose = entry.ShortPurpose,
            ShortPurposeEn = entry.ShortPurposeEn,
            ReasonEn = entry.ReasonEn,
            Origin = AiAnalysisResultOrigin.SharedExperience,
            AnalysisEvidenceScope = purposeOnly ? AiAnalysisEvidenceScope.PathPurpose : AiAnalysisEvidenceScope.DirectoryStructure,
            ConfidenceLevel = grade, HasExplicitNumericConfidence = false, PurposeCategory = entry.Purpose,
            SharedEntry = new(entry.Key, software, role, entry.Purpose, conditions, entry.RuleVersion, entry.Revision,
                entry.UpdatedAt, entry.ExpiresAt, explanation)
                { ConfidenceLevel = entry.ConfidenceLevel, IsOwnObservation = entry.IsOwnObservation,
                    DisplayTexts = display }
        };
        // Explicit cloud advice never upgrades legacy Review. Child scan diagnostics
        // remain separate from a successful path-purpose request's evidence.
        if (candidate.HasLimitedScope) result = result with
        {
            IsScopeLimited = true,
            ScopeWarning = (candidate.HasUnreadItem ? AiScopeWarning.ItemReadIncomplete
                : candidate.HasIncompleteScan ? AiScopeWarning.DescendantReadIncomplete : AiScopeWarning.None)
                | (candidate.IsStructureSummary ? AiScopeWarning.StructureSummary : AiScopeWarning.None),
        };
        return AiDeletionAdvicePolicy.Constrain(result, IsLocallyProtected(candidate));
    }

    private static bool IsLocallyProtected(AiCandidateRecord candidate)
    {
        if (candidate.IsProtectedLocation || candidate.Structure.IsProtectedLocation) return true;
        try { return LocalProtection.IsProtectedLocation(Path.GetFullPath(candidate.Path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return true; }
    }
}

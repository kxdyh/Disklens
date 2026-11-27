using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskLens.Core.SharedKnowledge;
using DiskLens.Core.Networking;

namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>One service-scoped, encrypted queue. It never stores paths, model names or credentials.</summary>
public sealed class SharedContributionOutbox : ISharedContributionOutbox, IDisposable
{
    // This is a bounded local retry buffer, not the cloud's storage quota.
    // The encrypted document is rewritten after acknowledged batches; larger
    // cloud libraries do not need to be held in this file at once.
    private const int MaximumItems = 20_000;
    private const int MaximumBytes = 64 * 1024 * 1024;
    private const int MaximumAutomaticRetries = 3;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly string _path;
    private readonly ISharedConsentSource _consent;
    private readonly ISharedExperienceClient _client;
    private readonly TimeProvider _time;
    private readonly INetworkAvailability _networkAvailability;
    private readonly bool _requiresNetwork;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Document _document = new(1, null, [], 0, default);
    private Binding? _rejectedConsent;
    private bool _loaded;
    private bool _halted;
    private bool _disposed;
    private SharedUploadStatus _uploadStatus = new(SharedUploadStage.Idle, 0);
    public string? LastError { get; private set; }
    public event EventHandler? Changed;

    public SharedContributionOutbox(string directory, ISharedConsentSource consent,
        ISharedExperienceClient client, TimeProvider? time = null,
        INetworkAvailability? networkAvailability = null, bool requiresNetwork = true)
    {
        _path = Path.Combine(directory, "shared-contribution-outbox.bin");
        _consent = consent;
        _client = client;
        _time = time ?? TimeProvider.System;
        _networkAvailability = networkAvailability ?? SystemNetworkAvailability.Instance;
        _requiresNetwork = requiresNetwork;
    }

    public async Task EnqueueAsync(SharedConsentSnapshot consent, IReadOnlyList<SharedQueuedContribution> items,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _stateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(token).ConfigureAwait(false);
            var current = await _consent.GetSnapshotAsync(token).ConfigureAwait(false);
            await SynchronizeLockedAsync(current, token).ConfigureAwait(false);
            if (!current.CanShare || Binding.For(consent) != Binding.For(current) || Binding.For(current) == _rejectedConsent) return;
            ThrowIfHalted();
            var now = _time.GetUtcNow();
            var entries = _document.Items.Where(x => HasValidAnalysisTime(x, now)).ToDictionary(Key, StringComparer.Ordinal);
            foreach (var item in items)
            {
                Validate(item);
                if (!HasShareableConfidence(item)) continue;
                if (!HasValidAnalysisTime(item, now) || item.Contribution.Source == "historical-ai" && !current.ShareHistory
                    || item.Contribution.Source == "fresh-ai" && item.AnalyzedAt < current.EnabledAt) continue;
                var key = Key(item);
                if (entries.TryGetValue(key, out var previous) && previous.AnalyzedAt >= item.AnalyzedAt) continue;
                if (!entries.ContainsKey(key) && entries.Count >= MaximumItems)
                    throw new InvalidDataException(L.T("待上传记录已达本机保存上限。"));
                entries[key] = item;
            }
            if (entries.Values.Select(x => x.Contribution.AnalysisId).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                throw new InvalidDataException(L.T("同一分析编号不能用于多个待上传项目。"));
            await SaveLockedAsync(_document with { Binding = Binding.For(current), Items = entries.Values.ToArray() }, token).ConfigureAwait(false);
            if (_uploadStatus.Stage != SharedUploadStage.Uploading && _document.Items.Length > 0)
                _uploadStatus = new(LastError is null ? SharedUploadStage.Pending : SharedUploadStage.Failed, _document.Items.Length);
        }
        finally { _stateGate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public Task<SharedContributionReceipt> RetryPendingAsync(CancellationToken cancellationToken = default)
        => FlushAsync(cancellationToken);

    public async Task<SharedContributionReceipt> FlushAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _flushGate.WaitAsync(token).ConfigureAwait(false);
        var accepted = 0; var skipped = 0; var failed = 0;
        var networkUnavailable = false;
        var retriesUsed = 0;
        HashSet<string>? admittedIds = null;
        long epoch = 0;
        try
        {
            while (true)
            {
                SharedQueuedContribution[] batch;
                SharedConsentSnapshot current;
                Binding binding;
                await _stateGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await LoadLockedAsync(token).ConfigureAwait(false);
                    current = await _consent.GetSnapshotAsync(token).ConfigureAwait(false);
                    epoch = current.Epoch;
                    await SynchronizeLockedAsync(current, token).ConfigureAwait(false);
                    if (_halted || !current.CanShare || Binding.For(current) == _rejectedConsent) break;
                    binding = Binding.For(current);
                    if (admittedIds is null)
                    {
                        var now = _time.GetUtcNow();
                        var retryStates = RetryStatesLocked();
                        admittedIds = _document.Items.Where(x => !retryStates.TryGetValue(x.Contribution.AnalysisId, out var retry)
                                || retry.RetriesUsed < MaximumAutomaticRetries && retry.NextAttemptAt <= now)
                            .Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
                        if (admittedIds.Count == 0) break;
                    }
                    batch = _document.Items.Where(x => admittedIds.Contains(x.Contribution.AnalysisId))
                        .OrderBy(x => x.AnalyzedAt).Take(10).ToArray();
                    if (batch.Length == 0) break;
                    var batchRetries = RetryStatesLocked();
                    retriesUsed = 0;
                    foreach (var item in batch)
                    {
                        var id = item.Contribution.AnalysisId;
                        if (!batchRetries.TryGetValue(id, out var retry)) continue;
                        // Reserve only the batch about to be sent. Unsent later batches
                        // retain their budget if the user closes the app mid-cycle.
                        batchRetries[id] = retry with { RetriesUsed = retry.RetriesUsed + 1,
                            NextAttemptAt = _time.GetUtcNow().Add(RetryInterval) };
                        retriesUsed = Math.Max(retriesUsed, retry.RetriesUsed + 1);
                    }
                    await SaveLockedAsync(_document with { RetryStates = batchRetries.Values.ToArray() }, token).ConfigureAwait(false);
                    _uploadStatus = new(SharedUploadStage.Uploading, _document.Items.Length, accepted, skipped,
                        accepted + skipped + _document.Items.Length, AutomaticRetriesUsed: retriesUsed);
                }
                finally { _stateGate.Release(); }
                Changed?.Invoke(this, EventArgs.Empty);

                try
                {
                    if (_requiresNetwork && !_networkAvailability.IsNetworkAvailable) throw new NetworkUnavailableException();
                    var receipt = await _client.ContributeAsync(current.Installation!, current.Epoch,
                        batch.Select(x => x.Contribution).ToArray(), token).ConfigureAwait(false);
                    if (receipt.Epoch != epoch || receipt.Accepted < 0 || receipt.Skipped < 0
                        || receipt.Accepted > batch.Length || receipt.Skipped != batch.Length - receipt.Accepted)
                        throw new InvalidDataException(L.T("共享上传回执不完整。"));
                    await _stateGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var latest = await _consent.GetSnapshotAsync(token).ConfigureAwait(false);
                        await SynchronizeLockedAsync(latest, token).ConfigureAwait(false);
                        if (!latest.CanShare || Binding.For(latest) != binding || _document.Binding != binding) break;
                        // An older in-flight receipt must not delete newer work for the same bucket.
                        var sentIds = batch.Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
                        var remaining = _document.Items.Where(x => !sentIds.Contains(x.Contribution.AnalysisId)).ToArray();
                        await SaveLockedAsync(_document with
                        {
                            Items = remaining,
                            RetryStates = (_document.RetryStates ?? []).Where(x => !sentIds.Contains(x.AnalysisId)).ToArray()
                        }, token).ConfigureAwait(false);
                        accepted += receipt.Accepted;
                        skipped += receipt.Skipped;
                        LastError = null;
                        _uploadStatus = new(_document.Items.Length == 0 ? SharedUploadStage.Completed : SharedUploadStage.Uploading,
                            _document.Items.Length, accepted, skipped, accepted + skipped + _document.Items.Length,
                            AutomaticRetriesUsed: retriesUsed);
                    }
                    finally { _stateGate.Release(); }
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException
                    or IOException or OperationCanceledException or UnauthorizedAccessException or CryptographicException)
                {
                    failed += batch.Length;
                    networkUnavailable = _requiresNetwork && NetworkFailureClassifier.IsOffline(ex, _networkAvailability);
                    // A canceled/lost response can already have committed. Keep the same analysis IDs.
                    await RecordFailureAsync(binding, admittedIds!, ex).ConfigureAwait(false);
                    if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) break;
                    break;
                }
            }
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { return new(accepted, skipped, epoch)
                { NetworkUnavailable = networkUnavailable, Failed = failed, Pending = _document.Items.Length }; }
            finally { _stateGate.Release(); }
        }
        finally
        {
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // Cancellation between acknowledged batches must not leave a phantom in-flight display.
                if (_uploadStatus.Stage == SharedUploadStage.Uploading)
                    _uploadStatus = _uploadStatus with { Stage = token.IsCancellationRequested
                        ? SharedUploadStage.Paused : SharedUploadStage.Pending };
            }
            finally { _stateGate.Release(); _flushGate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
        }
    }

    private async Task RecordFailureAsync(Binding binding, HashSet<string> admittedIds, Exception error)
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = await _consent.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            await SynchronizeLockedAsync(current, CancellationToken.None).ConfigureAwait(false);
            if (!current.CanShare || Binding.For(current) != binding || _document.Binding != binding || _halted) return;
            var retryStates = RetryStatesLocked();
            var pendingIds = _document.Items.Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in admittedIds)
            {
                if (pendingIds.Contains(id)) retryStates[id] = new(id,
                    retryStates.GetValueOrDefault(id)?.RetriesUsed ?? 0, _time.GetUtcNow().Add(RetryInterval));
            }
            var next = _document with { RetryStates = retryStates.Values.ToArray() };
            await SaveLockedAsync(next, CancellationToken.None).ConfigureAwait(false);
            LastError = _requiresNetwork && NetworkFailureClassifier.IsOffline(error, _networkAvailability)
                ? NetworkFailureClassifier.OfflineMessage
                : L.T("上传至云端经验库失败，待上传记录已保存在本机，将稍后重试。");
            _uploadStatus = _uploadStatus with { Stage = SharedUploadStage.Failed };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _halted = true;
            LastError = L.T("上传至云端经验库失败，待上传记录未能可靠保存，自动上传已暂停。");
            _uploadStatus = _uploadStatus with { Stage = SharedUploadStage.Paused };
        }
        finally { _stateGate.Release(); }
    }

    public async Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            await SynchronizeLockedAsync(await _consent.GetSnapshotAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return _document.Items.Length;
        }
        finally { _stateGate.Release(); }
    }

    internal async Task RetryAfterNetworkChangeAsync(CancellationToken cancellationToken)
    {
        // Connectivity notifications may wake the worker, but cannot shorten
        // the one-minute interval or replenish the persisted retry budget.
        await SynchronizeConsentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SharedUploadStatus> GetUploadStatusAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            await SynchronizeLockedAsync(await _consent.GetSnapshotAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var stage = _halted ? SharedUploadStage.Paused : _uploadStatus.Stage;
            var retryStates = RetryStatesLocked();
            var retries = retryStates.Values.ToArray();
            var nextRetry = retries.Where(x => x.RetriesUsed < MaximumAutomaticRetries)
                .Select(x => (DateTimeOffset?)x.NextAttemptAt).Min();
            var exhausted = _document.Items.Length > 0 && retries.Length == _document.Items.Length
                && retries.All(x => x.RetriesUsed >= MaximumAutomaticRetries);
            if (stage is not (SharedUploadStage.Uploading or SharedUploadStage.Paused))
            {
                if (LastError is not null || retries.Length > 0) stage = SharedUploadStage.Failed;
                else if (_document.Items.Length > 0) stage = SharedUploadStage.Pending;
                else if (stage == SharedUploadStage.Pending) stage = SharedUploadStage.Idle;
            }
            return _uploadStatus with { Stage = stage, Pending = _document.Items.Length, Error = LastError,
                AutomaticRetriesUsed = stage == SharedUploadStage.Uploading ? _uploadStatus.AutomaticRetriesUsed
                    : retries.Length == 0 ? 0 : retries.Where(x => !exhausted ? x.RetriesUsed < MaximumAutomaticRetries : true)
                        .Select(x => x.RetriesUsed).DefaultIfEmpty(0).Max(),
                NextRetryAt = !_halted && stage != SharedUploadStage.Uploading ? nextRetry : null,
                AutomaticRetriesExhausted = exhausted && stage != SharedUploadStage.Uploading,
                Total = Math.Max(_uploadStatus.Total, _uploadStatus.Confirmed + _document.Items.Length) };
        }
        finally { _stateGate.Release(); }
    }

    internal async Task<TimeSpan> GetNextUploadDelayAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var current = await _consent.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await SynchronizeLockedAsync(current, cancellationToken).ConfigureAwait(false);
            if (_halted || !current.CanShare || _document.Items.Length == 0) return Timeout.InfiniteTimeSpan;
            var retryStates = RetryStatesLocked();
            if (_document.Items.Any(x => !retryStates.ContainsKey(x.Contribution.AnalysisId))) return TimeSpan.Zero;
            var next = retryStates.Values.Where(x => x.RetriesUsed < MaximumAutomaticRetries)
                .Select(x => (DateTimeOffset?)x.NextAttemptAt).Min();
            if (next is null) return Timeout.InfiniteTimeSpan;
            var delay = next.Value - _time.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        finally { _stateGate.Release(); }
    }

    public async Task SynchronizeConsentAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            await SynchronizeLockedAsync(await _consent.GetSnapshotAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ClearLocked(); }
        finally { _stateGate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    internal async Task InvalidateConsentAsync()
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var previous = await _consent.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            // Reject old in-flight analysis even in the gap before settings persistence finishes.
            _rejectedConsent = previous.CanShare ? Binding.For(previous) : null;
            ClearLocked();
        }
        finally { _stateGate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private async Task SynchronizeLockedAsync(SharedConsentSnapshot current, CancellationToken token)
    {
        var binding = current.CanShare ? Binding.For(current) : null;
        if (binding is not null && binding == _rejectedConsent)
        {
            ClearLocked();
            return;
        }
        if (binding is not null && binding != _rejectedConsent) _rejectedConsent = null;
        if (binding is null || _document.Binding is not null && _document.Binding != binding)
        {
            ClearLocked();
            _document = _document with { Binding = binding };
            return;
        }
        if (_halted) return;
        // Pending uploads have no age expiry. Only invalid evidence or a changed
        // consent boundary can make a queued contribution ineligible.
        var eligible = _document.Items.Where(x => HasShareableConfidence(x) && HasValidAnalysisTime(x, _time.GetUtcNow())
            && (x.Contribution.Source == "historical-ai" ? current.ShareHistory : x.AnalyzedAt >= current.EnabledAt)).ToArray();
        if (eligible.Length != _document.Items.Length)
        {
            var eligibleIds = eligible.Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
            await SaveLockedAsync(_document with { Items = eligible,
                FailureCount = eligible.Length == 0 ? 0 : _document.FailureCount,
                RetryStates = RetryStatesLocked().Values.Where(x => eligibleIds.Contains(x.AnalysisId)).ToArray(),
                NextAttemptAt = eligible.Length == 0 ? default : _document.NextAttemptAt }, token).ConfigureAwait(false);
            if (eligible.Length == 0) LastError = null;
        }
    }

    private void ClearLocked()
    {
        // Also remove an unreadable old document; never reinterpret it under fresh consent.
        try
        {
            var interrupted = _document.Items.Length > 0 || _uploadStatus.Stage == SharedUploadStage.Uploading;
            if (File.Exists(_path)) File.Delete(_path);
            _document = new(1, null, [], 0, default);
            _loaded = true;
            _halted = false;
            LastError = null;
            _uploadStatus = new(interrupted || _uploadStatus.Stage == SharedUploadStage.Paused
                ? SharedUploadStage.Paused : SharedUploadStage.Idle, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _halted = true;
            LastError = L.T("上传至云端经验库已暂停，旧待上传记录未能清除，请检查本机存储权限。");
            throw;
        }
    }

    private static string Key(SharedQueuedContribution item) => new SharedFingerprint(item.Contribution.SoftwareId,
        item.Contribution.VersionMajor, item.Contribution.RoleId, item.Contribution.Shape).Key;
    private static bool HasValidAnalysisTime(SharedQueuedContribution item, DateTimeOffset now)
        => item.AnalyzedAt > DateTimeOffset.UnixEpoch && item.AnalyzedAt <= now;
    private static bool HasShareableConfidence(SharedQueuedContribution item)
        => item.Contribution.ConfidenceLevel is "Medium" or "High";

    private static void Validate(SharedQueuedContribution item)
    {
        var c = item.Contribution;
        if (!SharedFingerprintProtocol.IsValid(new(c.SoftwareId, c.VersionMajor, c.RoleId, c.Shape))
            || !SharedFingerprintProtocol.ValidConclusion(c.Purpose, c.Action)
            || !SharedPurposeText.IsValidPair(c.ShortPurpose, c.Reason)
            || !SharedPurposeText.IsValidEnglishPair(c.ShortPurposeEn, c.ReasonEn)
            || c.ShortPurposeEn.Length > 0 && c.ShortPurpose.Length == 0
            || c.ConfidenceLevel is not (null or "" or "Unknown" or "Low" or "Medium" or "High")
            || c.Source is not ("fresh-ai" or "historical-ai") || c.ProviderClass is not ("official" or "custom")
            || c.ModelFamily is not ("deepseek" or "openai" or "ollama" or "other")
            || !c.DefaultPrompt || c.SharedContext || !Regex.IsMatch(c.AnalysisId ?? "", "^[A-Za-z0-9_-]{8,80}$"))
            throw new InvalidDataException(L.T("待上传内容不符合匿名受控字段规则。"));
    }

    private async Task LoadLockedAsync(CancellationToken token)
    {
        if (_loaded) return;
        if (!File.Exists(_path)) { _loaded = true; return; }
        try
        {
            if (new FileInfo(_path).Length > MaximumBytes) throw new InvalidDataException();
            var encrypted = await File.ReadAllBytesAsync(_path, token).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var loaded = JsonSerializer.Deserialize<Document>(plain) ?? throw new InvalidDataException();
                if (loaded.Version != 1 || loaded.Items is null || loaded.Items.Length > MaximumItems
                    || loaded.Items.Length > 0 && loaded.Binding is null
                    || loaded.FailureCount is < 0 or > 16
                    || loaded.NextAttemptAt > _time.GetUtcNow().AddHours(7))
                    throw new InvalidDataException();
                foreach (var item in loaded.Items)
                {
                    if (item?.Contribution is null) throw new InvalidDataException();
                    Validate(item);
                }
                if (loaded.Items.Select(Key).Distinct(StringComparer.Ordinal).Count() != loaded.Items.Length
                    || loaded.Items.Select(x => x.Contribution.AnalysisId).Distinct(StringComparer.Ordinal).Count() != loaded.Items.Length)
                    throw new InvalidDataException();
                var retryStates = loaded.RetryStates;
                if (retryStates is null)
                    retryStates = loaded.FailureCount > 0 ? loaded.Items.Select(x => new RetryState(x.Contribution.AnalysisId, 0,
                        loaded.NextAttemptAt > _time.GetUtcNow().Add(RetryInterval)
                            ? _time.GetUtcNow().Add(RetryInterval) : loaded.NextAttemptAt)).ToArray() : [];
                var pendingIds = loaded.Items.Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
                if (retryStates.Length > MaximumItems
                    || retryStates.Any(x => x is null || !pendingIds.Contains(x.AnalysisId) || x.RetriesUsed is < 0 or > MaximumAutomaticRetries
                        || x.NextAttemptAt > _time.GetUtcNow().AddHours(7))
                    || retryStates.Select(x => x.AnalysisId).Distinct(StringComparer.Ordinal).Count() != retryStates.Length)
                    throw new InvalidDataException();
                _document = loaded with { RetryStates = retryStates };
                if (loaded.RetryStates is null)
                    await SaveLockedAsync(_document, token).ConfigureAwait(false);
                if (retryStates.Length > 0)
                    LastError = L.T("上传至云端经验库失败，待上传记录已保存在本机，将稍后重试。");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            // A canceled read must remain retryable; treating the empty in-memory
            // document as loaded would let the next enqueue overwrite durable work.
            _loaded = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _loaded = true;
            _halted = true;
            LastError = L.T("待上传记录无法安全读取，自动上传已暂停，请重新确认共享设置。");
        }
    }

    private async Task SaveLockedAsync(Document document, CancellationToken token)
    {
        ThrowIfHalted();
        // Legacy fields remain readable for the migration; all new scheduling
        // is per analysis ID so exhausted work cannot block new contributions.
        document = document with { FailureCount = 0, NextAttemptAt = default };
        var pendingIds = document.Items.Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
        document = document with { RetryStates = (document.RetryStates ?? [])
            .Where(x => pendingIds.Contains(x.AnalysisId)).ToArray() };
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(document);
            byte[] encrypted;
            try
            {
                if (plain.Length > MaximumBytes - 4096) throw new InvalidDataException(L.T("待上传记录超过本机保存上限。"));
                encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            await File.WriteAllBytesAsync(temporary, encrypted, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            _document = document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _halted = true;
            LastError = L.T("待上传记录未能可靠保存，自动上传已暂停，请检查本机存储权限。");
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Binding(string InstallationId, long Epoch, string AgreementVersion,
        DateTimeOffset EnabledAt, string PolicyRevision, bool ShareHistory, string ScopeDigest)
    {
        public static Binding For(SharedConsentSnapshot consent)
        {
            var exclusions = string.Join("\n", consent.ExcludedPaths.Select(x => x.ToUpperInvariant()).Order(StringComparer.Ordinal));
            return new(Digest(consent.Installation?.InstallationId ?? ""), consent.Epoch, Digest(consent.AgreementVersion),
                consent.EnabledAt, Digest(consent.PolicyRevision), consent.ShareHistory,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exclusions))));
        }
        private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record RetryState(string AnalysisId, int RetriesUsed, DateTimeOffset NextAttemptAt);

    private Dictionary<string, RetryState> RetryStatesLocked()
    {
        var ids = _document.Items.Select(x => x.Contribution.AnalysisId).ToHashSet(StringComparer.Ordinal);
        return (_document.RetryStates ?? []).Where(x => ids.Contains(x.AnalysisId))
            .ToDictionary(x => x.AnalysisId, StringComparer.Ordinal);
    }

    private sealed record Document(int Version, Binding? Binding, SharedQueuedContribution[] Items,
        int FailureCount, DateTimeOffset NextAttemptAt, RetryState[]? RetryStates = null);

    private void ThrowIfHalted()
    {
        if (_halted) throw new IOException(LastError ?? L.T("待上传记录不可用，共享上传已暂停。"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
    }
}

using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public enum AiAnalysisStage
{
    Preparing,
    LoadingCache,
    Analyzing,
    Retrying,
    Saving,
    Completed,
    Canceled,
    StoppedForBudget,
    Failed,
    Uploading,
}

/// <summary>
/// Per-invocation progress. Processed means a candidate has a result (including an explicitly
/// incomplete result); it does not mean every candidate was sent to or successfully analyzed by AI.
/// A request's network/model work has no measurable percentage, so callers should animate an
/// indeterminate indicator while IsWaitingForResponse is true instead of inventing a percentage.
/// </summary>
public sealed record AiAnalysisProgress(
    AiAnalysisStage Stage,
    int TotalCandidates,
    int ProcessedCandidates,
    int AiAnalyzedCount,
    int CacheReusedCount,
    int ProtectedCount,
    int IncompleteCount,
    int RequestCount,
    int CurrentBatchSize,
    bool IsRetry,
    bool IsWaitingForResponse,
    string Message,
    int SharedExperienceCount = 0)
{
    public int RetryRequestCount { get; init; }
    public int RetriedCandidateCount { get; init; }
    public int RecoveredCandidateCount { get; init; }
    public int QueuedRetryCount { get; init; }
    public int ActiveRequestCount { get; init; }
    public int FreshActiveBatchSize { get; init; }
    public int RetryActiveBatchSize { get; init; }

    public double Fraction => TotalCandidates > 0
        ? Math.Clamp((double)ProcessedCandidates / TotalCandidates, 0, 1)
        : 0;

    public bool IsTerminal => Stage is AiAnalysisStage.Completed or AiAnalysisStage.Canceled
        or AiAnalysisStage.StoppedForBudget or AiAnalysisStage.Failed;
}

internal sealed class AiAnalysisProgressTracker(IProgress<AiAnalysisProgress>? progress)
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _retryContext = new();
    private readonly Dictionary<string, AiAnalysisResult> _results = new(StringComparer.Ordinal);
    private int _total;
    private int _requests;
    private int _batchSize;
    private int _analyzed;
    private int _cached;
    private int _protected;
    private int _incomplete;
    private int _shared;
    private readonly HashSet<string> _submittedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retriedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recoveredPaths = new(StringComparer.Ordinal);
    private int _retryRequests;
    private int _queuedRetryCount;
    private int _activeRequests;
    private int _freshActiveBatchSize;
    private int _retryActiveBatchSize;
    public int TotalCandidates { get { lock (_gate) return _total; } }
    public int SubmittedCandidateCount { get { lock (_gate) return _submittedPaths.Count; } }
    public int RetryRequestCount { get { lock (_gate) return _retryRequests; } }
    public int RetriedCandidateCount { get { lock (_gate) return _retriedPaths.Count; } }
    public int RecoveredCandidateCount { get { lock (_gate) return _recoveredPaths.Count; } }
    public int QueuedRetryCount { get { lock (_gate) return _queuedRetryCount; } }
    public int ActiveRequestCount { get { lock (_gate) return _activeRequests; } }
    // The fresh and recovery lanes retain their identity across awaits without
    // changing one another's request classification.
    public bool IsRetry { get => _retryContext.Value; set => _retryContext.Value = value; }

    public void SetTotal(int total) { lock (_gate) _total = total; }

    public void QueueRetry(int count) { lock (_gate) _queuedRetryCount += count; }
    public void DequeueRetry(int count) { lock (_gate) _queuedRetryCount = Math.Max(0, _queuedRetryCount - count); }

    public void IncludeResults(IEnumerable<AiAnalysisResult> results)
    {
        lock (_gate)
        {
            foreach (var result in results)
            {
                if (_results.TryGetValue(result.Path, out var previous))
                {
                    if (previous == result) continue;
                    AdjustCounts(previous, -1);
                }
                _results[result.Path] = result;
                AdjustCounts(result, 1);
            }
        }
    }

    public void RequestStarted(IReadOnlyList<AiCandidateRecord> records)
    {
        lock (_gate)
        {
            foreach (var record in records)
            {
                _submittedPaths.Add(record.Path);
                if (IsRetry) _retriedPaths.Add(record.Path);
            }
            var count = records.Count;
            _requests++;
            if (IsRetry) _retryRequests++;
            _activeRequests++;
            _batchSize = count;
            if (IsRetry) _retryActiveBatchSize = count;
            else _freshActiveBatchSize = count;
            Report(IsRetry ? AiAnalysisStage.Retrying : AiAnalysisStage.Analyzing,
                IsRetry
                    ? $"重试通道第 {_retryRequests} 次请求 · {count} 项，等待模型返回。"
                    : $"新项目通道第 {_requests - _retryRequests} 次请求 · {count} 项，模型正在分析。",
                isWaitingForResponse: true);
        }
    }

    public void RequestFinished(bool isRetry)
    {
        lock (_gate)
        {
            _activeRequests = Math.Max(0, _activeRequests - 1);
            if (isRetry) _retryActiveBatchSize = 0;
            else _freshActiveBatchSize = 0;
        }
    }

    public void ResultsReceived(IEnumerable<AiAnalysisResult> results)
    {
        lock (_gate)
        {
            IncludeResults(results);
            Report(IsRetry ? AiAnalysisStage.Retrying : AiAnalysisStage.Analyzing,
                $"已处理 {_results.Count} / {_total} 项，重试 {_retryRequests} 次请求，已补回 {_recoveredPaths.Count} 项。");
        }
    }

    public void RecoveryResultsReceived(IEnumerable<AiAnalysisResult> results)
    {
        var snapshot = results.ToArray();
        lock (_gate)
        {
            foreach (var result in snapshot)
                if (!result.LooksLikeFallback()) _recoveredPaths.Add(result.Path);
            ResultsReceived(snapshot);
        }
    }

    public void Report(AiAnalysisStage stage, string message, bool isWaitingForResponse = false)
    {
        if (progress is null) return;
        lock (_gate)
        {
            progress.Report(new AiAnalysisProgress(stage, _total, _results.Count,
                _analyzed, _cached, _protected, _incomplete, _requests, _batchSize, IsRetry,
                isWaitingForResponse || _activeRequests > 0, message, _shared)
            {
                RetryRequestCount = _retryRequests,
                RetriedCandidateCount = _retriedPaths.Count,
                RecoveredCandidateCount = _recoveredPaths.Count,
                QueuedRetryCount = _queuedRetryCount,
                ActiveRequestCount = _activeRequests,
                FreshActiveBatchSize = _freshActiveBatchSize,
                RetryActiveBatchSize = _retryActiveBatchSize,
            });
        }
    }

    private void AdjustCounts(AiAnalysisResult result, int delta)
    {
        if (result.LooksLikeFallback())
        {
            _incomplete += delta;
            return;
        }
        switch (result.Origin)
        {
            case AiAnalysisResultOrigin.AiAnalyzed: _analyzed += delta; break;
            case AiAnalysisResultOrigin.CacheReused: _cached += delta; break;
            case AiAnalysisResultOrigin.ProtectedByRule: _protected += delta; break;
            case AiAnalysisResultOrigin.SharedExperience: _shared += delta; break;
            default: _incomplete += delta; break;
        }
    }

    // Placed inside BudgetedAiProvider: a rejected budget reservation never increments the
    // request count, and every real attempt (including recovery requests) is observable.
    public IAiProvider Observe(IAiProvider provider) => new ProgressProvider(provider, this);

    private sealed class ProgressProvider(IAiProvider provider, AiAnalysisProgressTracker tracker) : IAiProvider
    {
        public AiProviderKind Kind => provider.Kind;

        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isRetry = tracker.IsRetry;
            tracker.RequestStarted(batch.Records);
            try { return await provider.AnalyzeAsync(batch, options, cancellationToken).ConfigureAwait(false); }
            finally { tracker.RequestFinished(isRetry); }
        }
    }
}

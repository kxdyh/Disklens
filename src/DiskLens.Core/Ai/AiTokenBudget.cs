using System.Text;
using System.Text.Json;
using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

/// <summary>Provider-reported tokens; input already includes any prompt cache hits.</summary>
public sealed record AiTokenUsage(long InputTokens, long OutputTokens, long? ReportedTotalTokens = null)
{
    public long? InputCacheHitTokens { get; init; }
    public long? InputCacheMissTokens { get; init; }
    public bool HasInputOutputBreakdown { get; init; } = true;
    private long Sum => InputTokens > long.MaxValue - OutputTokens
        ? long.MaxValue
        : InputTokens + OutputTokens;
    public long TotalTokens => Math.Max(Sum, ReportedTotalTokens ?? 0);
}

/// <summary>A frozen request and output allowance shared by the budget and HTTP provider.</summary>
public sealed class AiRequestContext(string systemPrompt, string userPayload, int maxOutputTokens)
{
    internal long ReservedTokens { get; init; }
    internal bool ReservationCompleted { get; set; }
    public string SystemPrompt { get; } = systemPrompt;
    public string UserPayload { get; } = userPayload;
    public int MaxOutputTokens { get; } = maxOutputTokens;
    public AiTokenUsage? Usage { get; private set; }
    private readonly object _responseDiagnosticsGate = new();
    private AiResponseDiagnostics _responseDiagnostics = AiResponseDiagnostics.Empty;
    public AiResponseDiagnostics ResponseDiagnostics
    {
        get { lock (_responseDiagnosticsGate) return _responseDiagnostics; }
    }

    public void ReportResponseDiagnostics(AiResponseDiagnostics diagnostics)
    {
        lock (_responseDiagnosticsGate) _responseDiagnostics = _responseDiagnostics.Merge(diagnostics);
    }

    public void ReportUsage(AiTokenUsage usage)
    {
        if (usage.InputTokens >= 0 && usage.OutputTokens >= 0)
        {
            Usage = usage;
        }
    }

    public void ReadOpenAiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // prompt_tokens includes cached input; completion_tokens includes reasoning tokens.
        // Do not add prompt_cache_hit_tokens or completion_tokens_details again.
        if (TryCount(usage, "prompt_tokens", out var input) && TryCount(usage, "completion_tokens", out var output))
        {
            long? hit = TryCount(usage, "prompt_cache_hit_tokens", out var cacheHit) ? cacheHit : null;
            if (hit is null && usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object && TryCount(details, "cached_tokens", out cacheHit))
            {
                hit = cacheHit;
            }
            long? miss = TryCount(usage, "prompt_cache_miss_tokens", out var cacheMiss) ? cacheMiss : null;
            // Invalid cache figures must never make an estimated bill artificially small.
            if (hit > input || miss > input || (hit is not null && miss is not null && hit != input - miss))
            {
                hit = null;
                miss = null;
            }
            hit ??= miss is not null ? input - miss : null;
            miss ??= hit is not null ? input - hit : null;
            ReportUsage(new AiTokenUsage(input, output, TryCount(usage, "total_tokens", out var reportedTotal) ? reportedTotal : null)
            {
                InputCacheHitTokens = hit,
                InputCacheMissTokens = miss,
            });
        }
        else if (TryCount(usage, "total_tokens", out var total))
        {
            ReportUsage(new AiTokenUsage(total, 0) { HasInputOutputBreakdown = false });
        }
    }

    public void ReadOllamaUsage(JsonElement root)
    {
        if (TryCount(root, "prompt_eval_count", out var input) && TryCount(root, "eval_count", out var output))
        {
            ReportUsage(new AiTokenUsage(input, output));
        }
    }

    private static bool TryCount(JsonElement parent, string name, out long count)
    {
        count = 0;
        return parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out count) && count >= 0;
    }
}

public sealed record AiTokenBudgetReport(
    long Limit,
    long AccountedTokens,
    long ProviderReportedTokens,
    long EstimatedTokens,
    int RequestCount,
    bool StoppedForBudget)
{
    public AiResponseDiagnostics ResponseDiagnostics { get; init; } = AiResponseDiagnostics.Empty;
    public long NextRequestRequiredTokens { get; init; }
    public long RemainingTokens => Math.Max(0, Limit - AccountedTokens);
    public long ProviderInputTokens { get; init; }
    public long ProviderOutputTokens { get; init; }
    public long ProviderCacheHitTokens { get; init; }
    public long ProviderCacheMissTokens { get; init; }
    public long ProviderInputTokensWithoutCacheBreakdown { get; init; }
    public bool HasCompleteUsageBreakdown { get; init; }
    public string FormatStatusMessage()
    {
        var mode = EstimatedTokens > 0 ? "含保守预估" : "接口用量";
        var status = StoppedForBudget ? "已达预算或剩余预算不足，已停止后续请求。" : string.Empty;
        return $"本次 Token 预算 {AccountedTokens:N0} / {Limit:N0}（输入+输出，{mode}，{RequestCount} 次请求）。{status}";
    }
}

public sealed record AiAnalysisReport(IReadOnlyList<AiAnalysisResult> Results, AiTokenBudgetReport TokenBudget)
{
    public int RetryRequestCount { get; init; }
    public int RetriedCandidateCount { get; init; }
    public int RecoveredCandidateCount { get; init; }
    public int QueuedRetryCount { get; init; }
    public int ActiveRequestCount { get; init; }
    public int SuccessfulAiCount => Results.Count(result => result.Origin == AiAnalysisResultOrigin.AiAnalyzed && !result.LooksLikeFallback());
    public int UnfinishedCandidateCount => Results.Count(result => result.LooksLikeFallback());
    public int UnsubmittedCandidateCount => Math.Max(0, TotalCandidates - SubmittedCandidateCount
        - Results.Count(result => !result.LooksLikeFallback() && result.Origin is AiAnalysisResultOrigin.CacheReused
            or AiAnalysisResultOrigin.SharedExperience or AiAnalysisResultOrigin.ProtectedByRule));
    /// <summary>A confirmed network loss, including optional cloud lookup or upload; completed results remain usable.</summary>
    public bool NetworkUnavailable { get; init; }
    public string? SharedKnowledgeWarning { get; init; }
    public string? PersistenceWarning { get; init; }
    public int SharedSkippedItems { get; init; }
    /// <summary>Only paths explicitly reported unmatched by a successful cloud query.</summary>
    public IReadOnlyList<string> SharedMissingPaths { get; init; } = [];
    public int TotalCandidates { get; init; }
    /// <summary>Unique candidate paths actually submitted, excluding retries and local/shared results.</summary>
    public int SubmittedCandidateCount { get; init; }
    public int LimitedScopeCount { get; init; }
}

internal sealed class AiTokenBudgetExceededException() : Exception(AiTokenBudgetSession.StoppedReason);

/// <summary>Per-click client budget. Estimates and remote enforcement cannot guarantee a bill cap.</summary>
internal sealed class AiTokenBudgetSession
{
    public const string StoppedReason = "本次 AI 分析已达到 Token 预算，或剩余预算不足以安全发起下一次请求；本项未完成。已完成结果已保留，可调高预算或再次分析。";
    private readonly long _limit;
    private readonly object _gate = new();
    private TaskCompletionSource _reservationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeReservations;
    private bool _isStopped;
    private long _nextRequestRequiredTokens;
    private AiResponseDiagnostics _responseDiagnostics = AiResponseDiagnostics.Empty;
    public string SystemPrompt { get; }
    public string SystemPromptFingerprint { get; }

    public AiTokenBudgetSession(long limit, bool useTokenSavingMode = false)
    {
        _limit = limit > 0 ? limit : 100_000;
        SystemPrompt = AiPromptFactory.CreateSystemPrompt(useTokenSavingMode);
        SystemPromptFingerprint = AiPromptFactory.CreateSystemPromptFingerprint(SystemPrompt);
    }
    private long _reported;
    private long _estimated;
    private long _input;
    private long _output;
    private long _cacheHit;
    private long _cacheMiss;
    private long _unknownCacheInput;
    private bool _completeBreakdown = true;
    private int _requests;
    public bool IsStopped { get { lock (_gate) return _isStopped; } }

    public AiTokenBudgetReport Report { get { lock (_gate) return CreateReport(); } }
    internal void ReportResponseDiagnostics(AiResponseDiagnostics diagnostics)
    {
        lock (_gate) _responseDiagnostics = _responseDiagnostics.Merge(diagnostics);
    }

    private AiTokenBudgetReport CreateReport() => new(_limit, SaturatingAdd(_reported, _estimated), _reported, _estimated, _requests, _isStopped)
    {
        ProviderInputTokens = _input,
        ProviderOutputTokens = _output,
        ProviderCacheHitTokens = _cacheHit,
        ProviderCacheMissTokens = _cacheMiss,
        ProviderInputTokensWithoutCacheBreakdown = _unknownCacheInput,
        HasCompleteUsageBreakdown = _completeBreakdown && _estimated == 0,
        ResponseDiagnostics = _responseDiagnostics,
        NextRequestRequiredTokens = _nextRequestRequiredTokens,
    };

    public AiRequestContext Reserve(AiBatch batch)
    {
        var payload = AiPromptFactory.CreateUserPayload(batch);
        var desiredOutput = AiOutputTokenAllowance.ForRecordCount(batch.Records.Count);
        lock (_gate)
        {
            return ReservePrepared(payload, desiredOutput);
        }
    }

    public async Task<AiRequestContext> ReserveAsync(AiBatch batch, CancellationToken cancellationToken)
    {
        var payload = AiPromptFactory.CreateUserPayload(batch);
        var desiredOutput = AiOutputTokenAllowance.ForRecordCount(batch.Records.Count);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                var required = RequiredTokens(payload, desiredOutput);
                // A parallel request's temporary reservation may be larger than its
                // eventual reported usage. Wait for settlement before stopping both lanes.
                if (!_isStopped && required > _limit - SaturatingAdd(_reported, _estimated)
                    && _activeReservations > 0)
                    changed = _reservationChanged.Task;
                else return ReservePrepared(payload, desiredOutput);
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private long RequiredTokens(string payload, int output) => SaturatingAdd(
        (long)Encoding.UTF8.GetByteCount(SystemPrompt) + Encoding.UTF8.GetByteCount(payload) + 64, output);

    private AiRequestContext ReservePrepared(string payload, int output)
    {
        var required = RequiredTokens(payload, output);
        if (_isStopped || required > _limit - SaturatingAdd(_reported, _estimated))
        {
            _isStopped = true;
            _nextRequestRequiredTokens = required;
            throw new AiTokenBudgetExceededException();
        }
        _estimated = SaturatingAdd(_estimated, required);
        _activeReservations++;
        _requests++;
        return new AiRequestContext(SystemPrompt, payload, output) { ReservedTokens = required };
    }

    public void Complete(AiRequestContext context, long reservation)
    {
        lock (_gate)
        {
            if (context.ReservationCompleted) return;
            context.ReservationCompleted = true;
            reservation = context.ReservedTokens > 0 ? context.ReservedTokens : reservation;
            _activeReservations = Math.Max(0, _activeReservations - 1);
            _responseDiagnostics = _responseDiagnostics.Merge(context.ResponseDiagnostics);
            if (context.Usage is { } usage)
            {
                _estimated -= reservation;
                _reported = SaturatingAdd(_reported, usage.TotalTokens);
                _completeBreakdown &= usage.HasInputOutputBreakdown && usage.TotalTokens == usage.InputTokens + usage.OutputTokens;
                _input = SaturatingAdd(_input, usage.InputTokens);
                _output = SaturatingAdd(_output, usage.OutputTokens);
                if (usage.InputCacheHitTokens is { } hit && usage.InputCacheMissTokens is { } miss)
                {
                    _cacheHit = SaturatingAdd(_cacheHit, hit);
                    _cacheMiss = SaturatingAdd(_cacheMiss, miss);
                }
                else
                {
                    _unknownCacheInput = SaturatingAdd(_unknownCacheInput, usage.InputTokens);
                }
            }

            // Missing usage, failed parsing, timeout and uncertain transport outcomes retain the
            // full reservation. Never silently refund a request that may have reached a server.
            _isStopped |= _reported >= _limit || _activeReservations == 0
                && SaturatingAdd(_reported, _estimated) >= _limit;
            var completed = _reservationChanged;
            _reservationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completed.TrySetResult();
        }
    }

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal sealed class BudgetedAiProvider(IAiProvider provider, AiTokenBudgetSession budget) : IAiProvider
{
    public AiProviderKind Kind => provider.Kind;

    public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await budget.ReserveAsync(batch, cancellationToken).ConfigureAwait(false);
        try
        {
            return await provider.AnalyzeAsync(batch with { RequestContext = context }, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            budget.Complete(context, context.ReservedTokens);
        }
    }
}

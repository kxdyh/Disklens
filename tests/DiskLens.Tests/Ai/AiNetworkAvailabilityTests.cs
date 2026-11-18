using System.Net;
using System.Net.Sockets;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiNetworkAvailabilityTests
{
    [Fact]
    public async Task Offline_preflight_does_not_submit_any_remote_AI_request()
    {
        var provider = new Provider(AiProviderKind.OpenAiCompatible);
        var report = await Service(provider, false).AnalyzeWithReportAsync(Root(2), Options(), RemoteOptions);

        Assert.Equal(0, provider.Calls);
        Assert.True(report.NetworkUnavailable);
        Assert.Equal(0, report.SubmittedCandidateCount);
        Assert.All(report.Results, result => Assert.Equal(NetworkFailureClassifier.OfflineMessage, result.Reason));
    }

    [Fact]
    public async Task Losing_the_network_while_confirmation_is_open_blocks_the_accepted_submission()
    {
        var network = new Network(true);
        var provider = new Provider(AiProviderKind.OpenAiCompatible);
        var confirmed = false;
        var report = await Service(provider, network).AnalyzeWithReportAsync(Root(2), Options(), RemoteOptions,
            confirmSubmission: (preview, _) =>
            {
                Assert.Equal(0, provider.Calls);
                confirmed = true;
                network.IsNetworkAvailable = false;
                return Task.FromResult(true);
            });

        Assert.True(confirmed);
        Assert.Equal(0, provider.Calls);
        Assert.True(report.NetworkUnavailable);
        Assert.Equal(0, report.SubmittedCandidateCount);
        Assert.Equal(0, report.TokenBudget.RequestCount);
        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, result => Assert.Equal(NetworkFailureClassifier.OfflineMessage, result.Reason));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Losing_the_network_after_the_only_batch_starts_reports_offline_for_transport_timeouts(bool httpTimeoutCancellation)
    {
        var network = new Network(true);
        var provider = new Provider(AiProviderKind.OpenAiCompatible)
        {
            FailAt = 1,
            BeforeResponse = _ => network.IsNetworkAvailable = false,
            Failure = httpTimeoutCancellation
                ? new TaskCanceledException("synthetic HTTP timeout", new TimeoutException("synthetic transport timeout"))
                : new TimeoutException("synthetic transport timeout"),
        };
        // One batch is essential: a later batch must not hide the missed failure
        // classification by observing the now-offline interface in its preflight.
        var report = await Service(provider, network).AnalyzeWithReportAsync(Root(2),
            Options() with { MaxAiBatchSize = 2 }, RemoteOptions);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, report.TokenBudget.RequestCount);
        Assert.Equal(2, report.SubmittedCandidateCount);
        Assert.True(report.NetworkUnavailable);
        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, result =>
        {
            Assert.True(result.LooksLikeFallback());
            Assert.Equal(NetworkFailureClassifier.OfflineMessage, result.Reason);
        });
    }

    [Theory]
    [InlineData(AiProviderKind.Ollama, "http://localhost:11434")]
    [InlineData(AiProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1")]
    public async Task Local_model_endpoints_remain_usable_without_a_network_interface(AiProviderKind kind, string endpoint)
    {
        var provider = new Provider(kind);
        var report = await Service(provider, false).AnalyzeWithReportAsync(Root(2), Options(), new(kind, endpoint, "fixture", null));

        Assert.Equal(2, provider.Calls);
        Assert.False(report.NetworkUnavailable);
        Assert.All(report.Results, result => Assert.False(result.LooksLikeFallback()));
    }

    [Fact]
    public async Task Losing_the_network_mid_analysis_preserves_completed_results_and_stops_later_requests()
    {
        var provider = new Provider(AiProviderKind.OpenAiCompatible)
        { FailAt = 2, Failure = new HttpRequestException("synthetic network down", new SocketException((int)SocketError.NetworkDown)) };
        var report = await Service(provider, true).AnalyzeWithReportAsync(Root(3), Options(), RemoteOptions);

        Assert.True(report.NetworkUnavailable);
        Assert.Equal(2, provider.Calls);
        Assert.Single(report.Results, result => !result.LooksLikeFallback());
        Assert.Equal(2, report.Results.Count(result => result.Reason == NetworkFailureClassifier.OfflineMessage));
    }

    [Fact]
    public async Task Caller_cancellation_with_a_lost_network_preserves_completed_results_without_claiming_offline()
    {
        using var cancellation = new CancellationTokenSource();
        var network = new Network(true);
        var provider = new Provider(AiProviderKind.OpenAiCompatible)
        {
            FailAt = 2,
            BeforeResponse = call =>
            {
                if (call != 2) return;
                network.IsNetworkAvailable = false;
                cancellation.Cancel();
            },
            Failure = new TaskCanceledException("synthetic caller cancellation",
                new TimeoutException("synthetic concurrent transport timeout"), cancellation.Token),
        };

        var canceled = await Assert.ThrowsAsync<AiAnalysisCanceledException>(() =>
            Service(provider, network).AnalyzeWithReportAsync(Root(3), Options(), RemoteOptions, cancellation.Token));

        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(3, canceled.PartialResults.Count);
        Assert.Single(canceled.PartialResults, result => !result.LooksLikeFallback());
        Assert.All(canceled.PartialResults, result => Assert.NotEqual(NetworkFailureClassifier.OfflineMessage, result.Reason));
        Assert.All(canceled.PartialResults.Where(result => result.LooksLikeFallback()),
            result => Assert.Contains("取消", result.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Network_failure_during_response_recovery_reaches_the_report()
    {
        var provider = new Provider(AiProviderKind.OpenAiCompatible)
        { ReturnEmptyFirst = true, FailAt = 2, Failure = new HttpRequestException("synthetic no route", new SocketException((int)SocketError.NetworkUnreachable)) };
        var report = await Service(provider, true).AnalyzeWithReportAsync(Root(1), Options(), RemoteOptions);

        Assert.True(report.NetworkUnavailable);
        Assert.Equal(2, provider.Calls);
        Assert.Contains(NetworkFailureClassifier.OfflineMessage, Assert.Single(report.Results).Reason);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Service_failures_keep_their_existing_reason_and_do_not_claim_the_computer_is_offline(int status)
    {
        var provider = new Provider(AiProviderKind.OpenAiCompatible)
        { FailAt = 1, Failure = new HttpRequestException("synthetic service failure", null, (HttpStatusCode)status) };
        var report = await Service(provider, true).AnalyzeWithReportAsync(Root(1), Options(), RemoteOptions);

        Assert.False(report.NetworkUnavailable);
        Assert.NotEqual(NetworkFailureClassifier.OfflineMessage, Assert.Single(report.Results).Reason);
        Assert.True(report.Results[0].LooksLikeFallback());
    }

    private static readonly AiProviderOptions RemoteOptions = new(AiProviderKind.OpenAiCompatible, "https://test.invalid", "fixture", "synthetic");
    private static ScanSessionOptions Options() => new(@"E:\", AnalysisThresholdBytes: 1, MaxAiBatchSize: 1, UseTokenSavingMode: true);
    private static AiAnalysisService Service(Provider provider, bool available) => Service(provider, new Network(available));
    private static AiAnalysisService Service(Provider provider, Network network) => new([provider],
        new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), networkAvailability: network);
    private static FileSystemNode Root(int count)
    {
        var files = Enumerable.Range(0, count).Select(index => new FileSystemNode($@"E:\DiskLens-test\file{index}.bin",
            $"file{index}.bin", ".bin", 1024, FileAttributes.Normal, null, null, false, false, [], [], 1)).ToArray();
        return new(@"E:\", "E:", "", count * 1024, FileAttributes.Directory, null, null, true, false, [], files, count, 1);
    }
    private sealed class Network(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable { get; set; } = available; }
    private sealed class Provider(AiProviderKind kind) : IAiProvider
    {
        public AiProviderKind Kind => kind;
        public int Calls { get; private set; }
        public int FailAt { get; init; } = int.MaxValue;
        public Exception? Failure { get; init; }
        public bool ReturnEmptyFirst { get; init; }
        public Action<int>? BeforeResponse { get; init; }
        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeResponse?.Invoke(Calls);
            if (Calls >= FailAt && Failure is { } failure) return Task.FromException<IReadOnlyList<AiAnalysisResult>>(failure);
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(ReturnEmptyFirst && Calls == 1 ? [] : batch.Records.Select(record =>
                new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "合成用途说明", .9)).ToArray());
        }
    }
}

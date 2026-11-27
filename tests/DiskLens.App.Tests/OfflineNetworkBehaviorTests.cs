using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Offline_AI_preflight_sends_no_requests_disables_cloud_experience_and_shows_the_exact_message() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(false));
        fixture.Enable();
        fixture.ApplyScan();

        RunNetworkAnalysis(fixture);

        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.False(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.False(fixture.Vm.IsAnalyzing);
    });

    [Fact]
    public void Offline_cloud_lookup_preflight_does_not_query_or_start_AI_and_rolls_back_consent() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false, networkAvailability: new OfflineNetwork(false));
        fixture.Enable();
        fixture.ApplyScan();

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());

        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.False(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
    });

    [Theory]
    [InlineData(0, true)]
    [InlineData(503, false)]
    public void Cloud_lookup_rolls_back_only_for_a_confirmed_network_loss(int status, bool offline) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false, networkAvailability: new OfflineNetwork(true));
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Client.Failure = status == 0
            ? new HttpRequestException("synthetic network loss", new SocketException((int)SocketError.NetworkUnreachable))
            : new HttpRequestException("synthetic server failure", null, (HttpStatusCode)status);

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());

        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.Equal(!offline, fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        if (offline) Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        else Assert.Contains("失败", fixture.Vm.StatusText);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.Empty(fixture.Provider.RequestedPaths);
    });

    [Fact]
    public void Network_loss_inside_AI_batch_survives_the_report_and_closes_cloud_experience() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(true));
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Provider.Failure = new HttpRequestException("synthetic network loss", new SocketException((int)SocketError.NetworkDown));

        RunNetworkAnalysis(fixture);

        Assert.NotEmpty(fixture.Provider.RequestedPaths);
        Assert.False(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.False(fixture.Vm.IsAnalyzing);
    });

    [Fact]
    public void Local_compatible_AI_still_runs_when_cloud_experience_is_closed_for_missing_network() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(false));
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Vm.AiEndpoint = "http://127.0.0.1:1234/v1";

        RunNetworkAnalysis(fixture);

        Assert.NotEmpty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.False(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        Assert.Contains(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => node.Analysis is { } analysis && !analysis.LooksLikeFallback());
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
    });

    [Fact]
    public void Network_loss_only_during_upload_keeps_AI_results_consent_and_queue_for_background_retry() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(true));
        var now = DateTimeOffset.UtcNow;
        fixture.Client.PathIndex = new SharedPathIndex(1, [], true, now, now.AddMinutes(4));
        fixture.Enable();
        fixture.ApplyScan();
        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.Client.ContributionFailure = new HttpRequestException("synthetic upload network loss",
            new SocketException((int)SocketError.NetworkDown));

        RunNetworkAnalysis(fixture);
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.True(fixture.Client.ContributionCalls > 0);
        Assert.True(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        var upload = fixture.Runtime.Outbox.GetUploadStatusAsync().GetAwaiter().GetResult();
        Assert.Equal(SharedUploadStage.Failed, upload.Stage);
        Assert.True(upload.Pending > 0);
        Assert.Equal(0, upload.AutomaticRetriesUsed);
        Assert.NotNull(upload.NextRetryAt);
        Assert.False(upload.AutomaticRetriesExhausted);
        Assert.Contains(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => node.Analysis is { } analysis && !analysis.LooksLikeFallback());
        Assert.Contains("等待后台重试", FooterDetail(fixture.Vm));
        Assert.NotEqual(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.False(fixture.Vm.IsOfflineAlertVisible);
        Assert.False(fixture.Vm.IsFooterCloudActivityVisible);
        Assert.False(fixture.Vm.IsAnalyzing);
        Assert.True(fixture.Vm.AnalyzeCommand.CanExecute(null));
    });

    private static void RunNetworkAnalysis(Fixture fixture)
    {
        Assert.True(fixture.Vm.AnalyzeCommand.CanExecute(null));
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Vm.AnalyzeCommand.CanExecute(null) && !fixture.Vm.IsAnalyzing);
    }

    private sealed class OfflineNetwork(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable => available; }
}

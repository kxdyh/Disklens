using DiskLens.Core.Models;
using DiskLens.Core.Networking;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disconnecting_while_the_submission_confirmation_is_open_shows_offline_without_sending_AI(bool sharing) => OnSta(() =>
    {
        var network = new SubmissionNetwork();
        using var fixture = new Fixture(configured: true, networkAvailability: network);
        if (sharing) fixture.Enable();
        fixture.ApplyScan();
        var ready = new TaskCompletionSource<AiAnalysisSubmissionPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Vm.ConfirmAiSubmissionAsync = (preview, token) =>
        {
            ready.TrySetResult(preview);
            return decision.Task.WaitAsync(token);
        };
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => ready.Task.IsCompleted);
        Assert.NotEmpty(ready.Task.Result.Items);
        Assert.Empty(fixture.Provider.RequestedPaths);
        network.Available = false;
        decision.SetResult(true);
        WaitUntil(() => !fixture.Vm.IsAnalyzing && fixture.Vm.AnalyzeCommand.CanExecute(null));
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.True(fixture.Vm.IsOfflineAlertVisible);
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.False(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
    });

    [Fact]
    public void Disconnecting_after_submission_and_HTTP_timeout_shows_the_offline_alert() => OnSta(() =>
    {
        var network = new SubmissionNetwork();
        using var fixture = new Fixture(configured: true, networkAvailability: network);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Vm.ConfirmAiSubmissionAsync = (_, _) => Task.FromResult(true);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeResponse = response.Task;
        fixture.Provider.Failure = new TaskCanceledException("HTTP request timed out", new TimeoutException());
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Provider.RequestedPaths.Count > 0);
        network.Available = false;
        response.SetResult();
        WaitUntil(() => !fixture.Vm.IsAnalyzing && fixture.Vm.AnalyzeCommand.CanExecute(null));
        Assert.True(fixture.Vm.IsOfflineAlertVisible);
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.False(fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
    });

    private sealed class SubmissionNetwork : INetworkAvailability
    {
        public bool Available { get; set; } = true;
        public bool IsNetworkAvailable => Available;
    }
}

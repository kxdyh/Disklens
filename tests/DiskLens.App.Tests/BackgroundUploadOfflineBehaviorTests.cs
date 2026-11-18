using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.ViewModels;
using DiskLens.Core.Networking;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(503, false)]
    public void Background_upload_cycle_only_closes_sharing_and_notifies_the_UI_for_confirmed_network_loss(
        int status, bool offline) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(true));
        fixture.Enable();
        fixture.ApplyScan(withExistingAnalysis: true);
        var snapshot = fixture.Vm.ScanRoot!;
        var originalAnalysis = snapshot.Children[0].Analysis;
        var ownerThread = Environment.CurrentManagedThreadId;
        var notificationThreads = new List<int>();
        fixture.Vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.StatusText)
                or nameof(MainWindowViewModel.IsOfflineAlertVisible)
                or nameof(MainWindowViewModel.ShowSharedExperienceAnalysis))
                notificationThreads.Add(Environment.CurrentManagedThreadId);
        };
        fixture.Client.ContributionFailure = offline
            ? new HttpRequestException("synthetic background network loss",
                new SocketException((int)SocketError.NetworkDown))
            : new HttpRequestException("synthetic background server failure", null, (HttpStatusCode)status);
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("background-offline-" + status)]));

        // Exercise the same cycle called by the production worker without a 30-second timer wait.
        var cycle = typeof(SharedKnowledgeRuntime).GetMethod("FlushPendingUploadsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Complete(Task.Run(async () =>
            await (Task)cycle.Invoke(fixture.Runtime, [CancellationToken.None])!));
        WaitUntil(() => fixture.Client.ContributionCalls > 0
            && (!offline || fixture.Vm.IsOfflineAlertVisible && !fixture.Vm.ShowSharedExperienceAnalysis));
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Equal(!offline, fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult().Enabled);
        Assert.Equal(offline ? 0 : 1, fixture.Runtime.Outbox.PendingCountAsync().GetAwaiter().GetResult());
        Assert.Equal(offline, fixture.Vm.IsOfflineAlertVisible);
        Assert.Equal(!offline, fixture.Vm.ShowSharedExperienceAnalysis);
        Assert.Same(snapshot, fixture.Vm.ScanRoot);
        Assert.Same(originalAnalysis, fixture.Vm.ScanRoot!.Children[0].Analysis);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.NotEmpty(notificationThreads);
        Assert.All(notificationThreads, thread => Assert.Equal(ownerThread, thread));
        if (offline)
        {
            Assert.Null(fixture.Runtime.Outbox.LastError);
            Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
            Assert.Equal(NetworkFailureClassifier.OfflineMessage, fixture.Vm.SharedExperienceSummaryText);
            Assert.DoesNotContain("待上传", fixture.Vm.StatusText);
        }
        else
        {
            Assert.Contains("上传至云端经验库失败", fixture.Vm.StatusText);
            Assert.Contains("待上传 1 项", fixture.Vm.SharedExperienceSummaryText);
        }
    });

    [Fact]
    public void Manual_sharing_suspension_does_not_report_a_network_outage() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(true));
        fixture.Enable();
        fixture.ApplyScan();

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);

        Assert.False(fixture.Vm.IsOfflineAlertVisible);
        Assert.NotEqual(NetworkFailureClassifier.OfflineMessage, fixture.Vm.StatusText);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });
}

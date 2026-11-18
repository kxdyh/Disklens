using DiskLens.App.Models;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Fact]
    public void Every_scan_task_keeps_the_windows_submission_confirmation_callback_after_tab_switches() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        Layout(scene.Window, 1000, 640);
        var home = scene.Workspace.ActiveViewModel;
        Assert.Same(scene.Window, Assert.IsType<Func<AiAnalysisSubmissionPreview, CancellationToken, Task<bool>>>(
            home.ConfirmAiSubmissionAsync).Target);

        for (var index = 0; index < 3; index++)
        {
            scene.AddCompletedTask("submission-routing-" + index);
            Layout(scene.Window, 1000, 640);
            var current = scene.Workspace.ActiveViewModel;
            Assert.Same(current, scene.Window.DataContext);
            Assert.Same(scene.Window, Assert.IsType<Func<AiAnalysisSubmissionPreview, CancellationToken, Task<bool>>>(
                current.ConfirmAiSubmissionAsync).Target);
        }

        foreach (var tab in scene.Workspace.TaskTabs.Reverse())
        {
            scene.Workspace.SelectTab(tab);
            Layout(scene.Window, 1000, 640);
            foreach (var task in scene.Workspace.TaskTabs)
            {
                var callback = Assert.IsType<Func<AiAnalysisSubmissionPreview, CancellationToken, Task<bool>>>(
                    task.ViewModel.ConfirmAiSubmissionAsync);
                Assert.Same(scene.Window, callback.Target);
                Assert.Equal("ConfirmAiSubmissionAsync", callback.Method.Name);
            }
        }
    });
}

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analysis_waits_for_the_actual_submission_callback_once_and_rejection_sends_no_AI_or_contribution(
        bool accepted) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, networkAvailability: new OfflineNetwork(true));
        fixture.Enable();
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<AiAnalysisSubmissionPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        fixture.Vm.ConfirmAiSubmissionAsync = (preview, token) =>
        {
            Interlocked.Increment(ref confirmations);
            ready.TrySetResult(preview);
            return decision.Task.WaitAsync(token);
        };

        Assert.True(fixture.Vm.AnalyzeCommand.CanExecute(null));
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => ready.Task.IsCompleted);
        var preview = ready.Task.GetAwaiter().GetResult();
        Assert.Equal(snapshot.Path, preview.RootPath);
        Assert.NotEmpty(preview.Items);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.True(fixture.Vm.IsAnalyzing);
        decision.SetResult(accepted);
        WaitUntil(() => !fixture.Vm.IsAnalyzing && fixture.Vm.AnalyzeCommand.CanExecute(null));

        Assert.Equal(1, confirmations);
        if (accepted)
        {
            Assert.Equal(preview.Items.Select(item => item.Path).OrderBy(path => path),
                fixture.Provider.RequestedPaths.OrderBy(path => path));
            Assert.True(fixture.Client.ContributionCalls > 0);
        }
        else
        {
            Assert.Empty(fixture.Provider.RequestedPaths);
            Assert.Equal(0, fixture.Client.ContributionCalls);
            Assert.Same(snapshot, fixture.Vm.ScanRoot);
            Assert.Contains("已取消", fixture.Vm.StatusText);
        }
    });
}

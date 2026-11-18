using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.App.Tests;

// Settings stores use a process environment override, shared with the existing AI tests.
[Collection("Isolated AI settings")]
public sealed class ScanWorkspaceBehaviorTests
{
    [Fact]
    public void Home_is_fixed_selected_initially_and_cannot_be_closed() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var workspace = fixture.Workspace;
        var home = Assert.Single(workspace.TaskTabs);
        Assert.Equal(ScanTaskState.Home, home.State);
        Assert.False(home.CanClose);
        Assert.True(home.IsSelected);
        Assert.Same(home, workspace.ActiveTab);
        Assert.Same(workspace.HomeViewModel, workspace.ActiveViewModel);
        Assert.False(workspace.CloseTabCommand.CanExecute(home));

        workspace.CloseTab(home);
        workspace.CloseTabCommand.Execute(home);

        Assert.Same(home, Assert.Single(workspace.TaskTabs));
        var task = fixture.Start("first");
        Assert.Same(home, workspace.TaskTabs[0]);
        workspace.CloseTab(task);
        fixture.Scanner.ReleaseAll();
        Complete(task.Completion);
        Assert.Same(home, workspace.ActiveTab);
        Assert.True(home.IsSelected);
    });

    [Fact]
    public void Disposing_the_workspace_cancels_all_running_and_queued_tasks_without_starting_waiters() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var tasks = Enumerable.Range(1, 5).Select(index => fixture.Start($"scan-{index}")).ToArray();
        var running = tasks.Take(3).Select(fixture.Scanner.For).ToArray();
        var startedBeforeDispose = fixture.Scanner.StartedPaths;

        fixture.Workspace.Dispose();
        fixture.Workspace.Dispose();
        PumpDispatcher();

        Assert.Empty(fixture.Workspace.TaskTabs);
        Assert.All(running, invocation => Assert.True(invocation.Token.IsCancellationRequested));
        Assert.All(tasks.Skip(3), tab =>
        {
            Assert.True(tab.Completion.IsCompleted);
            Assert.Equal(ScanTaskState.Canceled, tab.State);
            Assert.False(tab.ViewModel.IsScanQueued);
        });
        Assert.Equal(3, fixture.Workspace.ActiveScanCount);
        Assert.Equal(0, fixture.Workspace.QueuedScanCount);
        Assert.Equal(startedBeforeDispose, fixture.Scanner.StartedPaths);

        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(tasks.Select(tab => tab.Completion)));

        Assert.Equal(0, fixture.Workspace.ActiveScanCount);
        Assert.Equal(startedBeforeDispose, fixture.Scanner.StartedPaths);
        Assert.All(tasks, tab =>
        {
            Assert.Equal(ScanTaskState.Canceled, tab.State);
            Assert.Null(tab.ViewModel.ScanRoot);
            Assert.Null(tab.ViewModel.CurrentRoot);
        });
        Complete(Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Workspace.StartScanAsync(tasks[0].Path)));
    });

    [Fact]
    public void First_run_guide_moves_from_Home_to_the_new_scan_and_resumes_at_the_result_step() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture(hasCompletedGuide: false);
        var home = fixture.Workspace.HomeViewModel;
        Assert.True(home.IsHomeWorkspace);
        Assert.False(home.HasCompletedExplorerGuide);
        Assert.True(home.IsExplorerGuideOpen);
        Assert.Equal(0, home.ExplorerGuideStep);
        home.ScanCommand.Execute(null);
        Assert.True(home.IsScanLocationPickerOpen);
        Assert.Equal(1, home.ExplorerGuideStep);
        var path = Path.Combine(fixture.DataPath, "first-guided-scan");
        Directory.CreateDirectory(path);

        home.SelectScanRootCommand.Execute(path);
        WaitUntil(() => fixture.Workspace.TaskTabs.Count == 2);

        var task = fixture.Workspace.ActiveTab;
        Assert.NotSame(home, task.ViewModel);
        Assert.False(home.IsScanLocationPickerOpen);
        Assert.False(home.IsExplorerGuideOpen);
        Assert.Equal(0, home.ExplorerGuideStep);
        Assert.Null(home.ScanRoot);
        Assert.True(task.ViewModel.IsScanning);
        Assert.False(task.ViewModel.IsExplorerGuideOpen);

        fixture.Scanner.For(task).Release();
        Complete(task.Completion);

        Assert.Equal(ScanTaskState.Completed, task.State);
        Assert.Same(task.ViewModel, fixture.Workspace.ActiveViewModel);
        Assert.NotNull(task.ViewModel.ScanRoot);
        Assert.True(task.ViewModel.IsExplorerGuideOpen);
        Assert.Equal(2, task.ViewModel.ExplorerGuideStep);
        Assert.True(task.ViewModel.IsFocusModeToggleVisible);
        Assert.False(home.IsExplorerGuideOpen);
        Assert.Null(home.ScanRoot);

        task.ViewModel.ToggleFocusMode();
        Assert.True(task.ViewModel.IsFocusMode);
        Assert.False(task.ViewModel.IsExplorerGuideOpen);
        Assert.False(home.IsExplorerGuideOpen);
    });

    [Fact]
    public void Scan_navigation_notice_opens_only_for_its_task_and_closes_by_command_switch_or_scan_completion() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var first = fixture.Start("first");
        var second = fixture.Start("second");
        var home = fixture.Workspace.HomeViewModel;
        Assert.False(home.ShowScanNavigationNoticeCommand.CanExecute(null));
        home.ShowScanNavigationNoticeCommand.Execute(null);
        Assert.False(home.IsScanNavigationNoticeOpen);
        fixture.Workspace.SelectTab(first);
        Assert.True(first.ViewModel.ShowScanNavigationNoticeCommand.CanExecute(null));

        first.ViewModel.ShowScanNavigationNoticeCommand.Execute(null);
        Assert.True(first.ViewModel.IsScanNavigationNoticeOpen);
        Assert.False(second.ViewModel.IsScanNavigationNoticeOpen);
        // The window's Escape handler and the modal close action invoke this same command.
        first.ViewModel.CloseScanNavigationNoticeCommand.Execute(null);
        Assert.False(first.ViewModel.IsScanNavigationNoticeOpen);
        Assert.True(first.ViewModel.IsScanning);
        Assert.False(fixture.Scanner.For(first).Token.IsCancellationRequested);

        first.ViewModel.ShowScanNavigationNoticeCommand.Execute(null);
        fixture.Workspace.SelectTab(second);
        Assert.False(first.ViewModel.IsScanNavigationNoticeOpen);
        Assert.False(second.ViewModel.IsScanNavigationNoticeOpen);
        fixture.Workspace.SelectTab(first);
        first.ViewModel.ShowScanNavigationNoticeCommand.Execute(null);
        Assert.True(first.ViewModel.IsScanNavigationNoticeOpen);
        fixture.Scanner.For(first).Release();
        Complete(first.Completion);

        Assert.False(first.ViewModel.IsScanning);
        Assert.False(first.ViewModel.IsScanNavigationNoticeOpen);
        Assert.False(first.ViewModel.ShowScanNavigationNoticeCommand.CanExecute(null));
        first.ViewModel.ShowScanNavigationNoticeCommand.Execute(null);
        Assert.False(first.ViewModel.IsScanNavigationNoticeOpen);
        Assert.True(second.ViewModel.IsScanning);
        Assert.False(fixture.Scanner.For(second).Token.IsCancellationRequested);
    });

    [Fact]
    public void Only_three_scans_run_and_waiting_scans_start_in_FIFO_order() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var tabs = Enumerable.Range(1, 5).Select(index => fixture.Start($"scan-{index}")).ToArray();
        var workspace = fixture.Workspace;
        Assert.Equal(3, workspace.ActiveScanCount);
        Assert.Equal(2, workspace.QueuedScanCount);
        Assert.Equal(tabs.Take(3).Select(tab => tab.Path), fixture.Scanner.StartedPaths);
        Assert.All(tabs.Skip(3), tab => Assert.Equal(ScanTaskState.Queued, tab.State));
        Assert.All(tabs.Skip(3), tab =>
        {
            Assert.True(tab.ViewModel.IsScanQueued);
            Assert.False(tab.ViewModel.IsScanning);
            Assert.True(tab.ViewModel.HasScanWorkspace);
            Assert.True(tab.ViewModel.ShowCancelAction);
            Assert.True(tab.ViewModel.CancelScanCommand.CanExecute(null));
        });
        Assert.All(tabs, tab => Assert.False(tab.Completion.IsCompleted));

        fixture.Scanner.For(tabs[1]).Release();
        Complete(tabs[1].Completion);
        Assert.Equal(ScanTaskState.Completed, tabs[1].State);
        Assert.Equal(tabs.Take(4).Select(tab => tab.Path), fixture.Scanner.StartedPaths);
        Assert.False(tabs[3].ViewModel.IsScanQueued);
        Assert.True(tabs[3].ViewModel.IsScanning);
        Assert.Equal(3, workspace.ActiveScanCount);
        Assert.Equal(1, workspace.QueuedScanCount);

        fixture.Scanner.For(tabs[0]).Release();
        Complete(tabs[0].Completion);
        Assert.Equal(tabs.Select(tab => tab.Path), fixture.Scanner.StartedPaths);
        Assert.Equal(3, workspace.ActiveScanCount);
        Assert.Equal(0, workspace.QueuedScanCount);
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(tabs.Select(tab => tab.Completion)));
        Assert.Equal(0, workspace.ActiveScanCount);
        Assert.Equal(3, fixture.Scanner.PeakActiveCount);
    });

    [Fact]
    public void Closing_a_queued_tab_never_starts_it_and_keeps_the_next_waiter() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var running = Enumerable.Range(1, 3).Select(index => fixture.Start($"running-{index}")).ToArray();
        var removed = fixture.Start("removed-waiter");
        var next = fixture.Start("next-waiter");

        fixture.Workspace.CloseTabCommand.Execute(removed);

        Assert.DoesNotContain(removed, fixture.Workspace.TaskTabs);
        Assert.True(removed.Completion.IsCompleted);
        Assert.Equal(1, fixture.Workspace.QueuedScanCount);
        Assert.DoesNotContain(removed.Path, fixture.Scanner.StartedPaths);
        fixture.Scanner.For(running[0]).Release();
        Complete(running[0].Completion);
        Assert.Equal(next.Path, fixture.Scanner.StartedPaths.Last());
        Assert.DoesNotContain(removed.Path, fixture.Scanner.StartedPaths);
        Assert.Equal(ScanTaskState.Scanning, next.State);
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(running.Select(tab => tab.Completion).Append(next.Completion)));
    });

    [Fact]
    public void Closing_a_running_tab_cancels_only_it_and_waits_for_actual_exit_before_reusing_the_slot() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var running = Enumerable.Range(1, 3).Select(index => fixture.Start($"running-{index}")).ToArray();
        var queued = fixture.Start("queued");
        var canceled = fixture.Scanner.For(running[1]);

        fixture.Workspace.CloseTab(running[1]);
        PumpDispatcher();

        Assert.DoesNotContain(running[1], fixture.Workspace.TaskTabs);
        Assert.True(canceled.Token.IsCancellationRequested);
        Assert.False(fixture.Scanner.For(running[0]).Token.IsCancellationRequested);
        Assert.False(fixture.Scanner.For(running[2]).Token.IsCancellationRequested);
        Assert.False(running[1].Completion.IsCompleted);
        Assert.Equal(3, fixture.Workspace.ActiveScanCount);
        Assert.Equal(ScanTaskState.Queued, queued.State);
        Assert.DoesNotContain(queued.Path, fixture.Scanner.StartedPaths);

        canceled.Release();
        Complete(running[1].Completion);
        Assert.Equal(ScanTaskState.Canceled, running[1].State);
        Assert.Equal(queued.Path, fixture.Scanner.StartedPaths.Last());
        Assert.Equal(3, fixture.Workspace.ActiveScanCount);
        Assert.Equal(3, fixture.Scanner.PeakActiveCount);
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(running.Select(tab => tab.Completion).Append(queued.Completion)));
        Assert.Equal(ScanTaskState.Completed, running[0].State);
        Assert.Equal(ScanTaskState.Completed, running[2].State);
        Assert.Equal(ScanTaskState.Completed, queued.State);
    });

    [Fact]
    public void A_failed_scan_preserves_other_tasks_and_starts_the_oldest_waiter() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var running = Enumerable.Range(1, 3).Select(index => fixture.Start($"running-{index}")).ToArray();
        var queued = fixture.Start("queued");

        fixture.Scanner.For(running[0]).Fail(new IOException("Synthetic scan failure"));
        Complete(running[0].Completion);

        Assert.Equal(ScanTaskState.Failed, running[0].State);
        Assert.Contains(running[0], fixture.Workspace.TaskTabs);
        Assert.Equal(queued.Path, fixture.Scanner.StartedPaths.Last());
        Assert.False(fixture.Scanner.For(running[1]).Token.IsCancellationRequested);
        Assert.False(fixture.Scanner.For(running[2]).Token.IsCancellationRequested);
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(running.Select(tab => tab.Completion).Append(queued.Completion)));
        Assert.Equal(0, fixture.Workspace.ActiveScanCount);
        Assert.Equal(0, fixture.Workspace.QueuedScanCount);
    });

    [Fact]
    public void Cancel_from_a_task_keeps_its_tab_and_does_not_cancel_its_neighbors() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var first = fixture.Start("first");
        var second = fixture.Start("second");

        first.ViewModel.CancelScanCommand.Execute(null);

        Assert.Contains(first, fixture.Workspace.TaskTabs);
        Assert.True(fixture.Scanner.For(first).Token.IsCancellationRequested);
        Assert.False(fixture.Scanner.For(second).Token.IsCancellationRequested);
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(first.Completion, second.Completion));
        Assert.Equal(ScanTaskState.Canceled, first.State);
        Assert.Equal(ScanTaskState.Completed, second.State);
    });

    [Fact]
    public void Canceling_a_queued_task_keeps_its_canceled_tab_and_never_runs_the_scanner() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var running = Enumerable.Range(1, 3).Select(index => fixture.Start($"running-{index}")).ToArray();
        var queued = fixture.Start("queued");

        queued.ViewModel.CancelScanCommand.Execute(null);
        Complete(queued.Completion);

        Assert.Contains(queued, fixture.Workspace.TaskTabs);
        Assert.Equal(ScanTaskState.Canceled, queued.State);
        Assert.False(queued.ViewModel.IsScanQueued);
        Assert.False(queued.ViewModel.IsScanning);
        Assert.True(queued.ViewModel.HasScanWorkspace);
        Assert.False(queued.ViewModel.CancelScanCommand.CanExecute(null));
        Assert.Equal(0, fixture.Workspace.QueuedScanCount);
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(running.Select(tab => tab.Completion)));
        Assert.DoesNotContain(queued.Path, fixture.Scanner.StartedPaths);
    });

    [Fact]
    public void Choosing_a_new_location_from_an_existing_result_creates_a_task_and_preserves_the_old_result() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var existing = fixture.Start("existing");
        fixture.Scanner.For(existing).Release();
        Complete(existing.Completion);
        var previousRoot = existing.ViewModel.ScanRoot!;
        var previousFolder = Assert.Single(previousRoot.Children);
        existing.ViewModel.ZoomIntoNodeCommand.Execute(previousFolder);
        var previousSelection = existing.ViewModel.SelectedNode;
        var nextPath = Path.Combine(fixture.DataPath, "new-location");
        Directory.CreateDirectory(nextPath);

        existing.ViewModel.ScanCommand.Execute(null);
        Assert.True(existing.ViewModel.IsScanLocationPickerOpen);
        existing.ViewModel.SelectScanRootCommand.Execute(nextPath);
        WaitUntil(() => fixture.Workspace.TaskTabs.Count == 3);

        var created = fixture.Workspace.ActiveTab;
        Assert.NotSame(existing.ViewModel, created.ViewModel);
        Assert.Equal(nextPath, created.Path);
        Assert.False(existing.ViewModel.IsScanLocationPickerOpen);
        Assert.Same(previousRoot, existing.ViewModel.ScanRoot);
        Assert.Same(previousFolder, existing.ViewModel.CurrentRoot);
        Assert.Same(previousSelection, existing.ViewModel.SelectedNode);
        Assert.Equal(existing.Path, existing.ViewModel.RootPath);
        fixture.Scanner.For(created).Release();
        Complete(created.Completion);
        Assert.Equal(new[] { existing.Path, nextPath }, fixture.Scanner.StartedPaths);
    });

    [Fact]
    public void Switching_between_Home_and_completed_tasks_preserves_each_location_selection_and_scan() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var first = fixture.Start("first");
        var second = fixture.Start("second");
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(first.Completion, second.Completion));
        var firstRoot = first.ViewModel.ScanRoot!;
        var secondRoot = second.ViewModel.ScanRoot!;
        var firstFolder = Assert.Single(firstRoot.Children);
        var secondFolder = Assert.Single(secondRoot.Children);
        first.ViewModel.ZoomIntoNodeCommand.Execute(firstFolder);
        first.ViewModel.SelectedNode = Assert.Single(firstFolder.Children);
        var firstSelection = first.ViewModel.SelectedNode;
        second.ViewModel.ZoomIntoNodeCommand.Execute(secondFolder);
        second.ViewModel.SelectedNode = secondFolder;
        var home = fixture.Workspace.TaskTabs[0];

        for (var index = 0; index < 4; index++)
        {
            fixture.Workspace.SelectTabCommand.Execute(home);
            Assert.Same(fixture.Workspace.HomeViewModel, fixture.Workspace.ActiveViewModel);
            Assert.Null(fixture.Workspace.ActiveViewModel.ScanRoot);
            fixture.Workspace.SelectTabCommand.Execute(first);
            Assert.Same(first.ViewModel, fixture.Workspace.ActiveViewModel);
            Assert.Same(firstRoot, first.ViewModel.ScanRoot);
            Assert.Same(firstFolder, first.ViewModel.CurrentRoot);
            Assert.Same(firstSelection, first.ViewModel.SelectedNode);
            Assert.True(first.ViewModel.ZoomOutCommand.CanExecute(null));
            fixture.Workspace.SelectTab(second);
            Assert.Same(secondRoot, second.ViewModel.ScanRoot);
            Assert.Same(secondFolder, second.ViewModel.CurrentRoot);
            Assert.Same(secondFolder, second.ViewModel.SelectedNode);
            Assert.Single(fixture.Workspace.TaskTabs, tab => tab.IsSelected);
        }

        first.ViewModel.ZoomOutCommand.Execute(null);
        Assert.Same(firstRoot, first.ViewModel.CurrentRoot);
        Assert.Same(secondFolder, second.ViewModel.CurrentRoot);
        Assert.Equal(new[] { first.Path, second.Path }, fixture.Scanner.StartedPaths);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Root_button_from_the_first_folder_returns_to_this_task_scan_root_and_preserves_the_other_task(bool focusMode) => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var first = fixture.Start("first");
        var second = fixture.Start("second");
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(first.Completion, second.Completion));
        var firstRoot = first.ViewModel.ScanRoot!;
        var firstFolder = Assert.Single(firstRoot.Children);
        var secondFolder = Assert.Single(second.ViewModel.ScanRoot!.Children);
        if (focusMode) first.ViewModel.ToggleFocusMode();
        var rootButton = BindRootButton(first.ViewModel);
        Assert.False(rootButton.IsEnabled);

        first.ViewModel.ZoomIntoNodeCommand.Execute(firstFolder);
        first.ViewModel.SelectedNode = Assert.Single(firstFolder.Children);
        second.ViewModel.ZoomIntoNodeCommand.Execute(secondFolder);
        var secondSelection = second.ViewModel.SelectedNode;
        fixture.Workspace.SelectTab(first);
        Assert.True(rootButton.IsEnabled, "The task root button must enable at the first folder level.");
        Assert.Equal(firstFolder.Path, first.ViewModel.CurrentLocationText);

        rootButton.ClickButton();

        Assert.Same(first, fixture.Workspace.ActiveTab);
        Assert.False(first.ViewModel.IsHomeWorkspace);
        Assert.Same(firstRoot, first.ViewModel.CurrentRoot);
        Assert.Same(firstRoot, first.ViewModel.SelectedNode);
        Assert.Equal(firstRoot.Path, first.ViewModel.CurrentLocationText);
        Assert.True(first.ViewModel.IsOverviewMode);
        Assert.False(rootButton.IsEnabled);
        Assert.False(first.ViewModel.ZoomOutCommand.CanExecute(null));
        Assert.Equal(focusMode, first.ViewModel.IsFocusMode);
        Assert.Same(secondFolder, second.ViewModel.CurrentRoot);
        Assert.Same(secondSelection, second.ViewModel.SelectedNode);
        Assert.Equal(3, fixture.Workspace.TaskTabs.Count);
        Assert.Equal(new[] { first.Path, second.Path }, fixture.Scanner.StartedPaths);

        first.ViewModel.ZoomIntoNodeCommand.Execute(firstFolder);
        Assert.True(first.ViewModel.ZoomOutCommand.CanExecute(null));
        first.ViewModel.ZoomOutCommand.Execute(null);
        Assert.Same(firstRoot, first.ViewModel.CurrentRoot);
        Assert.False(rootButton.IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Root_button_discards_all_overflow_pages_and_reentry_starts_at_the_first_page(bool focusMode) => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var task = fixture.Start("many-folders");
        fixture.Scanner.For(task).Release();
        Complete(task.Completion);
        var folders = Enumerable.Range(0, 160).Select(index => new FileSystemNode(
            Path.Combine(task.Path, $"Folder{index:D3}"), $"Folder{index:D3}", string.Empty,
            160 - index, FileAttributes.Directory, null, null, true, false, [], [])).ToArray();
        var root = new FileSystemNode(task.Path, "many-folders", string.Empty, folders.Sum(folder => folder.SizeBytes),
            FileAttributes.Directory, null, null, true, false, [], folders);
        ApplySnapshot(task.ViewModel, root);
        if (focusMode) task.ViewModel.ToggleFocusMode();
        var rootButton = BindRootButton(task.ViewModel);
        var firstOverflow = Assert.Single(task.ViewModel.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        task.ViewModel.ZoomIntoNodeCommand.Execute(firstOverflow);
        var nextOverflow = Assert.Single(task.ViewModel.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        task.ViewModel.ZoomIntoNodeCommand.Execute(nextOverflow);
        Assert.Equal(nextOverflow.Path, task.ViewModel.CurrentRoot?.Path);
        Assert.True(rootButton.IsEnabled);

        rootButton.ClickButton();

        Assert.Same(root, task.ViewModel.CurrentRoot);
        Assert.Same(root, task.ViewModel.SelectedNode);
        Assert.Equal(root.Path, task.ViewModel.CurrentLocationText);
        Assert.False(rootButton.IsEnabled);
        Assert.False(task.ViewModel.CanZoomOut);
        var firstOverflowAgain = Assert.Single(task.ViewModel.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        Assert.Equal(firstOverflow.Path, firstOverflowAgain.Path);
        task.ViewModel.ZoomIntoNodeCommand.Execute(firstOverflowAgain);
        task.ViewModel.ZoomOutCommand.Execute(null);
        Assert.Same(root, task.ViewModel.CurrentRoot);
        Assert.False(task.ViewModel.ZoomOutCommand.CanExecute(null));
    });

    [Fact]
    public void Root_and_back_commands_cannot_navigate_while_the_task_is_scanning() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var task = fixture.Start("scanning");
        var root = ScanTree(task.Path);
        var child = Assert.Single(root.Children);
        ApplySnapshot(task.ViewModel, root, child.Path, child.Path);
        var rootButton = BindRootButton(task.ViewModel);
        Assert.True(task.ViewModel.IsScanning);
        Assert.False(rootButton.IsEnabled);
        Assert.False(task.ViewModel.ResetZoomCommand.CanExecute(null));
        Assert.False(task.ViewModel.ZoomOutCommand.CanExecute(null));

        task.ViewModel.ResetZoomCommand.Execute(null);
        Assert.Same(child, task.ViewModel.CurrentRoot);
        task.ViewModel.ZoomOutCommand.Execute(null);
        Assert.Same(child, task.ViewModel.CurrentRoot);
        Assert.Same(child, task.ViewModel.SelectedNode);
        Assert.Same(task, fixture.Workspace.ActiveTab);
    });

    [Fact]
    public void Inflight_AI_results_return_to_their_own_task_after_selection_changes() => OnSta(() =>
    {
        var provider = new ControlledAiProvider();
        using var fixture = new WorkspaceFixture(provider);
        var first = fixture.Start("first");
        var second = fixture.Start("second");
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(first.Completion, second.Completion));

        fixture.Workspace.SelectTab(first);
        first.ViewModel.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Requests.Count == 1);
        fixture.Workspace.SelectTab(second);
        second.ViewModel.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Requests.Count == 2);
        provider.Requests[1].Release();
        WaitUntil(() => !second.ViewModel.IsAnalyzing);

        Assert.True(first.ViewModel.IsAnalyzing);
        Assert.Empty(first.ViewModel.AnalysisItems);
        Assert.NotEmpty(second.ViewModel.AnalysisItems);
        Assert.All(second.ViewModel.AnalysisItems, item => Assert.StartsWith(second.Path, item.Path));
        var secondResultRoot = second.ViewModel.ScanRoot;
        fixture.Workspace.SelectTab(fixture.Workspace.TaskTabs[0]);
        provider.Requests[0].Release();
        WaitUntil(() => !first.ViewModel.IsAnalyzing);

        Assert.Same(fixture.Workspace.HomeViewModel, fixture.Workspace.ActiveViewModel);
        Assert.NotEmpty(first.ViewModel.AnalysisItems);
        Assert.All(first.ViewModel.AnalysisItems, item => Assert.StartsWith(first.Path, item.Path));
        Assert.Same(secondResultRoot, second.ViewModel.ScanRoot);
        Assert.Empty(fixture.Workspace.HomeViewModel.AnalysisItems);
        Assert.DoesNotContain(first.ViewModel.AnalysisItems, item => item.Path.StartsWith(second.Path));
        Assert.DoesNotContain(second.ViewModel.AnalysisItems, item => item.Path.StartsWith(first.Path));
    });

    [Fact]
    public void Closing_a_task_during_AI_analysis_cancels_only_its_request_and_rejects_late_results() => OnSta(() =>
    {
        var provider = new ControlledAiProvider();
        using var fixture = new WorkspaceFixture(provider);
        var first = fixture.Start("first");
        var second = fixture.Start("second");
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(first.Completion, second.Completion));
        first.ViewModel.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Requests.Count == 1);
        second.ViewModel.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Requests.Count == 2);

        fixture.Workspace.CloseTab(first);
        Assert.True(provider.Requests[0].Token.IsCancellationRequested);
        Assert.False(provider.Requests[1].Token.IsCancellationRequested);
        provider.Requests[0].Release();
        provider.Requests[1].Release();
        WaitUntil(() => !first.ViewModel.IsAnalyzing && !second.ViewModel.IsAnalyzing);

        Assert.DoesNotContain(first, fixture.Workspace.TaskTabs);
        Assert.Null(first.ViewModel.ScanRoot);
        Assert.Null(first.ViewModel.CurrentRoot);
        Assert.Empty(first.ViewModel.AnalysisItems);
        Assert.NotEmpty(second.ViewModel.AnalysisItems);
        Assert.All(second.ViewModel.AnalysisItems, item => Assert.StartsWith(second.Path, item.Path));
    });

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(40)]
    public void Theme_and_AI_settings_are_shared_with_existing_and_new_tasks_and_old_tasks_cannot_overwrite_them(int batchSize) => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var older = fixture.Start("older");
        var newer = fixture.Start("newer");
        var editor = newer.ViewModel;
        editor.SelectedThemeId = "01";
        editor.SelectedAiConnectionId = AiConnectionOption.CustomId;
        editor.AiEndpoint = "https://workspace-test.invalid/v1";
        editor.AiModel = "shared-workspace-model";
        editor.AiReasoningEffort = "high";
        editor.ApiKey = "synthetic-workspace-key-never-sent";
        editor.MaxAiBatchSize = batchSize;
        editor.MaxAiAnalysisTokens = 120_000;
        editor.IsAiTokenSavingModeEnabled = true;
        Complete(fixture.Workspace.SaveSharedSettingsAsync(editor));
        var newest = fixture.Start("newest");

        foreach (var vm in fixture.Workspace.TaskTabs.Select(tab => tab.ViewModel))
        {
            Assert.Equal("01", vm.SelectedThemeId);
            Assert.Equal(AiConnectionOption.CustomId, vm.SelectedAiConnectionId);
            Assert.Equal("https://workspace-test.invalid/v1", vm.AiEndpoint);
            Assert.Equal("shared-workspace-model", vm.AiModel);
            Assert.Equal("high", vm.AiReasoningEffort);
            Assert.True(vm.ApiKey == "synthetic-workspace-key-never-sent");
            Assert.Equal(batchSize, vm.MaxAiBatchSize);
            Assert.Equal(120_000, vm.MaxAiAnalysisTokens);
            Assert.True(vm.IsAiTokenSavingModeEnabled);
        }

        // A late save by a task created before the edit must contain the current shared values.
        Complete(fixture.Workspace.SaveSharedSettingsAsync(older.ViewModel));
        fixture.Scanner.ReleaseAll();
        Complete(Task.WhenAll(older.Completion, newer.Completion, newest.Completion));
        var saved = Complete(fixture.SettingsStore.LoadAsync());
        Assert.Equal(newest.Path, saved.RootPath);
        Assert.Equal("01", saved.ThemeId);
        Assert.Equal(AiConnectionOption.CustomId, saved.AiConnectionProfileId);
        Assert.Equal("shared-workspace-model", saved.AiModel);
        Assert.Equal("https://workspace-test.invalid/v1", saved.AiEndpoint);
        Assert.Equal("high", saved.AiReasoningEffort);
        Assert.Equal(batchSize, saved.MaxAiBatchSize);
        Assert.Equal(120_000, saved.MaxAiAnalysisTokens);
        Assert.True(saved.IsAiTokenSavingModeEnabled);
        Assert.True(fixture.SecretStore.LoadApiKeyAsync().GetAwaiter().GetResult() == "synthetic-workspace-key-never-sent");
        Assert.DoesNotContain("synthetic-workspace-key-never-sent", File.ReadAllText(Path.Combine(fixture.DataPath, "settings.json")));
    });

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 20)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(40, 40)]
    [InlineData(99, 40)]
    public void Batch_settings_are_normalized_on_load_save_and_new_workspace(int configuredSize, int expectedSize) => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture(batchSize: configuredSize);
        var vm = fixture.Workspace.HomeViewModel;
        Assert.Equal(expectedSize, vm.MaxAiBatchSize);
        Assert.Equal(expectedSize, CaptureScanOptions(vm).MaxAiBatchSize);

        var settings = AppSettings.Default() with { MaxAiBatchSize = configuredSize };
        var path = Path.Combine(fixture.DataPath, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings));
        Assert.Equal(expectedSize, Complete(fixture.SettingsStore.LoadAsync()).MaxAiBatchSize);

        Complete(fixture.SettingsStore.SaveAsync(settings));
        var stored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        Assert.Equal(expectedSize, stored!.MaxAiBatchSize);
        Assert.Equal(expectedSize, Complete(fixture.SettingsStore.LoadAsync()).MaxAiBatchSize);
    });

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(40, 40)]
    [InlineData(99, 40)]
    public void Batch_slider_values_reach_the_request_snapshot_with_a_one_to_forty_limit(int requestedSize, int expectedSize) => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        var vm = fixture.Workspace.HomeViewModel;
        typeof(MainWindowViewModel).GetField("_suspendSettingsSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, true);

        vm.MaxAiBatchSize = requestedSize;

        Assert.Equal(expectedSize, vm.MaxAiBatchSize);
        Assert.Equal(expectedSize, CaptureScanOptions(vm).MaxAiBatchSize);
    });

    [Fact]
    public void New_and_legacy_settings_without_a_batch_size_keep_the_twenty_item_default() => OnSta(() =>
    {
        using var fixture = new WorkspaceFixture();
        Assert.Equal(20, AppSettings.Default().MaxAiBatchSize);
        Assert.Equal(20, Complete(fixture.SettingsStore.LoadAsync()).MaxAiBatchSize);
        var settings = JsonSerializer.SerializeToNode(AppSettings.Default())!.AsObject();
        settings.Remove(nameof(AppSettings.MaxAiBatchSize));
        File.WriteAllText(Path.Combine(fixture.DataPath, "settings.json"), settings.ToJsonString());

        Assert.Equal(20, Complete(fixture.SettingsStore.LoadAsync()).MaxAiBatchSize);
    });

    private static ScanSessionOptions CaptureScanOptions(MainWindowViewModel vm) =>
        (ScanSessionOptions)typeof(MainWindowViewModel).GetMethod("CreateScanOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string? _previousDataDirectory = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        public string DataPath { get; }
        public AppSettingsStore SettingsStore { get; }
        public AppSecretStore SecretStore { get; }
        public ControlledScanner Scanner { get; } = new();
        public ScanWorkspaceViewModel Workspace { get; }

        public WorkspaceFixture(IAiProvider? provider = null, bool hasCompletedGuide = true, int batchSize = 20)
        {
            var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS");
            DataPath = Path.Combine(string.IsNullOrWhiteSpace(artifactRoot)
                ? Path.Combine(Path.GetTempPath(), "DiskLens.App.Tests") : artifactRoot,
                "workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataPath);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", DataPath);
            SettingsStore = new AppSettingsStore();
            SecretStore = new AppSecretStore();
            var settings = AppSettings.Default() with
            {
                RootPath = DataPath,
                HasCompletedExplorerGuide = hasCompletedGuide,
                AnalysisThresholdMb = 1,
                MaxAiBatchSize = batchSize,
                AiConnectionProfileId = provider is null ? AiConnectionOption.DisabledId : AiConnectionOption.CustomId,
                ProviderKind = provider is null ? AiProviderKind.Disabled : AiProviderKind.OpenAiCompatible,
                AiEndpoint = provider is null ? string.Empty : "https://workspace-test.invalid/v1",
                AiModel = provider is null ? string.Empty : "synthetic-test-model",
            };
            Workspace = new ScanWorkspaceViewModel((configuration, key) =>
            {
                var policy = new WindowsLocationPolicy();
                return new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(),
                    new DeletionPolicyService(policy), new ExplorerFileLocationService(), new RecycleBinService(),
                    new AiAnalysisService(provider is null ? [] : [provider], new AiCandidateBuilder(policy), new AiBatchPlanner()),
                    SettingsStore, SecretStore, configuration, key);
            }, settings, provider is null ? string.Empty : "synthetic-test-key-never-sent",
                SettingsStore, SecretStore, Scanner.RunAsync);
        }

        public ScanTaskTabViewModel Start(string name)
        {
            var path = Path.Combine(DataPath, name);
            Directory.CreateDirectory(path);
            Complete(Workspace.StartScanAsync(path));
            var tab = Workspace.TaskTabs.Last();
            Assert.Same(tab, Workspace.ActiveTab);
            Assert.Same(tab.ViewModel, Workspace.ActiveViewModel);
            return tab;
        }

        public void Dispose()
        {
            try
            {
                var tabs = Workspace.TaskTabs.Skip(1).ToArray();
                foreach (var tab in tabs) Workspace.CloseTab(tab);
                Scanner.ReleaseAll();
                Complete(Task.WhenAll(tabs.Select(tab => tab.Completion)));
                Complete(Workspace.SaveSharedSettingsAsync(Workspace.HomeViewModel));
                Workspace.Dispose();
                PumpDispatcher();
            }
            finally
            {
                Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataDirectory);
            }
        }
    }

    // Deliberately ignores cancellation until released, modeling a scanner draining native I/O.
    // This verifies the concurrency limit against actual callback lifetime, not a UI counter.
    private sealed class ControlledScanner
    {
        private readonly List<ScanInvocation> _invocations = [];
        private int _activeCount;
        public int PeakActiveCount { get; private set; }
        public string[] StartedPaths => _invocations.Select(invocation => invocation.Path).ToArray();
        public ScanInvocation For(ScanTaskTabViewModel tab) => Assert.Single(_invocations, invocation => invocation.Path == tab.Path);

        public async Task RunAsync(MainWindowViewModel vm, string path, CancellationToken token)
        {
            var invocation = new ScanInvocation(path, token);
            _invocations.Add(invocation);
            PeakActiveCount = Math.Max(PeakActiveCount, ++_activeCount);
            try
            {
                var failure = await invocation.Finished.Task;
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                token.ThrowIfCancellationRequested();
                ApplySnapshot(vm, ScanTree(path));
            }
            finally { _activeCount--; }
        }

        public void ReleaseAll()
        {
            foreach (var invocation in _invocations.ToArray()) invocation.Release();
        }
    }

    private sealed class ScanInvocation(string path, CancellationToken token)
    {
        public string Path { get; } = path;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<Exception?> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Finished.TrySetResult(null);
        public void Fail(Exception exception) => Finished.TrySetResult(exception);
    }

    private sealed class ControlledAiProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public List<AiInvocation> Requests { get; } = [];

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            var invocation = new AiInvocation(batch, cancellationToken);
            Requests.Add(invocation);
            return invocation.Finished.Task;
        }
    }

    private sealed class AiInvocation(AiBatch batch, CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<IReadOnlyList<AiAnalysisResult>> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Finished.TrySetResult(batch.Records.Select(record => new AiAnalysisResult(
            record.Path, RiskLevel.Low, SuggestedAction.Keep, "Synthetic per-task result: " + record.Path, 0.9)).ToArray());
    }

    private static FileSystemNode ScanTree(string path)
    {
        var folderPath = Path.Combine(path, "Downloads");
        var file = new FileSystemNode(Path.Combine(folderPath, "sample.bin"), "sample.bin", ".bin",
            4 * 1024 * 1024, FileAttributes.Normal, null, null, false, false, [], []);
        var folder = new FileSystemNode(folderPath, "Downloads", string.Empty, file.SizeBytes,
            FileAttributes.Directory, null, null, true, false, [], [file]);
        return new FileSystemNode(path, Path.GetFileName(path), string.Empty, file.SizeBytes,
            FileAttributes.Directory, null, null, true, false, [], [folder]);
    }

    private static void ApplySnapshot(MainWindowViewModel vm, FileSystemNode root, string? currentPath = null, string? selectedPath = null) =>
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, currentPath, selectedPath]);

    private static ClickableNavigationButton BindRootButton(MainWindowViewModel vm)
    {
        var button = new ClickableNavigationButton();
        button.SetBinding(Button.CommandProperty, new Binding(nameof(vm.ResetZoomCommand)) { Source = vm });
        PumpDispatcher();
        return button;
    }

    private sealed class ClickableNavigationButton : Button
    {
        public void ClickButton()
        {
            Assert.True(IsEnabled, "A disabled root navigation button cannot be clicked.");
            OnClick();
            PumpDispatcher();
        }
    }

    private static void Complete(Task task)
    {
        WaitUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static T Complete<T>(Task<T> task)
    {
        Complete((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void WaitUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Workspace operation timed out.");
            PumpDispatcher();
            Thread.Sleep(1);
        }
        PumpDispatcher();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "WPF workspace test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DiskLens.App.Infrastructure;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.App.Tests;

public sealed class ParallelRecycleBehaviorTests
{
    [Fact]
    public void Async_commands_stay_serial_unless_explicitly_enabled() => OnSta(async () =>
    {
        var serialDone = new TaskCompletionSource();
        var serialCalls = 0;
        var serial = new AsyncRelayCommand(() => { serialCalls++; return serialDone.Task; });
        var first = serial.ExecuteAsync(null);
        Assert.False(serial.CanExecute(null));
        await serial.ExecuteAsync(null);
        Assert.Equal(1, serialCalls);
        serialDone.SetResult();
        await first;
        Assert.True(serial.CanExecute(null));

        var parallelDone = new TaskCompletionSource();
        var parallelCalls = 0;
        var parallel = new AsyncRelayCommand(() => { parallelCalls++; return parallelDone.Task; }, allowConcurrentExecutions: true);
        var a = parallel.ExecuteAsync(null);
        var b = parallel.ExecuteAsync(null);
        Assert.True(parallel.CanExecute(null));
        Assert.Equal(2, parallelCalls);
        parallelDone.SetResult();
        await Task.WhenAll(a, b);
    });

    [Fact]
    public void Large_recycle_keeps_both_commands_available_for_other_paths_and_merges_results() => OnSta(async () =>
    {
        var a = Folder(@"C:\DiskLensRecycleTests\A", Folder(@"C:\DiskLensRecycleTests\A\child"));
        var b = Folder(@"C:\DiskLensRecycleTests\B");
        var root = Folder(@"C:\DiskLensRecycleTests", a, b);
        var service = new ControlledRecycleService();
        using var vm = CreateViewModel(service);
        SetSnapshot(vm, root);

        var first = vm.RecycleCommand.ExecuteAsync(a);
        Assert.Equal(1, vm.PendingRecycleCount);
        Assert.False(vm.RecycleCommand.CanExecute(a));
        Assert.False(vm.RecycleAnalysisItemCommand.CanExecute(Item(a)));
        Assert.False(vm.RecycleCommand.CanExecute(root));
        Assert.False(vm.RecycleCommand.CanExecute(a.Children[0]));
        Assert.True(vm.RecycleCommand.CanExecute(b));
        Assert.True(vm.RecycleAnalysisItemCommand.CanExecute(Item(b)));
        Assert.False(vm.RefreshCurrentDirectoryCommand.CanExecute(null));
        await vm.RecycleCommand.ExecuteAsync(a);
        Assert.Single(service.RecycleCalls);

        var second = vm.RecycleAnalysisItemCommand.ExecuteAsync(Item(b));
        Assert.Equal(2, vm.PendingRecycleCount);
        Assert.Equal(2, service.RecycleCalls.Count);
        Assert.Equal(2, vm.ScanRoot!.Children.Count);
        service.Complete(a.Path);
        service.Complete(b.Path);
        await Task.WhenAll(first, second);
        Assert.Equal(0, vm.PendingRecycleCount);
        Assert.Empty(vm.ScanRoot!.Children);
    });

    [Fact]
    public void Click_submits_one_system_operation_immediately_and_waits_before_removing_the_node() => OnSta(async () =>
    {
        var node = Folder(@"C:\DiskLensRecycleTests\A");
        var service = new ControlledRecycleService();
        using var vm = CreateViewModel(service);
        SetSnapshot(vm, Folder(@"C:\DiskLensRecycleTests", node));

        var operation = vm.RecycleCommand.ExecuteAsync(node);
        Assert.Equal(new[] { node.Path }, service.RecycleCalls);
        Assert.False(operation.IsCompleted);
        Assert.Single(vm.ScanRoot!.Children);
        Assert.Equal(1, vm.PendingRecycleCount);

        service.Complete(node.Path);
        await operation;
        Assert.Single(service.RecycleCalls);
        Assert.Empty(vm.ScanRoot!.Children);
        Assert.Equal(0, vm.PendingRecycleCount);
    });

    [Theory]
    [InlineData(unchecked((int)0x80270028))] // Windows sharing violation.
    [InlineData(unchecked((int)0x80070005))] // Access denied.
    [InlineData(unchecked((int)0x80270037))] // Recycle Bin capacity limit.
    public void System_failure_retains_the_node_and_never_submits_another_delete(int errorCode) => OnSta(async () =>
    {
        var node = Folder(@"C:\DiskLensRecycleTests\A");
        var service = new ControlledRecycleService();
        using var vm = CreateViewModel(service);
        SetSnapshot(vm, Folder(@"C:\DiskLensRecycleTests", node));

        var operation = vm.RecycleCommand.ExecuteAsync(node);
        service.Fail(node.Path, new IOException("Simulated Windows Shell failure.", errorCode));
        await operation;

        Assert.Equal(new[] { node.Path }, service.RecycleCalls);
        Assert.Single(vm.ScanRoot!.Children);
        Assert.Equal(0, vm.PendingRecycleCount);
        Assert.True(vm.RecycleCommand.CanExecute(node));
    });

    [Fact]
    public void Cancelling_the_system_dialog_retains_the_node_and_releases_the_pending_path() => OnSta(async () =>
    {
        var node = Folder(@"C:\DiskLensRecycleTests\A");
        var service = new ControlledRecycleService();
        using var vm = CreateViewModel(service);
        SetSnapshot(vm, Folder(@"C:\DiskLensRecycleTests", node));

        var operation = vm.RecycleCommand.ExecuteAsync(node);
        Assert.Equal(1, vm.PendingRecycleCount);
        service.Fail(node.Path, new OperationCanceledException("Cancelled by Windows Shell."));
        await operation;

        Assert.Equal(new[] { node.Path }, service.RecycleCalls);
        Assert.Single(vm.ScanRoot!.Children);
        Assert.Equal(0, vm.PendingRecycleCount);
        Assert.True(vm.RecycleCommand.CanExecute(node));
    });

    [Fact]
    public void Background_removal_rebases_on_newer_snapshot_and_keeps_current_navigation() => OnSta(async () =>
    {
        var a = Folder(@"C:\DiskLensRecycleTests\A");
        var b = Folder(@"C:\DiskLensRecycleTests\B");
        var c = Folder(@"C:\DiskLensRecycleTests\C");
        using var vm = CreateViewModel(new ControlledRecycleService());
        SetSnapshot(vm, Folder(@"C:\DiskLensRecycleTests", a, b));
        var method = typeof(MainWindowViewModel).GetMethod("ApplyLocalRemovalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var removal = (Task<bool>)method.Invoke(vm, [a])!;
        // The background tree edit has yielded; a newer tree and a navigation
        // choice arrive before its continuation may commit on the dispatcher.
        var newerB = b with { SizeBytes = 4096 };
        SetSnapshot(vm, Folder(@"C:\DiskLensRecycleTests", a, newerB, c), b.Path);
        Assert.True(await removal);
        Assert.Equal(new[] { b.Path, c.Path }, vm.ScanRoot!.Children.Select(node => node.Path));
        Assert.Equal(4096, vm.ScanRoot.Children[0].SizeBytes);
        Assert.Equal(b.Path, vm.CurrentRoot!.Path);
    });

    [Fact]
    public void Shared_service_busy_state_blocks_overlapping_paths_across_view_models() => OnSta(async () =>
    {
        var a = Folder(@"C:\DiskLensRecycleTests\A");
        var b = Folder(@"C:\DiskLensRecycleTests\B");
        var root = Folder(@"C:\DiskLensRecycleTests", a, b);
        var service = new ControlledRecycleService();
        using var first = CreateViewModel(service);
        using var second = CreateViewModel(service);
        SetSnapshot(first, root);
        SetSnapshot(second, root);
        var task = first.RecycleCommand.ExecuteAsync(a);
        Assert.False(second.RecycleCommand.CanExecute(a));
        Assert.False(second.RecycleCommand.CanExecute(root));
        Assert.True(second.RecycleCommand.CanExecute(b));
        var uiThread = Environment.CurrentManagedThreadId;
        var notifications = 0;
        second.RecycleCommand.CanExecuteChanged += (_, _) =>
        {
            Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
            notifications++;
        };
        await Task.Run(service.SignalStateChanged);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.True(notifications > 0);
        service.Complete(a.Path);
        await task;
        Assert.True(second.RecycleCommand.CanExecute(a));
    });

    private static MainWindowViewModel CreateViewModel(IRecycleBinService recycle)
    {
        var policy = new WindowsLocationPolicy();
        return new MainWindowViewModel(new DirectoryScanner(), new RejectingFolderPicker(), new DeletionPolicyService(policy),
            new RejectingFileLocation(), recycle,
            new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(),
            AppSettings.Default() with { HasCompletedExplorerGuide = true }, string.Empty);
    }

    private static void SetSnapshot(MainWindowViewModel vm, FileSystemNode root, string? currentPath = null)
    {
        var method = typeof(MainWindowViewModel).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplySnapshot" && method.GetParameters().Length == 3);
        method.Invoke(vm, [root, currentPath ?? root.Path, currentPath ?? root.Path]);
    }

    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path), string.Empty, 1024, FileAttributes.Directory,
        null, null, true, false, [], children,
        Analysis: new AiAnalysisResult(path, RiskLevel.Low, SuggestedAction.RecycleCandidate, "Temporary files", 0.95));

    private static AnalysisListItem Item(FileSystemNode node) => new(node.Path, node.Name, "1 KB",
        node.Analysis!.RiskLevel, node.Analysis.SuggestedAction, node.Analysis.Reason, node);

    private static void OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Parallel recycle test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ControlledRecycleService : IRecycleBinService
    {
        private readonly Dictionary<string, TaskCompletionSource> _pending = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RecycleCalls { get; } = [];
        public event EventHandler? StateChanged;
        public void SignalStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
        public bool IsPathBusy(string path) => _pending.Keys.Any(pending =>
            string.Equals(path, pending, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(pending + "\\", StringComparison.OrdinalIgnoreCase)
            || pending.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));

        public Task RecycleAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
        {
            RecycleCalls.Add(path);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(path, completion);
            SignalStateChanged();
            return completion.Task;
        }

        public void Complete(string path)
        {
            var completion = _pending[path];
            _pending.Remove(path);
            SignalStateChanged();
            completion.SetResult();
        }

        public void Fail(string path, Exception error)
        {
            var completion = _pending[path];
            _pending.Remove(path);
            SignalStateChanged();
            completion.SetException(error);
        }
    }

    private sealed class RejectingFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string initialDirectory) => throw new InvalidOperationException("No dialogs in tests.");
    }

    private sealed class RejectingFileLocation : IFileLocationService
    {
        public Task OpenLocationAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No Explorer in tests.");
    }
}

using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.App.Tests;

public sealed class AutomaticRefreshBehaviorTests
{
    [Fact]
    public void Watcher_automatically_updates_create_resize_rename_delete_and_parent_totals() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        var file = Path.Combine(nested, "original.bin");
        File.WriteAllBytes(file, new byte[4096]);
        using var vm = CreateViewModel(fixture.Path);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.AutomaticRefreshBatchCount >= 1);
        File.WriteAllBytes(file, new byte[32768]);
        var added = Path.Combine(nested, "added.bin");
        File.WriteAllBytes(added, new byte[8192]);
        WaitUntil(() => vm.ScanRoot!.EnumerateFiles().Any(node => node.Path == added)
            && vm.ScanRoot.EnumerateFiles().Single(node => node.Path == file).LogicalSizeBytes == 32768);
        Assert.Equal(2, vm.ScanRoot!.DescendantFileCount);
        Assert.Equal(vm.ScanRoot.Children.Sum(node => node.SizeBytes), vm.ScanRoot.SizeBytes);
        var renamed = Path.Combine(nested, "renamed.bin");
        File.Move(added, renamed);
        WaitUntil(() => vm.ScanRoot!.EnumerateFiles().Any(node => node.Path == renamed)
            && vm.ScanRoot.EnumerateFiles().All(node => node.Path != added));
        File.Delete(file);
        WaitUntil(() => vm.ScanRoot!.EnumerateFiles().All(node => node.Path != file));
        Assert.Equal(8192, vm.ScanRoot.LogicalSizeBytes);
        Assert.Equal(1, vm.ScanRoot.DescendantFileCount);
    });

    [Fact]
    public void Inactive_task_buffers_changes_without_scanning_then_catches_up_once() => OnSta(() =>
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "keep.txt"), "sample");
        using var vm = CreateViewModel(fixture.Path);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.AutomaticRefreshBatchCount >= 1);
        vm.SetAutomaticRefreshActive(false);
        var old = vm.ScanRoot;
        var batches = vm.AutomaticRefreshBatchCount;
        File.WriteAllBytes(Path.Combine(fixture.Path, "added.bin"), new byte[8192]);
        PumpFor(TimeSpan.FromSeconds(1.8));
        Assert.Same(old, vm.ScanRoot);
        Assert.Equal(batches, vm.AutomaticRefreshBatchCount);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.ScanRoot!.DescendantFileCount == 2);
        Assert.Equal(batches + 1, vm.AutomaticRefreshBatchCount);
    });

    [Fact]
    public void Ai_busy_defers_events_and_automatically_resumes_without_stale_analysis() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Path, "sample.bin");
        File.WriteAllBytes(file, new byte[4096]);
        using var vm = CreateViewModel(fixture.Path);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.AutomaticRefreshBatchCount >= 1);
        var old = vm.ScanRoot;
        SetAnalyzing(vm, true);
        File.WriteAllBytes(file, new byte[24576]);
        PumpFor(TimeSpan.FromSeconds(1.8));
        Assert.Same(old, vm.ScanRoot);
        SetAnalyzing(vm, false);
        WaitUntil(() => vm.ScanRoot!.EnumerateFiles().Single().LogicalSizeBytes == 24576);
        Assert.Null(vm.ScanRoot!.Analysis);
    });

    [Fact]
    public void Disposed_task_stops_monitor_and_queued_callbacks_cannot_resurrect_tree() => OnSta(() =>
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "keep.txt"), "sample");
        var vm = CreateViewModel(fixture.Path);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.AutomaticRefreshBatchCount >= 1);
        File.WriteAllText(Path.Combine(fixture.Path, "added.txt"), "sample");
        vm.Dispose();
        PumpFor(TimeSpan.FromSeconds(1.8));
        Assert.Null(vm.ScanRoot);
        Assert.Null(vm.CurrentRoot);
        Assert.False(vm.IsAutomaticallyRefreshing);
    });

    [Fact]
    public void Event_burst_is_coalesced_and_idle_monitor_does_not_periodically_rescan() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var vm = CreateViewModel(fixture.Path);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.AutomaticRefreshBatchCount >= 1);
        var oldCount = vm.AutomaticRefreshBatchCount;
        for (var i = 0; i < 30; i++) File.WriteAllText(Path.Combine(fixture.Path, $"{i}.txt"), "sample");
        WaitUntil(() => vm.ScanRoot!.DescendantFileCount == 30);
        Assert.Equal(oldCount + 1, vm.AutomaticRefreshBatchCount);
        var after = vm.AutomaticRefreshBatchCount;
        PumpFor(TimeSpan.FromSeconds(2));
        Assert.Equal(after, vm.AutomaticRefreshBatchCount);
    });

    [Fact]
    public void Reconnected_root_catches_up_changes_created_before_watcher_reopens() => OnSta(() =>
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "old.txt"), "old");
        using var vm = CreateViewModel(fixture.Path);
        vm.SetAutomaticRefreshActive(true);
        WaitUntil(() => vm.AutomaticRefreshBatchCount >= 1);
        var timer = (DispatcherTimer)typeof(MainWindowViewModel).GetField("_automaticRefreshFallbackTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        timer.Interval = TimeSpan.FromMilliseconds(250); // Exercise production reconnect path without a 15-second test sleep.
        Directory.Delete(fixture.Path, recursive: true);
        PumpFor(TimeSpan.FromSeconds(1.8));
        Assert.Equal("old.txt", Assert.Single(vm.ScanRoot!.EnumerateFiles()).Name);
        Directory.CreateDirectory(fixture.Path);
        File.WriteAllText(Path.Combine(fixture.Path, "new.txt"), "new");
        WaitUntil(() => vm.ScanRoot!.EnumerateFiles().Any(file => file.Name == "new.txt"));
        Assert.Equal("new.txt", Assert.Single(vm.ScanRoot!.EnumerateFiles()).Name);
    });

    private static void SetAnalyzing(MainWindowViewModel vm, bool value) => typeof(MainWindowViewModel)
        .GetProperty(nameof(vm.IsAnalyzing))!.SetValue(vm, value);

    private static MainWindowViewModel CreateViewModel(string path)
    {
        var scanner = new DirectoryScanner();
        var root = scanner.ScanAsync(new ScanSessionOptions(path)).GetAwaiter().GetResult().Root;
        var policy = new WindowsLocationPolicy();
        var vm = new MainWindowViewModel(scanner, new WindowsFolderPickerService(), new DeletionPolicyService(policy),
            new ExplorerFileLocationService(), new RecycleBinService(), new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(), AppSettings.Default() with { RootPath = path, HasCompletedExplorerGuide = true }, "");
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, null, null]);
        return vm;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < until, "Automatic refresh timed out.");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WPF auto-refresh test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.Refresh.Tests"), "auto-fixture-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}

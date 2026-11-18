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

[Collection("Isolated AI settings")]
public sealed class ScanProgressBehaviorTests
{
    [Fact]
    public void Queued_tasks_start_at_zero_and_only_actual_scanning_is_indeterminate() => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var tabs = Enumerable.Range(0, 4).Select(index => fixture.Start("scan-" + index)).ToArray();
        Assert.All(tabs.Take(3), tab =>
        {
            Assert.Equal(0, tab.ProgressPercent);
            Assert.True(tab.IsIndeterminate);
            Assert.Equal("扫描中", tab.ProgressText);
            Assert.False(tab.IsComplete);
        });
        Assert.Equal(ScanTaskState.Queued, tabs[3].State);
        Assert.Equal(0, tabs[3].ProgressPercent);
        Assert.Equal("排队中", tabs[3].ProgressText);
        Assert.False(tabs[3].IsIndeterminate);
        Assert.False(tabs[3].IsComplete);
        Assert.Equal(tabs[3].Path, tabs[3].RootLabel);
    });

    [Fact]
    public void Measured_coverage_survives_completion_without_fabricating_one_hundred() => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var tab = fixture.Start("success");
        ApplyBytes(tab.ViewModel, 238);
        Assert.True(tab.IsIndeterminate);
        Assert.Equal(0, tab.ProgressPercent);
        SetBaseline(tab.ViewModel, 1000);
        ApplyBytes(tab.ViewModel, 238);
        Assert.Equal(23.8, tab.ProgressPercent, precision: 10);
        Assert.Equal("23%", tab.ProgressText);
        Assert.False(tab.IsIndeterminate);
        // The volume overview can refresh after the scan. Its new value must
        // not alter the frozen denominator belonging to this task.
        typeof(MainWindowViewModel).GetField("_volumeUsedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tab.ViewModel, 2000L);
        ApplyBytes(tab.ViewModel, 238);
        Assert.Equal(23.8, tab.ProgressPercent, precision: 10);
        Assert.False(tab.IsComplete);
        var doneNotifications = 0;
        tab.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(tab.IsComplete) && tab.IsComplete)
            {
                doneNotifications++;
                Assert.Equal(23.8, tab.ProgressPercent, precision: 10);
            }
        };

        fixture.Finish(tab, ScanTaskState.Completed);

        Assert.Equal(23.8, tab.ProgressPercent, precision: 10);
        Assert.Equal("23%", tab.ProgressText);
        Assert.True(tab.IsComplete);
        Assert.False(tab.IsIndeterminate);
        Assert.Equal(1, doneNotifications);
        fixture.Workspace.SelectTab(fixture.Workspace.TaskTabs[0]);
        fixture.Workspace.SelectTab(tab);
        Assert.Equal(23.8, tab.ProgressPercent, precision: 10);
        Assert.Equal(1, doneNotifications);
        Assert.Equal(1, fixture.StartedCount);
        ApplyBytes(tab.ViewModel, 900); // A delayed snapshot cannot undo accepted completion.
        Assert.Equal(23.8, tab.ProgressPercent, precision: 10);
    });

    [Fact]
    public void Known_volume_uses_bytes_only_and_reaching_one_hundred_does_not_complete_traversal() => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var tab = fixture.Start("full-volume");
        SetBaseline(tab.ViewModel, 1000);
        ApplyBytes(tab.ViewModel, 0);
        Assert.Equal("0%", tab.ProgressText);
        Assert.False(tab.IsIndeterminate);
        ApplyBytes(tab.ViewModel, -1);
        Assert.Equal(0, tab.ProgressPercent);
        ApplyBytes(tab.ViewModel, 500);
        Assert.Equal(50, tab.ProgressPercent);
        ApplyBytes(tab.ViewModel, 1500); // External file changes can exceed the initial baseline.
        Assert.Equal(100, tab.ProgressPercent);
        Assert.False(tab.IsComplete);
        Assert.Equal(ScanTaskState.Scanning, tab.State);
        fixture.Finish(tab, ScanTaskState.Completed);
        Assert.True(tab.IsComplete);
        Assert.Equal(100, tab.ProgressPercent);
    });

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Unavailable_or_empty_volume_never_invents_a_percentage(long? usedBytes) => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var tab = fixture.Start("unknown-volume");
        SetBaseline(tab.ViewModel, usedBytes);
        ApplyBytes(tab.ViewModel, 500);
        Assert.True(tab.IsIndeterminate);
        Assert.Equal("扫描中", tab.ProgressText);
        Assert.Equal(0, tab.ProgressPercent);
        fixture.Finish(tab, ScanTaskState.Completed);
        Assert.True(tab.IsComplete);
        Assert.False(tab.IsIndeterminate);
        Assert.Equal(0, tab.ProgressPercent);
    });

    [Fact]
    public void Concurrent_tasks_keep_independent_disk_baselines_and_progress() => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var first = fixture.Start("first-volume");
        var second = fixture.Start("second-volume");
        SetBaseline(first.ViewModel, 1000);
        SetBaseline(second.ViewModel, 2000);
        ApplyBytes(first.ViewModel, 500);
        ApplyBytes(second.ViewModel, 500);
        Assert.Equal(50, first.ProgressPercent);
        Assert.Equal(25, second.ProgressPercent);
        fixture.Workspace.SelectTab(fixture.Workspace.TaskTabs[0]);
        ApplyBytes(first.ViewModel, 750);
        Assert.Equal(75, first.ProgressPercent);
        Assert.Equal(25, second.ProgressPercent);
        fixture.Finish(first, ScanTaskState.Completed);
        Assert.Equal(75, first.ProgressPercent);
        Assert.False(second.IsComplete);
        Assert.Equal(ScanTaskState.Scanning, second.State);
    });

    [Fact]
    public void Real_scan_uses_final_physical_result_and_the_containing_volume_used_space() => OnSta(() =>
    {
        using var fixture = new ProgressFixture(useRealScanner: true);
        var path = Path.Combine(fixture.DataPath, "real-scan");
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "sample.bin"), new byte[8192]);
        var tab = fixture.Start("real-scan");
        Complete(tab.Completion);

        Assert.Equal(ScanTaskState.Completed, tab.State);
        var root = Assert.IsType<FileSystemNode>(tab.ViewModel.ScanRoot);
        var baseline = (long?)typeof(MainWindowViewModel)
            .GetField("_scanVolumeUsedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab.ViewModel);
        Assert.True(baseline > 0);
        Assert.Equal(ScanStorageProgress.CalculatePercent(root.SizeBytes, baseline)!.Value,
            tab.ProgressPercent, precision: 10);
        Assert.InRange(tab.ProgressPercent, 0, 99);
        Assert.True(tab.IsComplete);
        Assert.Contains("磁盘已用", tab.ToolTipText);
        Assert.Contains("done 表示遍历完成", tab.ToolTipText);
    });

    [Theory]
    [InlineData(ScanTaskState.Partial, "部分完成")]
    [InlineData(ScanTaskState.Failed, "失败")]
    [InlineData(ScanTaskState.Canceled, "已取消")]
    public void Unsuccessful_work_keeps_its_measured_fill_without_claiming_done(ScanTaskState outcome, string text) => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var tab = fixture.Start(outcome.ToString());
        SetBaseline(tab.ViewModel, 1000);
        ApplyBytes(tab.ViewModel, 664);
        if (outcome == ScanTaskState.Canceled)
        {
            tab.ViewModel.CancelScanCommand.Execute(null);
        }

        fixture.Finish(tab, outcome);

        Assert.Equal(outcome, tab.State);
        Assert.Equal(66.4, tab.ProgressPercent, precision: 10);
        Assert.Equal(text, tab.ProgressText);
        Assert.False(tab.IsComplete);
        Assert.False(tab.IsIndeterminate);
        Assert.Contains(tab, fixture.Workspace.TaskTabs);
    });

    [Fact]
    public void Coverage_updates_never_persist_or_change_shared_configuration() => OnSta(() =>
    {
        using var fixture = new ProgressFixture();
        var tab = fixture.Start("coverage-only");
        SetBaseline(tab.ViewModel, 1000);
        Complete(fixture.Workspace.SaveSharedSettingsAsync(fixture.Workspace.HomeViewModel));
        var settingsPath = Path.Combine(fixture.DataPath, "settings.json");
        var before = File.ReadAllBytes(settingsPath);
        var modified = File.GetLastWriteTimeUtc(settingsPath);
        var homeRoot = fixture.Workspace.HomeViewModel.RootPath;
        foreach (var bytes in new long[] { 50, 200, 550, 920 }) ApplyBytes(tab.ViewModel, bytes);
        PumpDispatcher();
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(settingsPath));
        Assert.Equal(homeRoot, fixture.Workspace.HomeViewModel.RootPath);
        Assert.Equal(0, fixture.Workspace.HomeViewModel.ScanProgressPercent);
    });

    private static void ApplyBytes(MainWindowViewModel vm, long bytes) => typeof(MainWindowViewModel)
        .GetMethod("ApplyScanStorageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [bytes]);

    private static void SetBaseline(MainWindowViewModel vm, long? volumeUsedBytes) => typeof(MainWindowViewModel)
        .GetMethod("ResetScanProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [volumeUsedBytes]);

    private sealed class ProgressFixture : IDisposable
    {
        private readonly string? _previousDataPath = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        private readonly Dictionary<string, TaskCompletionSource<ScanTaskState>> _running = new();
        public string DataPath { get; }
        public ScanWorkspaceViewModel Workspace { get; }
        public int StartedCount { get; private set; }

        public ProgressFixture(bool useRealScanner = false)
        {
            var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
                ?? Path.Combine(Path.GetTempPath(), "DiskLens.Progress.Tests");
            DataPath = Path.Combine(artifactRoot, "progress-behavior-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataPath);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", DataPath);
            var settingsStore = new AppSettingsStore();
            var secretStore = new AppSecretStore();
            Workspace = new ScanWorkspaceViewModel((settings, key) =>
            {
                var policy = new WindowsLocationPolicy();
                return new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(),
                    new DeletionPolicyService(policy), new ExplorerFileLocationService(), new RecycleBinService(),
                    new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
                    settingsStore, secretStore, settings, key);
            }, AppSettings.Default() with { RootPath = DataPath, HasCompletedExplorerGuide = true }, string.Empty,
                settingsStore, secretStore, useRealScanner ? null : RunAsync);
        }

        public ScanTaskTabViewModel Start(string name)
        {
            var path = Path.Combine(DataPath, name);
            Directory.CreateDirectory(path);
            Complete(Workspace.StartScanAsync(path));
            return Workspace.TaskTabs.Last();
        }

        private async Task RunAsync(MainWindowViewModel vm, string path, CancellationToken token)
        {
            StartedCount++;
            var completion = new TaskCompletionSource<ScanTaskState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _running.Add(path, completion);
            var outcome = await completion.Task;
            token.ThrowIfCancellationRequested();
            if (outcome == ScanTaskState.Failed) throw new IOException("Synthetic progress test failure");
            typeof(MainWindowViewModel).GetProperty("LastScanOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, outcome);
        }

        public void Finish(ScanTaskTabViewModel tab, ScanTaskState state)
        {
            _running[tab.Path].TrySetResult(state);
            Complete(tab.Completion);
        }

        public void Dispose()
        {
            try
            {
                Complete(Workspace.SaveSharedSettingsAsync(Workspace.HomeViewModel));
                var tasks = Workspace.TaskTabs.Skip(1).Select(tab => tab.Completion).ToArray();
                Workspace.Dispose();
                foreach (var pending in _running.Values) pending.TrySetResult(ScanTaskState.Completed);
                Complete(Task.WhenAll(tasks));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataPath);
            }
        }
    }

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Progress operation timed out.");
            PumpDispatcher();
            Thread.Sleep(1);
        }
        PumpDispatcher();
        task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Progress STA test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

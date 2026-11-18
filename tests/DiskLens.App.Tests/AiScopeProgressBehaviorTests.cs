using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
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

[Collection("Isolated AI settings")]
public sealed class AiScopeProgressBehaviorTests
{
    [Fact]
    public void Repeated_cache_analysis_keeps_the_selected_card_on_the_authoritative_snapshot_node() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var provider = new RecordingProvider();
        var file = File(@"E:\Scope-fixture\repeat-selected.bin");
        using var vm = CreateViewModel(Folder(@"E:\", file), provider, new MemoryStore());
        vm.SelectedNode = file;
        for (var pass = 0; pass < 4; pass++)
        {
            AnalyzeToCompletion(vm);
            var authoritative = Assert.Single(vm.ScanRoot!.Children);
            Assert.Same(authoritative, vm.SelectedNode);
            Assert.Same(authoritative, vm.SelectedAnalysisItem!.Node);
            Assert.Equal("合成分析结果", vm.SelectedAiReasonText);
            if (pass > 0)
            {
                Assert.Equal(AiAnalysisResultOrigin.CacheReused, authoritative.Analysis!.Origin);
                Assert.Equal("本次无新请求 · 0 Token", vm.AiUsageSummaryText);
            }
        }
        Assert.Single(provider.RequestedPaths);
    });

    [Fact]
    public void Clicking_each_analyzed_tile_updates_the_pinned_reason_without_scrolling_the_result_list() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var files = Enumerable.Range(1, 3).Select(index => File($@"E:\Scope-fixture\tile-{index}.bin") with
        { Analysis = Result($@"E:\Scope-fixture\tile-{index}.bin", $"第 {index} 项独立分析结论") }).ToArray();
        using var vm = CreateViewModel(Folder(@"E:\", files), new RecordingProvider());
        var reasonChanges = 0;
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.SelectedAiReasonText)) reasonChanges++; };
        foreach (var file in files.Reverse())
        {
            vm.SelectedNode = file;
            Assert.Equal(file.Path, vm.SelectedAnalysisItem!.Path);
            Assert.Equal(file.Analysis!.Reason, vm.SelectedAiReasonText);
            Assert.Equal("您的AI分析结论", vm.SelectedAiStatusText);
            Assert.True(vm.IsAiSidebarSelected);
        }
        Assert.Equal(3, reasonChanges);
    });

    [Fact]
    public void Unanalyzed_and_refreshed_tiles_never_retain_the_previous_tiles_ai_conclusion() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var analyzed = File(@"E:\Scope-fixture\analyzed.bin") with { Analysis = Result(@"E:\Scope-fixture\analyzed.bin", "旧结论必须清除") };
        var pending = File(@"E:\Scope-fixture\pending.bin");
        var unaffected = File(@"E:\Scope-fixture\unaffected.bin") with { Analysis = Result(@"E:\Scope-fixture\unaffected.bin") };
        using var vm = CreateViewModel(Folder(@"E:\", analyzed, pending, unaffected), new RecordingProvider());
        vm.SelectedNode = analyzed;
        Assert.Equal(analyzed.Analysis!.Reason, vm.SelectedAiReasonText);
        vm.SelectedNode = pending;
        Assert.Null(vm.SelectedAnalysisItem);
        Assert.Equal("尚无分析结论", vm.SelectedAiReasonText);
        vm.SelectedNode = analyzed;
        var replacement = analyzed with { SizeBytes = analyzed.SizeBytes + 1, Analysis = null };
        ApplySnapshot(vm, Folder(@"E:\", replacement, pending, unaffected));
        Assert.Equal(analyzed.Path, vm.SelectedNode!.Path);
        Assert.Null(vm.SelectedAnalysisItem);
        Assert.True(vm.HasVisibleAnalysisItems); // Other valid results remain in the queue.
        Assert.DoesNotContain("旧结论", vm.SelectedAiReasonText);
        Assert.Equal("尚无分析结论", vm.SelectedAiReasonText);
    });

    [Fact]
    public void Analysis_completion_updates_an_already_selected_tile_and_dispose_clears_the_pinned_result() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var file = File(@"E:\Scope-fixture\already-selected.bin");
        using var vm = CreateViewModel(Folder(@"E:\", file), new RecordingProvider());
        vm.SelectedNode = file;
        AnalyzeToCompletion(vm);
        Assert.Equal(file.Path, vm.SelectedAnalysisItem!.Path);
        Assert.Equal("合成分析结果", vm.SelectedAiReasonText);
        vm.Dispose();
        Assert.Null(vm.SelectedAnalysisItem);
        Assert.False(vm.HasVisibleAnalysisItems);
        Assert.DoesNotContain("合成分析结果", vm.SelectedAiReasonText);
    });

    [Fact]
    public void Current_directory_analysis_includes_itself_and_descendants_preserves_scan_root_and_sibling_analysis() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var descendant = File(@"E:\Scope-fixture\target\nested\deep.bin");
        var nested = Folder(@"E:\Scope-fixture\target\nested", descendant);
        var direct = File(@"E:\Scope-fixture\target\direct.bin");
        var target = Folder(@"E:\Scope-fixture\target", nested, direct);
        var sibling = File(@"E:\Scope-fixture\sibling.bin");
        var previousAnalysis = Result(sibling.Path, "之前的兄弟项目分析");
        sibling = sibling with { Analysis = previousAnalysis };
        var root = Folder(@"E:\Scope-fixture", target, sibling);
        var provider = new RecordingProvider();
        using var vm = CreateViewModel(root, provider);
        vm.ZoomIntoNodeCommand.Execute(target);
        vm.IsCurrentDirectoryAiScope = true;

        Assert.Equal(target.Path, vm.AiScopePathText);
        AnalyzeToCompletion(vm);

        Assert.Equal(new[] { target.Path, nested.Path, descendant.Path, direct.Path }.Order(StringComparer.Ordinal),
            provider.RequestedPaths.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(sibling.Path, provider.RequestedPaths);
        Assert.Equal(root.Path, vm.ScanRoot!.Path);
        Assert.Equal(root.SizeBytes, vm.ScanRoot.SizeBytes);
        Assert.Equal(2, vm.ScanRoot.Children.Count);
        Assert.Equal(target.Path, vm.CurrentRoot!.Path);
        Assert.Same(previousAnalysis, vm.ScanRoot.Children.Single(node => node.Path == sibling.Path).Analysis);
        Assert.Contains(vm.AnalysisItems, item => item.Path == sibling.Path && item.Reason == previousAnalysis.Reason);
        var analyzedTarget = vm.ScanRoot.Children.Single(node => node.Path == target.Path);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, analyzedTarget.Analysis!.Origin);
        Assert.Equal("合成分析结果", analyzedTarget.Analysis.Reason);
        Assert.Contains(vm.AnalysisItems, item => item.Path == target.Path && item.Reason == analyzedTarget.Analysis.Reason);
        Assert.NotNull(vm.ScanRoot.EnumerateFiles().Single(node => node.Path == descendant.Path).Analysis);
        Assert.True(vm.ZoomOutCommand.CanExecute(null));
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(root.Path, vm.CurrentRoot!.Path);
    });

    [Fact]
    public void Choosing_scan_root_while_browsing_a_child_analyzes_the_entire_scan() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var childFile = File(@"E:\Scope-fixture\target\large.bin");
        var target = Folder(@"E:\Scope-fixture\target", childFile);
        var sibling = File(@"E:\Scope-fixture\sibling.bin");
        var root = Folder(@"E:\Scope-fixture", target, sibling);
        var provider = new RecordingProvider();
        using var vm = CreateViewModel(root, provider);
        vm.ZoomIntoNodeCommand.Execute(target);
        vm.IsCurrentDirectoryAiScope = true;
        vm.IsRootDirectoryAiScope = true;

        Assert.False(vm.IsCurrentDirectoryAiScope);
        Assert.Equal(root.Path, vm.AiScopePathText);
        AnalyzeToCompletion(vm);

        Assert.Equal(new[] { root.Path, target.Path, childFile.Path, sibling.Path }.Order(StringComparer.Ordinal),
            provider.RequestedPaths.Order(StringComparer.Ordinal));
        Assert.Equal(target.Path, vm.CurrentRoot!.Path);
        Assert.Equal(root.Path, vm.ScanRoot!.Path);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, vm.ScanRoot.Analysis!.Origin);
        Assert.Equal("合成分析结果", vm.ScanRoot.Analysis.Reason);
    });

    [Fact]
    public void Current_scope_on_nested_overflow_pages_means_the_real_directory_and_all_its_files() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var files = Enumerable.Range(0, 123).Select(index => File($@"E:\Scope-fixture\target\file-{index:D3}.bin")).ToArray();
        var target = Folder(@"E:\Scope-fixture\target", files);
        var sibling = File(@"E:\Scope-fixture\sibling.bin");
        var root = Folder(@"E:\Scope-fixture", target, sibling);
        var provider = new RecordingProvider();
        using var vm = CreateViewModel(root, provider);
        vm.ZoomIntoNodeCommand.Execute(target);
        var firstOverflow = Assert.Single(vm.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        vm.ZoomIntoNodeCommand.Execute(firstOverflow);
        var nextOverflow = Assert.Single(vm.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        vm.ZoomIntoNodeCommand.Execute(nextOverflow);
        Assert.Contains(DisplayNodeMetadata.AggregatePathMarker, vm.CurrentRoot!.Path);
        vm.IsCurrentDirectoryAiScope = true;

        Assert.Equal(target.Path, vm.AiScopePathText);
        AnalyzeToCompletion(vm);

        Assert.Equal(files.Select(file => file.Path).Append(target.Path).Order(StringComparer.Ordinal),
            provider.RequestedPaths.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(sibling.Path, provider.RequestedPaths);
        Assert.DoesNotContain(provider.RequestedPaths, path => path.Contains(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal));
        Assert.Equal(root.Path, vm.ScanRoot!.Path);
        var analyzedTarget = vm.ScanRoot.Children.Single(node => node.Path == target.Path);
        Assert.Equal(files.Length, analyzedTarget.Children.Count);
        Assert.All(analyzedTarget.DescendantsAndSelf(), node => Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, node.Analysis!.Origin));
    });

    [Fact]
    public void Slow_model_keeps_dispatcher_responsive_and_exposes_real_progress_elapsed_time_and_cancel() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var provider = new DelayedProvider();
        using var vm = CreateViewModel(Folder(@"E:\", File(@"E:\Scope-fixture\large.bin")), provider);
        var uiThread = Environment.CurrentManagedThreadId;
        var cancel = BindButton(vm, nameof(vm.CancelScanCommand));
        var progressChangedOn = new List<int>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.AiProgressDetailText)) progressChangedOn.Add(Environment.CurrentManagedThreadId);
        };

        vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Started.Task.IsCompleted && vm.HasAiCandidateProgress);
        PumpDispatcher();
        Assert.True(vm.IsAnalyzing);
        Assert.NotEqual(uiThread, provider.CalledOnThread);
        Assert.True(cancel.IsEnabled);
        Assert.False(vm.AnalyzeCommand.CanExecute(null));
        Assert.Contains("等待模型返回", vm.AiActivityText);
        Assert.Contains("0 / 1", vm.AiCandidateProgressText);
        Assert.Equal(0, vm.AiCandidateProgressValue);
        Assert.All(progressChangedOn, id => Assert.Equal(uiThread, id));

        var dispatcherResponded = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, () => dispatcherResponded = true);
        WaitUntil(() => dispatcherResponded);
        var initialElapsed = vm.AiElapsedText;
        WaitUntil(() => vm.AiElapsedText != initialElapsed);
        Assert.True(vm.IsAnalyzing);
        Assert.False(provider.Completed.Task.IsCompleted);

        provider.Release();
        WaitUntil(() => !vm.IsAnalyzing && vm.AnalyzeCommand.CanExecute(null));
        Assert.False(cancel.IsEnabled);
        Assert.True(vm.HasAiUsageSummary);
        Assert.Contains("400", vm.AiUsageSummaryText);
    });

    [Fact]
    public void Cache_only_repeat_analysis_displays_zero_new_requests_and_zero_tokens() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var provider = new RecordingProvider();
        var store = new MemoryStore();
        using var vm = CreateViewModel(Folder(@"E:\", File(@"E:\Scope-fixture\large.bin")), provider, store);
        AnalyzeToCompletion(vm);
        Assert.Single(provider.RequestedPaths);
        Assert.Contains("400", vm.AiUsageSummaryText);

        AnalyzeToCompletion(vm);

        Assert.Single(provider.RequestedPaths);
        Assert.True(vm.HasAiUsageSummary);
        Assert.Equal("本次无新请求 · 0 Token", vm.AiUsageSummaryText);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, Assert.Single(vm.ScanRoot!.Children).Analysis!.Origin);
        Assert.Contains("缓存复用 1 项", vm.StatusText);
    });

    [Fact]
    public void Replacing_scan_while_model_is_waiting_discards_old_results_usage_and_delayed_progress() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var provider = new DelayedProvider();
        using var vm = CreateViewModel(Folder(@"E:\", File(@"E:\Scope-fixture\old.bin")), provider);
        vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Started.Task.IsCompleted);
        var replacement = Folder(@"E:\Replacement-fixture", File(@"E:\Replacement-fixture\new.bin"));
        ApplySnapshot(vm, replacement);

        provider.Release();
        WaitUntil(() => !vm.IsAnalyzing && vm.AnalyzeCommand.CanExecute(null));

        Assert.Same(replacement, vm.ScanRoot);
        Assert.Equal(replacement.Path, vm.CurrentRoot!.Path);
        Assert.All(vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));
        Assert.Empty(vm.AnalysisItems);
        Assert.False(vm.HasAiUsageSummary);
        Assert.Equal(replacement.Path, vm.AiScopePathText);
    });

    [Fact]
    public void Closing_task_before_delayed_model_returns_cannot_resurrect_the_scan_or_usage() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var provider = new DelayedProvider(ignoreCancellation: true);
        var vm = CreateViewModel(Folder(@"E:\", File(@"E:\Scope-fixture\old.bin")), provider);
        vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Started.Task.IsCompleted);

        vm.Dispose();
        Assert.Null(vm.ScanRoot);
        provider.Release();
        WaitUntil(() => provider.Completed.Task.IsCompleted && !vm.IsAnalyzing);
        PumpDispatcher();

        Assert.Null(vm.ScanRoot);
        Assert.Null(vm.CurrentRoot);
        Assert.Empty(vm.AnalysisItems);
        Assert.False(vm.HasAiUsageSummary);
        Assert.False(vm.IsAiActivityAnimated);
    });

    [Fact]
    public void Cancel_command_stops_model_wait_and_progress_without_claiming_a_successful_analysis() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var provider = new DelayedProvider();
        var root = Folder(@"E:\", File(@"E:\Scope-fixture\pending.bin"));
        using var vm = CreateViewModel(root, provider);
        vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => provider.Started.Task.IsCompleted && vm.HasAiCandidateProgress);
        Assert.True(vm.CancelScanCommand.CanExecute(null));

        vm.CancelScanCommand.Execute(null);
        WaitUntil(() => !vm.IsAnalyzing && vm.AnalyzeCommand.CanExecute(null));

        Assert.True(provider.Completed.Task.IsCompleted);
        Assert.False(vm.IsAiActivityAnimated);
        Assert.False(vm.CancelScanCommand.CanExecute(null));
        Assert.Contains("取消", vm.StatusText);
        Assert.Equal(root.Path, vm.ScanRoot!.Path);
        Assert.DoesNotContain(vm.ScanRoot.DescendantsAndSelf(), node => node.Analysis?.Origin == AiAnalysisResultOrigin.AiAnalyzed);
        var progressAfterCancel = vm.AiProgressDetailText;
        provider.Release();
        PumpDispatcher();
        Assert.Equal(progressAfterCancel, vm.AiProgressDetailText);
    });

    private static MainWindowViewModel CreateViewModel(FileSystemNode root, IAiProvider provider,
        IAiAnalysisMemoryStore? memoryStore = null)
    {
        var policy = new WindowsLocationPolicy();
        var settings = AppSettings.Default() with
        {
            RootPath = root.Path,
            AnalysisThresholdMb = 1,
            IsAiTokenSavingModeEnabled = true,
            ProviderKind = AiProviderKind.OpenAiCompatible,
            AiEndpoint = "https://example.invalid",
            AiModel = "synthetic-test-model",
            AiConnectionProfileId = AiConnectionOption.CustomId,
            HasCompletedExplorerGuide = true,
            IsReduceMotionEnabled = false,
        };
        var vm = new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(),
            new DeletionPolicyService(policy), new ExplorerFileLocationService(), new RecycleBinService(),
            new AiAnalysisService([provider], new AiCandidateBuilder(policy), new AiBatchPlanner(), memoryStore),
            new AppSettingsStore(), new AppSecretStore(), settings, "synthetic-key-never-sent");
        typeof(MainWindowViewModel).GetField("_suspendSettingsSave", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        ApplySnapshot(vm, root);
        return vm;
    }

    private static void ApplySnapshot(MainWindowViewModel vm, FileSystemNode root)
    {
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, null, null]);
        typeof(MainWindowViewModel).GetMethod("RebuildAnalysisItems", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode)], null)!.Invoke(vm, [root]);
        PumpDispatcher();
    }

    private static FileSystemNode File(string path) => new(path, Path.GetFileName(path), Path.GetExtension(path),
        4 * 1024 * 1024, FileAttributes.Normal, null, null, false, false, [], [], DescendantFileCount: 1);

    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path.TrimEnd('\\')), "", children.Sum(node => node.SizeBytes), FileAttributes.Directory,
        null, null, true, false, [], children, DescendantFileCount: children.Sum(node => node.DescendantFileCount),
        DescendantDirectoryCount: 1 + children.Sum(node => node.DescendantDirectoryCount));

    private static AiAnalysisResult Result(string path, string reason = "合成分析结果") =>
        new(path, RiskLevel.Review, SuggestedAction.Review, reason, 0.8);

    private static void AnalyzeToCompletion(MainWindowViewModel vm)
    {
        Assert.True(vm.AnalyzeCommand.CanExecute(null));
        vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => !vm.IsAnalyzing && vm.AnalyzeCommand.CanExecute(null));
    }

    private static Button BindButton(MainWindowViewModel vm, string commandProperty)
    {
        var button = new Button();
        button.SetBinding(Button.CommandProperty, new Binding(commandProperty) { Source = vm });
        PumpDispatcher();
        return button;
    }

    private static void PumpDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "AI scope/progress operation timed out.");
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
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "WPF AI scope/progress test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousDataDirectory = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        private readonly string _path = Path.Combine(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.Scope.Tests"), Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_path);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataDirectory);
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class RecordingProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public ConcurrentQueue<string> RequestedPaths { get; } = new();

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            foreach (var record in batch.Records) RequestedPaths.Enqueue(record.Path);
            batch.RequestContext!.ReportUsage(new AiTokenUsage(300, 100));
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record => Result(record.Path)).ToArray());
        }
    }

    private sealed class DelayedProvider(bool ignoreCancellation = false) : IAiProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CalledOnThread { get; private set; }
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            CalledOnThread = Environment.CurrentManagedThreadId;
            Started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
                batch.RequestContext!.ReportUsage(new AiTokenUsage(300, 100));
                return batch.Records.Select(record => Result(record.Path, "延迟返回的合成分析结果")).ToArray();
            }
            finally { Completed.TrySetResult(); }
        }
    }

    private sealed class MemoryStore : IAiAnalysisMemoryStore
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> _roots = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) => Task.FromResult(_roots.TryGetValue(rootPath, out var entries)
                ? entries : new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal));

        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            _roots[rootPath] = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }
}

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

public sealed class DirectoryRefreshBehaviorTests
{
    [Fact]
    public void Manual_refresh_preserves_location_updates_ancestors_and_keeps_first_level_back_working() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        var deleted = Path.Combine(nested, "deleted.bin");
        File.WriteAllBytes(deleted, new byte[16384]);
        File.WriteAllText(Path.Combine(nested, "keep.txt"), "Keep");
        var root = Scan(fixture.Path);
        using var vm = CreateViewModel(root);
        vm.ZoomIntoNodeCommand.Execute(root.Children.Single());
        vm.SelectedNode = vm.CurrentRoot!.Children.Single(node => node.Path == deleted);
        Assert.True(vm.ZoomOutCommand.CanExecute(null));
        File.Delete(deleted);

        var refresh = vm.RefreshCurrentDirectoryAsync();
        Assert.True(vm.IsRefreshing);
        Assert.False(vm.AnalyzeCommand.CanExecute(null));
        Assert.False(vm.ZoomOutCommand.CanExecute(null));
        Assert.True(vm.CancelScanCommand.CanExecute(null));
        Complete(refresh);

        Assert.False(vm.IsRefreshing);
        Assert.Equal(nested, vm.CurrentRoot?.Path);
        Assert.DoesNotContain(vm.ScanRoot!.EnumerateFiles(), node => node.Path == deleted);
        Assert.Equal(nested, vm.SelectedNode?.Path);
        Assert.True(vm.ZoomOutCommand.CanExecute(null));
        vm.ZoomOutCommand.Execute(null);
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
        Assert.False(vm.ZoomOutCommand.CanExecute(null));
        Assert.Equal(vm.ScanRoot.SizeBytes, vm.ScanRoot.Children.Sum(node => node.SizeBytes));
    });

    [Fact]
    public void Quiet_reactivation_check_removes_confirmed_deleted_visible_entries() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var deleted = Path.Combine(fixture.Path, "deleted.bin");
        File.WriteAllText(deleted, "Delete fixture");
        File.WriteAllText(Path.Combine(fixture.Path, "keep.txt"), "Keep");
        using var vm = CreateViewModel(Scan(fixture.Path));
        File.Delete(deleted);
        Complete(vm.VerifyVisibleEntriesAsync());
        Assert.DoesNotContain(vm.ScanRoot!.EnumerateFiles(), node => node.Path == deleted);
        Assert.Single(vm.ScanRoot.Children);
        Assert.Contains("已自动同步", vm.StatusText);
    });

    [Fact]
    public void Deleted_current_directory_returns_to_nearest_existing_parent() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "test.txt"), "sample");
        using var vm = CreateViewModel(Scan(fixture.Path));
        vm.ZoomIntoNodeCommand.Execute(vm.ScanRoot!.Children.Single());
        Directory.Delete(nested, recursive: true);
        Complete(vm.RefreshCurrentDirectoryAsync());
        Assert.Equal(fixture.Path, vm.CurrentRoot!.Path);
        Assert.Equal(fixture.Path, vm.SelectedNode!.Path);
        Assert.Empty(vm.ScanRoot.Children);
        Assert.False(vm.ZoomOutCommand.CanExecute(null));
        Assert.False(vm.ResetZoomCommand.CanExecute(null));
    });

    [Fact]
    public void Refresh_is_isolated_to_its_scan_task() => OnSta(() =>
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var deleted = Path.Combine(first.Path, "first.txt");
        File.WriteAllText(deleted, "sample");
        File.WriteAllText(Path.Combine(second.Path, "second.txt"), "sample");
        using var a = CreateViewModel(Scan(first.Path));
        using var b = CreateViewModel(Scan(second.Path));
        var secondRoot = b.ScanRoot;
        var secondSelection = b.SelectedNode;
        File.Delete(deleted);
        Complete(a.RefreshCurrentDirectoryAsync());
        Assert.Empty(a.ScanRoot!.Children);
        Assert.Same(secondRoot, b.ScanRoot);
        Assert.Same(secondSelection, b.SelectedNode);
        Assert.Single(b.ScanRoot!.Children);
    });

    [Fact]
    public void Canceling_explicit_refresh_preserves_original_tree() => OnSta(() =>
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "first.txt"), "sample");
        using var vm = CreateViewModel(Scan(fixture.Path));
        var original = vm.ScanRoot;
        var task = vm.RefreshCurrentDirectoryAsync();
        vm.CancelScanCommand.Execute(null);
        Complete(task);
        Assert.Same(original, vm.ScanRoot);
        Assert.False(vm.IsRefreshing);
        Assert.Contains("取消刷新", vm.StatusText);
    });

    [Fact]
    public void Closing_task_during_refresh_does_not_resurrect_disposed_snapshot() => OnSta(() =>
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Path, "first.txt"), "sample");
        var vm = CreateViewModel(Scan(fixture.Path));
        var task = vm.RefreshCurrentDirectoryAsync();
        vm.Dispose();
        Complete(task);
        Assert.Null(vm.ScanRoot);
        Assert.Null(vm.CurrentRoot);
        Assert.False(vm.IsRefreshing);
    });

    private static FileSystemNode Scan(string path) => new DirectoryScanner().ScanAsync(new ScanSessionOptions(path)).GetAwaiter().GetResult().Root;

    private static MainWindowViewModel CreateViewModel(FileSystemNode root)
    {
        var policy = new WindowsLocationPolicy();
        var vm = new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(), new DeletionPolicyService(policy),
            new ExplorerFileLocationService(), new RecycleBinService(), new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(), AppSettings.Default() with { RootPath = root.Path, HasCompletedExplorerGuide = true }, "");
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, null, null]);
        return vm;
    }

    private static void Complete(Task task)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            Assert.True(DateTimeOffset.UtcNow < until, "Refresh timed out.");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WPF refresh test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.Refresh.Tests"), "vm-fixture-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

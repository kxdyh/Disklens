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

public sealed class RecycleCommandBehaviorTests
{
    [Theory]
    [InlineData(Environment.SpecialFolder.CommonApplicationData, "Thunder Network")]
    [InlineData(Environment.SpecialFolder.ProgramFiles, "Example Application")]
    public void Protected_folder_children_allow_manual_recycle_from_both_entries(
        Environment.SpecialFolder parent, string child) => OnSta(() =>
    {
        var parentPath = Environment.GetFolderPath(parent);
        Assert.False(string.IsNullOrWhiteSpace(parentPath));
        var node = Folder(Path.Combine(parentPath, child));
        var recycle = new RejectingRecycleBinService();
        using var vm = CreateViewModel(recycle);
        vm.SelectedNode = node;
        var item = AnalysisItem(node);
        var controls = BindControls(vm, item);

        AssertAvailability(vm, item, controls, expected: true);
        Assert.Equal(0, recycle.CallCount);
    });

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\")]
    public void File_system_roots_remain_disabled_from_both_entries(string path) => OnSta(() =>
    {
        var recycle = new RejectingRecycleBinService();
        using var vm = CreateViewModel(recycle);
        vm.SelectedNode = Folder(path);
        var item = AnalysisItem(vm.SelectedNode);
        var controls = BindControls(vm, item);

        AssertAvailability(vm, item, controls, expected: false);
        Assert.Equal(0, recycle.CallCount);
    });

    [Fact]
    public void Aggregate_display_blocks_remain_disabled_from_both_entries() => OnSta(() =>
    {
        var node = Folder(@"C:\ProgramData::aggregate::other") with
        {
            RiskTags = [DisplayNodeMetadata.AggregateRiskTag],
        };
        var recycle = new RejectingRecycleBinService();
        using var vm = CreateViewModel(recycle);
        vm.SelectedNode = node;
        var item = AnalysisItem(node);
        var controls = BindControls(vm, item);

        AssertAvailability(vm, item, controls, expected: false);
        Assert.Equal(0, recycle.CallCount);
    });

    [Theory]
    [InlineData(nameof(MainWindowViewModel.IsScanning))]
    [InlineData(nameof(MainWindowViewModel.IsAnalyzing))]
    [InlineData(nameof(MainWindowViewModel.IsRefreshing))]
    public void Busy_transitions_disable_and_restore_both_bound_recycle_entries(string busyProperty) => OnSta(() =>
    {
        var recycle = new RejectingRecycleBinService();
        using var vm = CreateViewModel(recycle);
        vm.SelectedNode = Folder(@"C:\ProgramData\Thunder Network");
        var item = AnalysisItem(vm.SelectedNode);
        var controls = BindControls(vm, item);
        AssertAvailability(vm, item, controls, expected: true);

        // Set only the operation state; commands and WPF event subscriptions remain real.
        // Running a scan or invoking an enabled recycle command would touch the host.
        SetBusyState(vm, busyProperty, true);
        AssertAvailability(vm, item, controls, expected: false);
        SetBusyState(vm, busyProperty, false);
        AssertAvailability(vm, item, controls, expected: true);
        Assert.Equal(0, recycle.CallCount);
    });

    private static MainWindowViewModel CreateViewModel(IRecycleBinService recycle)
    {
        var policy = new WindowsLocationPolicy();
        return new MainWindowViewModel(
            new DirectoryScanner(), new RejectingFolderPickerService(), new DeletionPolicyService(policy),
            new RejectingFileLocationService(), recycle,
            new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(),
            AppSettings.Default() with { HasCompletedExplorerGuide = true }, string.Empty);
    }
    private static (MenuItem Menu, Button Analysis) BindControls(MainWindowViewModel vm, AnalysisListItem item)
    {
        var menu = new MenuItem();
        menu.SetBinding(MenuItem.CommandProperty,
            new Binding(nameof(vm.RecycleCommand)) { Source = vm });
        var analysis = new Button { CommandParameter = item };
        analysis.SetBinding(Button.CommandProperty,
            new Binding(nameof(vm.RecycleAnalysisItemCommand)) { Source = vm });
        PumpDispatcher();
        return (menu, analysis);
    }

    private static void AssertAvailability(MainWindowViewModel vm, AnalysisListItem item,
        (MenuItem Menu, Button Analysis) controls, bool expected)
    {
        PumpDispatcher();
        Assert.Equal(expected, vm.RecycleCommand.CanExecute(null));
        Assert.Equal(expected, vm.RecycleAnalysisItemCommand.CanExecute(item));
        Assert.Equal(expected, controls.Menu.IsEnabled);
        Assert.Equal(expected, controls.Analysis.IsEnabled);
    }

    private static void SetBusyState(MainWindowViewModel vm, string propertyName, bool value)
    {
        var property = typeof(MainWindowViewModel).GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public)!;
        property.GetSetMethod(nonPublic: true)!.Invoke(vm, [value]);
    }

    private static FileSystemNode Folder(string path) => new(
        path, Path.GetFileName(path.TrimEnd('\\')), string.Empty, 1024, FileAttributes.Directory,
        null, null, true, true, ["protected-location"], [],
        Analysis: new AiAnalysisResult(path, RiskLevel.Protected, SuggestedAction.DoNotDelete,
            "Do not delete application data without checking its purpose.", 0.95));

    private static AnalysisListItem AnalysisItem(FileSystemNode node) => new(
        node.Path, node.Name, "1 KB", RiskLevel.Protected, SuggestedAction.DoNotDelete,
        node.Analysis!.Reason, node);

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF recycle command test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RejectingFolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string initialDirectory) =>
            throw new InvalidOperationException("Command availability must not open a folder picker.");
    }

    private sealed class RejectingFileLocationService : IFileLocationService
    {
        public Task OpenLocationAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Command availability must not launch Explorer.");
    }

    private sealed class RejectingRecycleBinService : IRecycleBinService
    {
        public int CallCount { get; private set; }

        public Task RecycleAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Command availability must not recycle any real path.");
        }
    }
}

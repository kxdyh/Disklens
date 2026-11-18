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

public sealed class NavigationCommandBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_folder_entry_enables_bound_back_and_reset_buttons(bool focusMode) => OnSta(() =>
    {
        var users = Folder(@"C:\Users", Folder(@"C:\Users\Alice"));
        var root = Folder(@"C:\", users);
        var vm = CreateViewModel(root, focusMode);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));
        var reset = BindButton(vm, nameof(vm.ResetZoomCommand));
        Assert.False(back.IsEnabled);
        Assert.False(reset.IsEnabled);

        vm.ZoomIntoNodeCommand.Execute(users);
        PumpDispatcher();

        Assert.Equal(users.Path, vm.CurrentRoot?.Path);
        Assert.True(vm.CanZoomOut);
        Assert.True(vm.ZoomOutCommand.CanExecute(null));
        Assert.True(back.IsEnabled, "The bound WPF Back button must enable on the first folder entry.");
        Assert.True(reset.IsEnabled, "The bound WPF Reset button must enable on the first folder entry.");
        back.ClickButton();
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
        Assert.False(back.IsEnabled);
        Assert.False(reset.IsEnabled);
    });

    [Fact]
    public void Each_back_click_returns_exactly_one_level_and_reentry_enables_back() => OnSta(() =>
    {
        var alice = Folder(@"C:\Users\Alice");
        var users = Folder(@"C:\Users", alice);
        var root = Folder(@"C:\", users);
        var vm = CreateViewModel(root, focusMode: true);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));

        vm.ZoomIntoNodeCommand.Execute(users);
        vm.ZoomIntoNodeCommand.Execute(alice);
        Assert.True(back.IsEnabled);
        back.ClickButton();
        Assert.Equal(users.Path, vm.CurrentRoot?.Path);
        Assert.True(back.IsEnabled);
        back.ClickButton();
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
        Assert.False(back.IsEnabled);

        vm.ZoomIntoNodeCommand.Execute(users);
        Assert.True(back.IsEnabled, "Returning to root must not break the next first-level entry.");
        back.ClickButton();
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
    });

    [Fact]
    public void First_overflow_entry_enables_back_and_restores_scan_root() => OnSta(() =>
    {
        var folders = Enumerable.Range(0, 160)
            .Select(index => Folder($@"C:\Folder{index:D2}"))
            .ToArray();
        var root = Folder(@"C:\", folders);
        var vm = CreateViewModel(root, focusMode: true);
        var overflow = Assert.Single(vm.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));

        vm.ZoomIntoNodeCommand.Execute(overflow);

        Assert.Equal(overflow.Path, vm.CurrentRoot?.Path);
        Assert.True(back.IsEnabled, "An aggregate page is also a first-level navigation destination.");
        var nextPage = Assert.Single(vm.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        vm.ZoomIntoNodeCommand.Execute(nextPage);
        Assert.Equal(nextPage.Path, vm.CurrentRoot?.Path);
        back.ClickButton();
        Assert.Equal(overflow.Path, vm.CurrentRoot?.Path);
        back.ClickButton();
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
        Assert.False(back.IsEnabled);
    });

    [Fact]
    public void Reset_from_first_folder_restores_root_and_disables_navigation_buttons() => OnSta(() =>
    {
        var users = Folder(@"C:\Users");
        var root = Folder(@"C:\", users);
        var vm = CreateViewModel(root, focusMode: false);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));
        var reset = BindButton(vm, nameof(vm.ResetZoomCommand));

        vm.ZoomIntoNodeCommand.Execute(users);

        Assert.True(reset.IsEnabled);
        reset.ClickButton();
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
        Assert.False(back.IsEnabled);
        Assert.False(reset.IsEnabled);
    });

    [Fact]
    public void Switching_focus_mode_preserves_location_and_back_availability() => OnSta(() =>
    {
        var users = Folder(@"C:\Users");
        var root = Folder(@"C:\", users);
        var vm = CreateViewModel(root, focusMode: false);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));

        vm.ZoomIntoNodeCommand.Execute(users);
        Assert.True(back.IsEnabled);
        vm.ToggleFocusMode();
        Assert.True(vm.IsFocusMode);
        Assert.Equal(users.Path, vm.CurrentRoot?.Path);
        Assert.True(back.IsEnabled);
        vm.ExitFocusMode();
        Assert.False(vm.IsFocusMode);
        Assert.Equal(users.Path, vm.CurrentRoot?.Path);
        Assert.True(back.IsEnabled);
        back.ClickButton();
        Assert.Equal(root.Path, vm.CurrentRoot?.Path);
        Assert.False(back.IsEnabled);
    });

    private static MainWindowViewModel CreateViewModel(FileSystemNode root, bool focusMode)
    {
        var policy = new WindowsLocationPolicy();
        var vm = new MainWindowViewModel(
            new DirectoryScanner(), new WindowsFolderPickerService(), new DeletionPolicyService(policy),
            new ExplorerFileLocationService(), new RecycleBinService(),
            new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(),
            AppSettings.Default() with { RootPath = root.Path, HasCompletedExplorerGuide = true },
            string.Empty);
        // Feed a completed scan snapshot into the production VM. Navigation and commands
        // below use their public API, including real WPF command event subscriptions.
        var applySnapshot = typeof(MainWindowViewModel).GetMethod("ApplySnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(FileSystemNode), typeof(string), typeof(string)], null)!;
        applySnapshot.Invoke(vm, [root, null, null]);
        if (focusMode) vm.ToggleFocusMode();
        Assert.Equal(focusMode, vm.IsFocusMode);
        return vm;
    }

    private static ClickableButton BindButton(MainWindowViewModel vm, string commandProperty)
    {
        var button = new ClickableButton();
        button.SetBinding(Button.CommandProperty, new Binding(commandProperty) { Source = vm });
        PumpDispatcher();
        return button;
    }

    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(
        path, Path.GetFileName(path.TrimEnd('\\')), string.Empty,
        Math.Max(1, children.Sum(child => child.SizeBytes)), FileAttributes.Directory,
        null, null, true, false, [], children);

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF navigation test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ClickableButton : Button
    {
        public void ClickButton()
        {
            Assert.True(IsEnabled, "A disabled button cannot be clicked.");
            OnClick();
            PumpDispatcher();
        }
    }
}

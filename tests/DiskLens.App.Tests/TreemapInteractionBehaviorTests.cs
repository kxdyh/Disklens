using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DiskLens.App.Controls;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.App.Tests;

public sealed class TreemapInteractionBehaviorTests
{
    [Fact]
    public void Single_click_selects_files_and_directories_without_navigating_and_keeps_the_binding() => OnSta(() =>
    {
        var file = File(@"E:\Treemap-fixture\archive.zip", "文件的分析结果");
        var directory = Folder(@"E:\Treemap-fixture\pictures") with { Analysis = Result(@"E:\Treemap-fixture\pictures", "文件夹的分析结果") };
        var root = Folder(@"E:\Treemap-fixture", file, directory);
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));
        var reset = BindButton(vm, nameof(vm.ResetZoomCommand));

        foreach (var node in new[] { directory, file, directory, file })
        {
            Click(treemap, node, clickCount: 1);
            Assert.Equal(node.Path, vm.SelectedNode?.Path);
            Assert.Equal(node.Analysis!.Reason, vm.SelectedAiReasonText);
            Assert.Same(root, vm.CurrentRoot);
            Assert.False(back.IsEnabled);
            Assert.False(reset.IsEnabled);
            Assert.True(BindingOperations.IsDataBound(treemap, TreemapControl.SelectedNodeProperty));
        }

        Click(treemap, file, clickCount: 2);
        Assert.Equal(file.Path, vm.SelectedNode?.Path);
        Assert.Same(root, vm.CurrentRoot);
    });

    [Fact]
    public void Directory_double_click_enters_once_and_first_level_back_and_root_buttons_work() => OnSta(() =>
    {
        var nested = Folder(@"E:\Treemap-fixture\pictures\summer");
        var directory = Folder(@"E:\Treemap-fixture\pictures", nested);
        var root = Folder(@"E:\Treemap-fixture", directory);
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));
        var reset = BindButton(vm, nameof(vm.ResetZoomCommand));

        Click(treemap, directory, 1);
        Assert.Same(root, vm.CurrentRoot);
        Click(treemap, directory, 2);
        Assert.Equal(directory.Path, vm.CurrentRoot?.Path);
        Assert.True(back.IsEnabled);
        Assert.True(reset.IsEnabled);
        Click(treemap, directory, 3);
        back.ClickButton();
        Assert.Same(root, vm.CurrentRoot);
        Assert.False(back.IsEnabled);
        Assert.False(reset.IsEnabled);

        Click(treemap, directory, 1);
        Click(treemap, directory, 2);
        reset.ClickButton();
        Assert.Same(root, vm.CurrentRoot);
        Assert.Same(root, vm.SelectedNode);
        Assert.False(back.IsEnabled);

        Click(treemap, directory, 1);
        Click(treemap, directory, 2);
        Click(treemap, nested, 1);
        Click(treemap, nested, 2);
        back.ClickButton();
        Assert.Equal(directory.Path, vm.CurrentRoot?.Path);
        back.ClickButton();
        Assert.Same(root, vm.CurrentRoot);
        Assert.False(back.IsEnabled);
    });

    [Fact]
    public void Overflow_keeps_single_click_paging_but_a_double_click_never_skips_the_next_page() => OnSta(() =>
    {
        var root = Folder(@"E:\Treemap-fixture", Enumerable.Range(0, 160).Select(n => Folder($@"E:\Treemap-fixture\folder{n:D3}")).ToArray());
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        var back = BindButton(vm, nameof(vm.ZoomOutCommand));
        var reset = BindButton(vm, nameof(vm.ResetZoomCommand));
        var first = Assert.Single(vm.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        Click(treemap, first, 1);
        Assert.Equal(first.Path, vm.CurrentRoot?.Path);
        var next = Assert.Single(vm.TreemapDisplayRoot!.Children, DisplayNodeMetadata.IsOverflow);
        Click(treemap, next, 2);
        Assert.Equal(first.Path, vm.CurrentRoot?.Path);
        Click(treemap, next, 1);
        Assert.Equal(next.Path, vm.CurrentRoot?.Path);
        back.ClickButton();
        Assert.Equal(first.Path, vm.CurrentRoot?.Path);
        back.ClickButton();
        Assert.Same(root, vm.CurrentRoot);
        Assert.False(back.IsEnabled);
        Click(treemap, first, 1);
        reset.ClickButton();
        Assert.Same(root, vm.CurrentRoot);
        Assert.Same(root, vm.SelectedNode);
    });

    [Fact]
    public void Space_selects_without_navigation_and_Enter_enters_only_the_selected_directory() => OnSta(() =>
    {
        var file = File(@"E:\Treemap-fixture\archive.zip", "文件结果");
        var directory = Folder(@"E:\Treemap-fixture\pictures");
        var root = Folder(@"E:\Treemap-fixture", file, directory);
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        using var source = new HwndSource(new HwndSourceParameters("DiskLens keyboard input test") { Width = 1, Height = 1, WindowStyle = 0 });
        vm.SelectedNode = directory;
        PressKey(treemap, source, Key.Space);
        Assert.Equal(directory.Path, vm.SelectedNode?.Path);
        Assert.Same(root, vm.CurrentRoot);
        PressKey(treemap, source, Key.Enter);
        Assert.Equal(directory.Path, vm.CurrentRoot?.Path);
        vm.ResetZoomCommand.Execute(null);
        vm.SelectedNode = file;
        PressKey(treemap, source, Key.Enter);
        Assert.Equal(file.Path, vm.SelectedNode?.Path);
        Assert.Same(root, vm.CurrentRoot);
    });

    [Fact]
    public void Scanning_clicks_and_keyboard_show_the_notice_without_selection_or_navigation() => OnSta(() =>
    {
        var directory = Folder(@"E:\Treemap-fixture\pictures");
        var root = Folder(@"E:\Treemap-fixture", directory);
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        // A partial scan already has tiles, but no navigation is permitted yet.
        typeof(MainWindowViewModel).GetProperty(nameof(vm.IsScanning))!.SetValue(vm, true);
        var selection = vm.SelectedNode;
        Assert.True(treemap.IsScanning);
        foreach (var count in new[] { 1, 2 })
        {
            Click(treemap, directory, count);
            Assert.True(vm.IsScanNavigationNoticeOpen);
            Assert.Same(selection, vm.SelectedNode);
            Assert.Same(root, vm.CurrentRoot);
            vm.CloseScanNavigationNoticeCommand.Execute(null);
        }
        Click(treemap, directory, 1, rightButton: true);
        Assert.True(vm.IsScanNavigationNoticeOpen);
        Assert.Same(selection, vm.SelectedNode);
        vm.CloseScanNavigationNoticeCommand.Execute(null);
        using var source = new HwndSource(new HwndSourceParameters("DiskLens blocked keyboard test") { Width = 1, Height = 1, WindowStyle = 0 });
        foreach (var key in new[] { Key.Space, Key.Enter })
        {
            PressKey(treemap, source, key);
            Assert.True(vm.IsScanNavigationNoticeOpen);
            Assert.Same(root, vm.CurrentRoot);
            Assert.Same(selection, vm.SelectedNode);
            vm.CloseScanNavigationNoticeCommand.Execute(null);
        }
    });

    [Fact]
    public void Reanalysis_mode_checks_on_single_click_and_never_navigates_on_double_click() => OnSta(() =>
    {
        var directory = Folder(@"E:\Treemap-fixture\pictures");
        var root = Folder(@"E:\Treemap-fixture", directory);
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        var selected = new HashSet<string>();
        var toggles = 0;
        treemap.IsReanalysisSelectionMode = true;
        treemap.ToggleReanalysisSelectionCommand = new DiskLens.App.Infrastructure.RelayCommand(value =>
        {
            var path = Assert.IsType<string>(value);
            if (!selected.Add(path)) selected.Remove(path);
            toggles++;
            treemap.ReanalysisSelectedPaths = selected.ToHashSet();
        });

        Click(treemap, directory, 1);
        Assert.Contains(directory.Path, selected);
        Click(treemap, directory, 2);
        Assert.Equal(1, toggles);
        Assert.Same(root, vm.CurrentRoot);
        Click(treemap, directory, 1);
        Assert.Empty(selected);
        Assert.Equal(2, toggles);
    });

    [Fact]
    public void Reanalysis_mode_space_toggles_the_focused_tile_instead_of_entering_it() => OnSta(() =>
    {
        var directory = Folder(@"E:\Treemap-fixture\pictures");
        var root = Folder(@"E:\Treemap-fixture", directory);
        using var vm = CreateViewModel(root);
        var treemap = BindTreemap(vm);
        treemap.IsReanalysisSelectionMode = true;
        string? checkedPath = null;
        treemap.ToggleReanalysisSelectionCommand = new DiskLens.App.Infrastructure.RelayCommand(value => checkedPath = (string?)value);
        vm.SelectedNode = directory;
        using var source = new HwndSource(new HwndSourceParameters("DiskLens reanalysis keyboard test")
            { Width = 1, Height = 1, WindowStyle = 0 });
        PressKey(treemap, source, Key.Space);
        Assert.Equal(directory.Path, checkedPath);
        Assert.Same(root, vm.CurrentRoot);
    });

    private static TreemapControl BindTreemap(MainWindowViewModel vm)
    {
        var treemap = new TreemapControl { EnableViewportTransitions = false, Focusable = true };
        treemap.SetBinding(TreemapControl.RootNodeProperty, new Binding(nameof(vm.TreemapDisplayRoot)) { Source = vm });
        treemap.SetBinding(TreemapControl.SelectedNodeProperty, new Binding(nameof(vm.SelectedNode)) { Source = vm, Mode = BindingMode.TwoWay });
        treemap.SetBinding(TreemapControl.ZoomIntoCommandProperty, new Binding(nameof(vm.ZoomIntoNodeCommand)) { Source = vm });
        treemap.SetBinding(TreemapControl.ScanNavigationBlockedCommandProperty, new Binding(nameof(vm.ShowScanNavigationNoticeCommand)) { Source = vm });
        treemap.SetBinding(TreemapControl.IsScanningProperty, new Binding(nameof(vm.IsScanning)) { Source = vm });
        PumpDispatcher();
        return treemap;
    }

    private static void Click(TreemapControl treemap, FileSystemNode node, int clickCount, bool rightButton = false)
    {
        // Supply one rendered hit region; WPF still constructs and handles the
        // actual MouseButtonEventArgs, including its double-click count.
        typeof(TreemapControl).GetField("_rectangles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(treemap, new TreemapRectangle[] { new(node, -1_000_000, -1_000_000, 2_000_000, 2_000_000, 1) });
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, rightButton ? MouseButton.Right : MouseButton.Left)
        { RoutedEvent = rightButton ? UIElement.MouseRightButtonUpEvent : UIElement.MouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(args.ClickCount))!.SetValue(args, clickCount);
        var method = rightButton ? "OnMouseRightButtonUp" : "OnMouseLeftButtonDown";
        typeof(TreemapControl).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(treemap, [args]);
        PumpDispatcher();
        Assert.True(args.Handled);
    }

    private static void PressKey(TreemapControl treemap, PresentationSource source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
        typeof(TreemapControl).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(treemap, [args]);
        PumpDispatcher();
        Assert.True(args.Handled);
    }

    private static MainWindowViewModel CreateViewModel(FileSystemNode root)
    {
        var policy = new WindowsLocationPolicy();
        var vm = new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(),
            new DeletionPolicyService(policy), new ExplorerFileLocationService(), new RecycleBinService(),
            new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(),
            AppSettings.Default() with { RootPath = root.Path, HasCompletedExplorerGuide = true }, string.Empty);
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, null, null]);
        return vm;
    }

    private static ClickableButton BindButton(MainWindowViewModel vm, string property)
    {
        var button = new ClickableButton();
        button.SetBinding(Button.CommandProperty, new Binding(property) { Source = vm });
        PumpDispatcher();
        return button;
    }

    private static FileSystemNode File(string path, string reason) => new(path, Path.GetFileName(path), Path.GetExtension(path),
        2L * 1024 * 1024 * 1024, FileAttributes.Normal, null, null, false, false, [], []) { Analysis = Result(path, reason) };

    private static AiAnalysisResult Result(string path, string reason) => new(path, RiskLevel.Review, SuggestedAction.Review, reason, 0.9)
    { ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "downloads" };

    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(path,
        Path.GetFileName(path.TrimEnd('\\')), string.Empty, Math.Max(1, children.Sum(child => child.SizeBytes)),
        FileAttributes.Directory, null, null, true, false, [], children);

    private static void PumpDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF treemap input test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ClickableButton : Button
    {
        public void ClickButton()
        {
            Assert.True(IsEnabled);
            OnClick();
            PumpDispatcher();
        }
    }
}

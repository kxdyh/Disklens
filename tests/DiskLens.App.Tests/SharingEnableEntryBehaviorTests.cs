using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_entry_enables_closes_to_one_toast_and_next_settings_entry_opens_the_disable_confirmation(bool failFirst) => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(true));
        var policy = new WindowsLocationPolicy();
        using var vm = new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(),
            new DeletionPolicyService(policy), new ExplorerFileLocationService(), new RecycleBinService(),
            new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(), AppSettings.Default() with { HasCompletedExplorerGuide = true }, "");
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedRuntime))!.SetValue(vm, fixture.Runtime);
        var window = new MainWindow
        {
            DataContext = vm, SharedRuntime = fixture.Runtime, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10_000, Top = -10_000
        };
        var toast = (Border)window.FindName("SharingEnabledToast");
        var visibleTransitions = 0;
        var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border));
        EventHandler observed = (_, _) => { if (toast.Visibility == Visibility.Visible) visibleTransitions++; };
        visibility.AddValueChanged(toast, observed);
        try
        {
            window.Show(); Pump();
            if (failFirst)
            {
                fixture.Client.BeforeOperation = _ => Task.FromException(new OperationCanceledException("synthetic registration timeout"));
                DriveEntry(window, child =>
                {
                    var failed = Assert.IsType<SharedKnowledgeWindow>(child);
                    PrepareEnable(failed);
                    Invoke(failed, "Save_OnClick");
                    WaitUntil(() => !Busy(failed));
                    Assert.True(failed.IsVisible);
                    Assert.False(ReloadConsent(fixture).CanShare);
                    Assert.Equal(Visibility.Collapsed, toast.Visibility);
                    failed.Close();
                });
                Assert.Equal(0, visibleTransitions);
                fixture.Client.BeforeOperation = null;
            }

            DriveEntry(window, child =>
            {
                var enable = Assert.IsType<SharedKnowledgeWindow>(child);
                PrepareEnable(enable);
                Invoke(enable, "Save_OnClick");
            });
            WaitUntil(() => toast.Visibility == Visibility.Visible);
            Assert.Equal("文件用途经验共享已开启", ((TextBlock)window.FindName("SharingEnabledToastText")).Text);
            Assert.Equal(1, visibleTransitions);
            Assert.True(ReloadConsent(fixture).CanShare);
            Assert.True(vm.ShowSharedExperienceAnalysis);
            Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("SharedExperienceEntryButton")).Visibility);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("SharedExperienceEnabledNotice")).Visibility);

            // Once dismissed by its timer, reopening and keeping enabled must not announce again.
            var toastTimer = (DispatcherTimer)typeof(MainWindow).GetField("_sharingEnabledToastTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            toastTimer.Interval = TimeSpan.FromMilliseconds(1);
            WaitUntil(() => toast.Visibility == Visibility.Collapsed);
            DriveEntry(window, child =>
            {
                var disable = Assert.IsType<SharedKnowledgeDisableWindow>(child);
                Assert.Null(disable.FindName("IntroPage"));
                ((Button)disable.FindName("KeepEnabledButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(1, visibleTransitions);
            Assert.Equal(Visibility.Collapsed, toast.Visibility);
            Assert.True(ReloadConsent(fixture).CanShare);
        }
        finally { visibility.RemoveValueChanged(toast, observed); window.Close(); }
    });

    private static void DriveEntry(MainWindow window, Action<Window> inspect)
    {
        Exception? failure = null;
        var inspected = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            var child = window.OwnedWindows.Cast<Window>().FirstOrDefault();
            if (child is null) return;
            if (child is SharedKnowledgeWindow enable
                && !(bool)typeof(SharedKnowledgeWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(enable)!) return;
            timer.Stop();
            try { inspected = true; inspect(child); }
            catch (Exception ex) { failure = ex; child.Close(); }
        };
        try
        {
            timer.Start();
            ((Button)window.FindName("SharedKnowledgeSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => inspected || failure is not null);
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
    }
}

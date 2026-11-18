using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskLens.Core.Networking;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Header_and_settings_entries_share_the_intro_or_the_small_disable_confirmation(bool enabled, bool settingsEntry) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false, networkAvailability: new EntryNetwork(true));
        if (enabled) fixture.Enable();
        var window = new MainWindow
        {
            DataContext = fixture.Vm, SharedRuntime = fixture.Runtime,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10_000, Top = -10_000
        };
        var inspected = false;
        Exception? inspectionFailure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            var child = window.OwnedWindows.Cast<Window>().FirstOrDefault();
            if (child is null) return;
            if (child is SharedKnowledgeWindow && !(bool)typeof(SharedKnowledgeWindow)
                .GetField("_initialized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(child)!) return;
            timer.Stop();
            try
            {
                if (enabled)
                {
                    var confirmation = Assert.IsType<SharedKnowledgeDisableWindow>(child);
                    Assert.Null(confirmation.FindName("IntroPage"));
                    ((Button)confirmation.FindName("KeepEnabledButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    var intro = Assert.IsType<SharedKnowledgeWindow>(child);
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)intro.FindName("IntroPage")).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)intro.FindName("ManagementPage")).Visibility);
                    Assert.False(fixture.Vm.IsSettingsOpen);
                }
                inspected = true;
            }
            catch (Exception error) { inspectionFailure = error; }
            finally { child.Close(); }
        };
        try
        {
            window.Show(); Pump();
            fixture.Vm.IsSettingsOpen = settingsEntry;
            timer.Start();
            var entry = (Button)window.FindName(settingsEntry ? "SharedKnowledgeSettingsButton" : "SharedExperienceEntryButton");
            entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => inspected || inspectionFailure is not null);
            if (inspectionFailure is not null) throw inspectionFailure;
            var consentTask = fixture.Store.GetSnapshotAsync();
            Complete(consentTask);
            Assert.Equal(enabled, consentTask.GetAwaiter().GetResult().CanShare);
        }
        finally { timer.Stop(); window.Close(); }
    });

    private sealed class EntryNetwork(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable => available; }
}

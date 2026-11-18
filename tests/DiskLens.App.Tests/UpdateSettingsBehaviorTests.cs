using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskLens.App.Services;
using DiskLens.Core.Updates;

namespace DiskLens.App.Tests;

public sealed class UpdateSettingsBehaviorTests
{
    [Fact]
    public void Direct_window_construction_can_initialize_updates_without_starting_a_request_and_releases_owned_services() => OnSta(() =>
    {
        var window = new MainWindow();
        try
        {
            Assert.Null(window.UpdateCoordinator);
            var initialize = typeof(MainWindow).GetMethod("EnsureUpdateCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var coordinator = Assert.IsType<UpdateCheckCoordinator>(initialize.Invoke(window, null));

            Assert.Same(coordinator, window.UpdateCoordinator);
            Assert.Same(coordinator, initialize.Invoke(window, null));
            Assert.False(coordinator.IsChecking);
            Assert.Equal("启动时自动检查新版，也可在此手动检查。", coordinator.StatusText);
            Assert.Equal($"DiskLens {typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}", coordinator.CurrentVersionText);
            Assert.True(coordinator.CheckCommand.CanExecute(null));
            var client = Assert.IsType<HttpClient>(typeof(MainWindow).GetField("_ownedUpdateHttpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));

            window.Close();

            Assert.False(coordinator.CheckCommand.CanExecute(null));
            Assert.Throws<ObjectDisposedException>(() => client.Timeout = TimeSpan.FromSeconds(1));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_button_click_checks_the_injected_service_and_reports_the_actual_result(bool networkFailure) => OnSta(() =>
    {
        var feed = new LocalFeed(networkFailure);
        using var coordinator = new UpdateCheckCoordinator(feed, new EmptyPreferences(), new Version(1, 1, 0));
        var window = new MainWindow { UpdateCoordinator = coordinator };
        try
        {
            var button = Assert.IsType<Button>(window.FindName("CheckForUpdatesButton"));

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            Assert.Equal(1, feed.Requests);
            Assert.Same(coordinator, window.UpdateCoordinator);
            if (networkFailure) Assert.Contains("暂时无法检查更新", coordinator.StatusText);
            else Assert.Equal("当前已是最新版本：1.1.0", coordinator.StatusText);
            Assert.Equal(coordinator.StatusText, Assert.IsType<TextBlock>(window.FindName("UpdateCheckStatusText")).Text);
            Assert.Equal("检查更新", button.Content);
            Assert.False(coordinator.IsChecking);
            window.Close();
            Assert.True(coordinator.CheckCommand.CanExecute(null));
        }
        finally { window.Close(); }
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA update settings test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class LocalFeed(bool networkFailure) : IReleaseFeedClient
    {
        public int Requests { get; private set; }
        public Task<ReleaseManifest?> FetchAsync(CancellationToken cancellationToken)
        {
            Requests++;
            if (networkFailure) return Task.FromException<ReleaseManifest?>(new HttpRequestException("test offline"));
            return Task.FromResult<ReleaseManifest?>(new ReleaseManifest(new Version(1, 1, 0), new DateOnly(2026, 11, 6),
                ReleaseManifest.OfficialDownloadPage, ["测试更新说明"]));
        }
    }

    private sealed class EmptyPreferences : IUpdatePreferenceStore
    {
        public Task<string?> LoadIgnoredVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SaveIgnoredVersionAsync(string version, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

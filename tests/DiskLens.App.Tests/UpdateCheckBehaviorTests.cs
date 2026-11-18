using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using DiskLens.App.Services;
using DiskLens.Core.Updates;

namespace DiskLens.App.Tests;

public sealed class UpdateCheckBehaviorTests
{
    private static ReleaseManifest Release(string version = "1.2.0") => new(new Version(version),
        new DateOnly(2026, 11, 6), ReleaseManifest.OfficialDownloadPage, ["自动更新文件块", "AI 进度及费用估算"]);

    [Fact]
    public async Task Startup_is_checked_once_per_application_not_once_per_task()
    {
        var feed = new Feed(Release());
        using var coordinator = new UpdateCheckCoordinator(feed, new Preferences(), new Version(1, 1, 0));
        var offers = new List<string>();
        coordinator.UpdateAvailable += (_, release) => offers.Add(release.VersionText);
        await coordinator.CheckAtStartupAsync();
        await coordinator.CheckAtStartupAsync();
        await coordinator.CheckAtStartupAsync();
        Assert.Single(offers);
        Assert.Equal(1, feed.Requests);
        Assert.False(coordinator.IsChecking);
    }

    [Fact]
    public async Task Ignore_is_exact_persistent_and_later_release_still_shown()
    {
        var preferences = new Preferences();
        var feed = new Feed(Release());
        using (var first = new UpdateCheckCoordinator(feed, preferences, new Version(1, 1, 0)))
        {
            Assert.True(await first.IgnoreAsync(Release()));
        }
        using var nextLaunch = new UpdateCheckCoordinator(feed, preferences, new Version(1, 1, 0));
        var offers = new List<string>();
        nextLaunch.UpdateAvailable += (_, release) => offers.Add(release.VersionText);
        await nextLaunch.CheckAtStartupAsync();
        Assert.Empty(offers);
        feed.Release = Release("1.6.0");
        await nextLaunch.CheckAsync(manual: false);
        Assert.Equal(["1.6.0"], offers);
    }

    [Fact]
    public async Task Closing_offer_without_ignore_allows_next_launch_to_notify()
    {
        var preferences = new Preferences();
        var offers = 0;
        for (var i = 0; i < 2; i++)
        {
            using var coordinator = new UpdateCheckCoordinator(new Feed(Release()), preferences, new Version(1, 1, 0));
            coordinator.UpdateAvailable += (_, _) => offers++;
            await coordinator.CheckAtStartupAsync();
        }
        Assert.Equal(2, offers);
        Assert.Null(preferences.IgnoredVersion);
    }

    [Fact]
    public async Task Manual_check_can_rediscover_ignored_release()
    {
        using var coordinator = new UpdateCheckCoordinator(new Feed(Release()), new Preferences { IgnoredVersion = "1.2.0" }, new Version(1, 1, 0));
        var offers = 0;
        coordinator.UpdateAvailable += (_, _) => offers++;
        await coordinator.CheckAtStartupAsync();
        await coordinator.CheckAsync(manual: true);
        Assert.Equal(1, offers);
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.0.0")]
    public async Task Manual_check_of_current_or_older_release_clearly_reports_installed_version(string publishedVersion)
    {
        using var coordinator = new UpdateCheckCoordinator(new Feed(Release(publishedVersion)), new Preferences(), new Version(1, 1, 0));
        var offers = 0;
        coordinator.UpdateAvailable += (_, _) => offers++;

        await coordinator.CheckAsync(manual: true);

        Assert.Equal("当前已是最新版本：1.1.0", coordinator.StatusText);
        Assert.Equal("检查更新", coordinator.CheckButtonText);
        Assert.False(coordinator.IsChecking);
        Assert.True(coordinator.CheckCommand.CanExecute(null));
        Assert.Equal(0, offers);
    }

    [Fact]
    public async Task Manual_check_exposes_checking_state_until_response_and_restores_button_after_completion()
    {
        var feed = new DeferredFeed();
        using var coordinator = new UpdateCheckCoordinator(feed, new Preferences(), new Version(1, 1, 0));
        var buttonLabels = new List<string>();
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateCheckCoordinator.CheckButtonText)) buttonLabels.Add(coordinator.CheckButtonText);
        };

        var checking = coordinator.CheckAsync(manual: true);

        Assert.Equal("正在检查更新…", coordinator.StatusText);
        Assert.Equal("正在检查更新…", coordinator.CheckButtonText);
        Assert.True(coordinator.IsChecking);
        Assert.False(coordinator.CheckCommand.CanExecute(null));
        feed.Completion.SetResult(Release("1.1.0"));
        await checking;

        Assert.Equal("当前已是最新版本：1.1.0", coordinator.StatusText);
        Assert.Equal(["正在检查更新…", "检查更新"], buttonLabels);
        Assert.True(coordinator.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manual_check_with_unavailable_feed_reports_failure_and_allows_retry()
    {
        var feed = new Feed(null);
        using var coordinator = new UpdateCheckCoordinator(feed, new Preferences(), new Version(1, 1, 0));

        await coordinator.CheckAsync(manual: true);

        Assert.Contains("暂时无法检查更新", coordinator.StatusText);
        Assert.False(coordinator.IsChecking);
        Assert.Equal("检查更新", coordinator.CheckButtonText);
        Assert.True(coordinator.CheckCommand.CanExecute(null));
        feed.Release = Release("1.1.0");
        await coordinator.CheckAsync(manual: true);
        Assert.Equal("当前已是最新版本：1.1.0", coordinator.StatusText);
        Assert.Equal(2, feed.Requests);
    }

    [Fact]
    public async Task Manual_check_timeout_is_visible_and_reenables_check_button()
    {
        using var coordinator = new UpdateCheckCoordinator(new Feed(null) { Failure = new TaskCanceledException("test deadline") },
            new Preferences(), new Version(1, 1, 0));

        await coordinator.CheckAsync(manual: true);

        Assert.Equal("更新检查超时，请稍后重试。", coordinator.StatusText);
        Assert.False(coordinator.IsChecking);
        Assert.Equal("检查更新", coordinator.CheckButtonText);
        Assert.True(coordinator.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Network_failure_is_silent_on_startup_and_explained_on_manual_check()
    {
        using var coordinator = new UpdateCheckCoordinator(new Feed(null) { Failure = new HttpRequestException("offline") },
            new Preferences(), new Version(1, 1, 0));
        var originalStatus = coordinator.StatusText;
        var offers = 0;
        coordinator.UpdateAvailable += (_, _) => offers++;
        await coordinator.CheckAtStartupAsync();
        Assert.Equal(originalStatus, coordinator.StatusText);
        await coordinator.CheckAsync(manual: true);
        Assert.Contains("暂时无法检查更新", coordinator.StatusText);
        Assert.Equal(0, offers);
        Assert.True(coordinator.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Null_invalid_feed_is_silent_and_current_version_does_not_offer_downgrade()
    {
        var feed = new Feed(null);
        using var coordinator = new UpdateCheckCoordinator(feed, new Preferences(), new Version(1, 1, 0));
        var offers = 0;
        coordinator.UpdateAvailable += (_, _) => offers++;
        await coordinator.CheckAtStartupAsync();
        feed.Release = Release("1.1.0");
        await coordinator.CheckAsync(manual: true);
        Assert.Contains("当前已是最新版本", coordinator.StatusText);
        Assert.Equal(0, offers);
        Assert.Equal("DiskLens 1.1.0", coordinator.CurrentVersionText);
    }

    [Fact]
    public async Task Concurrent_checks_are_coalesced_and_late_response_after_dispose_does_not_popup()
    {
        var feed = new DeferredFeed();
        var coordinator = new UpdateCheckCoordinator(feed, new Preferences(), new Version(1, 1, 0));
        var offers = 0;
        coordinator.UpdateAvailable += (_, _) => offers++;
        var startup = coordinator.CheckAtStartupAsync();
        await coordinator.CheckAsync(manual: true);
        Assert.Equal(1, feed.Requests);
        Assert.True(coordinator.IsChecking);
        Assert.False(coordinator.CheckCommand.CanExecute(null));
        coordinator.Dispose();
        Assert.True(feed.Token.IsCancellationRequested);
        feed.Completion.SetResult(Release());
        await startup;
        Assert.Equal(0, offers);
        Assert.False(coordinator.IsChecking);
        Assert.False(coordinator.CheckCommand.CanExecute(null));
        coordinator.Dispose();
    }

    [Fact]
    public async Task Ignore_save_failure_is_reported_without_claiming_saved()
    {
        using var coordinator = new UpdateCheckCoordinator(new Feed(Release()), new Preferences { FailSave = true }, new Version(1, 1, 0));
        Assert.False(await coordinator.IgnoreAsync(Release()));
        Assert.Contains("未能保存忽略记录", coordinator.StatusText);
    }

    [Fact]
    public async Task Preference_store_persists_separately_from_application_settings_and_recovers_corruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiskLens-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(settings, "settings-must-stay-identical");
            var store = new UpdatePreferenceStore(directory);
            Assert.Null(await store.LoadIgnoredVersionAsync(CancellationToken.None));
            await store.SaveIgnoredVersionAsync("1.2.0", CancellationToken.None);
            var reopened = new UpdatePreferenceStore(directory);
            Assert.Equal("1.2.0", await reopened.LoadIgnoredVersionAsync(CancellationToken.None));
            Assert.Equal("settings-must-stay-identical", await File.ReadAllTextAsync(settings));
            await File.WriteAllTextAsync(Path.Combine(directory, "update-preferences.json"), "broken-json");
            Assert.Null(await reopened.LoadIgnoredVersionAsync(CancellationToken.None));
            await reopened.SaveIgnoredVersionAsync("1.6.0", CancellationToken.None);
            Assert.Equal("1.6.0", await reopened.LoadIgnoredVersionAsync(CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "update-preferences.json.tmp")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Malformed_unicode_from_http_is_silent_on_startup_and_does_not_disable_future_checks()
    {
        using var client = new HttpClient(new InvalidUnicodeHandler());
        using var coordinator = new UpdateCheckCoordinator(new ReleaseFeedClient(client), new Preferences(), new Version(1, 1, 0));
        var initialStatus = coordinator.StatusText;
        var offers = 0;
        coordinator.UpdateAvailable += (_, _) => offers++;
        await coordinator.CheckAtStartupAsync();
        Assert.Equal(initialStatus, coordinator.StatusText);
        Assert.Equal(0, offers);
        await coordinator.CheckAsync(manual: true);
        Assert.Contains("暂时无法检查更新", coordinator.StatusText);
        Assert.False(coordinator.IsChecking);
        Assert.True(coordinator.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Readonly_temporary_file_save_failure_releases_gate_so_next_check_and_save_can_recover()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiskLens-update-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "update-preferences.json.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, "test-owned readonly temporary file");
            File.SetAttributes(temporary, FileAttributes.ReadOnly);
            var store = new UpdatePreferenceStore(directory);
            using var coordinator = new UpdateCheckCoordinator(new Feed(Release()), store, new Version(1, 1, 0));
            Assert.False(await coordinator.IgnoreAsync(Release()));
            Assert.Contains("未能保存忽略记录", coordinator.StatusText);
            File.SetAttributes(temporary, FileAttributes.Normal);
            File.Delete(temporary);

            await coordinator.CheckAsync(manual: true).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(coordinator.IsChecking);
            Assert.True(coordinator.CheckCommand.CanExecute(null));
            Assert.True(await coordinator.IgnoreAsync(Release()).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("1.2.0", await store.LoadIgnoredVersionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (File.Exists(temporary)) File.SetAttributes(temporary, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class InvalidUnicodeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string json = """{"schemaVersion":1,"version":"1.2.0","publishedAt":"2026-11-06","downloadPage":"https://disklens.site/#download","notes":["\uD800"]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) });
        }
    }

    private sealed class Feed(ReleaseManifest? release) : IReleaseFeedClient
    {
        public ReleaseManifest? Release { get; set; } = release;
        public int Requests { get; private set; }
        public Exception? Failure { get; init; }
        public Task<ReleaseManifest?> FetchAsync(CancellationToken token)
        {
            Requests++;
            return Failure is { } failure ? Task.FromException<ReleaseManifest?>(failure) : Task.FromResult(Release);
        }
    }

    private sealed class DeferredFeed : IReleaseFeedClient
    {
        public TaskCompletionSource<ReleaseManifest?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<ReleaseManifest?> FetchAsync(CancellationToken token) { Requests++; Token = token; return Completion.Task; }
    }

    private sealed class Preferences : IUpdatePreferenceStore
    {
        public string? IgnoredVersion { get; set; }
        public bool FailSave { get; init; }
        public Task<string?> LoadIgnoredVersionAsync(CancellationToken token) => Task.FromResult(IgnoredVersion);
        public Task SaveIgnoredVersionAsync(string version, CancellationToken token)
        {
            if (FailSave) throw new IOException("test failure");
            IgnoredVersion = version;
            return Task.CompletedTask;
        }
    }
}

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Networking;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class WindowLayoutRegressionTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Shared_header_status_tracks_saved_consent_for_home_existing_scans_and_new_tasks(string theme) => OnSta(() =>
    {
        var previous = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS") ?? Path.GetTempPath(),
            "shared-header-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", directory);
        using var runtime = new SharedKnowledgeRuntime(new AnalysisMemoryStore(), new SharedPreferenceStore(Path.Combine(directory, "consent")),
            new HeaderClient(), networkAvailability: new HeaderNetwork());
        var settingsStore = new AppSettingsStore();
        var secretStore = new AppSecretStore();
        var scans = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var workspace = new ScanWorkspaceViewModel((settings, key) =>
        {
            var policy = new WindowsLocationPolicy();
            var vm = new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(), new DeletionPolicyService(policy),
                new ExplorerFileLocationService(), new RecycleBinService(),
                new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()), settingsStore, secretStore, settings, key);
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedRuntime))!.SetValue(vm, runtime);
            return vm;
        }, AppSettings.Default() with { RootPath = directory, HasCompletedExplorerGuide = true, ThemeId = theme }, "", settingsStore, secretStore,
            (_, _, token) => scans.Task.WaitAsync(token));
        var window = new MainWindow { DataContext = workspace.ActiveViewModel, SharedRuntime = runtime };
        try
        {
            var first = Path.Combine(directory, "scan-one");
            Directory.CreateDirectory(first);
            Complete(workspace.StartScanAsync(first));
            Assert.True(workspace.ActiveViewModel.IsScanning);
            AssertHeader(false);

            Complete(runtime.Settings.SaveAsync(new SharedPreferences(true, SharedConsentSnapshot.CurrentAgreementVersion, false,
                DateTimeOffset.UtcNow, 1, "header-consent", Installation: new("synthetic-header", "synthetic-header-token", 1))));
            WaitHeader(() => workspace.TaskTabs.All(tab => tab.ViewModel.ShowSharedExperienceAnalysis));
            foreach (var tab in workspace.TaskTabs.ToArray()) { workspace.SelectTab(tab); AssertHeader(true); }

            var second = Path.Combine(directory, "scan-two");
            Directory.CreateDirectory(second);
            Complete(workspace.StartScanAsync(second));
            WaitHeader(() => workspace.ActiveViewModel.ShowSharedExperienceAnalysis);
            AssertHeader(true);
            Assert.Equal(2, workspace.ActiveScanCount);

            Complete(runtime.Settings.SuspendAsync());
            WaitHeader(() => workspace.TaskTabs.All(tab => !tab.ViewModel.ShowSharedExperienceAnalysis));
            foreach (var tab in workspace.TaskTabs.ToArray()) { workspace.SelectTab(tab); AssertHeader(false); }
        }
        finally
        {
            scans.TrySetResult();
            Complete(Task.WhenAll(workspace.TaskTabs.Select(tab => tab.Completion)));
            window.Close();
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", previous);
        }

        void AssertHeader(bool enabled)
        {
            // The window follows the active task in production and in this fixture.
            window.DataContext = workspace.ActiveViewModel;
            var root = Layout(window, 1024, 640);
            var entry = Named<Button>(window, "SharedExperienceEntryButton");
            var notice = Named<TextBlock>(window, "SharedExperienceEnabledNotice");
            var newScan = Named<Button>(window, "NewScanButton");
            Assert.Equal(enabled ? Visibility.Collapsed : Visibility.Visible, entry.Visibility);
            Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, notice.Visibility);
            Assert.False(notice.IsHitTestVisible);
            if (enabled)
            {
                Assert.Equal("云端经验共享已开启", notice.Text);
                Assert.True(Bounds(notice, root).Right <= Bounds(newScan, root).Left);
                var header = Named<Border>(window, "AppHeader");
                Assert.True(Bounds(header, root).Contains(Bounds(notice, root)));
                Assert.True(Bounds(header, root).Contains(Bounds(newScan, root)));
            }
        }
    });

    private static void WaitHeader(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ready()) { Assert.True(DateTime.UtcNow < until); Pump(); Thread.Sleep(1); }
        Pump();
    }

    private sealed class HeaderNetwork : INetworkAvailability { public bool IsNetworkAvailable => true; }
    private sealed class HeaderClient : ISharedExperienceClient
    {
        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

// All shared-service and AI calls end in memory. These are VM behavior tests,
// including the real pipeline's distinction between an explicit miss and outage.
[Collection("Isolated AI settings")]
public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    private const string MissingText = "经验库没有这个文件哦，快使用ai分析顺便贡献出来吧";

    [Fact]
    public void Explicit_cloud_miss_is_visible_only_for_the_queried_snapshot_and_valid_consent() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Analyze();
        fixture.SelectDirectory();

        Assert.True(fixture.Vm.HasSelectedSharedMissing);
        Assert.Equal(MissingText, fixture.Vm.SelectedSharedMissingText);
        Assert.True(fixture.Client.QueryCalls > 0);

        var changes = new List<string?>();
        fixture.Vm.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        Complete(fixture.Store.SuspendAsync());
        Complete(fixture.Vm.RefreshSharedConsentAsync());
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.Contains(nameof(MainWindowViewModel.HasSelectedSharedMissing), changes);

        // Re-enabling without a fresh query must not revive the previous miss.
        fixture.Enable();
        Complete(fixture.Vm.RefreshSharedConsentAsync());
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
    });

    [Fact]
    public void Offline_query_never_claims_that_the_cloud_has_no_entry() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.Client.Offline = true;
        fixture.ApplyScan();
        fixture.Analyze();
        fixture.SelectDirectory();

        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.DoesNotContain(MissingText, fixture.Vm.SelectedSharedMissingText);
        Assert.Contains("云端经验库", fixture.Vm.StatusText);
        Assert.Contains("失败", fixture.Vm.StatusText);
    });

    [Fact]
    public void Disabled_or_unqueried_items_never_show_the_missing_invitation() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.ApplyScan();
        fixture.SelectDirectory();
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        fixture.Analyze();
        fixture.SelectDirectory();

        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
    });

    [Fact]
    public void Replacing_the_scan_snapshot_discards_previous_missing_paths_even_when_paths_match() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Analyze();
        fixture.SelectDirectory();
        Assert.True(fixture.Vm.HasSelectedSharedMissing);

        fixture.ApplyScan();
        fixture.SelectDirectory();
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
    });

    [Fact]
    public void Reanalysis_requires_explicit_checks_and_submits_only_the_selected_directory_itself() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan(withExistingAnalysis: true);
        var before = fixture.Vm.ScanRoot!;
        var directory = before.Children[0];
        var childResult = directory.Children[0].Analysis;
        var siblingResult = before.Children[1].Analysis;

        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        Assert.True(fixture.Vm.IsReanalysisSelectionMode);
        Assert.Equal(0, fixture.Vm.SelectedReanalysisCount);
        Assert.False(fixture.Vm.ReanalyzeWithoutMemoryCommand.CanExecute(null));
        Assert.Empty(fixture.Provider.RequestedPaths);

        fixture.Vm.ToggleReanalysisSelection(directory.Path);
        Assert.True(fixture.Vm.IsReanalysisSelected(directory.Path));
        Assert.Equal(1, fixture.Vm.SelectedReanalysisCount);
        Assert.True(fixture.Vm.ReanalyzeWithoutMemoryCommand.CanExecute(null));
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        WaitUntil(() => !fixture.Vm.IsAnalyzing && fixture.Provider.RequestedPaths.Count > 0);

        Assert.Equal([directory.Path], fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.QueryCalls);
        var updated = fixture.Vm.ScanRoot!;
        Assert.Equal("Synthetic selected analysis", updated.Children[0].Analysis?.Reason);
        Assert.Same(childResult, updated.Children[0].Children[0].Analysis);
        Assert.Same(siblingResult, updated.Children[1].Analysis);
        Assert.False(fixture.Vm.IsReanalysisSelectionMode);
    });

    [Fact]
    public void Canceling_reanalysis_selection_discards_checks_and_never_requests_analysis() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.ApplyScan();
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        fixture.Vm.ToggleReanalysisSelection(fixture.Vm.ScanRoot!.Children[0].Path);
        Assert.Equal(1, fixture.Vm.SelectedReanalysisCount);

        fixture.Vm.CancelReanalysisSelectionCommand.Execute(null);

        Assert.False(fixture.Vm.IsReanalysisSelectionMode);
        Assert.Equal(0, fixture.Vm.SelectedReanalysisCount);
        Assert.Empty(fixture.Provider.RequestedPaths);
    });

    [Fact]
    public void Missing_API_configuration_opens_setup_without_sending_the_checked_items() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.ApplyScan();
        var path = fixture.Vm.ScanRoot!.Children[0].Path;
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        fixture.Vm.ToggleReanalysisSelection(path);
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        WaitUntil(() => fixture.Vm.IsAiSetupOpen);

        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.True(fixture.Vm.IsReanalysisSelectionMode);
        Assert.True(fixture.Vm.IsReanalysisSelected(path));
        Assert.Contains("配置", fixture.Vm.StatusText);

        fixture.Vm.ChooseDeepSeekCommand.Execute(null);
        fixture.Vm.AiSetupApiKey = "synthetic-test-key-never-sent";
        fixture.Vm.SaveAiSetupAndAnalyzeCommand.Execute(null);
        WaitUntil(() => !fixture.Vm.IsAiSetupSaving && !fixture.Vm.IsAiSetupOpen);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.True(fixture.Vm.IsReanalysisSelectionMode);
        Assert.True(fixture.Vm.IsReanalysisSelected(path));
    });

    [Fact]
    public void Shared_upload_failure_is_visible_in_the_main_status_after_analysis() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.Client.FailContributions = true;
        fixture.ApplyScan();
        fixture.Analyze();

        Assert.True(fixture.Provider.RequestedPaths.Count > 0);
        Assert.True(fixture.Client.ContributionCalls > 0);
        Assert.Contains("上传至云端经验库失败", fixture.Vm.SharedExperienceSummaryText);
        Assert.Contains("上传至云端经验库失败", fixture.Vm.StatusText);
        Assert.DoesNotContain("个人 AI", fixture.Vm.StatusText);
        Assert.DoesNotContain("自己的 AI", fixture.Vm.StatusText);
    });

    [Fact]
    public void Multiple_checked_items_are_confirmed_together_and_never_expand_into_unchecked_children() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan(withExistingAnalysis: true);
        var snapshot = fixture.Vm.ScanRoot!;
        var directory = snapshot.Children[0];
        var file = snapshot.Children[1];
        var uncheckedChild = directory.Children[0];

        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        fixture.Vm.ToggleReanalysisSelection(directory.Path);
        fixture.Vm.ToggleReanalysisSelection(file.Path);
        Assert.Equal(2, fixture.Vm.SelectedReanalysisCount);
        Assert.Empty(fixture.Provider.RequestedBatches);
        Assert.False(fixture.Vm.IsReanalysisSelected(uncheckedChild.Path));

        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        WaitUntil(() => !fixture.Vm.IsAnalyzing && fixture.Provider.RequestedBatches.Count > 0);

        var batch = Assert.Single(fixture.Provider.RequestedBatches);
        Assert.Equal(2, batch.Count);
        Assert.True(new HashSet<string>([directory.Path, file.Path], StringComparer.Ordinal).SetEquals(batch));
        Assert.DoesNotContain(uncheckedChild.Path, fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Same(uncheckedChild.Analysis, fixture.Vm.ScanRoot!.Children[0].Children[0].Analysis);
    });

    [Fact]
    public void Settings_suspend_event_removes_the_missing_invitation_without_explicit_VM_refresh() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Analyze();
        fixture.SelectDirectory();
        Assert.True(fixture.Vm.HasSelectedSharedMissing);

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.HasSelectedSharedMissing);

        Assert.Equal("", fixture.Vm.SelectedSharedMissingText);
    });

    [Theory]
    [InlineData("policy")]
    [InlineData("scope")]
    public void Changed_consent_policy_or_exclusion_scope_invalidates_missing_paths_permanently(string change) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Analyze();
        fixture.SelectDirectory();
        Assert.True(fixture.Vm.HasSelectedSharedMissing);
        var queried = fixture.Client.QueryCalls;
        var original = fixture.Store.LoadAsync().GetAwaiter().GetResult();
        var changed = change == "policy"
            ? original with { PolicyRevision = "replacement-policy" }
            : original with { ExcludedPaths = [fixture.Vm.SelectedNode!.Path] };

        // Saving through application settings raises the production Changed event.
        Complete(fixture.Runtime.Settings.SaveAsync(changed));
        WaitUntil(() => !fixture.Vm.HasSelectedSharedMissing);
        Complete(fixture.Runtime.Settings.SuspendAsync());
        Complete(fixture.Runtime.Settings.SaveAsync(original with
        {
            Epoch = original.Epoch + 2,
            EnabledAt = DateTimeOffset.UtcNow,
            PolicyRevision = "reenabled-policy"
        }));
        Pump();

        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.Equal(queried, fixture.Client.QueryCalls);
    });

    [Fact]
    public void Late_shared_lookup_cannot_replace_a_snapshot_or_discard_checks_during_selection() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var path = snapshot.Children[0].Path;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        fixture.Vm.ToggleReanalysisSelection(path);

        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent, SharedHit(path)));

        Assert.Same(snapshot, fixture.Vm.ScanRoot);
        Assert.True(fixture.Vm.IsReanalysisSelectionMode);
        Assert.Equal(1, fixture.Vm.SelectedReanalysisCount);
        Assert.True(fixture.Vm.IsReanalysisSelected(path));
        Assert.Null(fixture.Vm.ScanRoot!.Children[0].Analysis);
    });

    [Fact]
    public void Late_shared_lookup_cannot_replace_the_snapshot_while_analysis_is_running() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeResponse = ready.Task;
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Vm.IsAnalyzing && fixture.Provider.RequestedPaths.Count > 0);
        try
        {
            Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent, SharedHit(snapshot.Children[0].Path)));

            Assert.Same(snapshot, fixture.Vm.ScanRoot);
            Assert.Null(fixture.Vm.ScanRoot!.Children[0].Analysis);
        }
        finally
        {
            ready.TrySetResult(true);
            WaitUntil(() => !fixture.Vm.IsAnalyzing);
        }
    });

    private static SharedResolutionBatch SharedHit(string path)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new AiAnalysisResult(path, RiskLevel.Review, SuggestedAction.Keep,
            "Synthetic late shared result", .9)
        {
            Origin = AiAnalysisResultOrigin.SharedExperience,
            ConfidenceLevel = AiConfidenceLevel.High,
            SharedEntry = new("generic-v3|0|path3:os-downloads/cache|purpose3-directory", "通用文件", "缓存", "cache", [], 2, 1,
                now, now.AddMinutes(15), "Synthetic late match") { ConfidenceLevel = "High" }
        };
        return new(new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal) { [path] = result }, 1, 0, false);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousDataDirectory = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        private readonly string _directory = Path.Combine(
            Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
                ?? Path.Combine(Path.GetTempPath(), "DiskLens.App.Tests"),
            "shared-selection-" + Guid.NewGuid().ToString("N"));
        public MainWindowViewModel Vm { get; }
        public SharedPreferenceStore Store { get; }
        public FakeClient Client { get; } = new();
        public RecordingProvider Provider { get; } = new();
        public Extractor FingerprintExtractor { get; } = new();
        public SharedKnowledgeRuntime Runtime { get; }

        public Fixture(bool configured, bool tokenSaving = true, INetworkAvailability? networkAvailability = null,
            bool usePersonalMemory = false)
        {
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _directory);
            Store = new(Path.Combine(_directory, "shared"));
            Runtime = new(new AnalysisMemoryStore(), Store, Client, FingerprintExtractor, networkAvailability);
            var policy = new WindowsLocationPolicy();
            var settings = AppSettings.Default() with
            {
                HasCompletedExplorerGuide = true,
                AnalysisThresholdMb = 1,
                IsAiTokenSavingModeEnabled = tokenSaving,
                ProviderKind = configured ? AiProviderKind.OpenAiCompatible : AiProviderKind.Disabled,
                AiEndpoint = configured ? "https://api.deepseek.com" : "",
                AiModel = configured ? "synthetic-analysis-model" : ""
            };
            Vm = new(new DirectoryScanner(), new WindowsFolderPickerService(), new DeletionPolicyService(policy),
                new ExplorerFileLocationService(), new RecycleBinService(),
                new AiAnalysisService([Provider], new AiCandidateBuilder(policy), new AiBatchPlanner(),
                    memoryStore: usePersonalMemory ? Runtime.Memory : null,
                    sharedKnowledge: Runtime.Pipeline, networkAvailability: networkAvailability), new AppSettingsStore(), new AppSecretStore(), settings,
                configured ? "synthetic-test-key-never-sent" : "");
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedRuntime))!
                .SetValue(Vm, Runtime);
            typeof(MainWindowViewModel).GetField("_suspendSettingsSave", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Vm, true);
        }

        public void Enable()
        {
            Complete(Store.SaveAsync(new SharedPreferences(Enabled: true,
                AgreementVersion: SharedConsentSnapshot.CurrentAgreementVersion,
                EnabledAt: DateTimeOffset.UtcNow.AddMinutes(-3), Epoch: 7,
                PolicyRevision: Guid.NewGuid().ToString("N"),
                Installation: new("synthetic-installation", "synthetic-token-never-sent", 7))));
            Complete(Vm.RefreshSharedConsentAsync());
        }

        public void ApplyScan(bool withExistingAnalysis = false)
        {
            const string rootPath = @"C:\Users\Alice\Downloads";
            var file = FileNode(rootPath + @"\cache\child.zip", withExistingAnalysis);
            var directory = new FileSystemNode(rootPath + @"\cache", "cache", "", file.SizeBytes,
                FileAttributes.Directory, null, null, true, false, [], [file], 1, 1,
                withExistingAnalysis ? Existing(rootPath + @"\cache") : null);
            var sibling = FileNode(rootPath + @"\sibling.zip", withExistingAnalysis);
            var root = new FileSystemNode(rootPath, "Downloads", "", directory.SizeBytes + sibling.SizeBytes,
                FileAttributes.Directory, null, null, true, false, [], [directory, sibling], 2, 2);
            typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(Vm, [root, null, null]);
        }

        public void Analyze()
        {
            Assert.True(Vm.AnalyzeCommand.CanExecute(null));
            var running = Vm.AnalyzeCommand.GetType().GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(running);
            Vm.AnalyzeCommand.Execute(null);
            // Local history is read before IsAnalyzing becomes true. The command
            // owns preparation and cleanup; its business predicate can remain
            // false after completion when an unconfigured provider opens setup.
            WaitUntil(() => running.GetValue(Vm.AnalyzeCommand) is false);
            Assert.False(Vm.IsAnalyzing);
        }

        public void SelectDirectory() => Vm.SelectedNode = Vm.ScanRoot!.Children[0];

        private static FileSystemNode FileNode(string path, bool analyzed) =>
            new(path, Path.GetFileName(path), ".zip", 4L * 1024 * 1024, FileAttributes.Normal,
                null, null, false, false, [], [], 1, 0, analyzed ? Existing(path) : null);

        private static AiAnalysisResult Existing(string path) => new(path, RiskLevel.Low,
            SuggestedAction.Keep, "Existing preserved result", .9)
            { ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "downloads" };

        public void Dispose()
        {
            Vm.Dispose();
            Runtime.Dispose();
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDataDirectory);
            // Preserve test artifacts for diagnostics; no cleanup touches unrelated data.
        }
    }

    private sealed class Extractor : ISharedFingerprintExtractor
    {
        public List<AiCandidateRecord> Candidates { get; } = [];

        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidates.Add(candidate);
            if (SharedFingerprintExtractor.IsExcluded(candidate.Path, exclusions))
                return Task.FromResult<SharedFingerprint?>(null);
            // Only the controlled cache directory is a reusable purpose fixture.
            // ZIP extensions remain valid anonymous observations without claiming
            // that unrelated private files have the same purpose.
            return Task.FromResult<SharedFingerprint?>(candidate.AnalysisEvidenceScope == AiAnalysisEvidenceScope.PathPurpose
                ? new("generic-v3", "0", candidate.IsDirectory ? "path3:os-downloads/cache" : "path3:os-downloads/file-zip",
                    candidate.IsDirectory ? "purpose3-directory" : "purpose3-file")
                : new("generic-v2", "0", candidate.IsDirectory ? "path2:user-downloads/cache" : "path2:user-downloads/file-zip",
                    "tree2-" + new string(candidate.IsDirectory ? 'a' : 'b', 64)));
        }
    }

    private sealed class RecordingProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public List<string> RequestedPaths { get; } = [];
        public List<IReadOnlyList<string>> RequestedBatches { get; } = [];
        public Task? BeforeResponse { get; set; }
        public Exception? Failure { get; set; }

        public async Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            RequestedBatches.Add(batch.Records.Select(record => record.Path).ToArray());
            RequestedPaths.AddRange(batch.Records.Select(record => record.Path));
            if (BeforeResponse is not null) await BeforeResponse.WaitAsync(cancellationToken);
            if (Failure is { } failure) throw failure;
            return batch.Records.Select(record =>
                new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review,
                    "Synthetic selected analysis", .9)
                { ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "cache" }).ToArray();
        }
    }

    private sealed class FakeClient : ISharedExperienceClient
    {
        public SharedPathIndex? PathIndex { get; set; }
        public bool Offline { get; set; }
        public Exception? Failure { get; set; }
        public bool FailContributions { get; set; }
        public Exception? ContributionFailure { get; set; }
        public bool ReturnMatches { get; set; }
        public bool ReturnPurposeTexts { get; set; }
        public Task? BeforeQueryResponse { get; set; }
        public Task? BeforeContributionResponse { get; set; }
        public Func<int, long, SharedContributionReceipt>? ContributionResponse { get; set; }
        public int QueryCalls { get; private set; }
        public int ContributionCalls { get; private set; }
        public Task<SharedPathIndex?> GetSharedPathIndexAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PathIndex);
        public async Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            if (BeforeQueryResponse is not null) await BeforeQueryResponse.WaitAsync(cancellationToken);
            if (Failure is { } failure) throw failure;
            if (Offline) throw new HttpRequestException("Synthetic offline query.");
            var now = DateTimeOffset.UtcNow;
            return new SharedQueryResponse(items.Select(item => new SharedQueryItem(item.Key, ReturnMatches,
                ReturnMatches ? new SharedExperienceEntry(item.Key, item.SoftwareId, "Synthetic software", item.RoleId,
                    "Synthetic role", "cache", "Keep", [], "Synthetic cloud purpose", 2, 1, "active",
                    now.AddMinutes(15), now) { ConfidenceLevel = "High",
                        ShortPurpose = ReturnPurposeTexts ? "应用临时缓存" : "",
                        Reason = ReturnPurposeTexts ? "应用生成的临时缓存，仍需核验使用状态" : "",
                        ShortPurposeEn = ReturnPurposeTexts ? "Application cache" : "",
                        ReasonEn = ReturnPurposeTexts ? "Temporary application cache. Check its current local use." : "" } : null)).ToArray());
        }

        public async Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default)
        {
            ContributionCalls++;
            if (BeforeContributionResponse is not null) await BeforeContributionResponse.WaitAsync(cancellationToken);
            if (ContributionFailure is { } failure) throw failure;
            if (FailContributions) throw new HttpRequestException("Synthetic upload failure.");
            return ContributionResponse?.Invoke(items.Count, epoch) ?? new SharedContributionReceipt(items.Count, 0, epoch);
        }

        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private static void Complete(Task task)
    {
        WaitUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Shared selection VM operation timed out.");
            Pump();
            Thread.Sleep(1);
        }
        Pump();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Shared selection WPF test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

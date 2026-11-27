using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskLens.App.Services;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.Localization;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Internationalization_preview_reformats_only_generated_text_and_preserves_consent_and_rows(bool hasScanRoot) => OnSta(() =>
    {
        L.SetLanguage("zh");
        using var fixture = new Fixture();
        var dialog = fixture.Open(hasScanRoot);
        try
        {
            Preview(dialog);
            var preview = (TextBlock)dialog.FindName("PreviewText");
            var chinese = preview.Text;
            var calls = fixture.PreviewCalls;
            L.SetLanguage("en");
            Assert.NotEqual(chinese, preview.Text);
            if (hasScanRoot)
            {
                Assert.StartsWith("1 anonymous items", preview.Text);
                Assert.Contains("合成目录 → android-sdk / sdk-emulator / Major version 35", preview.Text);
            }
            else Assert.Contains("No scan is available", preview.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
            Assert.False(Check(dialog, "AcceptFuture").IsChecked == true);
            Assert.Equal(calls, fixture.PreviewCalls);
            Assert.Equal(0, fixture.Client.RegisterCalls);
            L.SetLanguage("zh");
            Assert.Equal(chinese, preview.Text);
        }
        finally { dialog.Close(); L.SetLanguage("zh"); }
    });

    [Fact]
    public void Benefits_page_only_presents_the_two_animated_benefits_while_management_is_separate() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(true);
        Invoke(dialog, "Next_OnClick");
        WaitUntil(() => !Busy(dialog));
        Assert.True(((FrameworkElement)dialog.FindName("ScopePage")).IsVisible);
        Assert.True(((FrameworkElement)dialog.FindName("BenefitsIllustration")).IsVisible);
        Assert.Equal("无需AI，也能知道每个文件的用途", ((TextBlock)dialog.FindName("BenefitsHeading")).Text);
        Assert.Null(dialog.FindName("BenefitsTip"));
        Assert.Null(dialog.FindName("StateText"));
        Assert.Null(dialog.FindName("ServiceText"));
        foreach (var name in new[] { "ManagementPage", "PreviewButton", "ExclusionList", "ContributionList", "SaveButton" })
            Assert.False(((FrameworkElement)dialog.FindName(name)).IsVisible, name);
        Assert.Equal(0, fixture.PreviewCalls);
        Invoke(dialog, "Next_OnClick");
        WaitUntil(() => !Busy(dialog));
        Assert.True(((FrameworkElement)dialog.FindName("AgreementPage")).IsVisible);
        Assert.Equal("请阅读下列协议，决定是否加入我们的测试", ((TextBlock)dialog.FindName("AgreementHeading")).Text);
        Assert.Null(dialog.FindName("AgreementReadHint"));
        Assert.Equal(1, fixture.PreviewCalls);
        Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
        Assert.Equal(0, fixture.Client.RegisterCalls);
        dialog.Close();
    });

    [Fact]
    public void Settings_management_exposes_exclusions_without_showing_the_benefits_or_granting_consent() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(openManagement: true);
        Assert.True(((FrameworkElement)dialog.FindName("ManagementPage")).IsVisible);
        Assert.True(((FrameworkElement)dialog.FindName("ExclusionList")).IsVisible);
        Assert.False(((FrameworkElement)dialog.FindName("ScopePage")).IsVisible);
        Assert.False(((FrameworkElement)dialog.FindName("NextButton")).IsVisible);
        Assert.False(((FrameworkElement)dialog.FindName("SaveButton")).IsVisible);
        Assert.Equal(0, fixture.Client.RegisterCalls);
        Invoke(dialog, "ManagementContinue_OnClick");
        WaitUntil(() => !Busy(dialog));
        Assert.True(((FrameworkElement)dialog.FindName("IntroPage")).IsVisible);
        Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
        dialog.Close();
    });
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Missing_any_explicit_consent_never_registers_or_enables(bool read, bool agreement, bool future) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
        Assert.False(Check(dialog, "AcceptFuture").IsChecked == true);
        Assert.False(Check(dialog, "IncludeHistory").IsChecked == true);
        Preview(dialog);
        if (read) ReadAgreement(dialog);
        else ShowAgreementPage(dialog);
        Check(dialog, "AcceptAgreement").IsChecked = agreement;
        Check(dialog, "AcceptFuture").IsChecked = future;

        Invoke(dialog, "Save_OnClick");
        Pump();

        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.Equal(0, fixture.Client.UpdateCalls);
        Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        dialog.Close();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reviewed_scope_and_future_consent_can_enable_without_history_and_disable_resets_visible_consent(bool hasScanRoot) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(hasScanRoot);
        Preview(dialog);
        ReadAgreement(dialog);
        Check(dialog, "AcceptAgreement").IsChecked = true;
        Check(dialog, "AcceptFuture").IsChecked = true;
        Check(dialog, "IncludeHistory").IsChecked = false;

        Invoke(dialog, "Save_OnClick");
        WaitUntil(() => !Busy(dialog));

        var enabled = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Assert.True(enabled.CanShare);
        Assert.False(enabled.ShareHistory);
        Assert.Equal(0, fixture.HistoryCalls);
        Assert.Equal(hasScanRoot ? 1 : 0, fixture.PreviewCalls);
        Assert.Equal(1, fixture.Client.RegisterCalls);
        Assert.False(dialog.IsVisible, "Successful enablement returns to the main window.");
        dialog = fixture.Open(hasScanRoot);
        Invoke(dialog, "Disable_OnClick");
        WaitUntil(() => !Busy(dialog));

        Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.False(Check(dialog, "AcceptFuture").IsChecked == true);
        Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
        dialog.Close();
    });

    [Fact]
    public void Opening_expanding_layout_and_scrolling_do_not_bypass_the_agreement_countdown() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
        Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
        Preview(dialog);
        ShowAgreementPage(dialog);
        var scroll = AgreementScroll(dialog);
        WaitUntil(() => scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0);
        Invoke(dialog, "Agreement_OnExpanded");
        dialog.Width -= 25;
        dialog.UpdateLayout();
        Pump();
        Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);

        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
        WaitUntil(() => scroll.VerticalOffset > 0);
        Assert.True(scroll.VerticalOffset < scroll.ScrollableHeight - 1);
        Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
        Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.Equal(0, fixture.Client.UpdateCalls);

        scroll.ScrollToEnd();
        WaitUntil(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1);
        Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
        AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(5));
        WaitUntil(() => Check(dialog, "AcceptAgreement").IsEnabled);
        Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
        Check(dialog, "AcceptFuture").IsChecked = true;
        Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
        Check(dialog, "AcceptAgreement").IsChecked = true;
        Assert.True(((Button)dialog.FindName("SaveButton")).IsEnabled);
        Check(dialog, "AcceptFuture").IsChecked = false;
        Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.Equal(0, fixture.Client.UpdateCalls);
        dialog.Close();
    });

    [Fact]
    public void Explicit_consent_without_scope_preview_never_registers_or_enables() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        ReadAgreement(dialog);
        Check(dialog, "AcceptAgreement").IsChecked = true;
        Check(dialog, "AcceptFuture").IsChecked = true;

        Invoke(dialog, "Save_OnClick");
        Pump();

        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.Equal(0, fixture.Client.UpdateCalls);
        Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.Contains("预览", ((TextBlock)dialog.FindName("FeedbackText")).Text, StringComparison.Ordinal);
        dialog.Close();
    });

    [Fact]
    public void Exclusion_changes_require_another_scope_preview_before_enabling() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open(true);
        Preview(dialog);
        var changed = (Task)typeof(SharedKnowledgeWindow)
            .GetMethod("SuspendForScopeChangeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(dialog, [new[] { @"C:\private\new-exclusion" }])!;
        WaitUntil(() => changed.IsCompleted);
        changed.GetAwaiter().GetResult();
        ReadAgreement(dialog);
        Check(dialog, "AcceptAgreement").IsChecked = true;
        Check(dialog, "AcceptFuture").IsChecked = true;

        Invoke(dialog, "Save_OnClick");
        Pump();

        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.Contains("预览", ((TextBlock)dialog.FindName("FeedbackText")).Text, StringComparison.Ordinal);
        dialog.Close();
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
                ?? Path.Combine(Path.GetTempPath(), "DiskLens.Consent.Tests"),
            "dialog-" + Guid.NewGuid().ToString("N"));
        public SharedPreferenceStore Store { get; }
        public FakeClient Client { get; } = new();
        public SharedKnowledgeRuntime Runtime { get; }
        public int HistoryCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public Func<Task<SharedContributionReceipt>>? HistoryAction { get; set; }

        public Fixture(DiskLens.Core.Networking.INetworkAvailability? networkAvailability = null)
        {
            Directory.CreateDirectory(_directory);
            Store = new(_directory);
            Runtime = new(new AnalysisMemoryStore(), Store, Client, networkAvailability: networkAvailability);
        }

        public SharedKnowledgeWindow Open(bool hasScanRoot = false, string theme = "03", bool openManagement = false)
        {
            var dialog = new SharedKnowledgeWindow(Runtime, theme, preview: hasScanRoot ? _ =>
            {
                PreviewCalls++;
                return Task.FromResult<IReadOnlyList<string>>(["合成目录 → android-sdk / sdk-emulator / 主版本 35"]);
            } : null, history: () =>
            {
                HistoryCalls++;
                return HistoryAction?.Invoke() ?? Task.FromResult(new SharedContributionReceipt(0, 0, 1));
            }, openManagement: openManagement);
            typeof(SharedKnowledgeWindow).GetField("_agreementClock", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(dialog, new AgreementTestClock());
            // The visible third page owns the reading countdown. Keep tests
            // deterministic while exercising real WPF state and explicit consent.
            dialog.ShowActivated = false;
            dialog.ShowInTaskbar = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10_000;
            dialog.Top = -10_000;
            dialog.Show();
            WaitUntil(() => (bool)typeof(SharedKnowledgeWindow).GetField("_initialized", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!);
            return dialog;
        }

        public void Dispose()
        {
            Runtime.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private static CheckBox Check(SharedKnowledgeWindow dialog, string name) => (CheckBox)dialog.FindName(name);
    private static bool Busy(SharedKnowledgeWindow dialog) =>
        (bool)typeof(SharedKnowledgeWindow).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
    private static void Invoke(SharedKnowledgeWindow dialog, string method) => typeof(SharedKnowledgeWindow)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [dialog, new RoutedEventArgs()]);
    private static void Preview(SharedKnowledgeWindow dialog)
    {
        Invoke(dialog, "Preview_OnClick");
        WaitUntil(() => !Busy(dialog));
    }
    private static void ShowAgreementPage(SharedKnowledgeWindow dialog)
    {
        typeof(SharedKnowledgeWindow).GetMethod("SetPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [2]);
        dialog.UpdateLayout();
        Pump();
        Assert.True(((FrameworkElement)dialog.FindName("AgreementPage")).IsVisible);
    }
    private static ScrollViewer AgreementScroll(SharedKnowledgeWindow dialog) => (ScrollViewer)dialog.FindName("AgreementScroll");
    private static void ReadAgreement(SharedKnowledgeWindow dialog)
    {
        var selected = Check(dialog, "AcceptAgreement").IsChecked;
        ShowAgreementPage(dialog);
        var scroll = AgreementScroll(dialog);
        WaitUntil(() => scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0);
        AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(5));
        WaitUntil(() => Check(dialog, "AcceptAgreement").IsEnabled);
        Assert.Equal(selected, Check(dialog, "AcceptAgreement").IsChecked);
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    private static void WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Consent dialog operation timed out.");
            Pump();
            Thread.Sleep(1);
        }
        Pump();
    }

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
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var exporting = Environment.GetEnvironmentVariable("DISKLENS_EXPORT_FLOW_PREVIEW") == "1"
            || Environment.GetEnvironmentVariable("DISKLENS_EXPORT_BENEFITS_PREVIEW") == "1";
        Assert.True(thread.Join(TimeSpan.FromSeconds(exporting ? 120 : 30)), "Consent dialog STA timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeClient : ISharedExperienceClient
    {
        private SharedConsentState _state = new(0, false, "", false, "");
        public Func<string, Task>? BeforeOperation { get; set; }
        public Func<SharedConsentState, SharedConsentState>? AlterReceipt { get; set; }
        public int RegisterCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public async Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            if (BeforeOperation is { } before) await before("register");
            return new SharedInstallation("synthetic-installation", "synthetic-secret", 0);
        }
        public async Task<SharedConsentState> GetConsentAsync(SharedInstallation installation,
            CancellationToken cancellationToken = default)
        {
            if (BeforeOperation is { } before) await before("get-consent");
            return _state;
        }
        public async Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            if (BeforeOperation is { } before) await before("update-consent");
            _state = new(_state.Epoch + 1, update.Enabled, update.AgreementVersion, update.ShareHistory, update.PolicyRevision);
            return AlterReceipt?.Invoke(_state) ?? _state;
        }
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation,
            CancellationToken cancellationToken = default) => Task.FromResult(new SharedContributionList([]));
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch,
            string? contributionId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskLens.App.Localization;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Fact]
    public void Agreement_countdown_requires_five_visible_seconds_without_scrolling_or_selecting_consent() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        try
        {
            Preview(dialog);
            ShowAgreementPage(dialog);
            Assert.EndsWith(" - 5 s", CountdownText(dialog));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            Assert.Equal(0, AgreementScroll(dialog).VerticalOffset);
            Check(dialog, "AcceptFuture").IsChecked = true;

            AdvanceAgreementTime(dialog, TimeSpan.FromMilliseconds(4_999));
            Assert.EndsWith(" - 1 s", CountdownText(dialog));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);

            AdvanceAgreementTime(dialog, TimeSpan.FromMilliseconds(1));
            Assert.True(Check(dialog, "AcceptAgreement").IsEnabled);
            Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
            Assert.Equal(0, AgreementScroll(dialog).VerticalOffset);
            Assert.Equal("请仔细阅读协议内容再确定是否参与此测试功能", CountdownText(dialog));
            Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
            Check(dialog, "AcceptAgreement").IsChecked = true;
            Assert.True(((Button)dialog.FindName("SaveButton")).IsEnabled);
            Assert.Equal(0, fixture.Client.RegisterCalls);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Agreement_countdown_does_not_run_on_previous_or_management_pages(int page) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        try
        {
            ShowAgreementPage(dialog);
            AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(2));
            SetDialogPage(dialog, page);
            AdvanceAgreementTime(dialog, TimeSpan.FromHours(1));
            ShowAgreementPage(dialog);
            Assert.EndsWith(" - 3 s", CountdownText(dialog));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            AdvanceAgreementTime(dialog, TimeSpan.FromMilliseconds(2_999));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            AdvanceAgreementTime(dialog, TimeSpan.FromMilliseconds(1));
            Assert.True(Check(dialog, "AcceptAgreement").IsEnabled);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void Finishing_an_operation_does_not_unlock_an_incomplete_agreement_countdown() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ShowAgreementPage(dialog);
            var operation = (Task)typeof(SharedKnowledgeWindow).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(dialog, [new Func<Task>(() => completion.Task), null])!;
            Assert.True(Busy(dialog));
            AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(2));
            completion.SetResult();
            WaitUntil(() => operation.IsCompleted && !Busy(dialog));
            operation.GetAwaiter().GetResult();
            Assert.EndsWith(" - 3 s", CountdownText(dialog));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(3));
            Assert.True(Check(dialog, "AcceptAgreement").IsEnabled);
        }
        finally { completion.TrySetResult(); dialog.Close(); }
    });

    [Fact]
    public void Agreement_countdown_reformats_language_without_restarting_or_ticking_consent() => OnSta(() =>
    {
        L.SetLanguage("zh");
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        try
        {
            ShowAgreementPage(dialog);
            AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(2));
            L.SetLanguage("en");
            Assert.StartsWith("Please read the agreement carefully", CountdownText(dialog));
            Assert.EndsWith(" - 3 s", CountdownText(dialog));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(3));
            Assert.True(Check(dialog, "AcceptAgreement").IsEnabled);
            Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
            L.SetLanguage("zh");
            Assert.Equal("请仔细阅读协议内容再确定是否参与此测试功能", CountdownText(dialog));
            Assert.True(Check(dialog, "AcceptAgreement").IsEnabled);
        }
        finally { dialog.Close(); L.SetLanguage("zh"); }
    });

    [Fact]
    public void Starting_a_new_agreement_cycle_resets_the_countdown_and_explicit_selection() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        try
        {
            ReadAgreement(dialog);
            Check(dialog, "AcceptAgreement").IsChecked = true;
            typeof(SharedKnowledgeWindow).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: [typeof(bool)], modifiers: null)!
                .Invoke(dialog, [false]);
            Assert.EndsWith(" - 5 s", CountdownText(dialog));
            Assert.False(Check(dialog, "AcceptAgreement").IsEnabled);
            Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
            AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(5));
            Assert.True(Check(dialog, "AcceptAgreement").IsEnabled);
            Assert.False(Check(dialog, "AcceptAgreement").IsChecked == true);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void Closing_the_dialog_stops_its_agreement_timer_without_granting_consent() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var dialog = fixture.Open();
        ShowAgreementPage(dialog);
        AdvanceAgreementTime(dialog, TimeSpan.FromSeconds(2));
        dialog.Close();
        var timer = (DispatcherTimer)typeof(SharedKnowledgeWindow).GetField("_agreementTimer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(dialog)!;
        Assert.False(timer.IsEnabled);
        AdvanceAgreementTime(dialog, TimeSpan.FromMinutes(1));
        Assert.False((bool)typeof(SharedKnowledgeWindow).GetField("_agreementRead", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(dialog)!);
        Assert.Equal(0, fixture.Client.RegisterCalls);
        Assert.Equal(0, fixture.Client.UpdateCalls);
    });

    private static string CountdownText(SharedKnowledgeWindow dialog) => ((TextBlock)dialog.FindName("AgreementCountdownText")).Text;

    private static void SetDialogPage(SharedKnowledgeWindow dialog, int page) => typeof(SharedKnowledgeWindow)
        .GetMethod("SetPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [page]);

    private static void AdvanceAgreementTime(SharedKnowledgeWindow dialog, TimeSpan elapsed)
    {
        var clockField = typeof(SharedKnowledgeWindow).GetField("_agreementClock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (clockField.GetValue(dialog) is not AgreementTestClock clock)
        {
            // MainWindow creates the real dialog directly. Preserve its elapsed
            // system-clock time before switching to a deterministic test clock.
            typeof(SharedKnowledgeWindow).GetMethod("PauseAgreementCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(dialog, null);
            clock = new AgreementTestClock();
            clockField.SetValue(dialog, clock);
            typeof(SharedKnowledgeWindow).GetMethod("ResumeAgreementCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(dialog, null);
        }
        clock.Advance(elapsed);
        typeof(SharedKnowledgeWindow).GetMethod("AgreementCountdown_OnTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(dialog, [dialog, EventArgs.Empty]);
        Pump();
    }

    private sealed class AgreementTestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("timeout")]
    [InlineData("service-failure")]
    public void Successful_enable_closes_before_optional_history_finishes_and_survives_its_failure(string completion) => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(true));
        var history = new TaskCompletionSource<SharedContributionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAfterClose = false;
        SharedKnowledgeWindow? dialog = null;
        fixture.HistoryAction = () =>
        {
            startedAfterClose = !dialog!.IsVisible;
            return history.Task;
        };
        dialog = fixture.Open(hasScanRoot: true);
        PrepareEnable(dialog, history: true);
        Invoke(dialog, "Save_OnClick");
        WaitUntil(() => !dialog.IsVisible && fixture.HistoryCalls == 1);

        Assert.True(startedAfterClose);
        Assert.True(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.True(ReloadConsent(fixture).CanShare);
        Assert.False(HistoryTask(dialog).IsCompleted);
        // A stale queued click cannot repeat registration, save, or the history import.
        Invoke(dialog, "Save_OnClick");
        Assert.Equal(1, fixture.Client.RegisterCalls);
        Assert.Equal(1, fixture.Client.UpdateCalls);
        Assert.Equal(1, fixture.HistoryCalls);

        if (completion == "timeout") history.SetException(new OperationCanceledException("synthetic history timeout"));
        else if (completion == "service-failure") history.SetException(new System.Net.Http.HttpRequestException("synthetic service failure", null, System.Net.HttpStatusCode.ServiceUnavailable));
        else history.SetResult(new(1, 0, 1));
        WaitUntil(() => HistoryTask(dialog).IsCompleted);
        HistoryTask(dialog).GetAwaiter().GetResult();

        Assert.True(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.True(ReloadConsent(fixture).CanShare);
        Assert.False(dialog.IsVisible);
    });

    [Theory]
    [InlineData("register")]
    [InlineData("get-consent")]
    [InlineData("update-consent")]
    public void Enable_network_timeout_keeps_the_dialog_open_and_allows_a_successful_retry(string operation) => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(true));
        fixture.Client.BeforeOperation = stage => stage == operation
            ? Task.FromException(new OperationCanceledException("synthetic enable timeout")) : Task.CompletedTask;
        var dialog = fixture.Open();
        PrepareEnable(dialog);
        Invoke(dialog, "Save_OnClick");
        WaitUntil(() => !Busy(dialog));

        Assert.True(dialog.IsVisible);
        Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.False(ReloadConsent(fixture).CanShare);
        Assert.Contains("尚未确认开启，请重试", ((TextBlock)dialog.FindName("FeedbackText")).Text);
        Assert.Equal(0, fixture.HistoryCalls);

        fixture.Client.BeforeOperation = null;
        Invoke(dialog, "Save_OnClick");
        WaitUntil(() => !dialog.IsVisible);
        Assert.True(ReloadConsent(fixture).CanShare);
    });

    [Fact]
    public void A_successful_HTTP_receipt_that_did_not_confirm_enable_never_closes_or_grants_local_consent() => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(true));
        fixture.Client.AlterReceipt = state => state with { Enabled = false };
        var dialog = fixture.Open();
        PrepareEnable(dialog);
        Invoke(dialog, "Save_OnClick");
        WaitUntil(() => !Busy(dialog));

        Assert.True(dialog.IsVisible);
        Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
        Assert.False(ReloadConsent(fixture).CanShare);
        Assert.Equal(0, fixture.HistoryCalls);
        fixture.Client.AlterReceipt = null;
        ReadAgreement(dialog);
        Check(dialog, "AcceptAgreement").IsChecked = true;
        Check(dialog, "AcceptFuture").IsChecked = true;
        Invoke(dialog, "Save_OnClick");
        WaitUntil(() => !dialog.IsVisible);
        Assert.True(ReloadConsent(fixture).CanShare);
    });

    [Fact]
    public void Successful_remote_consent_without_a_durable_local_save_keeps_the_dialog_open_and_can_retry() => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(true));
        var blocker = Path.Combine(Path.GetDirectoryName(StoreFile(fixture.Store))!, "intentional-save-blocker");
        File.WriteAllText(blocker, "synthetic test blocker");
        var originalPath = StoreFile(fixture.Store);
        fixture.Client.BeforeOperation = stage =>
        {
            if (stage == "update-consent") SetStoreFile(fixture.Store, Path.Combine(blocker, "shared-experience.bin"));
            return Task.CompletedTask;
        };
        var dialog = fixture.Open();
        try
        {
            PrepareEnable(dialog);
            Invoke(dialog, "Save_OnClick");
            WaitUntil(() => !Busy(dialog));
            Assert.True(dialog.IsVisible);
            Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
            Assert.False(new SharedPreferenceStore(Path.GetDirectoryName(originalPath)).GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
            Assert.Equal(0, fixture.HistoryCalls);
            Assert.Contains("未能保存", ((TextBlock)dialog.FindName("FeedbackText")).Text);

            SetStoreFile(fixture.Store, originalPath);
            fixture.Client.BeforeOperation = null;
            ReadAgreement(dialog);
            Check(dialog, "AcceptAgreement").IsChecked = true;
            Check(dialog, "AcceptFuture").IsChecked = true;
            Invoke(dialog, "Save_OnClick");
            WaitUntil(() => !dialog.IsVisible);
            Assert.True(ReloadConsent(fixture).CanShare);
        }
        finally { SetStoreFile(fixture.Store, originalPath); dialog.Close(); }
    });

    private static void PrepareEnable(SharedKnowledgeWindow dialog, bool history = false)
    {
        Preview(dialog);
        ReadAgreement(dialog);
        Check(dialog, "AcceptAgreement").IsChecked = true;
        Check(dialog, "AcceptFuture").IsChecked = true;
        Check(dialog, "IncludeHistory").IsChecked = history;
        Assert.True(((Button)dialog.FindName("SaveButton")).IsEnabled);
    }

    private static Task HistoryTask(SharedKnowledgeWindow dialog) =>
        (Task)typeof(SharedKnowledgeWindow).GetField("_backgroundHistoryTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
    private static string StoreFile(SharedPreferenceStore store) =>
        (string)typeof(SharedPreferenceStore).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    private static void SetStoreFile(SharedPreferenceStore store, string path) =>
        typeof(SharedPreferenceStore).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, path);
    private static SharedConsentSnapshot ReloadConsent(Fixture fixture) =>
        new SharedPreferenceStore(Path.GetDirectoryName(StoreFile(fixture.Store))).GetSnapshotAsync().GetAwaiter().GetResult();
}

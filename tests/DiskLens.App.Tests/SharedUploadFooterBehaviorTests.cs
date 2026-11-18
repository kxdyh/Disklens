using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using DiskLens.App.Localization;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Footer_shows_real_inflight_upload_until_the_receipt_arrives() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var root = fixture.Vm.ScanRoot;
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeContributionResponse = response.Task;
        var footer = new TextBlock();
        footer.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainWindowViewModel.FooterStatusDetailText)) { Source = fixture.Vm });
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("footer-real-inflight")]));
        var upload = fixture.Runtime.Outbox.FlushAsync();
        try
        {
            WaitUntil(() => fixture.Client.ContributionCalls > 0);
            WaitUntil(() => footer.Text.Contains("正在上传"));
            Assert.False(upload.IsCompleted);
            Assert.Contains("正在上传", FooterDetail(fixture.Vm));
            Assert.DoesNotContain("上传完成", FooterDetail(fixture.Vm));
            Assert.DoesNotContain("上传成功", FooterDetail(fixture.Vm));
            Assert.Same(root, fixture.Vm.ScanRoot);
        }
        finally { response.TrySetResult(); Complete(upload); }

        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        WaitUntil(() => footer.Text.Contains("上传完成"));
        Assert.Contains("上传完成", FooterDetail(fixture.Vm));
        Assert.Same(root, fixture.Vm.ScanRoot);
    });

    [Fact]
    public void Footer_enters_upload_stage_after_model_work_but_before_analysis_task_returns() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var root = fixture.Vm.ScanRoot;
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeContributionResponse = response.Task;
        fixture.Vm.AnalyzeCommand.Execute(null);
        try
        {
            WaitUntil(() => fixture.Client.ContributionCalls > 0 && FooterDetail(fixture.Vm).Contains("正在上传"));
            Assert.True(fixture.Vm.IsAnalyzing); // The outer task still awaits the upload receipt.
            Assert.Contains("正在上传", fixture.Vm.FooterStatusHeadlineText);
            Assert.DoesNotContain("上传完成", FooterDetail(fixture.Vm));
            Assert.Same(root, fixture.Vm.ScanRoot);
        }
        finally { response.TrySetResult(); WaitUntil(() => !fixture.Vm.IsAnalyzing); }
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        Assert.Contains("上传完成", FooterDetail(fixture.Vm));
        Assert.NotEmpty(fixture.Vm.ScanRoot!.Children);
    });

    [Fact]
    public void Background_upload_does_not_replace_active_model_progress_and_language_switch_is_live() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var originalLanguage = L.Current.Language;
        var originalPath = fixture.Vm.ScanRoot!.Path;
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsAnalyzing))!.SetValue(fixture.Vm, true);
        SetFooterAiProgress(fixture.Vm, AiAnalysisStage.Analyzing, "模型正在分析。 " + originalPath);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeContributionResponse = response.Task;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("footer-busy-model")]));
        var upload = fixture.Runtime.Outbox.FlushAsync();
        try
        {
            WaitUntil(() => fixture.Client.ContributionCalls > 0);
            Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
            Assert.Contains("模型正在分析", FooterDetail(fixture.Vm));
            Assert.DoesNotContain("正在上传", fixture.Vm.FooterStatusHeadlineText);
            Assert.Contains(originalPath, FooterDetail(fixture.Vm));
            SetFooterAiProgress(fixture.Vm, AiAnalysisStage.Uploading, "AI 分析已完成，正在上传云端经验。");
            Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
            Assert.Contains("正在上传", FooterDetail(fixture.Vm));
            L.SetLanguage("en");
            Assert.Contains("Uploading shared experience", FooterDetail(fixture.Vm));
            Assert.Equal(originalPath, fixture.Vm.ScanRoot.Path);
            L.SetLanguage("zh");
            Assert.Contains("正在上传", FooterDetail(fixture.Vm));
        }
        finally
        {
            response.TrySetResult(); Complete(upload);
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsAnalyzing))!.SetValue(fixture.Vm, false);
            L.SetLanguage(originalLanguage);
        }
    });

    [Fact]
    public void Footer_counts_only_validated_batches_and_preserves_unacknowledged_items() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var secondResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.ContributionResponse = (count, epoch) =>
        {
            if (fixture.Client.ContributionCalls == 1)
            {
                fixture.Client.BeforeContributionResponse = secondResponse.Task;
                return new SharedContributionReceipt(6, 4, epoch);
            }
            return new SharedContributionReceipt(count, 0, epoch + 1); // Invalid second-batch acknowledgement.
        };
        var items = Enumerable.Range(1, 12).Select(i =>
        {
            var item = QueuedForUi("footer-partial-" + i);
            return item with { Contribution = item.Contribution with { Shape = "tree2-" + i.ToString("x64") } };
        }).ToArray();
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, items));
        var upload = fixture.Runtime.Outbox.FlushAsync();
        try
        {
            WaitUntil(() => fixture.Client.ContributionCalls == 2);
            Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
            Assert.Contains("已确认 10 / 12 项", FooterDetail(fixture.Vm));
            Assert.Contains("剩余 2 项", FooterDetail(fixture.Vm));
            Assert.DoesNotContain("上传完成", FooterDetail(fixture.Vm));
        }
        finally { secondResponse.TrySetResult(); Complete(upload); }
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        var snapshot = fixture.Runtime.Outbox.GetUploadStatusAsync().GetAwaiter().GetResult();
        Assert.Equal(6, snapshot.Accepted);
        Assert.Equal(4, snapshot.Skipped);
        Assert.Equal(2, snapshot.Pending);
        Assert.Contains("失败", FooterDetail(fixture.Vm));
        Assert.DoesNotContain("上传完成", FooterDetail(fixture.Vm));
    });

    [Fact]
    public void Revoking_sharing_during_upload_cannot_turn_an_old_receipt_into_completion() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeContributionResponse = response.Task;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("footer-revoked-consent")]));
        var upload = fixture.Runtime.Outbox.FlushAsync();
        try
        {
            WaitUntil(() => fixture.Client.ContributionCalls > 0);
            Complete(fixture.Runtime.Settings.SuspendAsync());
            Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
            Assert.Contains("暂停", FooterDetail(fixture.Vm));
        }
        finally { response.TrySetResult(); Complete(upload); }
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        Assert.DoesNotContain("上传完成", FooterDetail(fixture.Vm));
    });

    [Fact]
    public void Offline_alert_has_priority_over_a_cached_upload_receipt() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable(); fixture.ApplyScan();
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("footer-offline-priority")]));
        Complete(fixture.Runtime.Outbox.FlushAsync());
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        var offline = (Task)typeof(MainWindowViewModel).GetMethod("HandleOfflineAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Vm, [null])!;
        Complete(offline);
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        Assert.Equal(L.T(NetworkFailureClassifier.OfflineMessage), FooterDetail(fixture.Vm));
        Assert.DoesNotContain("上传完成", FooterDetail(fixture.Vm));
    });

    private static void SetFooterAiProgress(MainWindowViewModel vm, AiAnalysisStage stage, string message)
    {
        typeof(MainWindowViewModel).GetField("_analysisProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new AiAnalysisProgress(stage, 2, 1, 1, 0, 0, 0, 1, 1, false, stage == AiAnalysisStage.Analyzing, message));
        typeof(MainWindowViewModel).GetMethod("NotifyAiProgressChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    }

    private static string FooterDetail(MainWindowViewModel vm) =>
        typeof(MainWindowViewModel).GetProperty("FooterStatusDetailText", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(vm) as string ?? vm.StatusText;
}

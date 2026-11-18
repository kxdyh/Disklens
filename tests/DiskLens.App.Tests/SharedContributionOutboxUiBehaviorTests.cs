using System.IO;
using System.Reflection;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.ViewModels;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Older_pending_read_cannot_overwrite_a_newer_empty_queue_status() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("stale-pending-read")]));
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        var context = new DeferredOutboxContext();
        typeof(MainWindowViewModel).GetField("_sharedOutboxUiContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Vm, context);
        var oldRead = fixture.Vm.RefreshSharedOutboxStatusAsync();
        WaitUntil(() => context.Count > 0);
        Complete(fixture.Runtime.Outbox.FlushAsync());
        var newRead = fixture.Vm.RefreshSharedOutboxStatusAsync();
        WaitUntil(() => context.Count >= 2);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!oldRead.IsCompleted || !newRead.IsCompleted || context.Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Deferred outbox UI reads did not settle.");
            context.ApplyNewest();
            Pump();
        }
        Complete(oldRead);
        Complete(newRead);

        Assert.DoesNotContain("待上传 1 项", fixture.Vm.SharedExperienceSummaryText);
        Assert.DoesNotContain("保存到本机待上传队列", fixture.Vm.SharedExperienceSummaryText);
        Assert.DoesNotContain("上传成功", fixture.Vm.StatusText);
    });

    [Fact]
    public void Empty_queue_removes_only_stale_report_pending_line_and_keeps_query_failure() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        const string pending = "上传至云端经验库尚未完成：16 项已保存到本机待上传队列，将在网络恢复后自动重试。";
        const string query = "云端经验库查询失败，请检查网络后重试。";
        const string summary = "共享复用 3 项";
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedExperienceSummaryText))!
            .SetValue(fixture.Vm, summary + "\n" + query + "\n" + pending);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.StatusText))!
            .SetValue(fixture.Vm, "分析完成。 " + query + "\n" + pending);

        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Equal(summary + "\n" + query, fixture.Vm.SharedExperienceSummaryText);
        Assert.Contains(query, fixture.Vm.StatusText);
        Assert.DoesNotContain(pending, fixture.Vm.StatusText);
        Assert.DoesNotContain("上传成功", fixture.Vm.StatusText);
    });

    [Fact]
    public void Empty_queue_without_an_upload_notice_preserves_the_lookup_summary_and_status() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        const string summary = "云端经验库匹配 3 项。";
        const string status = "扫描完成，共 7 个文件。";
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedExperienceSummaryText))!
            .SetValue(fixture.Vm, summary);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.StatusText))!.SetValue(fixture.Vm, status);

        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Equal(summary, fixture.Vm.SharedExperienceSummaryText);
        Assert.Equal(status, fixture.Vm.StatusText);
    });

    [Fact]
    public void Empty_queue_does_not_erase_an_upload_failure_that_was_never_durably_queued() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        const string failure = "上传至云端经验库失败，部分内容未取得成功回执。已完成的分析结果仍可查看，目前不会自动补传。";
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedExperienceSummaryText))!
            .SetValue(fixture.Vm, "分析完成。\n" + failure);
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.StatusText))!.SetValue(fixture.Vm, failure);

        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Contains(failure, fixture.Vm.SharedExperienceSummaryText);
        Assert.Equal(failure, fixture.Vm.StatusText);
    });

    [Fact]
    public void Unchanged_pending_count_is_restored_after_analysis_replaces_the_shared_summary() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Client.FailContributions = true;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("same-pending-new-summary")]));
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.SharedExperienceSummaryText))!
            .SetValue(fixture.Vm, "本地历史 2 项，共享复用 3 项");

        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Contains("本地历史 2 项，共享复用 3 项", fixture.Vm.SharedExperienceSummaryText);
        Assert.Contains("待上传", fixture.Vm.SharedExperienceSummaryText);
    });

    [Fact]
    public void Background_upload_failure_is_visible_in_status_and_shared_summary() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Client.FailContributions = true;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("visible-upload-failure")]));
        Complete(fixture.Runtime.Outbox.FlushAsync());
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Contains("上传至云端经验库失败", fixture.Vm.StatusText);
        Assert.Contains("待上传 1 项", fixture.Vm.SharedExperienceSummaryText);
        Assert.True(fixture.Vm.HasSharedExperienceSummary);
    });

    [Fact]
    public void Background_upload_notice_waits_until_model_analysis_has_finished() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Client.FailContributions = true;
        var analyzing = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsAnalyzing))!;
        var status = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.StatusText))!;
        analyzing.SetValue(fixture.Vm, true);
        status.SetValue(fixture.Vm, "正在分析当前勾选项目");
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("deferred-upload-failure")]));
        Complete(fixture.Runtime.Outbox.FlushAsync());
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());

        Assert.Equal("正在分析当前勾选项目", fixture.Vm.StatusText);
        analyzing.SetValue(fixture.Vm, false);
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        Assert.Contains("上传至云端经验库失败", fixture.Vm.StatusText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Privacy_change_returns_only_after_old_durable_queue_has_been_cleared(bool exclusions) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.Client.FailContributions = true;
        var original = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(original, [QueuedForUi("obsolete-consent-upload")]));
        var file = Directory.EnumerateFiles(Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY")!,
            "shared-contribution-outbox.bin", SearchOption.AllDirectories).Single();
        if (exclusions)
        {
            var preferences = fixture.Runtime.Settings.LoadAsync().GetAwaiter().GetResult();
            Complete(fixture.Runtime.Settings.SaveAsync(preferences with
            {
                ExcludedPaths = [@"C:\Users\private-person\secrets"], PolicyRevision = "new-policy"
            }));
        }
        else Complete(fixture.Runtime.Settings.SuspendAsync());

        // Even a process mistakenly loading old consent cannot resurrect deleted work.
        using var restarted = new SharedContributionOutbox(Path.GetDirectoryName(file)!,
            new OriginalConsent(original), fixture.Client);
        Assert.Equal(0, restarted.PendingCountAsync().GetAwaiter().GetResult());
    });

    private static SharedQueuedContribution QueuedForUi(string id) => new(new(id, "generic-v2", "0",
        "path2:user-temp", "tree2-" + new string('a', 64), "cache", "Review", "fresh-ai",
        "official", "openai", true, false, "High"), DateTimeOffset.UtcNow);

    private sealed class OriginalConsent(SharedConsentSnapshot snapshot) : ISharedConsentSource
    {
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class DeferredOutboxContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _callbacks = [];
        public int Count { get { lock (_callbacks) return _callbacks.Count; } }
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_callbacks) _callbacks.Add((d, state));
        }
        public void ApplyNewest()
        {
            (SendOrPostCallback Callback, object? State) next;
            lock (_callbacks)
            {
                if (_callbacks.Count == 0) return;
                next = _callbacks[^1];
                _callbacks.RemoveAt(_callbacks.Count - 1);
            }
            next.Callback(next.State);
        }
    }
}

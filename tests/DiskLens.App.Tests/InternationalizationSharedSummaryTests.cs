using System.Reflection;
using DiskLens.App.Localization;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Internationalization_lookup_summary_and_partial_failure_follow_the_current_language() => OnSta(() =>
    {
        L.SetLanguage("zh");
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var path = snapshot.Children[0].Path;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent, SharedHit(path) with { Unavailable = true }));
        var chinese = fixture.Vm.SharedExperienceSummaryText;
        try
        {
            L.SetLanguage("en");
            Pump();
            Assert.Contains("Matched 1 shared results.", fixture.Vm.SharedExperienceSummaryText);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", fixture.Vm.SharedExperienceSummaryText);
            Assert.Equal(fixture.Vm.SharedExperienceSummaryText, fixture.Vm.StatusText);
            Assert.Equal(path, fixture.Vm.ScanRoot!.Children[0].Path);
            Assert.Empty(fixture.Provider.RequestedPaths);
            L.SetLanguage("zh");
            Pump();
            Assert.Equal(chinese, fixture.Vm.SharedExperienceSummaryText);
        }
        finally { L.SetLanguage("zh"); }
    });

    [Fact]
    public void Internationalization_analysis_summary_rerenders_all_lines_without_repeating_analysis() => OnSta(() =>
    {
        L.SetLanguage("zh");
        using var fixture = new Fixture(configured: true);
        fixture.ApplyScan();
        fixture.Analyze();
        var report = new AiAnalysisReport([], new(0, 0, 0, 0, 0, false))
        {
            TotalCandidates = 4, SubmittedCandidateCount = 2, SharedSkippedItems = 1,
            SharedKnowledgeWarning = "云端经验库查询失败，暂时无法获取文件用途经验，请检查网络后重试。\n上传至云端经验库失败，待上传记录已保存在本机，将稍后重试。",
            PersistenceWarning = "本次结果可查看，但本地缓存写入失败；重启后可能需要重新分析。"
        };
        typeof(MainWindowViewModel).GetMethod("SetSharedAnalysisSummary", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Vm, [report]);
        var chinese = fixture.Vm.SharedExperienceSummaryText;
        var requests = fixture.Provider.RequestedPaths.Count;
        try
        {
            L.SetLanguage("en");
            Pump();
            Assert.Contains("4 items meet the threshold; 2 submitted to AI", fixture.Vm.SharedExperienceSummaryText);
            Assert.Contains("1 items were duplicates", fixture.Vm.SharedExperienceSummaryText);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", fixture.Vm.SharedExperienceSummaryText);
            Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
            L.SetLanguage("zh");
            Pump();
            Assert.Equal(chinese, fixture.Vm.SharedExperienceSummaryText);
        }
        finally { L.SetLanguage("zh"); }
    });

    [Fact]
    public void Internationalization_pending_upload_notice_changes_language_without_changing_the_queue() => OnSta(() =>
    {
        L.SetLanguage("zh");
        using var fixture = new Fixture(configured: true);
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Client.FailContributions = true;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Outbox.EnqueueAsync(consent, [QueuedForUi("bilingual-pending-notice")]));
        Complete(fixture.Runtime.Outbox.FlushAsync());
        Complete(fixture.Vm.RefreshSharedOutboxStatusAsync());
        var chinese = fixture.Vm.SharedExperienceSummaryText;
        try
        {
            L.SetLanguage("en");
            Pump();
            Assert.Contains("1 items awaiting upload.", fixture.Vm.SharedExperienceSummaryText);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", fixture.Vm.SharedExperienceSummaryText);
            Assert.Equal(1, fixture.Runtime.Outbox.PendingCountAsync().GetAwaiter().GetResult());
            L.SetLanguage("zh");
            Pump();
            Assert.Equal(chinese, fixture.Vm.SharedExperienceSummaryText);
        }
        finally { L.SetLanguage("zh"); }
    });
}

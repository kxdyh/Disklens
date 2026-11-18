using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("category-only")]
    [InlineData("unowned-opaque")]
    public void Unsafe_or_incomplete_cloud_readback_never_removes_a_personal_original(string kind) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var captured = CompleteLocalEntries(fixture);
        fixture.Enable(); fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var path = snapshot.Children[0].Path;
        var hit = SharedHit(path).ResultsByPath[path] with
        {
            ShortPurpose = "应用临时缓存", Reason = "应用运行时生成的临时缓存",
            ShortPurposeEn = "Application cache", ReasonEn = "Temporary cache generated while the application is running.",
        };
        hit = kind switch
        {
            "unknown" => hit with { SharedEntry = hit.SharedEntry! with { Purpose = "unknown" } },
            "expired" => hit with { SharedEntry = hit.SharedEntry! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } },
            "category-only" => hit with { ShortPurpose = "" },
            _ => hit with { SharedEntry = hit.SharedEntry! with
                { Key = "generic-v3|0|path3:os-profile/mask|purpose3-observation-directory-" + new string('a', 32) } }
        };
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent,
            new(new Dictionary<string, AiAnalysisResult> { [path] = hit }, 1, 0, false), captured));
        Assert.Equal(captured.Count, CompleteLocalEntries(fixture).Count);
    });

    [Fact]
    public void Chinese_only_legacy_cloud_answer_remains_visible_without_deleting_a_bilingual_personal_original() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var path = fixture.Vm.ScanRoot!.Children[0].Path;
        var bilingualOriginal = cached[path] with
        {
            SnapshotGeneration = cached[path].SnapshotGeneration + 1,
            Result = cached[path].Result with
            {
                ShortPurpose = "应用临时缓存", Reason = "应用运行时生成的临时缓存",
                ShortPurposeEn = "Application cache", ReasonEn = "Temporary cache generated while the application is running.",
            },
        };
        Complete(fixture.Runtime.Memory.SaveEntriesAsync(fixture.Vm.ScanRoot.Path, [bilingualOriginal]));
        var captured = CompleteLocalEntries(fixture);
        Assert.Equal(bilingualOriginal, captured[path]);
        fixture.Enable(); fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var hit = SharedHit(path).ResultsByPath[path] with
        {
            ShortPurpose = "应用临时缓存", Reason = "应用运行时生成的临时缓存",
            ShortPurposeEn = "", ReasonEn = "",
        };
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();

        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent,
            new(new Dictionary<string, AiAnalysisResult> { [path] = hit }, 1, 0, false), captured));

        fixture.SelectDirectory();
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal(hit.ShortPurpose, fixture.Vm.SelectedNode.Analysis.ShortPurpose);
        Assert.Empty(fixture.Vm.SelectedNode.Analysis.ShortPurposeEn);
        Assert.Equal(bilingualOriginal, CompleteLocalEntries(fixture)[path]);
    });

    [Fact]
    public void Valid_own_observation_readback_cleans_its_original_and_has_a_distinct_source_label() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var captured = CompleteLocalEntries(fixture);
        fixture.Enable(); fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var path = snapshot.Children[0].Path;
        var hit = SharedHit(path).ResultsByPath[path] with
        {
            ShortPurpose = "应用临时缓存", Reason = "应用运行时生成的临时缓存",
            ShortPurposeEn = "Application cache", ReasonEn = "Temporary cache generated while the application is running.",
            SharedEntry = SharedHit(path).ResultsByPath[path].SharedEntry! with
            {
                Key = "generic-v3|0|path3:os-profile/mask|purpose3-observation-directory-" + new string('a', 32),
                IsOwnObservation = true,
            }
        };
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent,
            new(new Dictionary<string, AiAnalysisResult> { [path] = hit }, 1, 0, false), captured));
        fixture.SelectDirectory();
        Assert.Equal("本人的云端分析", fixture.Vm.SelectedAiOriginText);
        Assert.DoesNotContain(path, CompleteLocalEntries(fixture).Keys);
        Assert.Equal(captured.Count - 1, CompleteLocalEntries(fixture).Count);
    });

    [Fact]
    public void Known_cloud_answer_is_read_first_and_removes_only_its_matching_personal_cache() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.Enable(); fixture.ApplyScan();
        fixture.Client.ReturnMatches = true;
        fixture.Client.ReturnPurposeTexts = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeQueryResponse = gate.Task;
        var pending = fixture.Vm.RefreshSharedExperienceAfterScanAsync();
        try
        {
            WaitUntil(() => fixture.Client.QueryCalls > 0);
            Assert.All(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));
        }
        finally { gate.TrySetResult(); Complete(pending); }
        fixture.SelectDirectory();
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal("应用临时缓存", fixture.Vm.SelectedNode.Analysis.ShortPurpose);
        var remaining = CompleteLocalEntries(fixture);
        var cloudPaths = fixture.Vm.ScanRoot!.DescendantsAndSelf()
            .Where(node => node.Analysis?.Origin == AiAnalysisResultOrigin.SharedExperience).Select(node => node.Path).ToArray();
        Assert.Equal(cached.Count - cloudPaths.Length, remaining.Count);
        Assert.All(cloudPaths, path => Assert.DoesNotContain(path, remaining.Keys));
        Assert.DoesNotContain(fixture.Vm.SelectedNode.Path, remaining.Keys);
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, fixture.Vm.ScanRoot.Children[1].Analysis!.Origin);
    });

    [Fact]
    public void Known_cloud_answer_keeps_a_personal_original_with_an_unqueued_contribution_intent() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var target = fixture.Vm.ScanRoot!.Children[0].Path;
        var pendingOriginal = cached[target] with
        {
            SnapshotGeneration = cached[target].SnapshotGeneration + 1,
            SharedContributionPending = true,
            SharedContributionCompleted = false,
            SharedContributionConsentFingerprint = "synthetic-still-active-consent-intent",
        };
        Complete(fixture.Runtime.Memory.SaveEntriesAsync(fixture.Vm.ScanRoot.Path, [pendingOriginal]));
        Assert.Equal(pendingOriginal, CompleteLocalEntries(fixture)[target]);
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.Enable(); fixture.ApplyScan();
        fixture.Client.ReturnMatches = true;
        fixture.Client.ReturnPurposeTexts = true;

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());

        fixture.SelectDirectory();
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal("应用临时缓存", fixture.Vm.SelectedNode.Analysis.ShortPurpose);
        Assert.Equal(pendingOriginal, CompleteLocalEntries(fixture)[target]);
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Fact]
    public void New_personal_analysis_written_during_cloud_query_is_not_deleted_by_an_older_capture() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        fixture.Enable(); fixture.ApplyScan();
        var target = fixture.Vm.ScanRoot!.Children[0].Path;
        fixture.Client.ReturnMatches = true; fixture.Client.ReturnPurposeTexts = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeQueryResponse = gate.Task;
        var pending = fixture.Vm.RefreshSharedExperienceAfterScanAsync();
        var revised = cached[target] with { SnapshotGeneration = cached[target].SnapshotGeneration + 1,
            Result = cached[target].Result with { OriginalAnalysisId = "new-analysis-during-cloud-query" } };
        try
        {
            WaitUntil(() => fixture.Client.QueryCalls > 0);
            Complete(fixture.Runtime.Memory.SaveEntriesAsync(fixture.Vm.ScanRoot.Path, [revised]));
        }
        finally { gate.TrySetResult(); Complete(pending); }
        Assert.Equal(revised, CompleteLocalEntries(fixture)[target]);
    });

    [Fact]
    public void Category_only_cloud_answers_do_not_delete_a_personal_original() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        fixture.Enable(); fixture.ApplyScan(); fixture.Client.ReturnMatches = true;
        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        Assert.Equal(cached.Keys.OrderBy(x => x), CompleteLocalEntries(fixture).Keys.OrderBy(x => x));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cloud_miss_or_query_failure_falls_back_to_personal_history_without_deleting_it(bool failure) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan(); RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.Enable(); fixture.ApplyScan(); fixture.Client.Offline = failure;
        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        Assert.Equal(cached.Count, fixture.Vm.ScanRoot!.DescendantsAndSelf()
            .Count(node => node.Analysis?.Origin == AiAnalysisResultOrigin.CacheReused));
        Assert.Equal(cached.Keys.OrderBy(x => x), CompleteLocalEntries(fixture).Keys.OrderBy(x => x));
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
    });
}

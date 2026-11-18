using System.IO;
using System.Reflection;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Local_history_restore_is_invoked_by_real_disk_scan_completion() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        var scanPath = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY")!, "actual-scan");
        Directory.CreateDirectory(scanPath);
        using (var file = File.Create(Path.Combine(scanPath, "cache.bin"))) file.SetLength(2L * 1024 * 1024);
        void Scan() => Complete((Task)typeof(MainWindowViewModel)
            .GetMethod("StartScanAtPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Vm, [scanPath])!);
        Scan();
        RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        Assert.NotEmpty(cached);
        var requests = fixture.Provider.RequestedPaths.Count;

        Scan();

        WaitUntil(() => fixture.Vm.ScanRoot!.DescendantsAndSelf().Any(node => node.Analysis?.Origin == AiAnalysisResultOrigin.CacheReused));
        Assert.Equal(cached.Count, fixture.Vm.ScanRoot!.DescendantsAndSelf().Count(node => node.Analysis?.Origin == AiAnalysisResultOrigin.CacheReused));
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Fact]
    public void Local_history_is_restored_after_a_scan_without_sharing_AI_or_writing_the_cache() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan();
        RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        Assert.NotEmpty(cached);
        var requests = fixture.Provider.RequestedPaths.Count;
        var cacheFile = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY")!, "analysis-memory.json");
        var bytes = File.ReadAllBytes(cacheFile);
        fixture.ApplyScan();
        Assert.All(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());

        var restored = fixture.Vm.ScanRoot!.DescendantsAndSelf().Where(node => node.Analysis is not null).ToArray();
        Assert.Equal(cached.Keys.OrderBy(path => path), restored.Select(node => node.Path).OrderBy(path => path));
        Assert.All(restored, node =>
        {
            Assert.Equal(AiAnalysisResultOrigin.CacheReused, node.Analysis!.Origin);
            Assert.Equal(cached[node.Path].Result.Reason, node.Analysis.Reason);
            Assert.Equal(cached[node.Path].Result.OriginalAnalyzedAt, node.Analysis.OriginalAnalyzedAt);
        });
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.Equal(bytes, File.ReadAllBytes(cacheFile));
    });

    [Fact]
    public void Canceling_a_new_submission_keeps_the_local_history_already_restored_to_the_tree() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan();
        RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.ApplyScan();
        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        var before = fixture.Vm.ScanRoot!;
        Assert.Contains(before.DescendantsAndSelf(), node => node.Analysis?.Origin == AiAnalysisResultOrigin.CacheReused);
        var confirmations = 0;
        fixture.Vm.ConfirmAiSubmissionAsync = (preview, _) =>
        {
            confirmations++;
            Assert.Equal(cached.Count, preview.LocalCacheCount);
            return Task.FromResult(false);
        };
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Vm.AnalyzeCommand.CanExecute(null) && !fixture.Vm.IsAnalyzing);

        Assert.Equal(1, confirmations);
        Assert.Same(before, fixture.Vm.ScanRoot);
        Assert.All(before.DescendantsAndSelf().Where(node => cached.ContainsKey(node.Path)),
            node => Assert.Equal(AiAnalysisResultOrigin.CacheReused, node.Analysis!.Origin));
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Theory]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("mode")]
    [InlineData("threshold")]
    public void Local_history_restore_does_not_bypass_the_original_analysis_context(string changedSetting) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan();
        RunLocalHistorySeed(fixture);
        Assert.NotEmpty(CompleteLocalEntries(fixture));
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.ApplyScan();
        switch (changedSetting)
        {
            case "model": fixture.Vm.AiModel += "-changed"; break;
            case "endpoint": fixture.Vm.AiEndpoint = "https://different.invalid/v1"; break;
            case "mode": fixture.Vm.IsAiTokenSavingModeEnabled = false; break;
            case "threshold": fixture.Vm.AnalysisThresholdMb = 2; break;
        }

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());

        Assert.All(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Fact]
    public void Local_history_is_restored_before_immediate_AI_confirmation_and_remains_when_submission_is_canceled() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan();
        RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.ApplyScan();
        var confirmations = 0;
        fixture.Vm.ConfirmAiSubmissionAsync = (preview, _) =>
        {
            confirmations++;
            Assert.Equal(cached.Count, preview.LocalCacheCount);
            Assert.Equal(cached.Count, fixture.Vm.ScanRoot!.DescendantsAndSelf()
                .Count(node => node.Analysis?.Origin == AiAnalysisResultOrigin.CacheReused));
            return Task.FromResult(false);
        };

        // Start immediately after the new scan snapshot, without waiting for its
        // optional background restore. The submission path must join restoration.
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Vm.AnalyzeCommand.CanExecute(null) && !fixture.Vm.IsAnalyzing);

        Assert.Equal(1, confirmations);
        Assert.Equal(cached.Count, fixture.Vm.ScanRoot!.DescendantsAndSelf()
            .Count(node => node.Analysis?.Origin == AiAnalysisResultOrigin.CacheReused));
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Theory]
    [InlineData("signature")]
    [InlineData("evidence")]
    public void Local_history_restore_rejects_changed_signatures_and_wrong_evidence_scope(string mismatch) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan();
        RunLocalHistorySeed(fixture);
        var entries = CompleteLocalEntries(fixture);
        Assert.NotEmpty(entries);
        var changed = entries.Values.Select(entry => mismatch == "signature"
            ? entry with { Signature = entry.Signature + "changed" }
            : entry with { Result = entry.Result with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.DirectoryStructure } }).ToArray();
        Complete(fixture.Runtime.Memory.SaveEntriesAsync(fixture.Vm.ScanRoot!.Path, changed));
        fixture.ApplyScan();

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());

        Assert.All(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));
        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Fact]
    public void Cloud_lookup_finishes_before_personal_fallback_and_has_priority_over_duplicate_history() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, usePersonalMemory: true);
        fixture.ApplyScan();
        RunLocalHistorySeed(fixture);
        var cached = CompleteLocalEntries(fixture);
        var requests = fixture.Provider.RequestedPaths.Count;
        fixture.Enable();
        fixture.ApplyScan();
        fixture.Client.ReturnMatches = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeQueryResponse = release.Task;
        var refresh = fixture.Vm.RefreshSharedExperienceAfterScanAsync();
        try
        {
            WaitUntil(() => fixture.Client.QueryCalls > 0);
            Assert.False(refresh.IsCompleted);
            Assert.All(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));
        }
        finally { release.TrySetResult(); Complete(refresh); }
        fixture.SelectDirectory();
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal(cached.Count, fixture.Vm.ScanRoot!.DescendantsAndSelf().Count(node => node.Analysis is not null));
        // This fixture has category-only cloud data: local originals stay stored.
        Assert.Equal(cached.Count, CompleteLocalEntries(fixture).Count);
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    private static void RunLocalHistorySeed(Fixture fixture)
    {
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Vm.AnalyzeCommand.CanExecute(null) && !fixture.Vm.IsAnalyzing);
    }

    private static IReadOnlyDictionary<string, AiAnalysisMemoryEntry> CompleteLocalEntries(Fixture fixture)
    {
        var read = fixture.Runtime.Memory.LoadEntriesAsync(fixture.Vm.ScanRoot!.Path);
        Complete(read);
        return read.GetAwaiter().GetResult();
    }
}

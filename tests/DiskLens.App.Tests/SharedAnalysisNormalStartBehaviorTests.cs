using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normal_AI_start_after_sharing_off_submits_cloud_only_items_to_the_personal_provider(bool tokenSaving) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, tokenSaving: tokenSaving,
            networkAvailability: new VisibilityNetwork(), usePersonalMemory: true);
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();
        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();
        var cloudNode = fixture.Vm.SelectedNode!;
        var cloudResult = cloudNode.Analysis!;
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, cloudResult.Origin);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Empty(fixture.Runtime.Memory.LoadEntriesAsync(fixture.Vm.ScanRoot!.Path).GetAwaiter().GetResult());
        var queries = fixture.Client.QueryCalls;

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);
        Assert.Same(cloudResult, fixture.Vm.SelectedNode!.Analysis);
        StartNormalAiAndWait(fixture);

        Assert.Contains(cloudNode.Path, fixture.Provider.RequestedPaths);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal("Synthetic selected analysis", fixture.Vm.SelectedAiReasonText);
        Assert.False(fixture.Vm.IsReanalysisSelectionMode);
        Assert.Equal(queries, fixture.Client.QueryCalls);
        Assert.Contains(cloudNode.Path,
            fixture.Runtime.Memory.LoadEntriesAsync(fixture.Vm.ScanRoot!.Path).GetAwaiter().GetResult().Keys);
        var personal = fixture.Vm.SelectedNode.Analysis;
        var requests = fixture.Provider.RequestedPaths.Count;
        StartNormalAiAndWait(fixture);
        Assert.Equal(requests, fixture.Provider.RequestedPaths.Count);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal(personal!.OriginalAnalysisId, fixture.Vm.SelectedNode.Analysis.OriginalAnalysisId);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normal_AI_start_after_sharing_off_reuses_valid_personal_cache_despite_a_held_cloud_result(bool tokenSaving) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, tokenSaving: tokenSaving,
            networkAvailability: new VisibilityNetwork(), usePersonalMemory: true);
        fixture.ApplyScan();
        StartNormalAiAndWait(fixture);
        fixture.SelectDirectory();
        var personal = fixture.Vm.SelectedNode!.Analysis!;
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, personal.Origin);
        Assert.False(string.IsNullOrWhiteSpace(personal.OriginalAnalysisId));
        var providerPaths = fixture.Provider.RequestedPaths.ToArray();

        // Post-scan refresh now restores valid personal history before cloud
        // lookup. Seed a previously held cloud snapshot explicitly to retain
        // this regression's distinct admission scenario after sharing is off.
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent, SharedHit(snapshot.Children[0].Path)));
        fixture.SelectDirectory();
        var held = fixture.Vm.SelectedNode!.Analysis!;
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, held.Origin);
        Assert.Contains(fixture.Vm.SelectedNode.Path,
            fixture.Runtime.Memory.LoadEntriesAsync(fixture.Vm.ScanRoot!.Path).GetAwaiter().GetResult().Keys);
        var queries = fixture.Client.QueryCalls;

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);
        Assert.Same(held, fixture.Vm.SelectedNode.Analysis);
        StartNormalAiAndWait(fixture);

        Assert.Equal(providerPaths, fixture.Provider.RequestedPaths);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal(personal.OriginalAnalysisId, fixture.Vm.SelectedNode.Analysis.OriginalAnalysisId);
        Assert.Equal(personal.Reason, fixture.Vm.SelectedAiReasonText);
        Assert.Equal(queries, fixture.Client.QueryCalls);
        Assert.False(fixture.Vm.IsReanalysisSelectionMode);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normal_AI_start_while_sharing_on_skips_personal_requests_for_cloud_matches(bool tokenSaving) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true, tokenSaving: tokenSaving,
            networkAvailability: new VisibilityNetwork(), usePersonalMemory: true);
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();
        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();
        var path = fixture.Vm.SelectedNode!.Path;
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode.Analysis!.Origin);

        StartNormalAiAndWait(fixture);

        Assert.True(fixture.Vm.ShowSharedExperienceAnalysis);
        Assert.DoesNotContain(path, fixture.Provider.RequestedPaths);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode!.Analysis!.Origin);
        AssertCloudVisible(fixture.Vm, fixture.Vm.SelectedNode.Analysis);
        Assert.DoesNotContain(path,
            fixture.Runtime.Memory.LoadEntriesAsync(fixture.Vm.ScanRoot!.Path).GetAwaiter().GetResult().Keys);
        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.False(fixture.Vm.IsReanalysisSelectionMode);
    });

    private static void StartNormalAiAndWait(Fixture fixture)
    {
        // Exercise the ordinary start command with memory reuse enabled, never the bypass command.
        Assert.True(fixture.Vm.AnalyzeCommand.CanExecute(null));
        fixture.Vm.AnalyzeCommand.Execute(null);
        WaitUntil(() => fixture.Vm.AnalyzeCommand.CanExecute(null));
        Assert.False(fixture.Vm.IsAnalyzing);
    }
}

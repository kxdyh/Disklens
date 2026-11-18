using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

// Extends the existing isolated STA fixture. Only the shared pipeline's in-memory
// fake client is queried; these tests never execute a real scan, HTTP or paid AI.
public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Cloud_lookup_cancels_an_already_running_watcher_refresh_before_applying_results() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();
        using var refresh = new CancellationTokenSource();
        var field = fixture.Vm.GetType().GetField("_automaticRefreshCancellation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(fixture.Vm, refresh);
        try
        {
            Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
            Assert.True(refresh.IsCancellationRequested);
            fixture.SelectDirectory();
            Assert.Equal(AiAnalysisResultOrigin.SharedExperience, fixture.Vm.SelectedNode!.Analysis!.Origin);
            Assert.Empty(fixture.Provider.RequestedPaths);
        }
        finally { field.SetValue(fixture.Vm, null); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Post_scan_lookup_applies_cloud_hits_with_or_without_API_configuration_and_never_calls_AI(bool configured) => OnSta(() =>
    {
        using var fixture = new Fixture(configured);
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();

        var result = fixture.Vm.SelectedNode!.Analysis;
        Assert.NotNull(result);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, result.Origin);
        Assert.Equal(AiConfidenceLevel.High, result.EffectiveConfidenceLevel);
        Assert.NotNull(result.SharedEntry);
        Assert.False(result.LooksLikeFallback());
        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.False(fixture.Vm.IsAiSetupOpen);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.Contains("云端经验库", fixture.Vm.SharedExperienceSummaryText);
    });

    [Fact]
    public void Post_scan_lookup_with_shared_feature_disabled_sends_no_query_even_when_AI_is_configured() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: true);
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot;

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();

        Assert.Equal(0, fixture.Client.QueryCalls);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.Same(snapshot, fixture.Vm.ScanRoot);
        Assert.Null(fixture.Vm.SelectedNode!.Analysis);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
    });

    [Fact]
    public void Post_scan_lookup_uses_path_purpose_even_when_token_saving_is_disabled_in_settings() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false, tokenSaving: false);
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        fixture.ApplyScan();
        Assert.False(fixture.Vm.IsAiTokenSavingModeEnabled);

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();

        Assert.NotEmpty(fixture.FingerprintExtractor.Candidates);
        Assert.All(fixture.FingerprintExtractor.Candidates, candidate =>
            Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, candidate.AnalysisEvidenceScope));
        var result = fixture.Vm.SelectedNode!.Analysis;
        Assert.NotNull(result);
        Assert.Equal(AiAnalysisResultOrigin.SharedExperience, result.Origin);
        Assert.Equal(AiAnalysisEvidenceScope.PathPurpose, result.AnalysisEvidenceScope);
        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.False(fixture.Vm.IsAiTokenSavingModeEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Post_scan_lookup_never_reuses_or_reports_a_cloud_miss_for_a_ZIP_extension_alone(bool returnMatches) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.Client.ReturnMatches = returnMatches;
        fixture.ApplyScan();

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.Vm.SelectedNode = fixture.Vm.ScanRoot!.Children[1];

        Assert.False(fixture.Vm.SelectedNode.IsDirectory);
        Assert.Equal(".zip", fixture.Vm.SelectedNode.Extension);
        Assert.Null(fixture.Vm.SelectedNode.Analysis);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.True(fixture.Client.QueryCalls > 0); // The controlled directory was queried.
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });

    [Fact]
    public void Post_scan_lookup_shows_an_explicit_miss_without_starting_an_unconfigured_AI() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.ApplyScan();

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();

        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.True(fixture.Vm.HasSelectedSharedMissing);
        Assert.Equal(MissingText, fixture.Vm.SelectedSharedMissingText);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.False(fixture.Vm.IsAiSetupOpen);
        Assert.Null(fixture.Vm.SelectedNode!.Analysis);
    });

    [Fact]
    public void Post_scan_lookup_reports_an_outage_without_an_explicit_missing_invitation_or_AI_call() => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.Client.Offline = true;
        fixture.ApplyScan();

        Complete(fixture.Vm.RefreshSharedExperienceAfterScanAsync());
        fixture.SelectDirectory();

        Assert.True(fixture.Client.QueryCalls > 0);
        Assert.Contains("云端经验库", fixture.Vm.StatusText);
        Assert.Contains("失败", fixture.Vm.StatusText);
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.DoesNotContain(MissingText, fixture.Vm.SelectedSharedMissingText);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
        Assert.False(fixture.Vm.IsAiSetupOpen);
    });

    [Theory]
    [InlineData("replacement-scan")]
    [InlineData("disabled-in-settings")]
    public void Pending_post_scan_lookup_never_writes_late_hits_after_the_scan_or_consent_changes(string change) => OnSta(() =>
    {
        using var fixture = new Fixture(configured: false);
        fixture.Enable();
        fixture.Client.ReturnMatches = true;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BeforeQueryResponse = ready.Task;
        fixture.ApplyScan();
        var original = fixture.Vm.ScanRoot!;
        var pending = fixture.Vm.RefreshSharedExperienceAfterScanAsync();
        try
        {
            WaitUntil(() => fixture.Client.QueryCalls > 0);
            Assert.False(pending.IsCompleted);
            if (change == "replacement-scan")
            {
                // The new snapshot deliberately has identical paths, so path equality
                // cannot replace the snapshot identity guard for the delayed response.
                fixture.ApplyScan();
                Assert.NotSame(original, fixture.Vm.ScanRoot);
            }
            else Complete(fixture.Runtime.Settings.SuspendAsync());
        }
        finally
        {
            ready.TrySetResult(true);
            Complete(pending);
        }
        fixture.SelectDirectory();

        Assert.All(fixture.Vm.ScanRoot!.DescendantsAndSelf(), node => Assert.Null(node.Analysis));
        Assert.False(fixture.Vm.HasSelectedSharedMissing);
        Assert.Empty(fixture.Provider.RequestedPaths);
        Assert.Equal(0, fixture.Client.ContributionCalls);
    });
}

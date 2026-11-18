using System.Reflection;
using DiskLens.App.ViewModels;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeSelectionBehaviorTests
{
    [Fact]
    public void Settings_off_on_hides_cloud_cards_and_list_without_losing_the_selected_conclusion() => OnSta(() =>
    {
        using var fixture = VisibilityFixture();
        var snapshot = fixture.Vm.ScanRoot!;
        var node = snapshot.Children[0];
        var result = node.Analysis!;
        var selectedItem = fixture.Vm.SelectedAnalysisItem;
        var queryCalls = fixture.Client.QueryCalls;
        var changes = new List<string?>();
        fixture.Vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert.True(fixture.Vm.ShowSharedExperienceAnalysis);
        AssertCloudVisible(fixture.Vm, result);

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);

        Assert.Same(snapshot, fixture.Vm.ScanRoot);
        Assert.Same(node, fixture.Vm.SelectedNode);
        Assert.Same(result, node.Analysis);
        Assert.Single(fixture.Vm.AnalysisItems);
        Assert.Empty(fixture.Vm.VisibleAnalysisItems);
        Assert.Null(fixture.Vm.SelectedAnalysisItem);
        AssertCloudHidden(fixture.Vm, result);
        Assert.Contains(nameof(MainWindowViewModel.ShowSharedExperienceAnalysis), changes);
        Assert.Contains(nameof(MainWindowViewModel.SelectedAiReasonText), changes);
        Assert.Contains(nameof(MainWindowViewModel.VisibleAnalysisItems), changes);

        // Selecting the same hidden node again cannot recover a cloud list selection.
        fixture.Vm.SelectedNode = snapshot.Children[1];
        fixture.Vm.SelectedNode = node;
        Assert.Null(fixture.Vm.SelectedAnalysisItem);
        AssertCloudHidden(fixture.Vm, result);

        ReenableVisibility(fixture);

        Assert.Same(snapshot, fixture.Vm.ScanRoot);
        Assert.Same(node, fixture.Vm.SelectedNode);
        Assert.Same(result, node.Analysis);
        Assert.Same(selectedItem, fixture.Vm.SelectedAnalysisItem);
        Assert.Single(fixture.Vm.VisibleAnalysisItems);
        AssertCloudVisible(fixture.Vm, result);
        Assert.Equal(queryCalls, fixture.Client.QueryCalls);
    });

    [Theory]
    [InlineData(AiAnalysisResultOrigin.AiAnalyzed)]
    [InlineData(AiAnalysisResultOrigin.CacheReused)]
    [InlineData(AiAnalysisResultOrigin.ProtectedByRule)]
    public void Turning_sharing_off_keeps_personal_and_rule_results_visible(AiAnalysisResultOrigin origin) => OnSta(() =>
    {
        using var fixture = VisibilityFixture();
        var path = fixture.Vm.ScanRoot!.Children[1].Path;
        var personal = new AiAnalysisResult(path, RiskLevel.Low, SuggestedAction.Keep, "Synthetic personal-only reason", .9)
        { Origin = origin, ConfidenceLevel = AiConfidenceLevel.High, ShortPurpose = "个人用途" };
        ApplyVisibilityResults(fixture, [personal]);
        fixture.Vm.SelectedNode = fixture.Vm.ScanRoot!.Children[1];
        var selected = fixture.Vm.SelectedNode;

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);

        Assert.Same(selected, fixture.Vm.SelectedNode);
        Assert.Same(personal, selected!.Analysis);
        Assert.Equal(personal.Reason, fixture.Vm.SelectedAiReasonText);
        Assert.True(fixture.Vm.HasSelectedAiOriginBadge);
        Assert.True(fixture.Vm.HasSelectedAiAction);
        Assert.False(fixture.Vm.IsSelectedSharedExperience);
        Assert.Single(fixture.Vm.VisibleAnalysisItems);
        Assert.Equal(path, fixture.Vm.VisibleAnalysisItems.Single().Path);
        Assert.Contains(personal.Reason, fixture.Vm.AnalysisDescriptionText);
    });

    [Fact]
    public void Turning_sharing_off_during_bulk_selection_keeps_checks_and_new_personal_results_visible() => OnSta(() =>
    {
        using var fixture = VisibilityFixture(twoHits: true);
        var before = fixture.Vm.ScanRoot!;
        var path = before.Children[0].Path;
        var untouched = before.Children[1].Analysis;
        var queries = fixture.Client.QueryCalls;
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        fixture.Vm.ToggleReanalysisSelection(path);

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);

        Assert.Same(before, fixture.Vm.ScanRoot);
        Assert.True(fixture.Vm.IsReanalysisSelectionMode);
        Assert.True(fixture.Vm.IsReanalysisSelected(path));
        Assert.Equal(1, fixture.Vm.SelectedReanalysisCount);
        AssertCloudHidden(fixture.Vm, before.Children[0].Analysis!);
        ReenableVisibility(fixture);
        Assert.True(fixture.Vm.IsReanalysisSelected(path));
        AssertCloudVisible(fixture.Vm, before.Children[0].Analysis!);

        Complete(fixture.Runtime.Settings.SuspendAsync());
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);
        fixture.Vm.ReanalyzeWithoutMemoryCommand.Execute(null);
        WaitUntil(() => !fixture.Vm.IsAnalyzing && fixture.Provider.RequestedPaths.Count > 0);

        Assert.Equal([path], fixture.Provider.RequestedPaths);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, fixture.Vm.SelectedNode!.Analysis!.Origin);
        Assert.Equal("Synthetic selected analysis", fixture.Vm.SelectedAiReasonText);
        Assert.True(fixture.Vm.HasSelectedAiOriginBadge);
        Assert.Same(untouched, fixture.Vm.ScanRoot!.Children[1].Analysis);
        Assert.Single(fixture.Vm.VisibleAnalysisItems);
        Assert.False(fixture.Vm.IsReanalysisSelectionMode);

        ReenableVisibility(fixture);
        Assert.Equal(2, fixture.Vm.VisibleAnalysisItems.Count());
        Assert.Equal("Synthetic selected analysis", fixture.Vm.SelectedAiReasonText);
        Assert.Equal(queries, fixture.Client.QueryCalls);
    });

    [Fact]
    public void Background_suspension_notifies_visibility_on_the_VM_owner_thread() => OnSta(() =>
    {
        using var fixture = VisibilityFixture();
        var owner = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        fixture.Vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.ShowSharedExperienceAnalysis))
                threads.Add(Environment.CurrentManagedThreadId);
        };

        Complete(Task.Run(() => fixture.Runtime.Settings.SuspendAsync()));
        WaitUntil(() => !fixture.Vm.ShowSharedExperienceAnalysis);

        Assert.NotEmpty(threads);
        Assert.All(threads, thread => Assert.Equal(owner, thread));
        AssertCloudHidden(fixture.Vm, fixture.Vm.SelectedNode!.Analysis!);
    });

    private static Fixture VisibilityFixture(bool twoHits = false)
    {
        var fixture = new Fixture(configured: true, networkAvailability: new VisibilityNetwork());
        fixture.Enable();
        fixture.ApplyScan();
        var snapshot = fixture.Vm.ScanRoot!;
        var path = snapshot.Children[0].Path;
        var results = new Dictionary<string, AiAnalysisResult>(StringComparer.Ordinal)
        {
            [path] = SharedHit(path).ResultsByPath[path] with { ShortPurpose = "云端用途" }
        };
        if (twoHits)
        {
            var sibling = snapshot.Children[1].Path;
            results[sibling] = SharedHit(sibling).ResultsByPath[sibling] with { ShortPurpose = "云端文件用途" };
        }
        var consent = fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult();
        Complete(fixture.Vm.ApplySharedLookupResultAsync(snapshot, consent, new(results, results.Count, 0, false)));
        fixture.SelectDirectory();
        return fixture;
    }

    private static void ReenableVisibility(Fixture fixture)
    {
        var previous = fixture.Store.LoadAsync().GetAwaiter().GetResult();
        Complete(fixture.Runtime.Settings.SaveAsync(previous with
        {
            Enabled = true, EnabledAt = DateTimeOffset.UtcNow, Epoch = previous.Epoch + 1,
            PolicyRevision = Guid.NewGuid().ToString("N")
        }));
        WaitUntil(() => fixture.Vm.ShowSharedExperienceAnalysis);
    }

    private static void ApplyVisibilityResults(Fixture fixture, IReadOnlyList<AiAnalysisResult> results)
    {
        var vmType = typeof(MainWindowViewModel);
        var mapped = (FileSystemNode)vmType.GetMethod("ApplyAnalysisResults", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(IReadOnlyList<AiAnalysisResult>)], null)!
            .Invoke(null, [fixture.Vm.ScanRoot!, results])!;
        vmType.GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(fixture.Vm, [mapped, null, null]);
        vmType.GetMethod("RebuildAnalysisItems", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode)], null)!.Invoke(fixture.Vm, [mapped]);
    }

    private static void AssertCloudVisible(MainWindowViewModel vm, AiAnalysisResult result)
    {
        Assert.True(vm.IsSelectedSharedExperience);
        Assert.True(vm.HasSelectedAiOriginBadge);
        Assert.True(vm.HasSelectedAiAction);
        Assert.Equal(result.Reason, vm.SelectedAiReasonText);
        Assert.Contains("云端", vm.SelectedAiOriginText);
        Assert.Contains("云端", vm.SelectedAiConfidenceText);
        Assert.NotEmpty(vm.SelectedSharedExplanation);
        Assert.True(vm.ReportSharedExperienceCommand.CanExecute(null));
    }

    private static void AssertCloudHidden(MainWindowViewModel vm, AiAnalysisResult result)
    {
        Assert.False(vm.IsSelectedSharedExperience);
        Assert.False(vm.HasSelectedAiOriginBadge);
        Assert.False(vm.HasSelectedAiAction);
        Assert.Equal("", vm.SelectedAiActionText);
        Assert.Equal("", vm.SelectedAiConfidenceText);
        Assert.Equal("", vm.SelectedAiOriginIcon);
        Assert.Equal("", vm.SelectedSharedExplanation);
        Assert.Equal("", vm.SelectedAiStatusText);
        Assert.False(vm.HasSelectedSharedMissing);
        Assert.DoesNotContain(result.Reason, vm.SelectedAiReasonText);
        Assert.DoesNotContain(result.Reason, vm.SelectedDecision);
        Assert.DoesNotContain(result.Reason, vm.AnalysisDescriptionText);
        Assert.DoesNotContain("云端", vm.SelectedAiOriginText);
        Assert.DoesNotContain("AI 风险", vm.SelectedRiskText);
        Assert.False(vm.ReportSharedExperienceCommand.CanExecute(null));
    }

    private sealed class VisibilityNetwork : INetworkAvailability
    {
        public bool IsNetworkAvailable => true;
    }
}

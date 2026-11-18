using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiScopeMemoryTests
{
    private const string RootPath = @"E:\ScopeMemoryFixture";
    private static readonly AiProviderOptions ProviderOptions = new(
        AiProviderKind.OpenAiCompatible, "https://scope-memory.invalid", "fixture-model", "fixture-key");

    [Fact]
    public async Task Whole_root_then_current_directory_reuses_unchanged_results_without_new_requests()
    {
        var root = Tree();
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(root, Options(root.Path), ProviderOptions);
        var requestCount = provider.RequestedBatches.Count;
        var child = root.Children.Single(node => node.Name == "A");

        var report = await service.AnalyzeWithReportAsync(child, Options(child.Path), ProviderOptions);

        Assert.Equal(new[] { child.Path, child.Children[0].Path }, report.Results.Select(result => result.Path));
        Assert.All(report.Results, result => Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin));
        Assert.Equal("成功分析：A", report.Results[0].Reason);
        Assert.Equal("成功分析：known.bin", report.Results[1].Reason);
        Assert.Equal(requestCount, provider.RequestedBatches.Count);
        Assert.Equal(0, report.TokenBudget.RequestCount);
        Assert.All(store.LoadedRoots, path => Assert.Equal(RootPath, path));
        Assert.All(store.SavedRoots, path => Assert.Equal(RootPath, path));
        Assert.Single(store.Roots);
    }

    [Fact]
    public async Task Failed_new_item_in_current_scope_keeps_old_conclusion_and_other_directory_memory()
    {
        var root = Tree();
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(root, Options(root.Path), ProviderOptions);
        var oldA = root.Children.Single(node => node.Name == "A");
        var known = oldA.Children.Single();
        var outside = root.Children.Single(node => node.Name == "B").Children.Single();
        var outsideMemory = store.Roots[RootPath][outside.Path];
        var added = FileNode(Path.Combine(oldA.Path, "added.bin"));
        var refreshedA = Folder(oldA.Path, known, added);
        provider.Fail = true;
        var beforeRequests = provider.RequestedBatches.Count;

        var report = await service.AnalyzeWithReportAsync(refreshedA, Options(refreshedA.Path), ProviderOptions);

        var oldResult = Assert.Single(report.Results, result => result.Path == known.Path);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, oldResult.Origin);
        Assert.Equal("成功分析：known.bin", oldResult.Reason);
        Assert.False(oldResult.LooksLikeFallback());
        var failedResult = Assert.Single(report.Results, result => result.Path == added.Path);
        Assert.True(failedResult.LooksLikeFallback());
        Assert.True(Assert.Single(report.Results, result => result.Path == refreshedA.Path).LooksLikeFallback());
        Assert.DoesNotContain(provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch),
            path => path == known.Path || path == outside.Path);
        Assert.Same(outsideMemory, store.Roots[RootPath][outside.Path]);
        Assert.Equal("成功分析：known.bin", store.Roots[RootPath][known.Path].Result.Reason);
        Assert.DoesNotContain(added.Path, store.Roots[RootPath].Keys);
        Assert.All(store.SavedRoots, path => Assert.Equal(RootPath, path));
    }

    [Fact]
    public async Task Current_directory_then_whole_root_reuses_the_same_file_and_analyzes_other_candidates()
    {
        var root = Tree();
        var a = root.Children.Single(node => node.Name == "A");
        var known = a.Children.Single();
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(a, Options(a.Path), ProviderOptions);
        var beforeRequests = provider.RequestedBatches.Count;

        var report = await service.AnalyzeWithReportAsync(root, Options(root.Path), ProviderOptions);

        Assert.Equal(AiAnalysisResultOrigin.CacheReused,
            Assert.Single(report.Results, result => result.Path == known.Path).Origin);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused,
            Assert.Single(report.Results, result => result.Path == a.Path).Origin);
        Assert.DoesNotContain(known.Path, provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch));
        Assert.DoesNotContain(a.Path, provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch));
        Assert.Contains(report.Results, result => result.Path == root.Children.Single(node => node.Name == "B").Children.Single().Path
            && result.Origin == AiAnalysisResultOrigin.AiAnalyzed);
        Assert.Single(store.Roots);
    }

    [Fact]
    public async Task Shared_memory_does_not_reuse_a_changed_file_or_present_stale_conclusion_as_current()
    {
        var root = Tree();
        var a = root.Children.Single(node => node.Name == "A");
        var known = a.Children.Single();
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(root, Options(root.Path), ProviderOptions);
        var changed = known with { SizeBytes = known.SizeBytes + 100, ModifiedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        var updatedA = Folder(a.Path, changed);
        var beforeRequests = provider.RequestedBatches.Count;
        provider.Fail = true;

        var report = await service.AnalyzeWithReportAsync(updatedA, Options(updatedA.Path), ProviderOptions);

        Assert.Equal(new[] { updatedA.Path, changed.Path }, report.Results.Select(result => result.Path));
        Assert.All(report.Results, result =>
        {
            Assert.True(result.LooksLikeFallback());
            Assert.DoesNotContain("成功分析", result.Reason, StringComparison.Ordinal);
        });
        Assert.Contains(updatedA.Path, provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch));
        Assert.Contains(changed.Path, provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch));
    }

    [Theory]
    [InlineData("identity-old", "identity-new")]
    [InlineData("identity-old", null)]
    [InlineData(null, "identity-new")]
    public async Task Refresh_replacing_file_identity_with_same_path_size_and_time_invalidates_file_and_parent_cache(
        string? oldIdentity, string? newIdentity)
    {
        var original = Tree();
        var a = original.Children.Single(node => node.Name == "A");
        var known = a.Children.Single() with { StorageInfo = new FileStorageInfo(oldIdentity, 1024, 1024) };
        var root = Folder(RootPath, Folder(a.Path, known), original.Children.Single(node => node.Name == "B"));
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(root, Options(root.Path), ProviderOptions);
        var beforeRequests = provider.RequestedBatches.Count;
        var replacement = known with { StorageInfo = new FileStorageInfo(newIdentity, 1024, 1024) };
        var refreshed = DiskLens.Core.Scanning.RefreshTreeMerger.Replace(root, a.Path,
            Folder(a.Path, replacement)).Root;

        var report = await service.AnalyzeWithReportAsync(refreshed, Options(root.Path), ProviderOptions);

        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results, result => result.Path == known.Path).Origin);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results, result => result.Path == a.Path).Origin);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, Assert.Single(report.Results,
            result => result.Path == original.Children.Single(node => node.Name == "B").Children.Single().Path).Origin);
        Assert.Contains(known.Path, provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch));
    }

    [Fact]
    public async Task Missing_identity_uses_stable_metadata_but_new_creation_time_invalidates_the_cached_file()
    {
        var root = Tree();
        var a = root.Children.Single(node => node.Name == "A");
        var known = a.Children.Single() with { StorageInfo = new FileStorageInfo(null, 1024, 1024, IsEstimated: true) };
        root = Folder(RootPath, Folder(a.Path, known), root.Children.Single(node => node.Name == "B"));
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(root, Options(root.Path), ProviderOptions);
        var repeat = await service.AnalyzeWithReportAsync(root, Options(root.Path), ProviderOptions);
        Assert.All(repeat.Results, result => Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin));
        var beforeRequests = provider.RequestedBatches.Count;
        var replacement = known with { CreatedAt = known.CreatedAt!.Value.AddDays(1) };
        var refreshed = DiskLens.Core.Scanning.RefreshTreeMerger.Replace(root, a.Path, Folder(a.Path, replacement)).Root;

        var report = await service.AnalyzeWithReportAsync(refreshed, Options(root.Path), ProviderOptions);

        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results, result => result.Path == known.Path).Origin);
        Assert.Contains(known.Path, provider.RequestedBatches.Skip(beforeRequests).SelectMany(batch => batch));
    }
    [Fact]
    public async Task Logical_length_change_invalidates_cache_even_when_allocated_bytes_and_times_are_unchanged()
    {
        var original = Tree();
        var a = original.Children.Single(node => node.Name == "A");
        var known = a.Children.Single() with
        {
            LogicalSizeBytes = 100,
            StorageInfo = new FileStorageInfo("same-identity", 100, 1024),
        };
        var root = Folder(RootPath, Folder(a.Path, known), original.Children.Single(node => node.Name == "B"));
        var store = new PartitionedMemory();
        var provider = new RecordingProvider();
        var service = Service(provider, store);
        await service.AnalyzeAsync(root, Options(root.Path), ProviderOptions);
        var changed = known with
        {
            LogicalSizeBytes = 200,
            StorageInfo = new FileStorageInfo("same-identity", 200, 1024),
        };
        var refreshed = DiskLens.Core.Scanning.RefreshTreeMerger.Replace(root, a.Path, Folder(a.Path, changed)).Root;

        var report = await service.AnalyzeWithReportAsync(refreshed, Options(root.Path), ProviderOptions);

        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results, result => result.Path == known.Path).Origin);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results, result => result.Path == a.Path).Origin);
    }

    private static ScanSessionOptions Options(string scope) => new(scope,
        AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20,
        AnalysisMemoryRootPath: RootPath);

    private static AiAnalysisService Service(IAiProvider provider, IAiAnalysisMemoryStore memory) => new(
        [provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), memory);

    private static FileSystemNode Tree() => Folder(RootPath,
        Folder(Path.Combine(RootPath, "A"), FileNode(Path.Combine(RootPath, "A", "known.bin"))),
        Folder(Path.Combine(RootPath, "B"), FileNode(Path.Combine(RootPath, "B", "outside.bin"))));

    private static FileSystemNode FileNode(string path) => new(path, Path.GetFileName(path), ".bin", 1024,
        FileAttributes.Normal, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false, [], [], 1);

    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(path,
        Path.GetFileName(path), "", children.Sum(child => child.SizeBytes), FileAttributes.Directory,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false, [], children,
        children.Sum(child => child.DescendantFileCount), 1 + children.Sum(child => child.DescendantDirectoryCount));

    private sealed class RecordingProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public bool Fail { get; set; }
        public List<string[]> RequestedBatches { get; } = [];

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedBatches.Add(batch.Records.Select(record => record.Path).ToArray());
            if (Fail) throw new IOException("Isolated scope-memory test failure.");
            batch.RequestContext?.ReportUsage(new AiTokenUsage(20, 10));
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record => new AiAnalysisResult(
                record.Path, RiskLevel.Review, SuggestedAction.Review, "成功分析：" + record.Name, .9)).ToArray());
        }
    }

    // Exact-root partitions reproduce AnalysisMemoryStore's public behavior. Saves replace
    // their partition, so these checks also prove that the service carries untouched entries.
    private sealed class PartitionedMemory : IAiAnalysisMemoryStore
    {
        public Dictionary<string, IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> Roots { get; } = new(StringComparer.Ordinal);
        public List<string> LoadedRoots { get; } = [];
        public List<string> SavedRoots { get; } = [];

        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedRoots.Add(rootPath);
            return Task.FromResult(Roots.TryGetValue(rootPath, out var entries)
                ? entries : (IReadOnlyDictionary<string, AiAnalysisMemoryEntry>)new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal));
        }

        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedRoots.Add(rootPath);
            Roots[rootPath] = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }
}

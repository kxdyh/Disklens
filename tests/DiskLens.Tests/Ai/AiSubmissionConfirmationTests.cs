using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiSubmissionConfirmationTests
{
    private static readonly AiProviderOptions ProviderOptions = new(
        AiProviderKind.OpenAiCompatible, "https://example.invalid", "test-model", "test-key");

    [Theory]
    [InlineData(@"E:\", false)]
    [InlineData(@"E:\DiskLens-confirmation\selected", true)]
    public async Task Confirmation_uses_actual_scope_and_only_candidates_still_needing_the_model(
        string scopePath, bool includesDirectoryItself)
    {
        var cached = File(scopePath, "cached.bin");
        var protectedFile = File(scopePath, "protected.bin") with { IsProtectedLocation = true };
        var fresh = File(scopePath, "fresh.bin");
        var root = Root(scopePath, cached, protectedFile, fresh);
        var provider = new Provider();
        var service = Service(provider, StoreWithCache(root, cached.Path));
        var entered = new TaskCompletionSource<AiAnalysisSubmissionPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var confirmationCount = 0;
        var confirmationToken = default(CancellationToken);

        var pending = service.AnalyzeWithReportAsync(root, Options(), ProviderOptions, cancellation.Token,
            confirmSubmission: (preview, token) =>
            {
                confirmationCount++;
                confirmationToken = token;
                entered.TrySetResult(preview);
                return decision.Task.WaitAsync(token);
            });
        var preview = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string[] expectedPaths = includesDirectoryItself
            ? [root.Path, protectedFile.Path, fresh.Path] : [protectedFile.Path, fresh.Path];

        try
        {
            Assert.Equal(scopePath, preview.RootPath);
            Assert.Equal(cancellation.Token, confirmationToken);
            Assert.Equal(1, confirmationCount);
            Assert.Equal(1, preview.LocalCacheCount);
            Assert.Equal(0, preview.ProtectedCount);
            Assert.Equal(0, preview.SharedExperienceCount);
            AssertPaths(expectedPaths, preview.Items.Select(item => item.Path));
            var freshItem = Assert.Single(preview.Items, item => item.Path == fresh.Path);
            Assert.Equal(fresh.Name, freshItem.Name);
            Assert.Equal(fresh.SizeBytes, freshItem.SizeBytes);
            Assert.False(freshItem.IsDirectory);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, provider.Calls);
        }
        finally
        {
            decision.TrySetResult(true);
        }

        var report = await pending;
        AssertPaths(expectedPaths, provider.RequestedPaths);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused,
            Assert.Single(report.Results, result => result.Path == cached.Path).Origin);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed,
            Assert.Single(report.Results, result => result.Path == protectedFile.Path).Origin);
    }

    [Fact]
    public async Task Declining_confirmation_cancels_before_any_provider_call_or_cache_save()
    {
        var root = Root(@"E:\", File(@"E:\", "fresh.bin"));
        var provider = new Provider();
        var store = new MemoryStore();
        var confirmationCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(provider, store).AnalyzeWithReportAsync(root, Options(), ProviderOptions,
                confirmSubmission: (preview, _) =>
                {
                    confirmationCount++;
                    Assert.Equal(root.Path, preview.RootPath);
                    Assert.Equal(root.Children[0].Path, Assert.Single(preview.Items).Path);
                    return Task.FromResult(false);
                }));

        Assert.Equal(1, confirmationCount);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(provider.RequestedPaths);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Omitting_confirmation_preserves_existing_analysis_behavior()
    {
        var file = File(@"E:\", "fresh.bin");
        var provider = new Provider();

        var report = await Service(provider).AnalyzeWithReportAsync(Root(@"E:\", file), Options(), ProviderOptions);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(file.Path, Assert.Single(provider.RequestedPaths));
        var result = Assert.Single(report.Results);
        Assert.Equal(file.Path, result.Path);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
        Assert.False(result.LooksLikeFallback());
        Assert.Equal(1, report.TokenBudget.RequestCount);
    }

    [Fact]
    public async Task Confirmation_can_show_an_empty_submission_when_all_candidates_are_local_results()
    {
        var cached = File(@"E:\", "cached.bin");
        var protectedFile = File(@"E:\", "protected.bin") with { IsProtectedLocation = true };
        var root = Root(@"E:\", cached, protectedFile);
        var provider = new Provider();
        AiAnalysisSubmissionPreview? captured = null;

        var report = await Service(provider, StoreWithCache(root, cached.Path, protectedFile.Path))
            .AnalyzeWithReportAsync(root, Options(), ProviderOptions,
                confirmSubmission: (preview, _) =>
                {
                    captured = preview;
                    return Task.FromResult(true);
                });

        Assert.NotNull(captured);
        Assert.Equal(root.Path, captured.RootPath);
        Assert.Empty(captured.Items);
        Assert.Equal(2, captured.LocalCacheCount);
        Assert.Equal(0, captured.ProtectedCount);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, report.TokenBudget.RequestCount);
        Assert.Equal(2, report.Results.Count);
    }

    private static ScanSessionOptions Options() => new(
        @"E:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400,
        MaxAiBatchSize: 20, UseTokenSavingMode: true);

    [Fact]
    public async Task Every_over_threshold_protected_system_directory_and_child_is_submitted_then_cached()
    {
        const long gib = 1L << 30;
        var folders = new[] { "Windows", "Program Files", "ProgramData" }.Select(name =>
        {
            var path = @"C:\" + name;
            var child = File(path, "runtime.dll") with { SizeBytes = 2 * gib, IsProtectedLocation = true };
            return Root(path, child) with { IsProtectedLocation = true };
        }).ToArray();
        var root = Root(@"C:\", folders);
        var options = Options() with { RootPath = root.Path, AnalysisThresholdBytes = gib };
        var provider = new Provider { RecommendRemoval = true };
        var store = new MemoryStore();
        var service = Service(provider, store);
        AiAnalysisSubmissionPreview? submitted = null;

        var first = await service.AnalyzeWithReportAsync(root, options, ProviderOptions,
            confirmSubmission: (preview, _) => { submitted = preview; return Task.FromResult(true); });

        var expected = folders.SelectMany(folder => new[] { folder.Path, folder.Children[0].Path }).ToArray();
        AssertPaths(expected, submitted!.Items.Select(item => item.Path));
        AssertPaths(expected, provider.RequestedPaths);
        Assert.Equal(0, submitted.ProtectedCount);
        Assert.Equal(6, first.Results.Count);
        Assert.All(first.Results, result =>
        {
            Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
            Assert.Equal("测试文件", result.ShortPurpose);
            Assert.Equal("测试文件，请确认用途后处理。", result.Reason);
            Assert.Equal(SuggestedAction.DoNotDelete, result.SuggestedAction);
            Assert.Equal(RiskLevel.Protected, result.RiskLevel);
            Assert.False(string.IsNullOrWhiteSpace(result.OriginalAnalysisId));
        });

        var calls = provider.Calls;
        var second = await service.AnalyzeWithReportAsync(root, options, ProviderOptions,
            confirmSubmission: (preview, _) =>
            {
                Assert.Empty(preview.Items);
                Assert.Equal(6, preview.LocalCacheCount);
                Assert.Equal(0, preview.ProtectedCount);
                return Task.FromResult(true);
            });
        Assert.Equal(calls, provider.Calls);
        Assert.All(second.Results, result =>
        {
            Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin);
            Assert.Equal("测试文件", result.ShortPurpose);
            Assert.Equal(SuggestedAction.DoNotDelete, result.SuggestedAction);
        });
    }

    [Fact]
    public async Task Legacy_protection_rule_result_cannot_impersonate_a_successful_personal_cache()
    {
        var file = File(@"E:\", "protected.bin") with { IsProtectedLocation = true };
        var root = Root(@"E:\", file);
        var store = StoreWithCache(root, file.Path);
        var entry = store.Entries[file.Path];
        store.Entries[file.Path] = entry with
        {
            Result = entry.Result with { Origin = AiAnalysisResultOrigin.ProtectedByRule },
        };
        var provider = new Provider();
        var report = await Service(provider, store).AnalyzeWithReportAsync(root, Options(), ProviderOptions,
            confirmSubmission: (preview, _) =>
            {
                Assert.Equal(file.Path, Assert.Single(preview.Items).Path);
                Assert.Equal(0, preview.LocalCacheCount);
                return Task.FromResult(true);
            });
        Assert.Equal(1, provider.Calls);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, Assert.Single(report.Results).Origin);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"E:\")]
    public async Task Every_drive_submits_all_over_threshold_ordinary_custom_and_protected_items_and_reuses_their_cache(
        string drive)
    {
        const long gib = 1L << 30;
        var englishPath = drive + "ArbitraryEnglishFolder";
        var chinesePath = drive + "我自己的文件夹";
        var englishChild = File(englishPath, "large-assets.bin") with { SizeBytes = 2 * gib };
        var chineseChild = File(chinesePath, "任意数据.bin") with { SizeBytes = gib };
        var english = Root(englishPath, englishChild);
        var chinese = Root(chinesePath, chineseChild);
        var largeFile = File(drive, "standalone.bin") with { SizeBytes = 3 * gib };
        var protectedFile = File(drive, "protected-content.bin") with
        { SizeBytes = 4 * gib, IsProtectedLocation = true };
        var smallFile = File(drive, "small.bin") with { SizeBytes = gib / 2 };
        var smallFolderPath = drive + "SmallFolder";
        var smallFolder = Root(smallFolderPath,
            File(smallFolderPath, "tiny.bin") with { SizeBytes = gib / 4 });
        var root = Root(drive, english, chinese, largeFile, protectedFile, smallFile, smallFolder);
        var expected = new[] { english.Path, englishChild.Path, chinese.Path, chineseChild.Path,
            largeFile.Path, protectedFile.Path };
        var options = Options() with { RootPath = drive, AnalysisThresholdBytes = gib };
        var provider = new Provider();
        var service = Service(provider, new MemoryStore());

        var first = await service.AnalyzeWithReportAsync(root, options, ProviderOptions,
            confirmSubmission: (preview, _) =>
            {
                Assert.Equal(drive, preview.RootPath);
                AssertPaths(expected, preview.Items.Select(item => item.Path));
                Assert.Equal(0, preview.ProtectedCount);
                return Task.FromResult(true);
            });

        AssertPaths(expected, provider.RequestedPaths);
        AssertPaths(expected, first.Results.Select(result => result.Path));
        Assert.All(first.Results, result => Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin));
        Assert.DoesNotContain(first.Results, result => result.Path == smallFile.Path
            || result.Path == smallFolder.Path || result.Path == smallFolder.Children[0].Path);
        var firstCalls = provider.Calls;

        var second = await service.AnalyzeWithReportAsync(root, options, ProviderOptions,
            confirmSubmission: (preview, _) =>
            {
                Assert.Empty(preview.Items);
                Assert.Equal(expected.Length, preview.LocalCacheCount);
                return Task.FromResult(true);
            });

        Assert.Equal(firstCalls, provider.Calls);
        AssertPaths(expected, second.Results.Select(result => result.Path));
        Assert.All(second.Results, result => Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin));
    }

    private static FileSystemNode File(string parent, string name) => new(
        parent.TrimEnd('\\') + "\\" + name, name, ".bin", 1024,
        FileAttributes.Normal, null, null, false, false, [], [], DescendantFileCount: 1);

    private static FileSystemNode Root(string path, params FileSystemNode[] files) => new(
        path, Path.GetFileName(path.TrimEnd('\\')), "", files.Sum(file => file.SizeBytes),
        FileAttributes.Directory, null, null, true, false, [], files,
        DescendantFileCount: files.Length, DescendantDirectoryCount: 1);

    private static AiAnalysisService Service(IAiProvider provider, IAiAnalysisMemoryStore? store = null) => new(
        [provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner(), store);

    private static MemoryStore StoreWithCache(FileSystemNode root, params string[] cachedPaths)
    {
        var options = Options();
        var candidates = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(
            root, options.AnalysisThresholdBytes, options.MaxAiChunkTokens, options.UseTokenSavingMode)
            .Where(item => cachedPaths.Contains(item.Path));
        return new MemoryStore(candidates.Select(candidate => new AiAnalysisMemoryEntry(
            candidate.Path, candidate.IsDirectory, candidate.Signature,
            Result(candidate), DateTimeOffset.UtcNow.AddMinutes(-1),
            ProviderKind: ProviderOptions.Kind, Endpoint: ProviderOptions.Endpoint, Model: ProviderOptions.Model,
            PromptFingerprint: AiPromptFactory.CreateSystemPromptFingerprint(options.UseTokenSavingMode),
            UseTokenSavingMode: options.UseTokenSavingMode,
            AnalysisThresholdBytes: options.AnalysisThresholdBytes, MaxAiChunkTokens: options.MaxAiChunkTokens,
            ReasoningEffort: ProviderOptions.ReasoningEffort, Temperature: ProviderOptions.Temperature)).ToArray());
    }

    private static AiAnalysisResult Result(AiCandidateRecord record) => new(
        record.Path, RiskLevel.Low, SuggestedAction.Review, "测试文件，请确认用途后处理。", 0.9)
    {
        ShortPurpose = "测试文件",
        ConfidenceLevel = AiConfidenceLevel.High,
        AnalysisEvidenceScope = record.AnalysisEvidenceScope,
    };

    private static void AssertPaths(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.OrderBy(path => path, StringComparer.Ordinal),
            actual.OrderBy(path => path, StringComparer.Ordinal));

    private sealed class Provider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }
        public List<string> RequestedPaths { get; } = [];
        public bool RecommendRemoval { get; init; }

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestedPaths.AddRange(batch.Records.Select(record => record.Path));
            batch.RequestContext!.ReportUsage(new AiTokenUsage(300, 100));
            return Task.FromResult<IReadOnlyList<AiAnalysisResult>>(batch.Records.Select(record =>
                RecommendRemoval ? Result(record) with { SuggestedAction = SuggestedAction.RecycleCandidate, PurposeCategory = "cache" }
                    : Result(record)).ToArray());
        }
    }

    private sealed class MemoryStore(params AiAnalysisMemoryEntry[] entries) : IAiAnalysisMemoryStore
    {
        public Dictionary<string, AiAnalysisMemoryEntry> Entries { get; private set; } =
            entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        public int SaveCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(string rootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>>(Entries);

        public Task SaveEntriesAsync(string rootPath, IReadOnlyCollection<AiAnalysisMemoryEntry> values,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            Entries = values.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }
}

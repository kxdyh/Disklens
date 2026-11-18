using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_should_convert_provider_timeout_into_fallback_result_instead_of_bubbling_cancellation()
    {
        var root = CreateRootWithSingleCandidate();
        var service = new AiAnalysisService(
        [
            new TimeoutProvider()
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var result = Assert.Single(results);
        Assert.Equal(@"C:\Users\tester\Downloads\huge.iso", result.Path);
        Assert.Contains("超时", result.Reason, StringComparison.Ordinal);
        Assert.True(result.LooksLikeFallback());
        Assert.Equal(AiAnalysisResultOrigin.Incomplete, result.Origin);
    }

    [Fact]
    public async Task AnalyzeAsync_should_not_reuse_cached_fallback_entries_when_signature_matches()
    {
        var root = CreateRootWithSingleCandidate();
        var candidatePath = @"C:\Users\tester\Downloads\huge.iso";
        var currentSignature = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1, 2400)
            .Single()
            .Signature;

        var store = new InMemoryStore(new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [candidatePath] = new(
                Path: candidatePath,
                IsDirectory: false,
                Signature: currentSignature,
                Result: AiAnalysisResult.CreateFallback(candidatePath, "AI 分析失败，请检查网络连接或服务是否可用。"),
                AnalyzedAt: DateTimeOffset.UtcNow.AddMinutes(-5))
        });

        var provider = new RecordingProvider([
            new AiAnalysisResult(candidatePath, RiskLevel.High, SuggestedAction.DoNotDelete, "重新分析成功。", 0.91)
        ]);

        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(),
            store);

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var result = Assert.Single(results);
        Assert.Equal("重新分析成功。", result.Reason);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(store.SavedEntries);
        Assert.False(store.SavedEntries[0].Result.LooksLikeFallback());
    }

    [Fact]
    public async Task AnalyzeAsync_should_return_retryable_fallback_when_candidate_changed_and_reanalysis_fails()
    {
        var root = CreateRootWithSingleCandidate(modifiedAtUtcTicks: 638000000000000000);
        var candidatePath = @"C:\Users\tester\Downloads\huge.iso";
        var staleResult = new AiAnalysisResult(candidatePath, RiskLevel.Protected, SuggestedAction.DoNotDelete, "旧分析：不要删除。", 0.92);
        var store = new InMemoryStore(new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [candidatePath] = new(
                Path: candidatePath,
                IsDirectory: false,
                Signature: "OLD-SIGNATURE",
                Result: staleResult,
                AnalyzedAt: DateTimeOffset.UtcNow.AddHours(-1),
                ProviderKind: AiProviderKind.OpenAiCompatible,
                Endpoint: "https://example.invalid",
                Model: "demo-model",
                PromptFingerprint: AiPromptFactory.CreateSystemPromptFingerprint(),
                UseTokenSavingMode: false,
                AnalysisThresholdBytes: 1)
        });

        var service = new AiAnalysisService(
        [
            new TimeoutProvider()
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(),
            store);

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var result = Assert.Single(results);
        Assert.Equal(RiskLevel.Review, result.RiskLevel);
        Assert.Equal(SuggestedAction.Review, result.SuggestedAction);
        Assert.True(result.LooksLikeFallback());
        Assert.Contains("超时", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("沿用上次分析结果", result.Reason, StringComparison.Ordinal);
        Assert.Empty(store.SavedEntries);
    }

    [Fact]
    public async Task AnalyzeAsync_should_not_reuse_cached_result_when_model_context_changes()
    {
        var root = CreateRootWithSingleCandidate();
        var candidatePath = @"C:\Users\tester\Downloads\huge.iso";
        var currentSignature = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1, 2400)
            .Single()
            .Signature;
        var store = new InMemoryStore(new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [candidatePath] = new(
                Path: candidatePath,
                IsDirectory: false,
                Signature: currentSignature,
                Result: new AiAnalysisResult(candidatePath, RiskLevel.Protected, SuggestedAction.DoNotDelete, "旧模型分析结果。", 0.9),
                AnalyzedAt: DateTimeOffset.UtcNow.AddHours(-1))
        });
        var provider = new RecordingProvider([
            new AiAnalysisResult(candidatePath, RiskLevel.Review, SuggestedAction.Review, "新模型重新分析。", 0.72)
        ]);
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(),
            store);

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "new-model", "key"));

        var result = Assert.Single(results);
        Assert.Equal("新模型重新分析。", result.Reason);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_should_not_persist_transient_fallback_results_without_previous_success_entries()
    {
        var root = CreateRootWithSingleCandidate();
        var store = new InMemoryStore(new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.OrdinalIgnoreCase));
        var service = new AiAnalysisService(
        [
            new TimeoutProvider()
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(),
            store);

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var result = Assert.Single(results);
        Assert.True(result.LooksLikeFallback());
        Assert.Empty(store.SavedEntries);
    }

    [Fact]
    public async Task AnalyzeAsync_should_retry_a_single_oversized_directory_with_compact_payload()
    {
        const string candidatePath = @"C:\Users\tester\AppData\Local\Temp\cache";
        var folder = new FileSystemNode(
            Path: candidatePath,
            Name: "cache",
            Extension: string.Empty,
            SizeBytes: 4096,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: Path.Combine(candidatePath, "small.tmp"),
                    Name: "small.tmp",
                    Extension: ".tmp",
                    SizeBytes: 512,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [],
                    DescendantFileCount: 1)
            ],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 0);
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: folder.SizeBytes,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [folder],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 1);
        var provider = new CompactRetryProvider();
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1024, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var result = Assert.Single(results);
        Assert.Equal(candidatePath, result.Path);
        Assert.Contains("精简资料重试成功。", result.Reason);
        Assert.DoesNotContain("结构摘要", result.Reason);
        Assert.True(result.IsScopeLimited);
        Assert.Equal(AiScopeWarning.StructureSummary, result.ScopeWarning);
        Assert.Equal(AiConfidenceLevel.Unknown, result.EffectiveConfidenceLevel);
        Assert.Equal(.74, result.Confidence);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
        Assert.Equal(3, provider.CallCount);
        Assert.True(provider.ReceivedCompactPayload);
    }

    [Fact]
    public async Task Compact_file_recovery_does_not_invent_a_directory_structure_limitation()
    {
        var provider = new CompactRetryProvider();
        var service = new AiAnalysisService([provider], new AiCandidateBuilder(new WindowsLocationPolicy()), new AiBatchPlanner());
        var result = Assert.Single(await service.AnalyzeAsync(CreateRootWithSingleCandidate(),
            new ScanSessionOptions(@"C:\", AnalysisThresholdBytes: 1),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key")));

        Assert.Equal("精简资料重试成功。", result.Reason);
        Assert.Equal(.74, result.Confidence);
        Assert.False(result.IsScopeLimited);
        Assert.Equal(AiScopeWarning.None, result.ScopeWarning);
        Assert.True(provider.ReceivedCompactPayload);
    }

    [Fact]
    public async Task AnalyzeAsync_should_backfill_missing_items_when_provider_returns_only_partial_batch_results()
    {
        var root = CreateMicrosoftDirectoryRoot();
        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());
        var expectedPaths = builder
            .BuildCandidates(root, 1000, 2400)
            .Select(candidate => candidate.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var provider = new RecordingProvider(
        [
            new AiAnalysisResult(@"C:\Users\tester\AppData\Local\Microsoft", RiskLevel.Review, SuggestedAction.Review, "微软目录需要复核。", 0.6),
            new AiAnalysisResult(@"C:\Users\tester\AppData\Local\Microsoft\OneNote", RiskLevel.Review, SuggestedAction.Review, "OneNote 目录需要复核。", 0.58),
        ]);

        var service = new AiAnalysisService(
        [
            provider
        ],
            builder,
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1000, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        Assert.Equal(expectedPaths.Count, results.Count);
        Assert.True(expectedPaths.SetEquals(results.Select(result => result.Path)));
        Assert.Contains(results, result => string.Equals(result.Path, @"C:\Users\tester\AppData\Local\Microsoft\Edge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, result => string.Equals(result.Path, @"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAsync_should_continue_later_batches_after_a_provider_io_failure_and_cover_every_candidate()
    {
        var root = CreateRootWithThreeCandidates();
        var provider = new FailFirstBatchThenSucceedProvider();
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 1),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var expectedPaths = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1, 2400)
            .Select(candidate => candidate.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(3, provider.CallCount);
        Assert.Equal(expectedPaths.Count, results.Count);
        Assert.True(expectedPaths.SetEquals(results.Select(result => result.Path)));
        Assert.True(Assert.Single(results, result => result.LooksLikeFallback()).Reason.Contains("网络", StringComparison.Ordinal));
        Assert.Equal(2, results.Count(result => !result.LooksLikeFallback()));
    }

    [Fact]
    public async Task AnalyzeAsync_should_send_every_threshold_file_across_all_twenty_item_batches()
    {
        var files = Enumerable.Range(1, 43)
            .Select(index => ($"payload-{index:D2}.bin", 2048L + index))
            .Append(("below-threshold.tmp", 999L))
            .ToArray();
        var root = CreateRootWithFiles(files);
        var provider = new CollectingProvider();
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1000, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var expectedPaths = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1000, 2400)
            .Select(candidate => candidate.Path)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(43, expectedPaths.Count);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(43, provider.RequestedPaths.Count);
        Assert.True(expectedPaths.SetEquals(provider.RequestedPaths));
        Assert.Equal(43, results.Count);
        Assert.True(expectedPaths.SetEquals(results.Select(result => result.Path)));
        Assert.All(results, result => Assert.False(result.LooksLikeFallback()));
    }

    [Fact]
    public async Task AnalyzeAsync_should_split_context_limited_batches_until_every_file_is_analyzed()
    {
        var root = CreateRootWithFiles(Enumerable.Range(1, 17)
            .Select(index => ($"context-payload-{index:D2}.bin", 2048L + index))
            .ToArray());
        var provider = new ContextLimitedProvider(maximumRecordsPerRequest: 2);
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1000, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20, MaxAiAnalysisTokens: 1_000_000),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var expectedPaths = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1000, 2400)
            .Select(candidate => candidate.Path)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(17, results.Count);
        Assert.True(expectedPaths.SetEquals(provider.RequestedPaths));
        Assert.True(expectedPaths.SetEquals(results.Select(result => result.Path)));
        Assert.All(results, result => Assert.False(result.LooksLikeFallback()));
        Assert.True(provider.RejectedOversizedRequests > 0);
        Assert.All(provider.AcceptedBatchSizes, size => Assert.InRange(size, 1, 2));
    }

    [Fact]
    public async Task AnalyzeAsync_should_retry_only_omitted_files_after_a_truncated_provider_response()
    {
        var root = CreateRootWithFiles(Enumerable.Range(1, 9)
            .Select(index => ($"truncated-{index:D2}.bin", 4096L + index))
            .ToArray());
        var provider = new TruncatingProvider();
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1000, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var expectedPaths = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1000, 2400)
            .Select(candidate => candidate.Path)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(9, results.Count);
        Assert.True(expectedPaths.SetEquals(results.Select(result => result.Path)));
        Assert.True(expectedPaths.SetEquals(provider.ReturnedPaths));
        Assert.All(results, result => Assert.False(result.LooksLikeFallback()));
        Assert.True(provider.CallCount > 1);
    }

    [Fact]
    public async Task AnalyzeAsync_should_mark_a_batch_untrusted_when_provider_returns_duplicate_or_unknown_paths()
    {
        var root = CreateRootWithTwoCandidates();
        var candidates = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(root, 1, 2400);
        var duplicatePath = candidates[0].Path;
        var validPath = candidates[1].Path;
        var unexpectedPath = @"C:\Users\tester\Downloads\not-requested.tmp";
        var provider = new RecordingProvider(
        [
            new AiAnalysisResult(duplicatePath, RiskLevel.Low, SuggestedAction.RecycleCandidate, "第一条重复结果。", 0.8),
            new AiAnalysisResult(duplicatePath, RiskLevel.High, SuggestedAction.Keep, "第二条重复结果。", 0.7),
            new AiAnalysisResult(validPath, RiskLevel.Low, SuggestedAction.RecycleCandidate, "正常结果。", 0.9),
            new AiAnalysisResult(unexpectedPath, RiskLevel.High, SuggestedAction.Keep, "未请求的路径。", 0.5),
        ]);
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        Assert.Equal(2, results.Count);
        Assert.Equal(candidates.Select(candidate => candidate.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            results.Select(result => result.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        Assert.All(results, result => Assert.True(result.LooksLikeFallback()));
        Assert.All(results, result => Assert.Contains("重复", result.Reason, StringComparison.Ordinal));
        Assert.All(results, result => Assert.Contains(unexpectedPath, result.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAsync_should_propagate_caller_cancellation_instead_of_backfilling_it_as_provider_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new AiAnalysisService(
        [
            new CallerCancellationProvider(cancellation)
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(
            CreateRootWithSingleCandidate(),
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 4),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"),
            cancellation.Token));
    }

    [Fact]
    public async Task AnalyzeAsync_should_analyze_protected_purposes_and_only_constrain_deletion_advice()
    {
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var rootPath = Path.GetPathRoot(windowsPath) ?? @"C:\";
        var protectedPath = Path.Combine(windowsPath, "System32", "huge.dll");
        var root = new FileSystemNode(
            Path: rootPath,
            Name: rootPath,
            Extension: string.Empty,
            SizeBytes: 2048,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: protectedPath,
                    Name: "huge.dll",
                    Extension: ".dll",
                    SizeBytes: 2048,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [],
                    DescendantFileCount: 1)
            ],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 1);

        var provider = new RecordingProvider([
            new(protectedPath, RiskLevel.Low, SuggestedAction.RecycleCandidate, "系统运行所需的动态链接库", 0.9)
            {
                ShortPurpose = "系统运行依赖", ConfidenceLevel = AiConfidenceLevel.High,
                PurposeCategory = "runtime",
            }
        ]);
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner());

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: rootPath, AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400, MaxAiBatchSize: 20),
            new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key"));

        var result = Assert.Single(results);
        Assert.Equal(protectedPath, result.Path);
        Assert.Equal(RiskLevel.Protected, result.RiskLevel);
        Assert.Equal(SuggestedAction.DoNotDelete, result.SuggestedAction);
        Assert.Equal(AiAnalysisResultOrigin.AiAnalyzed, result.Origin);
        Assert.Equal("系统运行依赖", result.ShortPurpose);
        Assert.Equal("系统运行所需的动态链接库", result.Reason);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_should_mark_a_matching_memory_result_as_cached()
    {
        var root = CreateRootWithSingleCandidate();
        var candidatePath = @"C:\Users\tester\Downloads\huge.iso";
        var candidate = new AiCandidateBuilder(new WindowsLocationPolicy())
            .BuildCandidates(root, 1, 2400)
            .Single();
        var providerOptions = new AiProviderOptions(AiProviderKind.OpenAiCompatible, "https://example.invalid", "demo-model", "key");
        var cachedResult = new AiAnalysisResult(candidatePath, RiskLevel.Low, SuggestedAction.RecycleCandidate, "已缓存的结果。", 0.9);
        var store = new InMemoryStore(new Dictionary<string, AiAnalysisMemoryEntry>(StringComparer.Ordinal)
        {
            [candidatePath] = new(
                Path: candidatePath,
                IsDirectory: false,
                Signature: candidate.Signature,
                Result: cachedResult,
                AnalyzedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                ProviderKind: providerOptions.Kind,
                Endpoint: providerOptions.Endpoint,
                Model: providerOptions.Model,
                PromptFingerprint: AiPromptFactory.CreateSystemPromptFingerprint(),
                AnalysisThresholdBytes: 1)
        });
        var provider = new RecordingProvider([]);
        var service = new AiAnalysisService(
        [
            provider
        ],
            new AiCandidateBuilder(new WindowsLocationPolicy()),
            new AiBatchPlanner(),
            store);

        var results = await service.AnalyzeAsync(
            root,
            new ScanSessionOptions(RootPath: @"C:\", AnalysisThresholdBytes: 1, MaxAiChunkTokens: 2400),
            providerOptions);

        var result = Assert.Single(results);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, result.Origin);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(1, AiAnalysisSummary.FromResults(results).CacheReusedCount);
    }

    private static FileSystemNode CreateRootWithSingleCandidate(long modifiedAtUtcTicks = 637999999999999999)
    {
        return new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 1024,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\Downloads\huge.iso",
                    Name: "huge.iso",
                    Extension: ".iso",
                    SizeBytes: 1024,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: new DateTimeOffset(new DateTime(modifiedAtUtcTicks, DateTimeKind.Utc)),
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [],
                    DescendantFileCount: 1,
                    DescendantDirectoryCount: 0)
            ],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 1);
    }

    private static FileSystemNode CreateRootWithTwoCandidates()
    {
        return CreateRootWithFiles(
        [
            ("first.iso", 4096L),
            ("second.zip", 2048L),
        ]);
    }

    private static FileSystemNode CreateRootWithThreeCandidates()
    {
        return CreateRootWithFiles(
        [
            ("first.iso", 4096L),
            ("second.zip", 3072L),
            ("third.vhdx", 2048L),
        ]);
    }

    private static FileSystemNode CreateRootWithFiles(IReadOnlyList<(string Name, long Size)> files)
    {
        var children = files.Select(file => new FileSystemNode(
            Path: Path.Combine(@"C:\Users\tester\Downloads", file.Name),
            Name: file.Name,
            Extension: Path.GetExtension(file.Name),
            SizeBytes: file.Size,
            Attributes: FileAttributes.Normal,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: false,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 0)).ToArray();

        return new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: children.Sum(child => child.SizeBytes),
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: children,
            DescendantFileCount: children.Length,
            DescendantDirectoryCount: 0);
    }

    private static FileSystemNode CreateMicrosoftDirectoryRoot()
    {
        var userData = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data",
            Name: "User Data",
            Extension: string.Empty,
            SizeBytes: 1010,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);

        var edge = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft\Edge",
            Name: "Edge",
            Extension: string.Empty,
            SizeBytes: 1010,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [userData],
            DescendantFileCount: 0,
            DescendantDirectoryCount: 1);

        var oneNote = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft\OneNote",
            Name: "OneNote",
            Extension: string.Empty,
            SizeBytes: 1140,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [],
            DescendantFileCount: 0,
            DescendantDirectoryCount: 0);

        var microsoft = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Microsoft",
            Name: "Microsoft",
            Extension: string.Empty,
            SizeBytes: 4330,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                oneNote,
                edge,
                new FileSystemNode(
                    Path: @"C:\Users\tester\AppData\Local\Microsoft\OneDrive",
                    Name: "OneDrive",
                    Extension: string.Empty,
                    SizeBytes: 870,
                    Attributes: FileAttributes.Directory,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: true,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [],
                    DescendantFileCount: 0,
                    DescendantDirectoryCount: 0)
            ],
            DescendantFileCount: 0,
            DescendantDirectoryCount: 3);

        return new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 4330,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [microsoft],
            DescendantFileCount: 0,
            DescendantDirectoryCount: 4);
    }

    private sealed class InMemoryStore : IAiAnalysisMemoryStore
    {
        private readonly IReadOnlyDictionary<string, AiAnalysisMemoryEntry> _entries;

        public InMemoryStore(IReadOnlyDictionary<string, AiAnalysisMemoryEntry> entries)
        {
            _entries = entries;
        }

        public List<AiAnalysisMemoryEntry> SavedEntries { get; } = [];

        public Task<IReadOnlyDictionary<string, AiAnalysisMemoryEntry>> LoadEntriesAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entries);
        }

        public Task SaveEntriesAsync(
            string rootPath,
            IReadOnlyCollection<AiAnalysisMemoryEntry> entries,
            CancellationToken cancellationToken = default)
        {
            SavedEntries.Clear();
            SavedEntries.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProvider : IAiProvider
    {
        private readonly IReadOnlyList<AiAnalysisResult> _results;

        public RecordingProvider(IReadOnlyList<AiAnalysisResult> results)
        {
            _results = results;
        }

        public int CallCount { get; private set; }

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_results);
        }
    }

    private sealed class CollectingProvider : IAiProvider
    {
        public int CallCount { get; private set; }

        public HashSet<string> RequestedPaths { get; } = new(StringComparer.Ordinal);

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            foreach (var record in batch.Records)
            {
                RequestedPaths.Add(record.Path);
            }

            IReadOnlyList<AiAnalysisResult> results = batch.Records
                .Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "已分析。", 0.8))
                .ToArray();
            return Task.FromResult(results);
        }
    }

    private sealed class ContextLimitedProvider(int maximumRecordsPerRequest) : IAiProvider
    {
        public int RejectedOversizedRequests { get; private set; }

        public List<int> AcceptedBatchSizes { get; } = [];

        public HashSet<string> RequestedPaths { get; } = new(StringComparer.Ordinal);

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            if (batch.Records.Count > maximumRecordsPerRequest)
            {
                RejectedOversizedRequests++;
                throw new HttpRequestException("simulated model context limit", null, System.Net.HttpStatusCode.RequestEntityTooLarge);
            }

            AcceptedBatchSizes.Add(batch.Records.Count);
            foreach (var record in batch.Records)
            {
                RequestedPaths.Add(record.Path);
            }

            IReadOnlyList<AiAnalysisResult> results = batch.Records
                .Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "上下文拆批后已分析。", 0.82))
                .ToArray();
            return Task.FromResult(results);
        }
    }

    private sealed class TruncatingProvider : IAiProvider
    {
        public int CallCount { get; private set; }

        public HashSet<string> ReturnedPaths { get; } = new(StringComparer.Ordinal);

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var returnedRecords = batch.Records.Count > 3
                ? batch.Records.Take(3)
                : batch.Records;
            foreach (var record in returnedRecords)
            {
                ReturnedPaths.Add(record.Path);
            }

            IReadOnlyList<AiAnalysisResult> results = returnedRecords
                .Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "已分析。", 0.8))
                .ToArray();
            return Task.FromResult(results);
        }
    }

    private sealed class TimeoutProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new TaskCanceledException("simulated timeout");
        }
    }

    private sealed class CompactRetryProvider : IAiProvider
    {
        public int CallCount { get; private set; }

        public bool ReceivedCompactPayload { get; private set; }

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedCompactPayload = batch.UseCompactPayload;
            if (!batch.UseCompactPayload)
            {
                throw new HttpRequestException(
                    "simulated context limit",
                    inner: null,
                    System.Net.HttpStatusCode.RequestEntityTooLarge);
            }

            IReadOnlyList<AiAnalysisResult> results = batch.Records
                .Select(record => new AiAnalysisResult(record.Path, RiskLevel.Review, SuggestedAction.Review, "精简资料重试成功。", 0.74))
                .ToArray();
            return Task.FromResult(results);
        }
    }

    private sealed class FailFirstBatchThenSucceedProvider : IAiProvider
    {
        public int CallCount { get; private set; }

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new IOException("simulated transport failure");
            }

            IReadOnlyList<AiAnalysisResult> results = batch.Records
                .Select(record => new AiAnalysisResult(record.Path, RiskLevel.Low, SuggestedAction.RecycleCandidate, "正常分析结果。", 0.9))
                .ToArray();
            return Task.FromResult(results);
        }
    }

    private sealed class CallerCancellationProvider : IAiProvider
    {
        private readonly CancellationTokenSource _cancellation;

        public CallerCancellationProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(
            AiBatch batch,
            AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }
}

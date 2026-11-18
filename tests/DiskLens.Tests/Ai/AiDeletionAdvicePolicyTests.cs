using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiDeletionAdvicePolicyTests
{
    [Theory]
    [InlineData(@"C:\Users\tester\AppData\Local\Temp", "cache", RiskLevel.Low)]
    [InlineData(@"C:\Users\tester\AppData\Local\Browser\User Data\Default\Cache", "cache", RiskLevel.Review)]
    [InlineData(@"E:\Build\shader-cache", "cache", RiskLevel.Review)]
    [InlineData(@"E:\Diagnostics\application.log", "logs", RiskLevel.Low)]
    [InlineData(@"E:\Diagnostics\application.dmp", "logs", RiskLevel.Review)]
    [InlineData(@"C:\Users\tester\Downloads\ExampleOS-installer.iso", "downloads", RiskLevel.Low)]
    [InlineData(@"C:\Users\tester\Downloads\ExampleApp-setup.msi", "downloads", RiskLevel.Review)]
    public void Explicit_disposable_advice_should_survive_minor_risk_and_rebuild_cost(
        string path, string category, RiskLevel risk)
    {
        var original = Recycle(path, category) with { RiskLevel = risk };

        var constrained = AiDeletionAdvicePolicy.Constrain(original);

        Assert.Same(original, constrained);
        Assert.Equal(SuggestedAction.RecycleCandidate, constrained.SuggestedAction);
        Assert.Equal(risk, constrained.RiskLevel);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("application")]
    [InlineData("configuration")]
    [InlineData("user-data")]
    [InlineData("unknown")]
    [InlineData("unrecognized-legacy-category")]
    [InlineData("")]
    [InlineData(null)]
    public void Runtime_personal_or_unknown_purposes_should_override_contradictory_recycle_advice(string? category)
    {
        var original = Recycle(@"E:\Example\data", category!);

        var constrained = AiDeletionAdvicePolicy.Constrain(original);

        Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
        Assert.Equal(RiskLevel.High, constrained.RiskLevel);
        Assert.Equal(original.PurposeCategory, constrained.PurposeCategory);
        Assert.Equal(original.Reason, constrained.Reason);
        Assert.Equal(original.ConfidenceLevel, constrained.ConfidenceLevel);
    }

    [Theory]
    [InlineData(AiConfidenceLevel.Unknown, .95)]
    [InlineData(AiConfidenceLevel.Unknown, 0)]
    [InlineData(AiConfidenceLevel.Low, .95)]
    [InlineData(AiConfidenceLevel.Low, .3)]
    [InlineData(AiConfidenceLevel.Medium, .3)]
    [InlineData(AiConfidenceLevel.High, .3)]
    public void Missing_or_effectively_low_confidence_should_not_authorize_recycle(
        AiConfidenceLevel confidenceLevel, double confidence)
    {
        var original = Recycle(@"E:\Example\cache", "cache") with
        {
            ConfidenceLevel = confidenceLevel, Confidence = confidence,
        };

        var constrained = AiDeletionAdvicePolicy.Constrain(original);

        Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
        Assert.Equal(original.ConfidenceLevel, constrained.ConfidenceLevel);
        Assert.Equal(original.Confidence, constrained.Confidence);
    }

    [Theory]
    [InlineData(AiConfidenceLevel.Medium, .7)]
    [InlineData(AiConfidenceLevel.High, .9)]
    public void Explicit_medium_or_high_confidence_can_keep_existing_disposable_advice(
        AiConfidenceLevel confidenceLevel, double confidence)
    {
        var original = Recycle(@"E:\Example\cache", "cache") with
        {
            ConfidenceLevel = confidenceLevel, Confidence = confidence,
        };

        Assert.Same(original, AiDeletionAdvicePolicy.Constrain(original));
    }

    [Theory]
    [InlineData(RiskLevel.High, false, RiskLevel.High)]
    [InlineData(RiskLevel.Protected, false, RiskLevel.Protected)]
    [InlineData(RiskLevel.Low, true, RiskLevel.Protected)]
    [InlineData(RiskLevel.Review, true, RiskLevel.Protected)]
    public void High_risk_and_local_protection_should_override_even_high_confidence_cache_advice(
        RiskLevel risk, bool protectedLocation, RiskLevel expectedRisk)
    {
        var original = Recycle(@"C:\Windows\System32\example.tmp", "cache") with { RiskLevel = risk };

        var constrained = AiDeletionAdvicePolicy.Constrain(original, protectedLocation);

        Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
        Assert.Equal(expectedRisk, constrained.RiskLevel);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\tester")]
    [InlineData(@"C:\Users\tester\Downloads")]
    [InlineData(@"c:/users/tester/downloads/")]
    [InlineData(@"C:\Users\tester\AppData")]
    [InlineData(@"C:\Users\tester\AppData\Local")]
    [InlineData(@"C:\Users\tester\AppData\LocalLow")]
    [InlineData(@"C:\Users\tester\AppData\Roaming")]
    public void Mixed_roots_should_not_become_disposable_when_mislabeled_as_cache(string path)
    {
        var constrained = AiDeletionAdvicePolicy.Constrain(Recycle(path, "cache"));

        Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
    }

    [Theory]
    [InlineData(@"C:\Users\tester\Documents\report.docx")]
    [InlineData(@"C:\Users\tester\Desktop\draft.txt")]
    [InlineData(@"C:\Users\tester\Pictures\photo.jpg")]
    [InlineData(@"C:\Users\tester\Videos\recording.mp4")]
    [InlineData(@"C:\Users\tester\Music\track.flac")]
    [InlineData(@"E:\Browser\Default\IndexedDB\store")]
    [InlineData(@"E:\Browser\Default\Local Storage\leveldb")]
    [InlineData(@"E:\Browser\Default\Session Storage\store")]
    [InlineData(@"E:\Browser\Default\Service Worker\data")]
    [InlineData(@"E:\Games\Saved Games\slot1")]
    [InlineData(@"E:\Games\saves\slot1")]
    [InlineData(@"E:\Ai\models\parameters.bin")]
    [InlineData(@"E:\Ai\weights\parameters.bin")]
    [InlineData(@"E:\Ai\checkpoints\training.bin")]
    [InlineData(@"E:\Data\application.db")]
    [InlineData(@"E:\Data\application.sqlite")]
    [InlineData(@"E:\Data\application.sqlite3")]
    [InlineData(@"E:\Data\application.db-wal")]
    [InlineData(@"E:\Data\application.sqlite-shm")]
    [InlineData(@"C:\Users\tester\Downloads\machine.vhd")]
    [InlineData(@"C:\Users\tester\Downloads\machine.vhdx")]
    [InlineData(@"C:\Users\tester\Downloads\machine.qcow2")]
    [InlineData(@"E:\Ai\parameters.gguf")]
    [InlineData(@"E:\Ai\parameters.safetensors")]
    public void Persistent_personal_stores_databases_and_models_should_resist_a_wrong_cache_label(string path)
    {
        var constrained = AiDeletionAdvicePolicy.Constrain(Recycle(path, "cache"));

        Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
        Assert.Equal(RiskLevel.High, constrained.RiskLevel);
    }

    [Fact]
    public void Actual_personal_known_folders_should_remain_protected_when_redirected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folders = new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic };
        var configuredRoots = folders.Select(Environment.GetFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct().ToArray();
        Assert.NotEmpty(configuredRoots);
        foreach (var root in configuredRoots)
        {
            // The configured root also covers OneDrive/other redirected Known Folders;
            // no file is created or read at this synthetic descendant path.
            var path = root.TrimEnd('\\') + @"\policy-test\personal.bin";
            var constrained = AiDeletionAdvicePolicy.Constrain(Recycle(path, "cache"));
            Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
        }
    }

    [Fact]
    public void Unknown_legacy_cached_advice_should_not_become_an_authorized_recycle_result()
    {
        var legacy = new AiAnalysisResult(@"E:\Legacy\cache", RiskLevel.Low,
            SuggestedAction.RecycleCandidate, "旧格式结果", .95)
        {
            Origin = AiAnalysisResultOrigin.CacheReused,
        };

        var constrained = AiDeletionAdvicePolicy.Constrain(legacy);

        Assert.Equal(SuggestedAction.DoNotDelete, constrained.SuggestedAction);
        Assert.Equal(AiPurposeCategory.Unknown, constrained.PurposeCategory);
        Assert.Equal(AiConfidenceLevel.Unknown, constrained.ConfidenceLevel);
        Assert.Equal(AiAnalysisResultOrigin.CacheReused, constrained.Origin);
    }

    [Theory]
    [InlineData(SuggestedAction.Review)]
    [InlineData(SuggestedAction.Keep)]
    [InlineData(SuggestedAction.DoNotDelete)]
    public void Policy_should_never_promote_or_rewrite_a_non_recycle_result(SuggestedAction action)
    {
        var original = Recycle(@"E:\Example\cache", "cache") with { SuggestedAction = action };

        Assert.Same(original, AiDeletionAdvicePolicy.Constrain(original));
        Assert.Same(original, AiDeletionAdvicePolicy.Constrain(original, protectedLocation: true));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("application")]
    [InlineData("configuration")]
    [InlineData("user-data")]
    public void Retained_runtime_or_personal_descendant_should_override_its_cache_parent(string category)
    {
        var parent = Recycle(@"E:\Audit\cache", "cache");
        var child = Recycle(parent.Path + @"\important.bin", category) with
        {
            SuggestedAction = SuggestedAction.DoNotDelete, RiskLevel = RiskLevel.High,
        };
        var results = new Dictionary<string, AiAnalysisResult> { [parent.Path] = parent, [child.Path] = child };

        AiDeletionAdvicePolicy.ConstrainAncestors(results);

        Assert.Equal(SuggestedAction.DoNotDelete, results[parent.Path].SuggestedAction);
        Assert.Equal(RiskLevel.High, results[parent.Path].RiskLevel);
        Assert.Same(child, results[child.Path]);
        Assert.Equal(parent.Reason, results[parent.Path].Reason);
        Assert.Equal(parent.Confidence, results[parent.Path].Confidence);
    }

    [Fact]
    public void Retained_descendant_should_match_parent_boundaries_instead_of_similar_name_prefixes()
    {
        var parent = Recycle(@"E:\Audit\cache", "cache");
        var similar = Recycle(@"E:\Audit\cache2", "cache");
        var child = Recycle(parent.Path + @"\runtime.dll", "runtime") with
        {
            SuggestedAction = SuggestedAction.DoNotDelete, RiskLevel = RiskLevel.High,
        };
        var results = new Dictionary<string, AiAnalysisResult>
        {
            [parent.Path] = parent, [similar.Path] = similar, [child.Path] = child,
        };

        AiDeletionAdvicePolicy.ConstrainAncestors(results);

        Assert.Equal(SuggestedAction.DoNotDelete, results[parent.Path].SuggestedAction);
        Assert.Same(similar, results[similar.Path]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_warning_or_unfinished_fallback_alone_should_not_reclassify_a_cache_parent(bool fallback)
    {
        var parent = Recycle(@"E:\Audit\cache", "cache");
        var childPath = parent.Path + @"\unread.bin";
        var child = fallback
            ? AiAnalysisResult.CreateFallback(childPath, "读取失败，未完成分析。") with
            {
                RiskLevel = RiskLevel.High, PurposeCategory = "runtime",
            }
            : new AiAnalysisResult(childPath, RiskLevel.Review, SuggestedAction.Review, "读取问题", .3)
            {
                ConfidenceLevel = AiConfidenceLevel.Low, IsScopeLimited = true,
                ScopeWarning = AiScopeWarning.ItemReadIncomplete,
            };
        var results = new Dictionary<string, AiAnalysisResult> { [parent.Path] = parent, [child.Path] = child };

        AiDeletionAdvicePolicy.ConstrainAncestors(results);

        Assert.Same(parent, results[parent.Path]);
        Assert.Same(child, results[child.Path]);
    }

    [Fact]
    public void Entirely_regenerable_cache_descendants_should_preserve_the_parents_existing_advice()
    {
        var parent = Recycle(@"E:\Audit\cache", "cache");
        var child = Recycle(parent.Path + @"\generated.tmp", "cache");
        var results = new Dictionary<string, AiAnalysisResult> { [parent.Path] = parent, [child.Path] = child };

        AiDeletionAdvicePolicy.ConstrainAncestors(results);

        Assert.Same(parent, results[parent.Path]);
        Assert.Same(child, results[child.Path]);
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\ExampleGame\content\texture.cache")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\ExampleGame\Shaders\shader.pack")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\ExampleGame\Textures\assets.pak")]
    [InlineData(@"E:\SteamLibrary\steamapps\workshop\content\123\assets.cache")]
    [InlineData(@"E:\SDK\runtimes\win-x64\native.dll")]
    [InlineData(@"E:\SDK\shared-runtimes\native.dll")]
    [InlineData(@"E:\sample-volume\Users")]
    [InlineData(@"E:\sample-volume\Users\tester")]
    [InlineData(@"E:\sample-volume\Users\tester\Downloads")]
    public void Installed_assets_runtime_payloads_and_nested_personal_roots_cannot_be_reclassified_as_cache(string path)
    {
        Assert.Equal(SuggestedAction.DoNotDelete,
            AiDeletionAdvicePolicy.Constrain(Recycle(path, "cache")).SuggestedAction);
    }

    [Fact]
    public void Explicit_game_shader_cache_should_remain_distinct_from_installed_shaders_and_assets()
    {
        var result = Recycle(@"E:\SteamLibrary\steamapps\common\ExampleGame\ShaderCache\generated.bin", "cache");

        Assert.Same(result, AiDeletionAdvicePolicy.Constrain(result));
    }

    private static AiAnalysisResult Recycle(string path, string category) => new(
        path, RiskLevel.Low, SuggestedAction.RecycleCandidate, "用于测试的可重建数据", .95)
    {
        ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = category,
    };
}

using System.IO;

namespace DiskLens.Tests.Ai;

public sealed class AiAnalysisServiceSourceTests
{
    [Fact]
    public void AiAnalysisService_should_map_http_failures_to_user_friendly_messages()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var servicePath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "AiAnalysisService.cs");
        var source = File.ReadAllText(servicePath);

        Assert.Contains("MapAiFailureReason", source, StringComparison.Ordinal);
        Assert.Contains("StatusCode", source, StringComparison.Ordinal);
        Assert.Contains("CreateFallback", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AiAnalysisService_should_only_reuse_successful_memory_entries()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var servicePath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Ai", "AiAnalysisService.cs");
        var resultPath = Path.Combine(repoRoot, "src", "DiskLens.Core", "Models", "AiAnalysisResult.cs");
        var serviceSource = File.ReadAllText(servicePath);
        var resultSource = File.ReadAllText(resultPath);

        Assert.Contains("!entry.Result.LooksLikeFallback()", serviceSource, StringComparison.Ordinal);
        Assert.Contains("CreateStaleReuse", serviceSource, StringComparison.Ordinal);
        Assert.Contains("result.LooksLikeFallback()", serviceSource, StringComparison.Ordinal);
        Assert.Contains("public bool LooksLikeFallback()", resultSource, StringComparison.Ordinal);
        Assert.Contains("IsFallback: true", resultSource, StringComparison.Ordinal);
    }
}

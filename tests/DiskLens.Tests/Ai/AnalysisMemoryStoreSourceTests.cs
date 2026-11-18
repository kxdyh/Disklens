using System.IO;

namespace DiskLens.Tests.Ai;

public sealed class AnalysisMemoryStoreSourceTests
{
    [Fact]
    public void SaveEntriesAsync_should_replace_memory_file_atomically()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sourcePath = Path.Combine(repoRoot, "src", "DiskLens.App", "Services", "AnalysisMemoryStore.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(tempPath, _storagePath, overwrite: true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Create(_storagePath)", source, StringComparison.Ordinal);
    }
}

using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class ScanReadIssueTests : IDisposable
{
    private readonly string _directory;

    public ScanReadIssueTests()
    {
        var root = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(AppContext.BaseDirectory, ".temp-tests");
        _directory = Path.Combine(root, "read-issues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task A_failed_file_keeps_its_path_and_native_reason_and_does_not_stop_other_files()
    {
        var deniedPath = Path.Combine(_directory, "denied.bin");
        var readablePath = Path.Combine(_directory, "readable.bin");
        await File.WriteAllTextAsync(deniedPath, "denied");
        await File.WriteAllTextAsync(readablePath, "readable");
        var scanner = new DirectoryScanner(new WindowsLocationPolicy(), new DeniedFileReader(deniedPath));

        var result = await scanner.ScanAsync(new ScanSessionOptions(_directory));

        Assert.True(result.IsPartial);
        Assert.Equal(1, result.ScanIssueCount);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(deniedPath, issue.Path);
        Assert.False(issue.IsDirectory);
        Assert.Equal(5, issue.NativeErrorCode);
        Assert.Equal("读取文件信息", issue.Operation);
        Assert.Contains("diagnostic access denied", issue.Message);
        var failed = Assert.Single(result.Root.Children, node => node.Path == deniedPath);
        Assert.Contains("error", failed.RiskTags);
        Assert.Equal(0, failed.SizeBytes);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(4096, result.BytesDiscovered);
        Assert.Equal(4096, result.Root.SizeBytes);
    }

    [Fact]
    public async Task An_estimated_allocation_is_preserved_and_does_not_become_a_read_failure()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "estimated.bin"), "known-length");
        var result = await new DirectoryScanner(new WindowsLocationPolicy(), new EstimatedReader())
            .ScanAsync(new ScanSessionOptions(_directory));

        Assert.False(result.IsPartial);
        Assert.Empty(result.Issues);
        Assert.Equal(0, result.ScanIssueCount);
        Assert.Equal(1, result.EstimatedStorageItemCount);
        Assert.Equal(12, result.BytesDiscovered);
        Assert.Contains("storage-size-estimated", Assert.Single(result.Root.Children).RiskTags);
    }

    private sealed class DeniedFileReader(string deniedPath) : IFileStorageReader
    {
        public FileStorageInfo Read(FileInfo fileInfo) => fileInfo.FullName == deniedPath
            ? throw new UnauthorizedAccessException("diagnostic access denied")
            : new FileStorageInfo(fileInfo.FullName, fileInfo.Length, 4096);
    }

    private sealed class EstimatedReader : IFileStorageReader
    {
        public FileStorageInfo Read(FileInfo fileInfo) => new(null, fileInfo.Length, fileInfo.Length,
            IsEstimated: true, EstimationReason: "allocation-open-failed:5");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class ScanStorageProgressTests : IDisposable
{
    private readonly string _rootDirectory;

    public ScanStorageProgressTests()
    {
        var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Progress.Tests");
        _rootDirectory = Path.Combine(artifactRoot, "storage-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(long.MaxValue, null)]
    [InlineData(100L, 0L)]
    [InlineData(100L, -1L)]
    [InlineData(100L, long.MinValue)]
    public void Unknown_or_nonpositive_volume_usage_has_no_percentage(long discoveredBytes, long? volumeUsedBytes)
    {
        Assert.Null(ScanStorageProgress.CalculatePercent(discoveredBytes, volumeUsedBytes));
    }

    [Theory]
    [InlineData(long.MinValue, 100L, 0d)]
    [InlineData(-1L, 100L, 0d)]
    [InlineData(0L, 100L, 0d)]
    [InlineData(1L, 4L, 25d)]
    [InlineData(3L, 4L, 75d)]
    [InlineData(100L, 100L, 100d)]
    [InlineData(101L, 100L, 100d)]
    [InlineData(long.MaxValue, 1L, 100d)]
    [InlineData(long.MaxValue, long.MaxValue, 100d)]
    [InlineData(long.MaxValue / 2, long.MaxValue, 50d)]
    public void Percentage_uses_byte_ratio_and_clamps_without_integer_overflow(
        long discoveredBytes, long volumeUsedBytes, double expected)
    {
        Assert.Equal(expected, ScanStorageProgress.CalculatePercent(discoveredBytes, volumeUsedBytes)!.Value, 10);
    }

    [Fact]
    public async Task Scanner_snapshots_use_physical_allocation_and_count_shared_storage_once()
    {
        var path = Path.Combine(_rootDirectory, "physical-storage");
        Directory.CreateDirectory(Path.Combine(path, "branch-a"));
        Directory.CreateDirectory(Path.Combine(path, "branch-b"));
        await File.WriteAllBytesAsync(Path.Combine(path, "branch-a", "original.bin"), new byte[4]);
        await File.WriteAllBytesAsync(Path.Combine(path, "branch-b", "alias.bin"), new byte[4]);
        await File.WriteAllBytesAsync(Path.Combine(path, "compressed.bin"), new byte[300]);
        await File.WriteAllBytesAsync(Path.Combine(path, "sparse.bin"), new byte[700]);
        var snapshots = new List<ScanProgressSnapshot>();
        var scanner = new DirectoryScanner(new WindowsLocationPolicy(), new PhysicalStorageReader());

        var result = await scanner.ScanAsync(
            new ScanSessionOptions(path, IncludeIntermediateTreeSnapshots: true,
                ProgressReportInterval: 1, TreeSnapshotIntervalMilliseconds: 0),
            new RecordingProgress(snapshots));

        Assert.False(result.IsPartial);
        Assert.Equal(4, result.FilesScanned);
        Assert.Equal(60L, result.BytesDiscovered);
        Assert.Equal(1008L, result.LogicalBytesDiscovered);
        Assert.Single(result.Root.EnumerateFiles(), file => file.RiskTags.Contains("hard-link-shared"));
        Assert.Contains(snapshots.SkipLast(1), snapshot => snapshot.FilesScanned > 0);
        Assert.All(snapshots, snapshot =>
        {
            var files = snapshot.Root!.EnumerateFiles().ToArray();
            var uniqueAllocation = files.Select(file => file.StorageInfo!)
                .DistinctBy(storage => storage.Identity).Sum(storage => storage.AllocatedBytes);
            Assert.Equal(uniqueAllocation, snapshot.BytesDiscovered);
            Assert.Equal(uniqueAllocation / 240d * 100,
                ScanStorageProgress.CalculatePercent(snapshot.BytesDiscovered, 240));
        });
        Assert.Equal(25d, ScanStorageProgress.CalculatePercent(snapshots[^1].BytesDiscovered, 240));
        Assert.NotEqual(ScanStorageProgress.CalculatePercent(snapshots[^1].LogicalBytesDiscovered, 240),
            ScanStorageProgress.CalculatePercent(snapshots[^1].BytesDiscovered, 240));
    }

    [Fact]
    public async Task Equal_storage_has_equal_percentage_regardless_of_branches_or_item_count()
    {
        var singleFileRoot = Path.Combine(_rootDirectory, "single-file");
        var flatRoot = Path.Combine(_rootDirectory, "flat-files");
        var branchedRoot = Path.Combine(_rootDirectory, "many-branches");
        Directory.CreateDirectory(singleFileRoot);
        Directory.CreateDirectory(flatRoot);
        Directory.CreateDirectory(branchedRoot);
        await File.WriteAllBytesAsync(Path.Combine(singleFileRoot, "single.bin"), new byte[100]);
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(flatRoot, $"file-{index}.bin"), new byte[25]);
            var branch = Directory.CreateDirectory(Path.Combine(branchedRoot, $"branch-{index}")).FullName;
            Directory.CreateDirectory(Path.Combine(branch, "empty"));
            await File.WriteAllBytesAsync(Path.Combine(branch, $"file-{index}.bin"), new byte[25]);
        }

        var results = new List<ScanResult>();
        foreach (var path in new[] { singleFileRoot, flatRoot, branchedRoot })
        {
            var snapshots = new List<ScanProgressSnapshot>();
            var scanner = new DirectoryScanner(new WindowsLocationPolicy(), new LogicalLengthStorageReader());
            results.Add(await scanner.ScanAsync(new ScanSessionOptions(path), new RecordingProgress(snapshots)));

            // A completed traversal retains its actual fraction of the volume baseline.
            Assert.Equal(100L, snapshots[^1].BytesDiscovered);
            Assert.Equal(25d, ScanStorageProgress.CalculatePercent(snapshots[^1].BytesDiscovered, 400));
        }

        Assert.NotEqual(results[0].FilesScanned, results[1].FilesScanned);
        Assert.Equal(results[1].FilesScanned, results[2].FilesScanned);
        Assert.NotEqual(results[1].DirectoriesScanned, results[2].DirectoriesScanned);
    }

    private sealed class RecordingProgress(List<ScanProgressSnapshot> snapshots) : IProgress<ScanProgressSnapshot>
    {
        public void Report(ScanProgressSnapshot value) => snapshots.Add(value);
    }

    private sealed class PhysicalStorageReader : IFileStorageReader
    {
        public FileStorageInfo Read(FileInfo info)
        {
            // Cross the production throttle to exercise real intermediate snapshots.
            Thread.Sleep(110);
            return info.Name switch
            {
                "original.bin" or "alias.bin" => new FileStorageInfo("shared-identity", info.Length, 40, LinkCount: 2),
                "compressed.bin" => new FileStorageInfo(info.FullName, info.Length, 20),
                "sparse.bin" => new FileStorageInfo(info.FullName, info.Length, 0),
                _ => throw new InvalidOperationException($"Unexpected test file: {info.FullName}")
            };
        }
    }

    private sealed class LogicalLengthStorageReader : IFileStorageReader
    {
        public FileStorageInfo Read(FileInfo info) => new(info.FullName, info.Length, info.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}

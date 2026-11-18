using DiskLens.Core.Models;
using DiskLens.Core.Scanning;
using DiskLens.Core.Visualization;
using DiskLens.Core.Layout;

namespace DiskLens.Tests.Scanning;

public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly List<string> _reparsePoints = [];

    public DirectoryScannerTests()
    {
        var baseDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".temp-tests"));
        Directory.CreateDirectory(baseDirectory);
        _rootDirectory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public async Task Scanned_folder_should_display_fifty_items_and_keep_every_file_in_the_scan_tree()
    {
        for (var index = 0; index < 72; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_rootDirectory, $"file-{index:000}.bin"), new byte[index]);
        }

        var nestedPath = Path.Combine(_rootDirectory, "nested");
        Directory.CreateDirectory(nestedPath);
        for (var index = 0; index < 61; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(nestedPath, $"child-{index:000}.bin"), new byte[index]);
        }

        var result = await new DirectoryScanner().ScanAsync(new ScanSessionOptions(_rootDirectory));
        var builder = new VisualExplorerBuilder();
        var items = builder.Build(result.Root, new VisualExplorerOptions(long.MaxValue));
        var overflow = Assert.Single(items, item => DisplayNodeMetadata.IsOverflow(item.DisplayNode));

        Assert.Equal(133, result.FilesScanned);
        Assert.Equal(133, result.Root.EnumerateFiles().Count());
        Assert.Equal(73, result.Root.Children.Count);
        Assert.Equal(51, items.Count);
        Assert.Equal(23, overflow.DisplayNode.Children.Count);
        Assert.Equal(_rootDirectory, DisplayNodeMetadata.GetOverflowParentPath(overflow.DisplayNode));
        Assert.True(Directory.Exists(DisplayNodeMetadata.GetOverflowParentPath(overflow.DisplayNode)));
        Assert.Equal(result.Root.SizeBytes, items.Sum(item => item.DisplayNode.SizeBytes));

        var nested = result.Root.Children.Single(node => node.Path == nestedPath);
        var nestedItems = builder.Build(nested, new VisualExplorerOptions());
        Assert.Equal(51, nestedItems.Count);
        Assert.Equal(11, nestedItems.Single(item => DisplayNodeMetadata.IsOverflow(item.DisplayNode)).DisplayNode.Children.Count);
        var displayRoot = result.Root with { Children = items.Select(item => item.DisplayNode).ToArray() };
        var rectangles = new SquarifiedTreemapLayoutEngine().Layout(displayRoot, 0, 0, 1100, 650, 1);
        Assert.Equal(51, rectangles.Count);
        Assert.All(rectangles, rectangle => Assert.True(rectangle.Width > 20 && rectangle.Height > 20));
        Assert.Equal(133, result.Root.EnumerateFiles().Count());
    }

    [Fact]
    public async Task ScanAsync_should_build_tree_and_aggregate_file_sizes()
    {
        await File.WriteAllBytesAsync(Path.Combine(_rootDirectory, "root.bin"), new byte[32]);

        var nested = Path.Combine(_rootDirectory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "child.bin"), new byte[64]);

        var scanner = new DirectoryScanner();
        var result = await scanner.ScanAsync(new ScanSessionOptions(_rootDirectory));

        Assert.Equal(_rootDirectory, result.Root.Path);
        Assert.True(result.Root.IsDirectory);
        Assert.Equal(96L, result.Root.LogicalSizeBytes);
        Assert.Equal(96L, result.LogicalBytesDiscovered);
        Assert.Equal(result.Root.EnumerateFiles().Sum(node => node.StorageInfo!.AllocatedBytes), result.Root.SizeBytes);
        Assert.Equal(result.Root.SizeBytes, result.BytesDiscovered);
        Assert.Equal(2, result.FilesScanned);
        Assert.True(result.DirectoriesScanned >= 2);
        Assert.NotEmpty(result.Root.Children);
        Assert.Equal(2, result.Root.DescendantFileCount);
        Assert.True(result.Root.DescendantDirectoryCount >= 2);
    }

    [Fact]
    public async Task ScanAsync_should_precompute_descendant_counts_for_directories()
    {
        var nested = Path.Combine(_rootDirectory, "nested");
        var deep = Path.Combine(nested, "deep");
        Directory.CreateDirectory(deep);
        await File.WriteAllBytesAsync(Path.Combine(_rootDirectory, "root.bin"), new byte[32]);
        await File.WriteAllBytesAsync(Path.Combine(nested, "child.bin"), new byte[64]);
        await File.WriteAllBytesAsync(Path.Combine(deep, "grandchild.bin"), new byte[16]);

        var scanner = new DirectoryScanner();
        var result = await scanner.ScanAsync(new ScanSessionOptions(_rootDirectory));

        var nestedNode = result.Root.DescendantsAndSelf().Single(node => node.Path == nested);
        var deepNode = result.Root.DescendantsAndSelf().Single(node => node.Path == deep);

        Assert.Equal(3, result.Root.DescendantFileCount);
        Assert.True(result.Root.DescendantDirectoryCount >= 3);
        Assert.Equal(2, nestedNode.DescendantFileCount);
        Assert.True(nestedNode.DescendantDirectoryCount >= 2);
        Assert.Equal(1, deepNode.DescendantFileCount);
        Assert.Equal(1, deepNode.DescendantDirectoryCount);
    }

    [Fact]
    public async Task ScanAsync_should_throw_when_cancellation_is_requested_before_start()
    {
        var scanner = new DirectoryScanner();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scanner.ScanAsync(new ScanSessionOptions(_rootDirectory), cancellationToken: cancellationTokenSource.Token));
    }

    [Fact]
    public async Task ScanAsync_should_skip_directory_symbolic_links_by_default()
    {
        var scanRoot = Path.Combine(_rootDirectory, "scan-root");
        var target = Path.Combine(_rootDirectory, "target-directory");
        Directory.CreateDirectory(scanRoot);
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(scanRoot, "real.bin"), new byte[16]);
        await File.WriteAllBytesAsync(Path.Combine(target, "linked.bin"), new byte[128]);
        var linkPath = Path.Combine(scanRoot, "linked-directory");
        CreateDirectorySymbolicLinkOrSkip(linkPath, target);

        var result = await new DirectoryScanner().ScanAsync(new ScanSessionOptions(scanRoot));

        var linkNode = Assert.Single(result.Root.Children, node => node.Path == linkPath);
        Assert.True(linkNode.IsDirectory);
        Assert.Contains("reparse-point", linkNode.RiskTags);
        Assert.Contains("skipped", linkNode.RiskTags);
        Assert.Empty(linkNode.Children);
        Assert.Equal(1, result.FilesScanned);
        var realFile = Assert.Single(result.Root.EnumerateFiles());
        Assert.Equal(16L, realFile.LogicalSizeBytes);
        Assert.Equal(realFile.StorageInfo!.AllocatedBytes, result.BytesDiscovered);
        Assert.Equal(result.Root.SizeBytes, result.BytesDiscovered);
    }

    [Fact]
    public async Task ScanAsync_should_skip_file_symbolic_links_by_default()
    {
        var scanRoot = Path.Combine(_rootDirectory, "scan-root");
        Directory.CreateDirectory(scanRoot);
        await File.WriteAllBytesAsync(Path.Combine(scanRoot, "real.bin"), new byte[16]);
        var target = Path.Combine(_rootDirectory, "linked-target.bin");
        await File.WriteAllBytesAsync(target, new byte[128]);
        var linkPath = Path.Combine(scanRoot, "linked-file.bin");
        CreateFileSymbolicLinkOrSkip(linkPath, target);

        var result = await new DirectoryScanner().ScanAsync(new ScanSessionOptions(scanRoot));

        Assert.DoesNotContain(result.Root.DescendantsAndSelf(), node => node.Path == linkPath);
        Assert.Equal(1, result.FilesScanned);
        var realFile = Assert.Single(result.Root.EnumerateFiles());
        Assert.Equal(16L, realFile.LogicalSizeBytes);
        Assert.Equal(realFile.StorageInfo!.AllocatedBytes, result.BytesDiscovered);
        Assert.Equal(result.Root.SizeBytes, result.BytesDiscovered);
    }

    [Fact]
    public async Task ScanAsync_should_skip_a_symbolic_link_used_as_the_scan_root()
    {
        var target = Path.Combine(_rootDirectory, "target-root");
        Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target, "linked.bin"), new byte[128]);
        var linkPath = Path.Combine(_rootDirectory, "root-link");
        CreateDirectorySymbolicLinkOrSkip(linkPath, target);

        var result = await new DirectoryScanner().ScanAsync(new ScanSessionOptions(linkPath));

        Assert.Contains("reparse-point", result.Root.RiskTags);
        Assert.Contains("skipped", result.Root.RiskTags);
        Assert.Empty(result.Root.Children);
        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(0, result.BytesDiscovered);
    }

    [Fact]
    public async Task ScanAsync_should_observe_cancellation_during_file_enumeration()
    {
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_rootDirectory, $"cancel-{index}.bin"), new byte[16]);
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationRequested = false;
        var progress = new CallbackProgress<ScanProgressSnapshot>(snapshot =>
        {
            if (snapshot.FilesScanned >= 1 && !cancellationRequested)
            {
                cancellationRequested = true;
                cancellationTokenSource.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DirectoryScanner().ScanAsync(
                new ScanSessionOptions(_rootDirectory, ProgressReportInterval: 1),
                progress,
                cancellationTokenSource.Token));

        Assert.True(cancellationRequested);
    }

    [Fact]
    public async Task ScanAsync_should_throw_a_clear_error_when_the_root_does_not_exist()
    {
        var missingRoot = Path.Combine(_rootDirectory, "missing-root");

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new DirectoryScanner().ScanAsync(new ScanSessionOptions(missingRoot)));

        Assert.Contains(missingRoot, exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ScanAsync_progress_snapshots_should_always_be_rooted_at_requested_path()
    {
        var nested = Path.Combine(_rootDirectory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "child-a.bin"), new byte[32]);
        await File.WriteAllBytesAsync(Path.Combine(nested, "child-b.bin"), new byte[64]);

        var snapshots = new List<ScanProgressSnapshot>();
        var progress = new CallbackProgress<ScanProgressSnapshot>(snapshot => snapshots.Add(snapshot));
        var scanner = new DirectoryScanner();

        await scanner.ScanAsync(
            new ScanSessionOptions(_rootDirectory, ProgressReportInterval: 1),
            progress);

        var rootedSnapshots = snapshots.Where(snapshot => snapshot.Root is not null).ToList();
        Assert.NotEmpty(rootedSnapshots);
        Assert.All(rootedSnapshots, snapshot => Assert.Equal(_rootDirectory, snapshot.Root!.Path));
        Assert.Contains(rootedSnapshots, snapshot => snapshot.Root!.Children.Count > 0);
    }

    [Fact]
    public async Task ScanAsync_should_allow_stats_only_intermediate_progress_updates_without_full_tree_snapshots()
    {
        var nested = Path.Combine(_rootDirectory, "nested");
        Directory.CreateDirectory(nested);

        for (var index = 0; index < 8; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(nested, $"child-{index}.bin"), new byte[16 + index]);
        }

        var snapshots = new List<ScanProgressSnapshot>();
        var progress = new CallbackProgress<ScanProgressSnapshot>(snapshot => snapshots.Add(snapshot));
        var scanner = new DirectoryScanner();

        await scanner.ScanAsync(
            new ScanSessionOptions(
                _rootDirectory,
                ProgressReportInterval: 1),
            progress);

        Assert.True(snapshots.Count >= 3);
        Assert.NotNull(snapshots.First().Root);
        Assert.NotNull(snapshots.Last().Root);
        Assert.All(snapshots.Skip(1).SkipLast(1), snapshot => Assert.Null(snapshot.Root));
    }

    [Fact]
    public void Adaptive_tree_snapshot_interval_should_back_off_more_aggressively_for_large_trees()
    {
        Assert.Equal(400, ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(100, 120000));
        Assert.Equal(300, ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(100, 50000));
        Assert.Equal(200, ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(100, 15000));
        Assert.Equal(100, ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(100, 5000));
    }

    [Fact]
    public void Adaptive_tree_snapshot_interval_should_not_overflow_for_large_base_intervals()
    {
        var interval = ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(int.MaxValue, 120000);

        Assert.Equal(int.MaxValue, interval);
    }

    private void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            _reparsePoints.Add(linkPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Directory symbolic links are unavailable without elevation: {ex.Message}");
        }

        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
    }

    private void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            _reparsePoints.Add(linkPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"File symbolic links are unavailable without elevation: {ex.Message}");
        }

        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
    public void Dispose()
    {
        foreach (var linkPath in _reparsePoints)
        {
            if ((File.GetAttributes(linkPath) & FileAttributes.Directory) == FileAttributes.Directory)
            {
                Directory.Delete(linkPath);
            }
            else
            {
                File.Delete(linkPath);
            }
        }

        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}

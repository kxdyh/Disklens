using System.Diagnostics;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class DirectoryChangeMonitorTests
{
    [Fact]
    public async Task Continuous_event_stream_is_flushed_at_maximum_latency_and_paths_coalesce()
    {
        using var fixture = new Fixture();
        using var monitor = new DirectoryChangeMonitor(fixture.Path, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.BatchReady += (_, _) => ready.TrySetResult();
        monitor.SetActive(true);
        var watch = Stopwatch.StartNew();
        while (!ready.Task.IsCompleted && watch.ElapsedMilliseconds < 2000)
        {
            monitor.RecordChange(Path.Combine(fixture.Path, "same.txt"));
            await Task.Delay(30);
        }
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange(watch.ElapsedMilliseconds, 200, 1800);
        Assert.Single(monitor.TakeBatch().Entries);
    }

    [Fact]
    public async Task Inactive_events_are_bounded_and_disposal_cancels_signal()
    {
        using var fixture = new Fixture();
        using var monitor = new DirectoryChangeMonitor(fixture.Path, TimeSpan.FromMilliseconds(30));
        var signals = 0;
        monitor.BatchReady += (_, _) => Interlocked.Increment(ref signals);
        for (var i = 0; i < 10000; i++) monitor.RecordChange(Path.Combine(fixture.Path, $"{i}.txt"));
        await Task.Delay(80);
        Assert.Equal(0, signals);
        var batch = monitor.TakeBatch();
        Assert.Equal(DirectoryChangeMonitor.MaximumBatchPaths, batch.Entries.Count);
        Assert.True(batch.NeedsCurrentDirectoryCheck);
        monitor.SetActive(true);
        monitor.Dispose();
        await Task.Delay(80);
        Assert.Equal(0, signals);
    }

    [Fact]
    public async Task Local_file_event_does_not_rescan_existing_parent_subtrees()
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        var changed = Path.Combine(nested, "changed.bin");
        var untouched = Path.Combine(nested, "untouched.bin");
        await File.WriteAllBytesAsync(changed, new byte[4096]);
        await File.WriteAllBytesAsync(untouched, new byte[8192]);
        var storage = new RecordingStorageReader();
        var scanner = new DirectoryScanner(new DiskLens.Core.Policies.WindowsLocationPolicy(), storage);
        var root = (await scanner.ScanAsync(new ScanSessionOptions(fixture.Path))).Root;
        storage.Paths.Clear();
        await File.WriteAllBytesAsync(changed, new byte[32768]);
        var result = await new DirectoryRefreshService(scanner).RefreshEntriesAsync(root, FileSystemNodeIndex.Build(root),
            [new(nested), new(changed)], new ScanSessionOptions(fixture.Path));
        Assert.Single(storage.Paths);
        Assert.Equal(changed, storage.Paths[0]);
        Assert.Equal(32768, result.Root.EnumerateFiles().Single(node => node.Path == changed).LogicalSizeBytes);
        Assert.Equal(40960, result.Root.SizeBytes);
    }

    [Fact]
    public async Task New_directory_read_once_and_inserted_even_if_child_event_arrives_first()
    {
        using var fixture = new Fixture();
        var scanner = new DirectoryScanner();
        var root = (await scanner.ScanAsync(new ScanSessionOptions(fixture.Path))).Root;
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        var file = Path.Combine(nested, "added.bin");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var result = await new DirectoryRefreshService(scanner).RefreshEntriesAsync(root, FileSystemNodeIndex.Build(root),
            [new(file), new(nested, true)], new ScanSessionOptions(fixture.Path));
        Assert.Single(result.Root.Children);
        Assert.Equal(nested, result.Root.Children[0].Path);
        Assert.Equal(file, Assert.Single(result.Root.EnumerateFiles()).Path);
        Assert.Equal(4096, result.Root.SizeBytes);
    }

    [Fact]
    public async Task Catch_up_checks_current_directory_itself_and_does_not_drop_unavailable_root()
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "test.txt"), "sample");
        var scanner = new DirectoryScanner();
        var root = (await scanner.ScanAsync(new ScanSessionOptions(fixture.Path))).Root;
        var service = new DirectoryRefreshService(scanner);
        Directory.Delete(nested, recursive: true);
        var changes = await service.ReadCurrentDirectoryEntriesAsync(root.Children[0]);
        Assert.Contains(changes, change => change.Path == nested);
        var result = await service.RefreshEntriesAsync(root, FileSystemNodeIndex.Build(root), changes, new ScanSessionOptions(fixture.Path));
        Assert.Empty(result.Root.Children);
        Directory.Delete(fixture.Path);
        var unavailable = await service.RefreshEntriesAsync(root, FileSystemNodeIndex.Build(root), changes, new ScanSessionOptions(fixture.Path));
        Assert.Same(root, unavailable.Root);
    }

    private sealed class RecordingStorageReader : IFileStorageReader
    {
        public List<string> Paths { get; } = [];
        public FileStorageInfo Read(FileInfo file)
        { Paths.Add(file.FullName); return new FileStorageInfo(file.FullName, file.Length, file.Length); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.Refresh.Tests"), "core-fixture-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}

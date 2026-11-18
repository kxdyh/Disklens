using DiskLens.Core.Models;
using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class DirectoryRefreshServiceTests
{
    [Fact]
    public async Task Explicit_refresh_reconciles_external_deletion_addition_resize_and_ancestors()
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        var keep = Path.Combine(nested, "keep.bin");
        var removed = Path.Combine(nested, "removed.bin");
        await File.WriteAllBytesAsync(keep, new byte[16]);
        await File.WriteAllBytesAsync(removed, new byte[8192]);
        var scanner = new DirectoryScanner();
        var original = (await scanner.ScanAsync(new ScanSessionOptions(fixture.Path))).Root;
        var target = original.Children.Single();
        // These operations imitate Explorer and only touch the isolated test fixture.
        File.Delete(removed);
        await File.WriteAllBytesAsync(keep, new byte[16384]);
        await File.WriteAllBytesAsync(Path.Combine(nested, "added.bin"), new byte[4096]);

        var refreshed = await new DirectoryRefreshService(scanner).RefreshAsync(original, target, new ScanSessionOptions(fixture.Path));
        var result = refreshed.Root;
        Assert.Equal(2, result.DescendantFileCount);
        Assert.Equal(2, result.DescendantDirectoryCount);
        Assert.DoesNotContain(result.EnumerateFiles(), node => node.Path == removed);
        Assert.Contains(result.EnumerateFiles(), node => node.Name == "added.bin");
        Assert.Equal(16384, result.EnumerateFiles().Single(node => node.Path == keep).LogicalSizeBytes);
        Assert.Equal(result.Children.Sum(node => node.SizeBytes), result.SizeBytes);
        Assert.Contains(original.EnumerateFiles(), node => node.Path == removed);
    }

    [Fact]
    public async Task Missing_target_is_removed_but_missing_scan_root_is_preserved()
    {
        using var fixture = new Fixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Path, "nested")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "a.txt"), "sample");
        var scanner = new DirectoryScanner();
        var original = (await scanner.ScanAsync(new ScanSessionOptions(fixture.Path))).Root;
        var service = new DirectoryRefreshService(scanner);
        Directory.Delete(nested, recursive: true);
        var refreshed = await service.RefreshAsync(original, original.Children.Single(), new ScanSessionOptions(fixture.Path));
        Assert.Empty(refreshed.Root.Children);
        Assert.Equal(0, refreshed.Root.SizeBytes);
        Assert.Equal(1, refreshed.ConfirmedRemovedCount);
        Directory.Delete(fixture.Path);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.RefreshAsync(original, original, new ScanSessionOptions(fixture.Path)));
    }

    [Fact]
    public async Task Metadata_checks_are_bounded_and_do_not_walk_or_rebuild_unchanged_tree()
    {
        var children = Enumerable.Range(0, 10000).Select(i => FileNode($@"E:\Data\file{i:D5}.bin", 4)).ToArray();
        var root = Folder(@"E:\Data", children);
        var checks = 0;
        var service = new DirectoryRefreshService(new DirectoryScanner(), _ => { checks++; return FileSystemEntryPresence.Present; });
        var result = await service.VerifyMissingAsync(root, children.Select(node => node.Path));
        Assert.Equal(DirectoryRefreshService.MaximumVerifiedEntries, checks);
        Assert.Same(root, result.Root);
    }

    [Fact]
    public async Task Access_denied_or_other_io_failure_never_counts_as_deleted()
    {
        var inaccessible = FileNode(@"E:\Data\private.bin", 4096);
        var removed = FileNode(@"E:\Data\removed.bin", 8192);
        var root = Folder(@"E:\Data", inaccessible, removed);
        var service = new DirectoryRefreshService(new DirectoryScanner(), path => path == root.Path
            ? FileSystemEntryPresence.Present : path == inaccessible.Path
            ? FileSystemEntryPresence.Unreadable : FileSystemEntryPresence.Missing);
        var result = await service.VerifyMissingAsync(root, [inaccessible.Path, removed.Path]);
        Assert.Same(inaccessible, Assert.Single(result.Root.Children));
        Assert.Equal(1, result.PreservedUnreadableCount);
        Assert.Equal(1, result.ConfirmedRemovedCount);
        Assert.Equal(4096, result.Root.SizeBytes);
        await Assert.ThrowsAsync<IOException>(() => new DirectoryRefreshService(new DirectoryScanner(), _ => FileSystemEntryPresence.Unreadable)
            .RefreshAsync(root, root, new ScanSessionOptions(root.Path)));
    }

    [Fact]
    public async Task Disconnected_or_unavailable_root_never_erases_visible_children()
    {
        var root = Folder(@"E:\Data", FileNode(@"E:\Data\visible.bin", 4096));
        var probes = 0;
        var service = new DirectoryRefreshService(new DirectoryScanner(), _ => { probes++; return FileSystemEntryPresence.Missing; });
        var result = await service.VerifyMissingAsync(root, root.Children.Select(node => node.Path));
        Assert.Same(root, result.Root);
        Assert.Equal(0, result.ConfirmedRemovedCount);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Cancellation_does_not_return_a_partially_mutated_snapshot()
    {
        var original = Folder(@"E:\Data", FileNode(@"E:\Data\a.bin", 20));
        using var cancellation = new CancellationTokenSource();
        var service = new DirectoryRefreshService(new DirectoryScanner(), _ =>
        {
            cancellation.Cancel();
            return FileSystemEntryPresence.Missing;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyMissingAsync(original, [original.Children[0].Path], cancellation.Token));
        Assert.Single(original.Children);
        Assert.Equal(20, original.SizeBytes);
    }

    [Fact]
    public void Incomplete_directory_listing_retains_unseen_old_entries_and_invalidates_analysis()
    {
        var known = FileNode(@"E:\Data\known.bin", 10);
        var unseen = FileNode(@"E:\Data\unseen.bin", 20) with { Analysis = Analysis(@"E:\Data\unseen.bin") };
        var original = Folder(@"E:\Data", known, unseen);
        var partial = Folder(@"E:\Data", known) with { RiskTags = ["scan-error"] };
        var result = RefreshTreeMerger.Replace(original, original.Path, partial);
        Assert.Equal(2, result.Root.Children.Count);
        Assert.Equal(30, result.Root.SizeBytes);
        Assert.Equal(1, result.PreservedUnreadableCount);
        var retained = result.Root.Children.Single(node => node.Path == unseen.Path);
        Assert.Null(retained.Analysis);
        Assert.Contains("scan-error", retained.RiskTags);
    }

    [Fact]
    public void Unreadable_file_does_not_replace_old_storage_with_zero()
    {
        var old = FileNode(@"E:\Data\a.bin", 100);
        var original = Folder(@"E:\Data", old);
        var error = old with { SizeBytes = 0, RiskTags = ["error"] };
        var result = RefreshTreeMerger.Replace(original, original.Path, Folder(original.Path, error));
        Assert.Equal(100, result.Root.SizeBytes);
        Assert.Equal(1, result.PreservedUnreadableCount);
    }

    [Fact]
    public void Refresh_preserves_link_owner_outside_target_and_transfers_deleted_owner()
    {
        var storage = new FileStorageInfo("volume:id", 100, 4096, 2);
        var owner = FileNode(@"E:\Data\A\owner.bin", 4096) with { LogicalSizeBytes = 100, StorageInfo = storage };
        var alias = FileNode(@"E:\Data\B\alias.bin", 0) with { LogicalSizeBytes = 100, StorageInfo = storage, RiskTags = ["hard-link-shared"] };
        var a = Folder(@"E:\Data\A", owner) with { LogicalSizeBytes = 100 };
        var b = Folder(@"E:\Data\B", alias) with { LogicalSizeBytes = 100 };
        var original = Folder(@"E:\Data", a, b) with { LogicalSizeBytes = 200 };
        var freshAlias = alias with { SizeBytes = 4096, RiskTags = [] };
        var result = RefreshTreeMerger.Replace(original, b.Path, b with { Children = [freshAlias], SizeBytes = 4096 });
        Assert.Equal(4096, result.Root.SizeBytes);
        Assert.Equal(0, result.Root.Children.Single(node => node.Path == b.Path).SizeBytes);
        Assert.DoesNotContain("hard-link-shared", result.Root.Children.Single(node => node.Path == a.Path).Children[0].RiskTags);

        var transferred = RefreshTreeMerger.Replace(original, a.Path, a with { Children = [], SizeBytes = 0, LogicalSizeBytes = 0 });
        Assert.Equal(4096, transferred.Root.SizeBytes);
        var remaining = transferred.Root.Children.Single(node => node.Path == b.Path).Children[0];
        Assert.Equal(4096, remaining.SizeBytes);
        Assert.DoesNotContain("hard-link-shared", remaining.RiskTags);
        Assert.Equal(100, transferred.Root.LogicalSizeBytes);
    }

    [Fact]
    public void Delete_invalidates_ancestor_analysis_but_preserves_unaffected_siblings()
    {
        var removed = FileNode(@"E:\Data\A\deleted.bin", 20);
        var a = Folder(@"E:\Data\A", removed) with { Analysis = Analysis(@"E:\Data\A") };
        var b = Folder(@"E:\Data\B", FileNode(@"E:\Data\B\keep.bin", 10));
        var original = Folder(@"E:\Data", a, b) with { Analysis = Analysis(@"E:\Data") };
        var updated = RefreshTreeMerger.Remove(original, new HashSet<string>(StringComparer.Ordinal) { removed.Path });
        Assert.Null(updated.Analysis);
        Assert.Null(updated.Children.Single(node => node.Path == a.Path).Analysis);
        Assert.Same(b, updated.Children.Single(node => node.Path == b.Path));
        Assert.Equal(10, updated.SizeBytes);
    }

    [Fact]
    public void Root_with_trailing_separator_still_reconciles_confirmed_deletion()
    {
        var removed = FileNode(@"E:\Data\removed.bin", 10);
        var root = Folder(@"E:\Data\", removed);
        var updated = RefreshTreeMerger.Remove(root, new HashSet<string>(StringComparer.Ordinal) { removed.Path });
        Assert.Empty(updated.Children);
        Assert.Equal(0, updated.SizeBytes);
    }

    [Fact]
    public void Child_changes_keep_path_purpose_but_directory_identity_change_expires_it()
    {
        var file = FileNode(@"E:\Data\A\old.bin", 20);
        var purpose = Analysis(@"E:\Data\A") with
        { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose, ShortPurpose = "应用数据目录" };
        var directory = Folder(@"E:\Data\A", file) with { Analysis = purpose };
        var root = Folder(@"E:\Data", directory);
        var removed = RefreshTreeMerger.Remove(root, new HashSet<string>(StringComparer.Ordinal) { file.Path });
        Assert.Same(purpose, removed.Children[0].Analysis);
        var replaced = RefreshTreeMerger.Replace(root, directory.Path,
            Folder(directory.Path, FileNode(@"E:\Data\A\new.bin", 30)));
        Assert.Same(purpose, replaced.Root.Children[0].Analysis);
        var changedType = RefreshTreeMerger.Replace(root, directory.Path, FileNode(directory.Path, 30));
        Assert.Null(changedType.Root.Children[0].Analysis);
    }

    private static AiAnalysisResult Analysis(string path) => AiAnalysisResult.CreateFallback(path, "old");
    private static FileSystemNode FileNode(string path, long bytes) => new(path, System.IO.Path.GetFileName(path), ".bin", bytes,
        FileAttributes.Normal, null, null, false, false, [], [], 1);
    private static FileSystemNode Folder(string path, params FileSystemNode[] children) => new(path,
        System.IO.Path.GetFileName(path), "", children.Sum(child => child.SizeBytes), FileAttributes.Directory,
        null, null, true, false, [], children, children.Sum(child => child.DescendantFileCount),
        1 + children.Sum(child => child.DescendantDirectoryCount));

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.Refresh.Tests"), "fixture-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

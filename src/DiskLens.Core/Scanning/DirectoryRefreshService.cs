using DiskLens.Core.Models;

namespace DiskLens.Core.Scanning;

public enum FileSystemEntryPresence { Present, Missing, Unreadable }

public sealed record DirectoryRefreshResult(FileSystemNode Root, int PreservedUnreadableCount,
    ScanResult? RefreshedScan = null, int ConfirmedRemovedCount = 0)
{
    public bool HasChangesFrom(FileSystemNode previousRoot) => !ReferenceEquals(Root, previousRoot);
}

/// <summary>Explicit subtree refresh and bounded existence checks; no watcher or recurring full-disk polling.</summary>
public sealed class DirectoryRefreshService(DirectoryScanner scanner,
    Func<string, FileSystemEntryPresence>? entryProbe = null)
{
    public const int MaximumVerifiedEntries = 50;
    private readonly Func<string, FileSystemEntryPresence> _probe = entryProbe ?? ProbeEntry;

    /// <summary>Refresh only event targets. Existing directory writes never trigger a recursive volume scan.</summary>
    public async Task<DirectoryRefreshResult> RefreshEntriesAsync(FileSystemNode root, FileSystemNodeIndex index,
        IEnumerable<DirectoryEntryChange> changes, ScanSessionOptions options, CancellationToken cancellationToken = default)
    {
        if (await Task.Run(() => _probe(root.Path), cancellationToken).ConfigureAwait(false) != FileSystemEntryPresence.Present)
            return new DirectoryRefreshResult(root, 1);
        var targets = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var path = change.Path;
            if (!WithinRoot(path, root.Path) || path == root.Path) continue;
            // A created file can arrive before its parent event. Read the highest new directory once.
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrWhiteSpace(parent)
                && WithinRoot(parent, root.Path) && index.FindNode(parent) is null; parent = Path.GetDirectoryName(parent))
                path = parent;
            targets[path] = targets.TryGetValue(path, out var old) && old || change.ReadSubtree;
        }
        var replacements = new List<FileSystemNode>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var readSubtrees = new List<string>();
        var unreadable = 0;
        foreach (var (path, readSubtree) in targets.OrderBy(pair => pair.Key.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readSubtrees.Any(parent => WithinRoot(path, parent))) continue;
            var presence = await Task.Run(() => _probe(path), cancellationToken).ConfigureAwait(false);
            if (presence == FileSystemEntryPresence.Missing) { removed.Add(path); continue; }
            if (presence == FileSystemEntryPresence.Unreadable) { unreadable++; continue; }
            try
            {
                var attributes = await Task.Run(() => File.GetAttributes(path), cancellationToken).ConfigureAwait(false);
                var directory = attributes.HasFlag(FileAttributes.Directory);
                if (directory && index.FindNode(path) is { IsDirectory: true } && !readSubtree) continue;
                var scan = await scanner.ScanAsync(options with { RootPath = path, IncludeIntermediateTreeSnapshots = false },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                replacements.Add(scan.Root);
                if (directory) readSubtrees.Add(path);
                if (scan.IsPartial) unreadable++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (_probe(path) == FileSystemEntryPresence.Missing) removed.Add(path);
                else unreadable++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { unreadable++; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var merged = RefreshTreeMerger.ApplyEntries(root, replacements, removed);
            cancellationToken.ThrowIfCancellationRequested();
            return new DirectoryRefreshResult(merged.Root, unreadable + merged.PreservedUnreadableCount,
                ConfirmedRemovedCount: removed.Count);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recovery after a watcher gap/overflow: current directory entries only, no recursive existing folders.</summary>
    public Task<IReadOnlyList<DirectoryEntryChange>> ReadCurrentDirectoryEntriesAsync(FileSystemNode target,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DirectoryEntryChange>>(() =>
    {
        var changes = new Dictionary<string, bool>(StringComparer.Ordinal);
        changes.TryAdd(target.Path, false);
        // Existing entries are also checked for confirmed deletion even when enumeration is partial.
        foreach (var child in target.Children) changes.TryAdd(child.Path, false);
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(target.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                changes.TryAdd(path, false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        cancellationToken.ThrowIfCancellationRequested();
        return changes.Select(pair => new DirectoryEntryChange(pair.Key, pair.Value)).ToArray();
    }, cancellationToken);

    private static bool WithinRoot(string path, string root) => path == root
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public async Task<DirectoryRefreshResult> RefreshAsync(FileSystemNode root, FileSystemNode target,
        ScanSessionOptions options, IProgress<ScanProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!target.IsDirectory) throw new ArgumentException("请选择目录进行刷新。", nameof(target));
        var presence = await Task.Run(() => _probe(target.Path), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (presence == FileSystemEntryPresence.Unreadable)
            throw new IOException("当前目录暂时无法读取，原扫描结果已保留。请检查访问权限或磁盘连接。");
        if (presence == FileSystemEntryPresence.Missing)
            return RemoveMissingTarget(root, target);

        ScanResult result;
        try
        {
            result = await scanner.ScanAsync(options with { RootPath = target.Path, IncludeIntermediateTreeSnapshots = false },
                progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // Directory.Exists returns false on access denied too. Recheck with the throwing metadata API.
            if (_probe(target.Path) != FileSystemEntryPresence.Missing) throw;
            cancellationToken.ThrowIfCancellationRequested();
            return RemoveMissingTarget(root, target);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var merged = RefreshTreeMerger.Replace(root, target.Path, result.Root);
            cancellationToken.ThrowIfCancellationRequested();
            return new DirectoryRefreshResult(merged.Root, merged.PreservedUnreadableCount, result);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DirectoryRefreshResult> VerifyMissingAsync(FileSystemNode root, IEnumerable<string> visiblePaths,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = 0;
        cancellationToken.ThrowIfCancellationRequested();
        var rootPresence = _probe(root.Path);
        cancellationToken.ThrowIfCancellationRequested();
        // An offline drive / unavailable scan root must not make every visible child look deleted.
        if (rootPresence != FileSystemEntryPresence.Present) return new DirectoryRefreshResult(root, 1);
        foreach (var path in visiblePaths.Where(path => !string.IsNullOrWhiteSpace(path))
                     .Where(path => path != root.Path).Distinct(StringComparer.Ordinal).Take(MaximumVerifiedEntries - 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var presence = _probe(path);
            if (presence == FileSystemEntryPresence.Missing && path != root.Path) removed.Add(path);
            else if (presence == FileSystemEntryPresence.Unreadable) unreadable++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var updated = removed.Count == 0 ? root : RefreshTreeMerger.Remove(root, removed);
        cancellationToken.ThrowIfCancellationRequested();
        return new DirectoryRefreshResult(updated, unreadable, ConfirmedRemovedCount: removed.Count);
    }, cancellationToken);

    public static FileSystemEntryPresence ProbeEntry(string path)
    {
        try { _ = File.GetAttributes(path); return FileSystemEntryPresence.Present; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return FileSystemEntryPresence.Missing; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException
            or ArgumentException or NotSupportedException)
        { return FileSystemEntryPresence.Unreadable; }
    }

    private static DirectoryRefreshResult RemoveMissingTarget(FileSystemNode root, FileSystemNode target)
    {
        if (target.Path == root.Path)
            throw new DirectoryNotFoundException("扫描根目录已不存在或磁盘已断开。原结果已保留，请选择其他扫描位置。");
        var updated = RefreshTreeMerger.Remove(root, new HashSet<string>(StringComparer.Ordinal) { target.Path });
        return new DirectoryRefreshResult(updated, 0, ConfirmedRemovedCount: 1);
    }
}

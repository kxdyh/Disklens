using System.Diagnostics;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Core.Scanning;

public sealed class DirectoryScanner
{
    private readonly ILocationPolicy _locationPolicy;
    private readonly IFileStorageReader _fileStorageReader;

    public DirectoryScanner()
        : this(new WindowsLocationPolicy())
    {
    }

    public DirectoryScanner(ILocationPolicy locationPolicy, IFileStorageReader? fileStorageReader = null)
    {
        _locationPolicy = locationPolicy;
        _fileStorageReader = fileStorageReader ?? new WindowsFileStorageReader();
    }

    public Task<ScanResult> ScanAsync(
        ScanSessionOptions options,
        IProgress<ScanProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ScanInternal(options, progress, cancellationToken), cancellationToken);
    }

    private ScanResult ScanInternal(
        ScanSessionOptions options,
        IProgress<ScanProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var backupRead = WindowsBackupPrivilegeScope.TryEnable();
        WindowsBackupDirectoryEntry? rootEntry = null;
        var rootReadError = 0;
        if (OperatingSystem.IsWindows() && !options.FollowReparsePoints)
        {
            new WindowsBackupDirectoryReader().TryReadMetadata(options.RootPath,
                out rootEntry, out rootReadError, cancellationToken);
        }

        if (rootEntry is not null
                ? (rootEntry.Attributes & FileAttributes.Directory) == 0
                : File.Exists(options.RootPath))
        {
            var fileStopwatch = Stopwatch.StartNew();
            var fileInfo = new FileInfo(options.RootPath);
            var skipRootFile = !options.FollowReparsePoints
                               && (rootEntry?.Attributes ?? fileInfo.Attributes).HasFlag(FileAttributes.ReparsePoint)
                               && (rootEntry is not null
                                   ? WindowsReparsePointClassifier.ShouldSkipReparseTag(rootEntry.ReparseTag)
                                   : WindowsReparsePointClassifier.ShouldSkipFile(fileInfo));
            var rootFile = rootEntry is null
                ? skipRootFile
                    ? CreateSkippedFileNode(fileInfo, parent: null)
                    : CreateFileNode(fileInfo, parent: null, new HashSet<string>(StringComparer.Ordinal))
                : skipRootFile
                    ? CreateSkippedFileNode(fileInfo.FullName, rootEntry, parent: null)
                    : CreateFileNode(fileInfo.FullName, rootEntry.Name, fileInfo.Extension,
                        rootEntry.Attributes,
                        _fileStorageReader.Read(fileInfo.FullName, rootEntry.LogicalSizeBytes,
                            rootEntry.Attributes, rootEntry.ReparseTag),
                        rootEntry.CreatedAt, rootEntry.ModifiedAt, null,
                        new HashSet<string>(StringComparer.Ordinal));
            var fileState = new ScanState(
                options.ProgressReportInterval,
                options.TreeSnapshotIntervalMilliseconds,
                options.IncludeIntermediateTreeSnapshots,
                rootFile);

            if (!skipRootFile)
            {
                fileState.FilesScanned = 1;
                fileState.BytesDiscovered = rootFile.SizeBytes;
                fileState.LogicalBytesDiscovered = rootFile.LogicalSizeBytes;
                fileState.EstimatedStorageItemCount = rootFile.StorageInfo?.IsEstimated == true ? 1 : 0;
            }
            else
            {
                fileState.SkippedReparsePointCount++;
            }

            fileState.CurrentPath = fileInfo.FullName;
            progress?.Report(fileState.CreateSnapshot(includeRoot: true));

            var fileSnapshot = rootFile.ToImmutable();
            fileStopwatch.Stop();
            progress?.Report(fileState.CreateSnapshot(includeRoot: false) with { Root = fileSnapshot });
            cancellationToken.ThrowIfCancellationRequested();
            return new ScanResult(fileSnapshot, fileState.FilesScanned, fileState.DirectoriesScanned, fileState.BytesDiscovered, fileStopwatch.Elapsed)
            {
                SkippedReparsePointCount = fileState.SkippedReparsePointCount,
                LogicalBytesDiscovered = fileState.LogicalBytesDiscovered,
                EstimatedStorageItemCount = fileState.EstimatedStorageItemCount,
                BackupReadEnabled = backupRead.IsEnabled,
                BackupReadFailureCode = backupRead.FailureCode
            };
        }

        if (rootEntry is null && !Directory.Exists(options.RootPath))
        {
            if (rootReadError is 5 or 1314)
            {
                throw new UnauthorizedAccessException($"Access to scan root was denied (Windows error {rootReadError}): {options.RootPath}");
            }
            throw new DirectoryNotFoundException($"Could not find scan root: {options.RootPath}");
        }

        var rootInfo = new DirectoryInfo(options.RootPath);
        var rootNode = rootEntry is null
            ? CreateDirectoryNode(rootInfo, parent: null)
            : CreateDirectoryNode(rootInfo.FullName, rootEntry, parent: null);
        var state = new ScanState(
            options.ProgressReportInterval,
            options.TreeSnapshotIntervalMilliseconds,
            options.IncludeIntermediateTreeSnapshots,
            rootNode);
        var stopwatch = Stopwatch.StartNew();

        progress?.Report(state.CreateSnapshot(includeRoot: true));

        var rootAttributes = rootEntry?.Attributes ?? rootInfo.Attributes;
        if (!options.FollowReparsePoints && rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            rootNode.AddRiskTag("reparse-point");
            rootNode.AddRiskTag("skipped");
            state.SkippedReparsePointCount++;
        }
        else
        {
            var rootCanonicalPath = GetCanonicalDirectoryPath(
                rootInfo,
                rootAttributes.HasFlag(FileAttributes.ReparsePoint),
                parentCanonicalPath: null);
            ScanDirectory(
                rootInfo,
                rootNode,
                rootCanonicalPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                state,
                options,
                progress,
                cancellationToken);
        }

        var rootSnapshot = rootNode.ToImmutable();
        stopwatch.Stop();
        state.CurrentPath = options.RootPath;
        progress?.Report(state.CreateSnapshot(includeRoot: false) with { Root = rootSnapshot });
        cancellationToken.ThrowIfCancellationRequested();
        return new ScanResult(rootSnapshot, state.FilesScanned, state.DirectoriesScanned, state.BytesDiscovered, stopwatch.Elapsed)
        {
            ScanIssueCount = state.ScanIssueCount,
            Issues = state.Issues,
            SkippedReparsePointCount = state.SkippedReparsePointCount,
            LogicalBytesDiscovered = state.LogicalBytesDiscovered,
            EstimatedStorageItemCount = state.EstimatedStorageItemCount,
            SkippedDuplicateTargetCount = state.SkippedDuplicateTargetCount,
            BackupReadEnabled = backupRead.IsEnabled,
            BackupReadFailureCode = backupRead.FailureCode
        };
    }

    private void ScanDirectory(
        DirectoryInfo directoryInfo,
        MutableNode current,
        string canonicalPath,
        HashSet<string> ancestorCanonicalPaths,
        ScanState state,
        ScanSessionOptions options,
        IProgress<ScanProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ancestorCanonicalPaths.Add(canonicalPath);
        state.VisitedCanonicalDirectoryPaths.Add(canonicalPath);
        try
        {
            state.DirectoriesScanned++;
            state.CurrentPath = directoryInfo.FullName;
            state.ReportIfNeeded(progress);

            // One handle supplies both directory and file metadata. Backup intent
            // can use SeBackupPrivilege without changing any filesystem ACL.
            if (!options.FollowReparsePoints && OperatingSystem.IsWindows()
                && TryScanNativeDirectory(directoryInfo, current, canonicalPath,
                    ancestorCanonicalPaths, state, options, progress, cancellationToken))
            {
                return;
            }

            ScanSubDirectories(directoryInfo, current, canonicalPath, ancestorCanonicalPaths, state, options, progress, cancellationToken);
            ScanFiles(directoryInfo, current, state, options, progress, cancellationToken);
        }
        finally
        {
            ancestorCanonicalPaths.Remove(canonicalPath);
        }
    }

    private bool TryScanNativeDirectory(
        DirectoryInfo directoryInfo,
        MutableNode current,
        string canonicalPath,
        HashSet<string> ancestorCanonicalPaths,
        ScanState state,
        ScanSessionOptions options,
        IProgress<ScanProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var reader = new WindowsBackupDirectoryReader();
        if (!reader.TryRead(directoryInfo.FullName, out var entries, out var readError, cancellationToken))
        {
            if (readError == WindowsBackupDirectoryReader.ErrorStoppedOnReparsePoint)
            {
                current.AddRiskTag("reparse-point");
                current.AddRiskTag("skipped");
                state.SkippedReparsePointCount++;
                state.DirectoriesScanned--;
                return true;
            }
            // Unsupported filesystems and unsuccessful reads retain the existing
            // enumeration/error-reporting path. No partial native page was applied.
            return false;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }

            var path = Path.Combine(directoryInfo.FullName, entry.Name);
            var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            MutableNode child;
            try
            {
                child = CreateDirectoryNode(path, entry, current);
            }
            catch (Exception ex) when (IsRecoverableScanException(ex))
            {
                RecordEntryFailure(path, isDirectory: true, current, state, progress, ex);
                continue;
            }
            current.AddChild(child);
            if (isReparsePoint)
            {
                child.AddRiskTag("reparse-point");
                child.AddRiskTag("skipped");
                state.SkippedReparsePointCount++;
                state.CurrentPath = path;
                state.ReportIfNeeded(progress);
                continue;
            }

            var childCanonicalPath = Path.Combine(canonicalPath, entry.Name);
            if (state.VisitedCanonicalDirectoryPaths.Contains(childCanonicalPath))
            {
                child.AddRiskTag("skipped");
                child.AddRiskTag("duplicate-target");
                state.SkippedDuplicateTargetCount++;
                continue;
            }

            ScanDirectory(new DirectoryInfo(path), child, childCanonicalPath,
                ancestorCanonicalPaths, state, options, progress, cancellationToken);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0
                && WindowsReparsePointClassifier.ShouldSkipReparseTag(entry.ReparseTag))
            {
                state.SkippedReparsePointCount++;
                state.ReportIfNeeded(progress);
                continue;
            }

            var path = Path.Combine(directoryInfo.FullName, entry.Name);
            MutableNode fileNode;
            try
            {
                var storage = _fileStorageReader.Read(path, entry.LogicalSizeBytes, entry.Attributes, entry.ReparseTag);
                fileNode = CreateFileNode(path, entry.Name, Path.GetExtension(entry.Name),
                    entry.Attributes, storage, entry.CreatedAt, entry.ModifiedAt,
                    current, state.AccountedFileIdentities);
            }
            catch (Exception ex) when (IsRecoverableScanException(ex))
            {
                RecordEntryFailure(path, isDirectory: false, current, state, progress, ex);
                continue;
            }

            current.AddChild(fileNode);
            current.AddSize(fileNode.SizeBytes, fileNode.LogicalSizeBytes);
            state.FilesScanned++;
            state.BytesDiscovered += fileNode.SizeBytes;
            state.LogicalBytesDiscovered += fileNode.LogicalSizeBytes;
            if (fileNode.StorageInfo?.IsEstimated == true)
            {
                state.EstimatedStorageItemCount++;
            }
            state.CurrentPath = path;
            state.ReportIfNeeded(progress);
        }

        return true;
    }

    private void ScanSubDirectories(
        DirectoryInfo directoryInfo,
        MutableNode current,
        string canonicalPath,
        HashSet<string> ancestorCanonicalPaths,
        ScanState state,
        ScanSessionOptions options,
        IProgress<ScanProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        IEnumerable<DirectoryInfo> entries;
        try
        {
            entries = directoryInfo.EnumerateDirectories();
        }
        catch (Exception ex) when (IsRecoverableScanException(ex))
        {
            RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
            return;
        }

        IEnumerator<DirectoryInfo> enumerator;
        try
        {
            enumerator = entries.GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverableScanException(ex))
        {
            RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
            return;
        }

        try
        {
            while (true)
            {
                DirectoryInfo subDirectory;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    subDirectory = enumerator.Current;
                }
                catch (Exception ex) when (IsRecoverableScanException(ex))
                {
                    RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
                    break;
                }

                MutableNode child;
                bool shouldScan;
                bool isReparseCycle;
                bool isDuplicateTarget;
                string? childCanonicalPath;
                try
                {
                    var isReparsePoint = subDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    shouldScan = options.FollowReparsePoints || !isReparsePoint;
                    childCanonicalPath = shouldScan
                        ? GetCanonicalDirectoryPath(subDirectory, isReparsePoint, canonicalPath)
                        : null;
                    isReparseCycle = shouldScan
                                     && isReparsePoint
                                     && childCanonicalPath is not null
                                     && ancestorCanonicalPaths.Contains(childCanonicalPath);
                    isDuplicateTarget = shouldScan
                                        && childCanonicalPath is not null
                                        && state.VisitedCanonicalDirectoryPaths.Contains(childCanonicalPath);
                    child = shouldScan && !isReparseCycle && !isDuplicateTarget
                        ? CreateDirectoryNode(subDirectory, current)
                        : CreateSkippedDirectoryNode(subDirectory, current, isReparseCycle, isDuplicateTarget, isReparsePoint);
                }
                catch (Exception ex) when (IsRecoverableScanException(ex))
                {
                    RecordEntryFailure(subDirectory.FullName, isDirectory: true, current, state, progress, ex);
                    continue;
                }

                current.AddChild(child);
                if (!shouldScan || isReparseCycle || isDuplicateTarget)
                {
                    if (!shouldScan || isReparseCycle || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        state.SkippedReparsePointCount++;
                    }

                    if (isDuplicateTarget && !isReparseCycle)
                    {
                        state.SkippedDuplicateTargetCount++;
                    }
                    state.CurrentPath = subDirectory.FullName;
                    state.ReportIfNeeded(progress);
                    continue;
                }

                state.ReportIfNeeded(progress);
                ScanDirectory(
                    subDirectory,
                    child,
                    childCanonicalPath!,
                    ancestorCanonicalPaths,
                    state,
                    options,
                    progress,
                    cancellationToken);
            }
        }
        finally
        {
            try
            {
                enumerator.Dispose();
            }
            catch (Exception ex) when (IsRecoverableScanException(ex))
            {
                RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
            }
        }
    }

    private void ScanFiles(
        DirectoryInfo directoryInfo,
        MutableNode current,
        ScanState state,
        ScanSessionOptions options,
        IProgress<ScanProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        IEnumerable<FileInfo> entries;
        try
        {
            entries = directoryInfo.EnumerateFiles();
        }
        catch (Exception ex) when (IsRecoverableScanException(ex))
        {
            RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
            return;
        }

        IEnumerator<FileInfo> enumerator;
        try
        {
            enumerator = entries.GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverableScanException(ex))
        {
            RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
            return;
        }

        try
        {
            while (true)
            {
                FileInfo fileInfo;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    fileInfo = enumerator.Current;
                }
                catch (Exception ex) when (IsRecoverableScanException(ex))
                {
                    RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
                    break;
                }

                MutableNode fileNode;
                try
                {
                    if (!options.FollowReparsePoints
                        && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        && WindowsReparsePointClassifier.ShouldSkipFile(fileInfo))
                    {
                        state.SkippedReparsePointCount++;
                        state.ReportIfNeeded(progress);
                        continue;
                    }

                    fileNode = CreateFileNode(fileInfo, current, state.AccountedFileIdentities);
                }
                catch (Exception ex) when (IsRecoverableScanException(ex))
                {
                    RecordEntryFailure(fileInfo.FullName, isDirectory: false, current, state, progress, ex);
                    continue;
                }

                current.AddChild(fileNode);
                current.AddSize(fileNode.SizeBytes, fileNode.LogicalSizeBytes);
                state.FilesScanned++;
                state.BytesDiscovered += fileNode.SizeBytes;
                state.LogicalBytesDiscovered += fileNode.LogicalSizeBytes;
                if (fileNode.StorageInfo?.IsEstimated == true)
                {
                    state.EstimatedStorageItemCount++;
                }
                state.CurrentPath = fileInfo.FullName;
                state.ReportIfNeeded(progress);
            }
        }
        finally
        {
            try
            {
                enumerator.Dispose();
            }
            catch (Exception ex) when (IsRecoverableScanException(ex))
            {
                RecordEnumerationFailure(directoryInfo, current, state, progress, ex);
            }
        }
    }

    private static void RecordEnumerationFailure(
        DirectoryInfo directoryInfo,
        MutableNode current,
        ScanState state,
        IProgress<ScanProgressSnapshot>? progress,
        Exception exception)
    {
        current.AddRiskTag("scan-error");
        current.AddRiskTag($"enumeration-error: {exception.Message}");
        state.RecordIssue(directoryInfo.FullName, isDirectory: true, "枚举目录", exception);
        state.CurrentPath = directoryInfo.FullName;
        state.ReportIfNeeded(progress);
    }

    private static void RecordEntryFailure(
        string path,
        bool isDirectory,
        MutableNode current,
        ScanState state,
        IProgress<ScanProgressSnapshot>? progress,
        Exception exception)
    {
        RecordUnreadItem(path, isDirectory, current, state, progress, exception);
    }

    private static void RecordUnreadItem(
        string path,
        bool isDirectory,
        MutableNode current,
        ScanState state,
        IProgress<ScanProgressSnapshot>? progress,
        Exception exception)
    {
        current.AddChild(CreateErrorNode(path, exception.Message, isDirectory, current));
        state.RecordIssue(path, isDirectory, isDirectory ? "读取目录信息" : "读取文件信息", exception);
        state.CurrentPath = path;
        state.ReportIfNeeded(progress);
    }

    private MutableNode CreateDirectoryNode(DirectoryInfo directoryInfo, MutableNode? parent)
    {
        var assessment = _locationPolicy.AssessPath(directoryInfo.FullName, isDirectory: true, directoryInfo.Attributes);
        return new MutableNode(
            parent: parent,
            path: directoryInfo.FullName,
            name: GetDisplayName(directoryInfo),
            extension: string.Empty,
            sizeBytes: 0,
            attributes: directoryInfo.Attributes,
            createdAt: directoryInfo.Exists ? new DateTimeOffset(directoryInfo.CreationTimeUtc) : null,
            modifiedAt: directoryInfo.Exists ? new DateTimeOffset(directoryInfo.LastWriteTimeUtc) : null,
            isDirectory: true,
            isProtectedLocation: assessment.IsProtectedLocation,
            riskTags: assessment.RiskTags.ToList());
    }

    private MutableNode CreateDirectoryNode(string path, WindowsBackupDirectoryEntry entry, MutableNode? parent)
    {
        var assessment = _locationPolicy.AssessPath(path, isDirectory: true, entry.Attributes);
        return new MutableNode(parent, path, entry.Name, string.Empty, 0,
            entry.Attributes, entry.CreatedAt, entry.ModifiedAt, true,
            assessment.IsProtectedLocation, assessment.RiskTags.ToList());
    }

    private MutableNode CreateFileNode(FileInfo fileInfo, MutableNode? parent, HashSet<string> accountedFileIdentities)
    {
        var attributes = fileInfo.Attributes;
        var storageInfo = _fileStorageReader.Read(fileInfo);
        // Finish fallible metadata reads before claiming this identity in the scan ledger.
        var createdAt = new DateTimeOffset(fileInfo.CreationTimeUtc);
        var modifiedAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc);
        return CreateFileNode(fileInfo.FullName, fileInfo.Name, fileInfo.Extension,
            attributes, storageInfo, createdAt, modifiedAt, parent, accountedFileIdentities);
    }

    private MutableNode CreateFileNode(string path, string name, string extension,
        FileAttributes attributes, FileStorageInfo storageInfo,
        DateTimeOffset? createdAt, DateTimeOffset? modifiedAt,
        MutableNode? parent, HashSet<string> accountedFileIdentities)
    {
        var assessment = _locationPolicy.AssessPath(path, isDirectory: false, attributes);
        var isShared = storageInfo.Identity is not null && !accountedFileIdentities.Add(storageInfo.Identity);
        var riskTags = assessment.RiskTags.ToList();
        if (isShared)
        {
            riskTags.Add("hard-link-shared");
        }

        if (storageInfo.IsEstimated)
        {
            riskTags.Add("storage-size-estimated");
            if (!string.IsNullOrWhiteSpace(storageInfo.EstimationReason))
            {
                riskTags.Add($"storage-estimate: {storageInfo.EstimationReason}");
            }
        }

        return new MutableNode(
            parent: parent,
            path: path,
            name: name,
            extension: extension,
            sizeBytes: isShared ? 0 : storageInfo.AllocatedBytes,
            attributes: attributes,
            createdAt: createdAt,
            modifiedAt: modifiedAt,
            isDirectory: false,
            isProtectedLocation: assessment.IsProtectedLocation,
            riskTags: riskTags,
            logicalSizeBytes: storageInfo.LogicalSizeBytes,
            storageInfo: storageInfo);
    }

    private static MutableNode CreateSkippedDirectoryNode(
        DirectoryInfo directoryInfo,
        MutableNode parent,
        bool isReparseCycle = false,
        bool isDuplicateTarget = false,
        bool isReparsePoint = true)
    {
        var riskTags = new List<string> { "skipped" };
        if (isReparsePoint)
        {
            riskTags.Add("reparse-point");
        }

        if (isReparseCycle)
        {
            riskTags.Add("reparse-cycle");
        }

        if (isDuplicateTarget)
        {
            riskTags.Add("duplicate-target");
        }

        return new MutableNode(
            parent: parent,
            path: directoryInfo.FullName,
            name: GetDisplayName(directoryInfo),
            extension: string.Empty,
            sizeBytes: 0,
            attributes: directoryInfo.Attributes,
            createdAt: directoryInfo.Exists ? new DateTimeOffset(directoryInfo.CreationTimeUtc) : null,
            modifiedAt: directoryInfo.Exists ? new DateTimeOffset(directoryInfo.LastWriteTimeUtc) : null,
            isDirectory: true,
            isProtectedLocation: false,
            riskTags: riskTags);
    }

    private MutableNode CreateSkippedFileNode(FileInfo fileInfo, MutableNode? parent)
    {
        var attributes = fileInfo.Attributes;
        var assessment = _locationPolicy.AssessPath(fileInfo.FullName, isDirectory: false, attributes);
        var riskTags = assessment.RiskTags
            .Concat(["reparse-point", "skipped"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MutableNode(
            parent: parent,
            path: fileInfo.FullName,
            name: fileInfo.Name,
            extension: fileInfo.Extension,
            sizeBytes: 0,
            attributes: attributes,
            createdAt: null,
            modifiedAt: null,
            isDirectory: false,
            isProtectedLocation: assessment.IsProtectedLocation,
            riskTags: riskTags);
    }

    private MutableNode CreateSkippedFileNode(string path, WindowsBackupDirectoryEntry entry, MutableNode? parent)
    {
        var assessment = _locationPolicy.AssessPath(path, isDirectory: false, entry.Attributes);
        return new MutableNode(parent, path, entry.Name, Path.GetExtension(entry.Name), 0,
            entry.Attributes, null, null, false, assessment.IsProtectedLocation,
            assessment.RiskTags.Concat(["reparse-point", "skipped"])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static MutableNode CreateErrorNode(string path, string errorMessage, bool isDirectory, MutableNode parent)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = trimmed;
        }

        return new MutableNode(
            parent: parent,
            path: path,
            name: name,
            extension: isDirectory ? string.Empty : Path.GetExtension(path),
            sizeBytes: 0,
            attributes: FileAttributes.Normal,
            createdAt: null,
            modifiedAt: null,
            isDirectory: isDirectory,
            isProtectedLocation: false,
            riskTags: ["error", errorMessage]);
    }

    private static string GetCanonicalDirectoryPath(
        DirectoryInfo directoryInfo,
        bool isReparsePoint,
        string? parentCanonicalPath)
    {
        var path = isReparsePoint
            ? directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directoryInfo.FullName
            : parentCanonicalPath is null
                ? directoryInfo.FullName
                : Path.Combine(parentCanonicalPath, directoryInfo.Name);

        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        return pathRoot is not null && fullPath.Length > pathRoot.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    private static bool IsRecoverableScanException(Exception exception)
    {
        return exception is UnauthorizedAccessException or IOException or System.Security.SecurityException;
    }

    private static string GetDisplayName(DirectoryInfo directoryInfo)
    {
        if (!string.IsNullOrWhiteSpace(directoryInfo.Name))
        {
            return directoryInfo.Name;
        }

        return directoryInfo.Root.FullName;
    }

    private sealed class ScanState
    {
        private const int MinimumProgressReportIntervalMilliseconds = 100;
        private readonly int _progressReportInterval;
        private readonly int _treeSnapshotIntervalMilliseconds;
        private readonly bool _includeIntermediateTreeSnapshots;
        private readonly Stopwatch _snapshotStopwatch;
        private int _pendingUpdates;
        private long _lastProgressReportAtMilliseconds = -1;
        private long _lastRootSnapshotAtMilliseconds = -1;
        private readonly Dictionary<string, ScanReadIssue> _issues = new(StringComparer.OrdinalIgnoreCase);

        public ScanState(
            int progressReportInterval,
            int treeSnapshotIntervalMilliseconds,
            bool includeIntermediateTreeSnapshots,
            MutableNode root)
        {
            _progressReportInterval = Math.Max(1, progressReportInterval);
            _treeSnapshotIntervalMilliseconds = Math.Max(0, treeSnapshotIntervalMilliseconds);
            _includeIntermediateTreeSnapshots = includeIntermediateTreeSnapshots;
            _snapshotStopwatch = Stopwatch.StartNew();
            Root = root;
        }

        public int FilesScanned { get; set; }

        public int DirectoriesScanned { get; set; }

        public long BytesDiscovered { get; set; }

        public long LogicalBytesDiscovered { get; set; }

        public int EstimatedStorageItemCount { get; set; }

        public int ScanIssueCount => _issues.Count;

        public IReadOnlyList<ScanReadIssue> Issues => _issues.Values.ToArray();

        public void RecordIssue(string path, bool isDirectory, string operation, Exception exception)
        {
            // Directory and file enumeration may both fail for the same inaccessible folder.
            // Report that path once rather than doubling the count and hiding the real cause.
            _issues.TryAdd(path, new ScanReadIssue(path, isDirectory, operation,
                exception.HResult & 0xFFFF, exception.Message));
        }

        public int SkippedReparsePointCount { get; set; }

        public int SkippedDuplicateTargetCount { get; set; }

        public HashSet<string> AccountedFileIdentities { get; } = new(StringComparer.Ordinal);

        public HashSet<string> VisitedCanonicalDirectoryPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? CurrentPath { get; set; }

        public MutableNode Root { get; }

        public void ReportIfNeeded(IProgress<ScanProgressSnapshot>? progress)
        {
            _pendingUpdates++;
            if (_pendingUpdates < _progressReportInterval || progress is null)
            {
                return;
            }

            var elapsed = _snapshotStopwatch.ElapsedMilliseconds;
            if (_lastProgressReportAtMilliseconds >= 0
                && elapsed - _lastProgressReportAtMilliseconds < MinimumProgressReportIntervalMilliseconds)
            {
                return;
            }

            _pendingUpdates = 0;
            _lastProgressReportAtMilliseconds = elapsed;
            progress.Report(CreateSnapshot(includeRoot: _includeIntermediateTreeSnapshots && ShouldIncludeRootSnapshot()));
        }

        public ScanProgressSnapshot CreateSnapshot(bool includeRoot)
        {
            return new ScanProgressSnapshot(
                FilesScanned,
                DirectoriesScanned,
                BytesDiscovered,
                CurrentPath,
                includeRoot ? Root.ToImmutable() : null)
            {
                LogicalBytesDiscovered = LogicalBytesDiscovered,
                EstimatedStorageItemCount = EstimatedStorageItemCount,
                SkippedDuplicateTargetCount = SkippedDuplicateTargetCount,
            };
        }

        private bool ShouldIncludeRootSnapshot()
        {
            if (_treeSnapshotIntervalMilliseconds <= 0)
            {
                return true;
            }

            var totalNodes = FilesScanned + DirectoriesScanned;
            var effectiveInterval = ScanProgressTuning.GetAdaptiveTreeSnapshotInterval(_treeSnapshotIntervalMilliseconds, totalNodes);
            var elapsed = _snapshotStopwatch.ElapsedMilliseconds;
            if (_lastRootSnapshotAtMilliseconds < 0 || elapsed - _lastRootSnapshotAtMilliseconds >= effectiveInterval)
            {
                _lastRootSnapshotAtMilliseconds = elapsed;
                return true;
            }

            return false;
        }
    }

    private sealed class MutableNode
    {
        private readonly List<string> _riskTags;
        private readonly List<MutableNode> _children = [];
        private FileSystemNode? _cachedSnapshot;

        public MutableNode(
            MutableNode? parent,
            string path,
            string name,
            string extension,
            long sizeBytes,
            FileAttributes attributes,
            DateTimeOffset? createdAt,
            DateTimeOffset? modifiedAt,
            bool isDirectory,
            bool isProtectedLocation,
            List<string> riskTags,
            long logicalSizeBytes = 0,
            FileStorageInfo? storageInfo = null)
        {
            Parent = parent;
            Path = path;
            Name = name;
            Extension = extension;
            SizeBytes = sizeBytes;
            LogicalSizeBytes = logicalSizeBytes;
            StorageInfo = storageInfo;
            Attributes = attributes;
            CreatedAt = createdAt;
            ModifiedAt = modifiedAt;
            IsDirectory = isDirectory;
            IsProtectedLocation = isProtectedLocation;
            _riskTags = riskTags;
        }

        public MutableNode? Parent { get; }

        public string Path { get; }

        public string Name { get; }

        public string Extension { get; }

        public long SizeBytes { get; private set; }

        public long LogicalSizeBytes { get; private set; }

        public FileStorageInfo? StorageInfo { get; }

        public FileAttributes Attributes { get; }

        public DateTimeOffset? CreatedAt { get; }

        public DateTimeOffset? ModifiedAt { get; }

        public bool IsDirectory { get; }

        public bool IsProtectedLocation { get; }

        public void AddChild(MutableNode child)
        {
            _children.Add(child);
            // Zero-byte, unreadable and skipped children also change the tree and counts.
            InvalidateSnapshot();
        }

        public void AddRiskTag(string riskTag)
        {
            if (_riskTags.Contains(riskTag, StringComparer.Ordinal))
            {
                return;
            }
            _riskTags.Add(riskTag);
            InvalidateSnapshot();
        }

        public void AddSize(long delta, long logicalDelta)
        {
            if (delta == 0 && logicalDelta == 0)
            {
                return;
            }

            SizeBytes += delta;
            LogicalSizeBytes += logicalDelta;
            InvalidateSnapshot();
            Parent?.AddSize(delta, logicalDelta);
        }

        private void InvalidateSnapshot()
        {
            // A dirty node already has dirty ancestors. Stop at the first dirty node,
            // so repeated mutations between snapshots do not walk the parent chain.
            for (MutableNode? node = this; node is not null && node._cachedSnapshot is not null; node = node.Parent)
            {
                node._cachedSnapshot = null;
            }
        }

        public FileSystemNode ToImmutable()
        {
            if (_cachedSnapshot is not null)
            {
                return _cachedSnapshot;
            }

            var immutableChildren = _children.Count == 0
                ? Array.Empty<FileSystemNode>()
                : _children
                    .OrderByDescending(child => child.SizeBytes)
                    .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => child.ToImmutable())
                    .ToArray();
            var descendantFileCount = IsDirectory
                ? immutableChildren.Sum(child => child.DescendantFileCount)
                : 1;
            var descendantDirectoryCount = IsDirectory
                ? 1 + immutableChildren.Sum(child => child.DescendantDirectoryCount)
                : 0;

            _cachedSnapshot = new FileSystemNode(
                Path,
                Name,
                Extension,
                SizeBytes,
                Attributes,
                CreatedAt,
                ModifiedAt,
                IsDirectory,
                IsProtectedLocation,
                _riskTags.ToArray(),
                immutableChildren,
                descendantFileCount,
                descendantDirectoryCount)
            {
                LogicalSizeBytes = LogicalSizeBytes,
                StorageInfo = StorageInfo
            };
            return _cachedSnapshot;
        }
    }
}

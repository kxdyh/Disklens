using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Tests.Scanning;

public sealed class DiskAllocationTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly List<string> _symbolicLinks = [];

    public DiskAllocationTests()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Native allocation regression tests require Windows NTFS.");
        }

        var baseDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".temp-tests"));
        var drive = new DriveInfo(Path.GetPathRoot(baseDirectory)!);
        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Native allocation regression tests require NTFS; {drive.Name} uses {drive.DriveFormat}.");
        }

        Directory.CreateDirectory(baseDirectory);
        _rootDirectory = Path.Combine(baseDirectory, $"disk-allocation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public async Task Hard_links_should_keep_every_path_and_charge_allocation_once_per_scan()
    {
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_rootDirectory, "first")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_rootDirectory, "second")).FullName;
        var original = Path.Combine(firstDirectory, "original.bin");
        var alias = Path.Combine(secondDirectory, "alias.bin");
        var rootAlias = Path.Combine(_rootDirectory, "root-alias.bin");
        var content = CreateNonResidentContent();
        await File.WriteAllBytesAsync(original, content);
        CreateHardLink(alias, original);
        CreateHardLink(rootAlias, original);

        var allocated = ReadNativeAllocation(original);
        Assert.True(allocated > 0);
        var scanner = new DirectoryScanner();
        var expectedPaths = new[] { original, alias, rootAlias }.Order(StringComparer.OrdinalIgnoreCase).ToArray();

        for (var scan = 0; scan < 2; scan++)
        {
            var snapshots = new List<ScanProgressSnapshot>();
            var result = await scanner.ScanAsync(CreateOptions(_rootDirectory), new CallbackProgress(snapshots));
            var files = result.Root.EnumerateFiles().ToArray();

            Assert.Equal(expectedPaths, files.Select(node => node.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            Assert.Equal(3, result.FilesScanned);
            Assert.Equal(allocated, result.BytesDiscovered);
            Assert.Equal(3L * content.Length, result.LogicalBytesDiscovered);
            Assert.Single(files, node => node.SizeBytes == allocated);
            Assert.Equal(2, files.Count(node => node.SizeBytes == 0 && node.RiskTags.Contains("hard-link-shared")));
            Assert.Single(files.Select(node => node.StorageInfo!.Identity).Distinct(StringComparer.Ordinal));
            // The same physical allocation drives progress even though all three
            // logical paths are present. Completion does not replace this ratio.
            var volumeUsedBytesAtScanStart = allocated * 4;
            Assert.Equal(25d, ScanStorageProgress.CalculatePercent(snapshots[^1].BytesDiscovered, volumeUsedBytesAtScanStart));
            Assert.All(snapshots, snapshot =>
                Assert.InRange(ScanStorageProgress.CalculatePercent(snapshot.BytesDiscovered, volumeUsedBytesAtScanStart)!.Value, 0, 25));
            Assert.NotEqual(ScanStorageProgress.CalculatePercent(result.LogicalBytesDiscovered, volumeUsedBytesAtScanStart),
                ScanStorageProgress.CalculatePercent(result.BytesDiscovered, volumeUsedBytesAtScanStart));
            Assert.All(files, node =>
            {
                AssertNativeMetadata(node, content.Length, allocated);
                Assert.Equal(3U, node.StorageInfo!.LinkCount);
            });
            AssertScanTotals(result, snapshots);
        }

        // A new session must acquire ownership even when only a formerly shared alias is scanned.
        foreach (var path in new[] { alias, rootAlias, original })
        {
            var snapshots = new List<ScanProgressSnapshot>();
            var result = await scanner.ScanAsync(CreateOptions(path), new CallbackProgress(snapshots));
            Assert.False(result.Root.IsDirectory);
            Assert.Equal(path, result.Root.Path);
            Assert.Equal(1, result.FilesScanned);
            Assert.Equal(0, result.DirectoriesScanned);
            Assert.Equal(allocated, result.Root.SizeBytes);
            Assert.DoesNotContain("hard-link-shared", result.Root.RiskTags);
            AssertNativeMetadata(result.Root, content.Length, allocated);
            AssertScanTotals(result, snapshots);
        }
    }

    [Fact]
    public async Task Different_files_with_identical_lengths_and_contents_should_each_own_their_allocation()
    {
        var content = CreateNonResidentContent();
        var first = Path.Combine(_rootDirectory, "first.bin");
        var second = Path.Combine(_rootDirectory, "second.bin");
        await File.WriteAllBytesAsync(first, content);
        await File.WriteAllBytesAsync(second, content);
        var expectedAllocation = ReadNativeAllocation(first) + ReadNativeAllocation(second);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(CreateOptions(_rootDirectory), new CallbackProgress(snapshots));

        var files = result.Root.EnumerateFiles().ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(expectedAllocation, result.BytesDiscovered);
        Assert.Equal(2L * content.Length, result.LogicalBytesDiscovered);
        Assert.Equal(2, files.Select(node => node.StorageInfo!.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.All(files, node =>
        {
            AssertNativeMetadata(node, content.Length, ReadNativeAllocation(node.Path));
            Assert.Equal(node.StorageInfo!.AllocatedBytes, node.SizeBytes);
            Assert.DoesNotContain("hard-link-shared", node.RiskTags);
        });
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task Sparse_files_should_use_allocated_ranges_instead_of_their_logical_length()
    {
        RequireVolumeFeature(0x40, "sparse files");
        var path = Path.Combine(_rootDirectory, "sparse.bin");
        const long logicalLength = 128L * 1024 * 1024 + 37;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            SetSparseOrSkip(stream.SafeFileHandle);
            stream.SetLength(logicalLength);
            stream.Write(Enumerable.Repeat((byte)0x5A, 4096).ToArray());
            stream.Position = logicalLength - 4096;
            stream.Write(Enumerable.Repeat((byte)0xA5, 4096).ToArray());
            stream.Flush(flushToDisk: true);
        }

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
        Assert.Equal(logicalLength, new FileInfo(path).Length);
        var allocated = ReadNativeAllocation(path);
        Assert.InRange(allocated, 1L, logicalLength / 64);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(CreateOptions(_rootDirectory), new CallbackProgress(snapshots));

        var file = Assert.Single(result.Root.EnumerateFiles());
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(allocated, file.SizeBytes);
        Assert.NotEqual(new FileInfo(path).Length, file.SizeBytes);
        AssertNativeMetadata(file, logicalLength, allocated);
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task Compressed_root_files_should_report_native_allocation_without_estimation()
    {
        RequireVolumeFeature(0x10, "file compression");
        var path = Path.Combine(_rootDirectory, "compressed.bin");
        const int logicalLength = 8 * 1024 * 1024;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            SetCompressionOrSkip(stream.SafeFileHandle);
            var block = Enumerable.Repeat((byte)0x42, 64 * 1024).ToArray();
            for (var offset = 0; offset < logicalLength; offset += block.Length)
            {
                stream.Write(block);
            }

            stream.Flush(flushToDisk: true);
        }

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Compressed));
        var allocated = ReadNativeAllocation(path);
        Assert.InRange(allocated, 1L, logicalLength - 1L);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(CreateOptions(path), new CallbackProgress(snapshots));

        Assert.False(result.Root.IsDirectory);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.DirectoriesScanned);
        Assert.Equal(allocated, result.Root.SizeBytes);
        Assert.Equal(logicalLength, new FileInfo(path).Length);
        AssertNativeMetadata(result.Root, logicalLength, allocated);
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task A_regular_root_file_should_report_the_native_allocation_and_preserve_logical_length()
    {
        var path = Path.Combine(_rootDirectory, "root.bin");
        var content = CreateNonResidentContent();
        await File.WriteAllBytesAsync(path, content);
        var allocated = ReadNativeAllocation(path);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(CreateOptions(path), new CallbackProgress(snapshots));

        Assert.Equal(path, result.Root.Path);
        Assert.False(result.Root.IsDirectory);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.DirectoriesScanned);
        Assert.Equal(allocated, result.BytesDiscovered);
        AssertNativeMetadata(result.Root, content.Length, allocated);
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task An_empty_directory_should_have_zero_allocation_and_logical_bytes()
    {
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(CreateOptions(_rootDirectory), new CallbackProgress(snapshots));

        Assert.True(result.Root.IsDirectory);
        Assert.Empty(result.Root.Children);
        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(1, result.DirectoriesScanned);
        Assert.Equal(0L, result.BytesDiscovered);
        Assert.Equal(0L, result.LogicalBytesDiscovered);
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task A_zero_length_file_should_still_be_counted_and_have_native_metadata()
    {
        var path = Path.Combine(_rootDirectory, "empty.bin");
        await File.WriteAllBytesAsync(path, []);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(CreateOptions(path), new CallbackProgress(snapshots));

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.DirectoriesScanned);
        Assert.Equal(0L, result.BytesDiscovered);
        Assert.Equal(0L, ReadNativeAllocation(path));
        AssertNativeMetadata(result.Root, logicalLength: 0, allocated: 0);
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task Followed_file_symbolic_links_should_keep_alias_paths_without_duplicate_allocation()
    {
        var original = Path.Combine(_rootDirectory, "original.bin");
        var alias = Path.Combine(_rootDirectory, "alias.bin");
        var content = CreateNonResidentContent();
        await File.WriteAllBytesAsync(original, content);
        CreateSymbolicLinkOrSkip(alias, original, isDirectory: false);
        var allocated = ReadNativeAllocation(original);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(
            CreateOptions(_rootDirectory) with { FollowReparsePoints = true },
            new CallbackProgress(snapshots));

        var files = result.Root.EnumerateFiles().ToArray();
        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(2, files.Length);
        Assert.Contains(files, node => node.Path == original);
        Assert.Contains(files, node => node.Path == alias);
        Assert.Single(files, node => node.SizeBytes == allocated);
        Assert.Single(files, node => node.SizeBytes == 0 && node.RiskTags.Contains("hard-link-shared"));
        Assert.Single(files.Select(node => node.StorageInfo!.Identity).Distinct(StringComparer.Ordinal));
        Assert.Equal(allocated, result.BytesDiscovered);
        Assert.Equal(2L * content.Length, result.LogicalBytesDiscovered);
        Assert.All(files, node => AssertNativeMetadata(node, content.Length, allocated));
        AssertScanTotals(result, snapshots);
    }

    [Fact]
    public async Task Followed_directory_aliases_should_preserve_both_directory_nodes_and_scan_the_target_once()
    {
        var target = Directory.CreateDirectory(Path.Combine(_rootDirectory, "target")).FullName;
        var alias = Path.Combine(_rootDirectory, "alias");
        var path = Path.Combine(target, "content.bin");
        var content = CreateNonResidentContent();
        await File.WriteAllBytesAsync(path, content);
        CreateSymbolicLinkOrSkip(alias, target, isDirectory: true);
        var allocated = ReadNativeAllocation(path);
        var snapshots = new List<ScanProgressSnapshot>();

        var result = await new DirectoryScanner().ScanAsync(
            CreateOptions(_rootDirectory) with { FollowReparsePoints = true },
            new CallbackProgress(snapshots));

        Assert.Equal(2, result.Root.Children.Count);
        Assert.Contains(result.Root.Children, node => node.Path == target && node.IsDirectory);
        Assert.Contains(result.Root.Children, node => node.Path == alias && node.IsDirectory);
        Assert.Single(result.Root.Children, node => node.RiskTags.Contains("skipped") && node.RiskTags.Contains("duplicate-target"));
        var file = Assert.Single(result.Root.EnumerateFiles());
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(2, result.DirectoriesScanned);
        Assert.Equal(1, result.SkippedDuplicateTargetCount);
        Assert.Equal(allocated, result.BytesDiscovered);
        Assert.Equal(content.LongLength, result.LogicalBytesDiscovered);
        AssertNativeMetadata(file, content.Length, allocated);
        AssertScanTotals(result, snapshots);
    }

    private static ScanSessionOptions CreateOptions(string path) => new(
        path,
        IncludeIntermediateTreeSnapshots: true,
        ProgressReportInterval: 1,
        TreeSnapshotIntervalMilliseconds: 0);

    private static byte[] CreateNonResidentContent()
    {
        var content = new byte[96 * 1024 + 13];
        new Random(1729).NextBytes(content);
        return content;
    }

    private static void AssertNativeMetadata(FileSystemNode node, long logicalLength, long allocated)
    {
        var storage = Assert.IsType<FileStorageInfo>(node.StorageInfo);
        Assert.False(storage.IsEstimated, storage.EstimationReason);
        Assert.Null(storage.EstimationReason);
        Assert.False(string.IsNullOrWhiteSpace(storage.Identity));
        Assert.Equal(logicalLength, node.LogicalSizeBytes);
        Assert.Equal(logicalLength, storage.LogicalSizeBytes);
        Assert.Equal(allocated, storage.AllocatedBytes);
    }

    private static void AssertScanTotals(ScanResult result, IReadOnlyList<ScanProgressSnapshot> snapshots)
    {
        Assert.Equal(0, result.ScanIssueCount);
        Assert.Equal(0, result.EstimatedStorageItemCount);
        AssertTreeTotals(result.Root, result.BytesDiscovered, result.LogicalBytesDiscovered);
        Assert.NotEmpty(snapshots);
        Assert.NotNull(snapshots[^1].Root);
        Assert.Equal(result.BytesDiscovered, snapshots[^1].BytesDiscovered);
        Assert.Equal(result.LogicalBytesDiscovered, snapshots[^1].LogicalBytesDiscovered);
        long previousBytes = 0;
        long previousLogicalBytes = 0;
        foreach (var snapshot in snapshots)
        {
            Assert.InRange(snapshot.BytesDiscovered, previousBytes, result.BytesDiscovered);
            Assert.InRange(snapshot.LogicalBytesDiscovered, previousLogicalBytes, result.LogicalBytesDiscovered);
            Assert.Equal(0, snapshot.EstimatedStorageItemCount);
            if (snapshot.Root is not null)
            {
                AssertTreeTotals(snapshot.Root, snapshot.BytesDiscovered, snapshot.LogicalBytesDiscovered);
            }

            previousBytes = snapshot.BytesDiscovered;
            previousLogicalBytes = snapshot.LogicalBytesDiscovered;
        }
    }

    private static void AssertTreeTotals(FileSystemNode root, long bytes, long logicalBytes)
    {
        var files = root.EnumerateFiles().ToArray();
        Assert.Equal(bytes, root.SizeBytes);
        Assert.Equal(bytes, files.Sum(node => node.SizeBytes));
        Assert.Equal(logicalBytes, root.LogicalSizeBytes);
        Assert.Equal(logicalBytes, files.Sum(node => node.LogicalSizeBytes ?? 0));
        foreach (var directory in root.DescendantsAndSelf().Where(node => node.IsDirectory))
        {
            Assert.Equal(directory.SizeBytes, directory.Children.Sum(node => node.SizeBytes));
            Assert.Equal(directory.LogicalSizeBytes, directory.Children.Sum(node => node.LogicalSizeBytes ?? 0));
        }
    }

    private static void CreateHardLink(string alias, string original)
    {
        AssertNativeSuccess(CreateHardLinkW(alias, original, IntPtr.Zero), "CreateHardLinkW");
    }

    private void CreateSymbolicLinkOrSkip(string alias, string target, bool isDirectory)
    {
        const uint allowUnprivilegedCreate = 2;
        if (!CreateSymbolicLinkW(alias, target, allowUnprivilegedCreate | (isDirectory ? 1U : 0U)))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 1 or 5 or 50 or 87 or 1314)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Symbolic links are unavailable on this Windows host: Win32 {error} ({new Win32Exception(error).Message}).");
            }

            Assert.Fail($"CreateSymbolicLinkW failed: Win32 {error} ({new Win32Exception(error).Message}).");
        }

        _symbolicLinks.Add(alias);
        Assert.True(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint));
    }

    private static void SetSparseOrSkip(SafeFileHandle handle)
    {
        const uint fsctlSetSparse = 0x000900C4;
        if (!DeviceIoControl(handle, fsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            ThrowUnsupportedFeatureOrFail("FSCTL_SET_SPARSE", Marshal.GetLastWin32Error());
        }
    }

    private void RequireVolumeFeature(uint featureFlag, string featureName)
    {
        AssertNativeSuccess(GetVolumeInformationW(Path.GetPathRoot(_rootDirectory)!, IntPtr.Zero, 0,
            out _, out _, out var flags, IntPtr.Zero, 0), "GetVolumeInformationW");
        if ((flags & featureFlag) == 0)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"The NTFS test volume does not advertise support for {featureName}.");
        }
    }

    private static void SetCompressionOrSkip(SafeFileHandle handle)
    {
        const uint fsctlSetCompression = 0x0009C040;
        ushort compressionFormatLznt1 = 2;
        if (!DeviceIoControlCompression(handle, fsctlSetCompression, ref compressionFormatLznt1, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            ThrowUnsupportedFeatureOrFail("FSCTL_SET_COMPRESSION", Marshal.GetLastWin32Error());
        }
    }

    private static void ThrowUnsupportedFeatureOrFail(string operation, int error)
    {
        var message = $"{operation}: Win32 {error} ({new Win32Exception(error).Message}).";
        if (error is 1 or 50)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"The test volume does not support {message}");
        }

        Assert.Fail(message);
    }

    private static long ReadNativeAllocation(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        AssertNativeSuccess(
            GetFileInformationByHandleEx(handle, 1, out var information, (uint)Marshal.SizeOf<FileStandardInfo>()),
            "GetFileInformationByHandleEx(FileStandardInfo)");
        Assert.Equal(new FileInfo(path).Length, information.EndOfFile);
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.SparseFile) && !attributes.HasFlag(FileAttributes.Compressed))
        {
            return information.AllocationSize;
        }

        var low = GetCompressedFileSizeW(path, out var high);
        var error = Marshal.GetLastWin32Error();
        Assert.True(low != uint.MaxValue || error == 0,
            $"GetCompressedFileSizeW failed: Win32 {error} ({new Win32Exception(error).Message}).");
        return checked((long)(((ulong)high << 32) | low));
    }

    private static void AssertNativeSuccess(bool succeeded, string operation)
    {
        var error = Marshal.GetLastWin32Error();
        Assert.True(succeeded, $"{operation} failed: Win32 {error} ({new Win32Exception(error).Message}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string symbolicLink, string target, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlCompression(SafeFileHandle device, uint controlCode, ref ushort input, uint inputSize,
        IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileStandardInfo information, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPath, IntPtr volumeName, uint volumeNameSize,
        out uint volumeSerial, out uint maximumComponentLength, out uint fileSystemFlags, IntPtr fileSystemName, uint fileSystemNameSize);

    private sealed class CallbackProgress(List<ScanProgressSnapshot> snapshots) : IProgress<ScanProgressSnapshot>
    {
        public void Report(ScanProgressSnapshot value) => snapshots.Add(value);
    }

    public void Dispose()
    {
        foreach (var path in _symbolicLinks)
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}

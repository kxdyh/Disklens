using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Tests.Scanning;

public sealed class WindowsFileStorageReaderTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = [];

    public WindowsFileStorageReaderTests()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Storage metadata tests require Windows NTFS.");
        }

        var artifactRoot = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".temp-tests"));
        if (new DriveInfo(Path.GetPathRoot(artifactRoot)!).DriveFormat != "NTFS")
        {
            throw Xunit.Sdk.SkipException.ForSkip("Storage metadata tests require NTFS.");
        }

        _root = Directory.CreateDirectory(Path.Combine(artifactRoot, "file-storage-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    [Fact]
    public async Task Named_data_stream_allocation_should_be_charged_once_across_hard_links()
    {
        var original = Path.Combine(_root, "original.bin");
        var alias = Path.Combine(_root, "alias.bin");
        await File.WriteAllBytesAsync(original, new byte[100_013]);
        await File.WriteAllBytesAsync(original + ":large-stream:$DATA", new byte[200_027]);
        await File.WriteAllBytesAsync(original + ":small-stream:$DATA", new byte[20_017]);
        AssertNativeSuccess(CreateHardLinkW(alias, original, IntPtr.Zero), "CreateHardLinkW");
        var expected = ReadAllocation(original) + ReadAllocation(original + ":large-stream:$DATA")
            + ReadAllocation(original + ":small-stream:$DATA");

        var result = await new DirectoryScanner().ScanAsync(new ScanSessionOptions(_root));

        Assert.False(result.IsPartial);
        Assert.Equal(0, result.EstimatedStorageItemCount);
        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(expected, result.BytesDiscovered);
        Assert.Equal(200_026L, result.LogicalBytesDiscovered); // Logical size remains the primary data stream.
        Assert.Single(result.Root.EnumerateFiles(), file => file.SizeBytes == expected);
        Assert.Single(result.Root.EnumerateFiles(), file => file.RiskTags.Contains("hard-link-shared"));
        Assert.All(result.Root.EnumerateFiles(), file => Assert.Equal(expected, file.StorageInfo!.AllocatedBytes));
    }

    [Fact]
    public async Task An_empty_primary_stream_should_still_report_named_stream_allocation()
    {
        var path = Path.Combine(_root, "empty-primary.bin");
        await File.WriteAllBytesAsync(path, []);
        await File.WriteAllBytesAsync(path + ":content:$DATA", new byte[100_013]);
        var expected = ReadAllocation(path + ":content:$DATA");

        var storage = new WindowsFileStorageReader().Read(new FileInfo(path));

        Assert.False(storage.IsEstimated, storage.EstimationReason);
        Assert.Equal(0, storage.LogicalSizeBytes);
        Assert.True(expected > 0);
        Assert.Equal(expected, storage.AllocatedBytes);
    }

    [Fact]
    public async Task Sparse_named_streams_should_report_only_locally_allocated_storage()
    {
        var path = Path.Combine(_root, "sparse-stream.bin");
        await File.WriteAllBytesAsync(path, new byte[100_013]);
        var streamPath = path + ":sparse-content:$DATA";
        using (var stream = new FileStream(streamPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            AssertNativeSuccess(DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0,
                IntPtr.Zero, 0, out _, IntPtr.Zero), "FSCTL_SET_SPARSE");
            stream.SetLength(128L * 1024 * 1024);
            stream.Write(new byte[4096]);
            stream.Flush(flushToDisk: true);
        }

        var expected = ReadAllocation(path) + ReadCompressedAllocation(streamPath);
        var storage = new WindowsFileStorageReader().Read(new FileInfo(path));

        Assert.False(storage.IsEstimated, storage.EstimationReason);
        Assert.Equal(expected, storage.AllocatedBytes);
        Assert.InRange(storage.AllocatedBytes, 100_013, 2 * 1024 * 1024);
    }

    [Fact]
    public async Task Stream_enumeration_should_grow_its_buffer_without_losing_streams()
    {
        var path = Path.Combine(_root, "many-streams.bin");
        await File.WriteAllBytesAsync(path, []);
        long expected = 0;
        for (var index = 0; index < 48; index++)
        {
            var streamPath = path + ":" + new string('x', 64) + index + ":$DATA";
            await File.WriteAllBytesAsync(streamPath, new byte[12_000 + index]);
            expected += ReadAllocation(streamPath);
        }

        var storage = new WindowsFileStorageReader().Read(new FileInfo(path));

        Assert.False(storage.IsEstimated, storage.EstimationReason);
        Assert.Equal(expected, storage.AllocatedBytes);
    }

    [Fact]
    public async Task A_failed_native_open_should_keep_the_last_enumerated_size_as_an_explicit_estimate()
    {
        var path = Path.Combine(_root, "disappearing.bin");
        await File.WriteAllBytesAsync(path, new byte[100_013]);
        var enumeratedInfo = new DirectoryInfo(_root).EnumerateFiles().Single();
        File.Delete(path);

        var storage = new WindowsFileStorageReader().Read(enumeratedInfo);

        Assert.Equal(100_013, storage.LogicalSizeBytes);
        Assert.Equal(100_013, storage.AllocatedBytes);
        Assert.True(storage.IsEstimated);
        Assert.StartsWith("allocation-open-failed:", storage.EstimationReason);
        Assert.Null(storage.Identity);
    }

    [Fact]
    public async Task Ordinary_files_should_be_classified_without_following_symbolic_links()
    {
        var original = Path.Combine(_root, "ordinary.bin");
        var alias = Path.Combine(_root, "symbolic-link.bin");
        await File.WriteAllBytesAsync(original, new byte[100_013]);
        Assert.False(WindowsReparsePointClassifier.ShouldSkipFile(new FileInfo(original)));
        Assert.True(WindowsReparsePointClassifier.ShouldSkipFile(new FileInfo(Path.Combine(_root, "missing.bin"))));
        if (!CreateSymbolicLinkW(alias, original, 2))
        {
            var error = Marshal.GetLastWin32Error();
            throw Xunit.Sdk.SkipException.ForSkip($"Symbolic link creation unavailable: Win32 {error}.");
        }

        _links.Add(alias);
        Assert.True(WindowsReparsePointClassifier.ShouldSkipFile(new FileInfo(alias)));
        File.Delete(original);
        Assert.True(WindowsReparsePointClassifier.ShouldSkipFile(new FileInfo(alias)));
    }

    [Theory]
    [InlineData(0x80000017U, false)] // WOF single-file compression.
    [InlineData(0x9000001AU, false)] // CLOUD.
    [InlineData(0x9000101AU, false)]
    [InlineData(0x9000F01AU, false)]
    [InlineData(0xA000000CU, true)] // SYMLINK, name surrogate.
    [InlineData(0xA0000003U, true)] // MOUNT_POINT, name surrogate.
    [InlineData(0x80000099U, true)] // Unknown non-link provider.
    [InlineData(0xB000001AU, true)] // Cloud-like tag with name surrogate bit.
    [InlineData(0U, true)]
    public void Only_known_non_link_reparse_tags_should_be_inspected(uint tag, bool shouldSkip)
    {
        Assert.Equal(shouldSkip, WindowsReparsePointClassifier.ShouldSkipReparseTag(tag));
    }

    [WofFixtureFact]
    public void Existing_Wof_system_fixture_should_preserve_local_allocation_without_reading_contents()
    {
        var fixture = FindWofFixture()!;

        Assert.False(WindowsReparsePointClassifier.ShouldSkipFile(new FileInfo(fixture)));
        // The active WOF filter can hide the backing stream and reparse tag while
        // exposing the compressed allocation on the primary stream. Do not charge it twice.
        var compressedAllocation = ReadCompressedAllocation(fixture);
        Assert.True(compressedAllocation > 0);

        var storage = new WindowsFileStorageReader().Read(new FileInfo(fixture));

        Assert.False(storage.IsEstimated, storage.EstimationReason);
        Assert.True(storage.LogicalSizeBytes > 0);
        Assert.Equal(compressedAllocation, storage.AllocatedBytes);
        Assert.True(storage.AllocatedBytes < storage.LogicalSizeBytes);
    }

    public sealed class WofFixtureFactAttribute : FactAttribute
    {
        public WofFixtureFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || FindWofFixture() is null)
            {
                Skip = "No WOF compressed file exists among the fixed local system fixtures.";
            }
        }
    }

    private static string? FindWofFixture()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            "System32\\notepad.exe", "System32\\kernel32.dll", "System32\\KernelBase.dll", "System32\\ntdll.dll",
            "System32\\drivers\\ntfs.sys", "System32\\drivers\\Wof.sys", "System32\\calc.exe", "System32\\mspaint.exe"
        };
        return candidates.Select(path => Path.Combine(windows, path)).FirstOrDefault(IsWofFile);
    }

    private static bool IsWofFile(string path)
    {
        uint size = 0;
        return WofIsExternalFile(path, out var external, out var provider, IntPtr.Zero, ref size) >= 0
            && external && provider == 2; // WOF_PROVIDER_FILE: local single-file compression.
    }

    private static long ReadAllocation(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x00300000, IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"Metadata open failed: Win32 {Marshal.GetLastWin32Error()}.");
        AssertNativeSuccess(GetStandardInfo(handle, 1, out var info, (uint)Marshal.SizeOf<FileStandardInfo>()), "FileStandardInfo");
        return info.AllocationSize;
    }

    private static long ReadCompressedAllocation(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        var error = Marshal.GetLastWin32Error();
        Assert.True(low != uint.MaxValue || error == 0, $"GetCompressedFileSizeW failed: Win32 {error}.");
        return checked((long)(((ulong)high << 32) | low));
    }

    private static void AssertNativeSuccess(bool success, string operation)
    {
        var error = Marshal.GetLastWin32Error();
        Assert.True(success, $"{operation}: Win32 {error} ({new Win32Exception(error).Message}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle file, int informationClass, out FileStandardInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeTagInfo(SafeFileHandle file, int informationClass, out FileAttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkW(string path, string target, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    [DllImport("Wofutil.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WofIsExternalFile(string path, [MarshalAs(UnmanagedType.Bool)] out bool external,
        out uint provider, IntPtr information, ref uint size);

    public void Dispose()
    {
        foreach (var link in _links)
        {
            File.Delete(link);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

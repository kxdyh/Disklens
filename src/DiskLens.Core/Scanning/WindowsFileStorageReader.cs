using System.Runtime.InteropServices;
using DiskLens.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core.Scanning;

/// <summary>Reads allocation and file identity without opening a file for its contents.</summary>
public sealed class WindowsFileStorageReader : IFileStorageReader
{
    private const int InitialStreamBufferSize = 4096;
    [ThreadStatic] private static byte[]? s_streamInfoBuffer;
    [ThreadStatic] private static bool s_streamInfoBufferInUse;

    public FileStorageInfo Read(FileInfo fileInfo)
    {
        return Read(fileInfo.FullName, fileInfo.Length, fileInfo.Attributes);
    }

    public FileStorageInfo Read(string path, long logicalBytes, FileAttributes attributes, uint? reparseTag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(logicalBytes);
        if (!OperatingSystem.IsWindows())
        {
            return new FileStorageInfo(null, logicalBytes, logicalBytes,
                IsEstimated: true, EstimationReason: "allocation-query-unavailable");
        }

        var nativePath = WindowsReparsePointClassifier.ToNativePath(Path.GetFullPath(path));
        var openFlags = NativeMethods.OpenNoRecall | NativeMethods.BackupSemantics;
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (!reparseTag.HasValue && WindowsReparsePointClassifier.TryReadTag(path, out var detectedTag))
            {
                reparseTag = detectedTag;
            }

            if (!reparseTag.HasValue)
            {
                return new FileStorageInfo(null, logicalBytes, logicalBytes,
                    IsEstimated: true, EstimationReason: "reparse-tag-query-failed");
            }

            // WOF and cloud files store data on this file itself. Do not follow their
            // provider or recall content merely to obtain local storage metadata.
            if (WindowsReparsePointClassifier.IsKnownNonLinkTag(reparseTag.Value))
            {
                openFlags |= NativeMethods.OpenReparsePoint;
            }
        }

        using var handle = NativeMethods.CreateFile(
            nativePath,
            NativeMethods.FileReadAttributes,
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            openFlags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return new FileStorageInfo(null, logicalBytes, logicalBytes,
                IsEstimated: true, EstimationReason: $"allocation-open-failed:{error}");
        }

        var reasons = new List<string>();
        long? allocatedBytes = null;
        uint linkCount = 1;
        if (NativeMethods.GetStandardInfo(handle, NativeMethods.FileStandardInfoClass,
                out var standardInfo, (uint)Marshal.SizeOf<NativeMethods.FileStandardInfo>())
            && standardInfo.AllocationSize >= 0 && standardInfo.EndOfFile >= 0)
        {
            logicalBytes = standardInfo.EndOfFile;
            allocatedBytes = standardInfo.AllocationSize;
            linkCount = standardInfo.NumberOfLinks;
        }
        else
        {
            reasons.Add($"allocation-query-failed:{Marshal.GetLastWin32Error()}");
        }

        string? identity = null;
        var hasIdentity = NativeMethods.GetIdInfo(handle, NativeMethods.FileIdInfoClass,
            out var idInfo, (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>());
        var identityError = Marshal.GetLastWin32Error();
        if (hasIdentity && (idInfo.FileIdLow != 0 || idInfo.FileIdHigh != 0))
        {
            identity = $"{idInfo.VolumeSerialNumber:X16}:{idInfo.FileIdHigh:X16}{idInfo.FileIdLow:X16}";
        }
        else
        {
            reasons.Add(hasIdentity ? "file-identity-empty" : $"file-identity-unavailable:{identityError}");
        }

        // Query the opened object, which may be the target of an explicitly followed
        // symbolic link. Attribute queries and compression queries never read data.
        if (NativeMethods.GetAttributeTagInfo(handle, NativeMethods.FileAttributeTagInfoClass,
                out var attributeInfo, (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
        {
            attributes = (FileAttributes)attributeInfo.FileAttributes;
        }

        if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            allocatedBytes = ReadCompressedAllocation(handle, allocatedBytes ?? logicalBytes, reasons, "default-stream");
        }

        var totalAllocatedBytes = allocatedBytes ?? logicalBytes;
        if (TryReadStreams(handle, out var streams, out var streamFailure))
        {
            foreach (var stream in streams)
            {
                if (string.Equals(stream.Name, "::$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // The default stream was accounted for above.
                }

                var streamAllocation = ReadNamedStreamAllocation(nativePath, stream, openFlags, reasons);
                if (streamAllocation > long.MaxValue - totalAllocatedBytes)
                {
                    totalAllocatedBytes = long.MaxValue;
                    reasons.Add("data-stream-allocation-overflow");
                    break;
                }

                totalAllocatedBytes += streamAllocation;
            }
        }
        else
        {
            reasons.Add(streamFailure!);
        }

        return new FileStorageInfo(
            identity,
            logicalBytes,
            totalAllocatedBytes,
            linkCount,
            IsEstimated: reasons.Count > 0,
            EstimationReason: reasons.Count > 0 ? string.Join(";", reasons) : null);
    }

    private static long ReadCompressedAllocation(SafeFileHandle handle, long fallback,
        List<string> reasons, string streamDescription)
    {
        if (NativeMethods.GetCompressionInfo(handle, NativeMethods.FileCompressionInfoClass,
                out var information, (uint)Marshal.SizeOf<NativeMethods.FileCompressionInfo>())
            && information.CompressedFileSize >= 0)
        {
            return information.CompressedFileSize;
        }

        reasons.Add($"compressed-allocation-query-failed:{streamDescription}:{Marshal.GetLastWin32Error()}");
        return fallback;
    }

    private static long ReadNamedStreamAllocation(string nativePath, StreamAllocation stream,
        uint openFlags, List<string> reasons)
    {
        using var handle = NativeMethods.CreateFile(nativePath + stream.Name, NativeMethods.FileReadAttributes,
            NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete, IntPtr.Zero,
            NativeMethods.OpenExisting, openFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            reasons.Add($"data-stream-open-failed:{stream.Name}:{Marshal.GetLastWin32Error()}");
            return stream.AllocatedBytes;
        }

        if (!NativeMethods.GetAttributeTagInfo(handle, NativeMethods.FileAttributeTagInfoClass,
                out var attributes, (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
        {
            reasons.Add($"data-stream-attributes-failed:{stream.Name}:{Marshal.GetLastWin32Error()}");
            return stream.AllocatedBytes;
        }

        return ((FileAttributes)attributes.FileAttributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0
            ? ReadCompressedAllocation(handle, stream.AllocatedBytes, reasons, stream.Name)
            : stream.AllocatedBytes;
    }

    private static bool TryReadStreams(SafeFileHandle handle, out IReadOnlyList<StreamAllocation> streams,
        out string? failure)
    {
        streams = Array.Empty<StreamAllocation>();
        failure = null;
        const int headerSize = 24;
        const int maximumBufferSize = 16 * 1024 * 1024;
        // Each scan thread owns one small pinned buffer. Reentrant reads use a
        // separate temporary allocation rather than overwrite an active query.
        var borrowCachedBuffer = !s_streamInfoBufferInUse;
        var cachedBuffer = borrowCachedBuffer
            ? s_streamInfoBuffer ??= GC.AllocateArray<byte>(InitialStreamBufferSize, pinned: true)
            : null;
        if (borrowCachedBuffer)
        {
            s_streamInfoBufferInUse = true;
        }

        Span<char> defaultStreamName = stackalloc char[7];
        try
        {
            for (var bufferSize = InitialStreamBufferSize; bufferSize <= maximumBufferSize; bufferSize *= 2)
            {
                var useCachedBuffer = cachedBuffer is not null && bufferSize == InitialStreamBufferSize;
                var buffer = useCachedBuffer
                    ? Marshal.UnsafeAddrOfPinnedArrayElement(cachedBuffer!, 0)
                    : Marshal.AllocHGlobal(bufferSize);
                try
                {
                    if (!NativeMethods.GetStreamInfo(handle, NativeMethods.FileStreamInfoClass, buffer, (uint)bufferSize))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is 122 or 234) // INSUFFICIENT_BUFFER or MORE_DATA
                        {
                            continue;
                        }

                        if (error == 38) // HANDLE_EOF: the file has no data streams.
                        {
                            return true;
                        }

                        failure = $"data-stream-enumeration-failed:{error}";
                        return false;
                    }

                    List<StreamAllocation>? namedStreams = null;
                    var offset = 0;
                    while (true)
                    {
                        if (offset < 0 || offset > bufferSize - headerSize)
                        {
                            break;
                        }

                        var entry = IntPtr.Add(buffer, offset);
                        var nextOffset = unchecked((uint)Marshal.ReadInt32(entry));
                        var nameBytes = Marshal.ReadInt32(entry, 4);
                        var allocation = Marshal.ReadInt64(entry, 16);
                        if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > bufferSize - offset - headerSize
                            || allocation < 0)
                        {
                            break;
                        }

                        var isDefaultStream = false;
                        if (nameBytes == defaultStreamName.Length * sizeof(char))
                        {
                            for (var index = 0; index < defaultStreamName.Length; index++)
                            {
                                defaultStreamName[index] = (char)Marshal.ReadInt16(entry, headerSize + index * sizeof(char));
                            }
                            isDefaultStream = MemoryExtensions.Equals(defaultStreamName, "::$DATA".AsSpan(),
                                StringComparison.OrdinalIgnoreCase);
                        }

                        if (!isDefaultStream)
                        {
                            var name = Marshal.PtrToStringUni(IntPtr.Add(entry, headerSize), nameBytes / 2) ?? string.Empty;
                            // Validate remote filesystem names before appending to a path.
                            if (!name.StartsWith(':') || !name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase)
                                || name.AsSpan(1, name.Length - 1).IndexOfAny('\\', '/', '\0') >= 0)
                            {
                                break;
                            }

                            (namedStreams ??= []).Add(new StreamAllocation(name, allocation));
                        }

                        if (nextOffset == 0)
                        {
                            streams = namedStreams is null ? Array.Empty<StreamAllocation>() : namedStreams;
                            return true;
                        }

                        if (nextOffset < headerSize + nameBytes || (nextOffset & 7) != 0
                            || nextOffset > bufferSize - offset - headerSize)
                        {
                            break;
                        }

                        offset += (int)nextOffset;
                    }

                    failure = "data-stream-enumeration-invalid";
                    return false;
                }
                finally
                {
                    if (!useCachedBuffer)
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }

            failure = "data-stream-enumeration-buffer-limit";
            return false;
        }
        finally
        {
            if (borrowCachedBuffer)
            {
                s_streamInfoBufferInUse = false;
            }
            GC.KeepAlive(cachedBuffer);
        }
    }

    private sealed record StreamAllocation(string Name, long AllocatedBytes);

    private static class NativeMethods
    {
        internal const uint FileReadAttributes = 0x80;
        internal const uint ShareRead = 0x1;
        internal const uint ShareWrite = 0x2;
        internal const uint ShareDelete = 0x4;
        internal const uint OpenExisting = 3;
        internal const uint OpenNoRecall = 0x00100000;
        internal const uint OpenReparsePoint = 0x00200000;
        internal const uint BackupSemantics = 0x02000000;
        internal const int FileStandardInfoClass = 1;
        internal const int FileStreamInfoClass = 7;
        internal const int FileCompressionInfoClass = 8;
        internal const int FileAttributeTagInfoClass = 9;
        internal const int FileIdInfoClass = 18;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileStandardInfo
        {
            internal long AllocationSize;
            internal long EndOfFile;
            internal uint NumberOfLinks;
            internal byte DeletePending;
            internal byte Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdInfo
        {
            internal ulong VolumeSerialNumber;
            internal ulong FileIdLow;
            internal ulong FileIdHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileAttributeTagInfo
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileCompressionInfo
        {
            internal long CompressedFileSize;
            internal ushort CompressionFormat;
            internal byte CompressionUnitShift;
            internal byte ChunkShift;
            internal byte ClusterShift;
            internal byte Reserved1;
            internal byte Reserved2;
            internal byte Reserved3;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetStandardInfo(
            SafeFileHandle handle, int informationClass, out FileStandardInfo information, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIdInfo(
            SafeFileHandle handle, int informationClass, out FileIdInfo information, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetAttributeTagInfo(SafeFileHandle handle, int informationClass,
            out FileAttributeTagInfo information, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCompressionInfo(SafeFileHandle handle, int informationClass,
            out FileCompressionInfo information, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetStreamInfo(SafeFileHandle handle, int informationClass,
            IntPtr information, uint bufferSize);
    }
}

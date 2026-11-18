using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("DiskLens.Tests")]

namespace DiskLens.Core.Scanning;

/// <summary>Directory-entry metadata; allocation is a hint, not an ADS/compression accounting result.</summary>
public sealed record WindowsBackupDirectoryEntry(
    string Name,
    FileAttributes Attributes,
    long LogicalSizeBytes,
    long AllocatedBytes,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    uint ReparseTag,
    string? Identity);

/// <summary>
/// Enumerates an already-authorized backup-intent directory handle without reading
/// contents or following its final reparse point. Ancestor link policy belongs to the caller.
/// A failed read never publishes a partial enumeration.
/// </summary>
public sealed class WindowsBackupDirectoryReader
{
    private const int ErrorInvalidData = 13;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorNotSupported = 50;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorDirectory = 267;
    internal const int ErrorStoppedOnReparsePoint = 681; // ERROR_STOPPED_ON_SYMLINK.
    private const int HeaderSize = 88; // FILE_ID_EXTD_DIR_INFO, before FileName.
    private const int InitialBufferSize = 64 * 1024;
    private const int MaximumBufferSize = 1024 * 1024;

    /// <summary>Reads one object's metadata without enumerating a directory or following its final link.</summary>
    public bool TryReadMetadata(string path, out WindowsBackupDirectoryEntry? metadata,
        out int failureCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        metadata = null;
        failureCode = 0;
        if (!OperatingSystem.IsWindows())
        {
            failureCode = ErrorNotSupported;
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        using var handle = NativeMethods.CreateFile(WindowsReparsePointClassifier.ToNativePath(fullPath),
            0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3,
            0x02000000 | 0x00100000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            failureCode = Marshal.GetLastWin32Error();
            return false;
        }

        if (!NativeMethods.GetBasicInfo(handle, 0, out var basic,
                (uint)Marshal.SizeOf<NativeMethods.FileBasicInfo>())
            || !NativeMethods.GetStandardInfo(handle, 1, out var standard,
                (uint)Marshal.SizeOf<NativeMethods.FileStandardInfo>())
            || !NativeMethods.GetAttributeTagInfo(handle, 9, out var tag,
                (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
        {
            failureCode = Marshal.GetLastWin32Error();
            return false;
        }

        if (standard.EndOfFile < 0 || standard.AllocationSize < 0
            || !TryReadTime(basic.CreationTime, out var created)
            || !TryReadTime(basic.LastWriteTime, out var modified))
        {
            failureCode = ErrorInvalidData;
            return false;
        }

        var identity = NativeMethods.GetIdInfo(handle, 18, out var id,
                (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>()) && (id.Low != 0 || id.High != 0)
            ? $"{id.VolumeSerialNumber:X16}:{id.High:X16}{id.Low:X16}"
            : null;
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        cancellationToken.ThrowIfCancellationRequested();
        metadata = new WindowsBackupDirectoryEntry(string.IsNullOrEmpty(name) ? fullPath : name,
            (FileAttributes)tag.Attributes, standard.EndOfFile, standard.AllocationSize,
            created, modified, tag.ReparseTag, identity);
        return true;
    }

    public bool TryRead(string directoryPath, out IReadOnlyList<WindowsBackupDirectoryEntry> entries,
        out int failureCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        entries = Array.Empty<WindowsBackupDirectoryEntry>();
        failureCode = 0;
        if (!OperatingSystem.IsWindows())
        {
            failureCode = ErrorNotSupported;
            return false;
        }

        var nativePath = WindowsReparsePointClassifier.ToNativePath(Path.GetFullPath(directoryPath));
        using var handle = NativeMethods.CreateFile(nativePath, 0x1 | 0x80,
            0x1 | 0x2 | 0x4, IntPtr.Zero, 3,
            0x02000000 | 0x00100000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            failureCode = Marshal.GetLastWin32Error();
            return false;
        }

        if (!NativeMethods.GetAttributeTagInfo(handle, 9, out var tagInfo,
                (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
        {
            failureCode = Marshal.GetLastWin32Error();
            return false;
        }

        if ((tagInfo.Attributes & (uint)FileAttributes.Directory) == 0)
        {
            failureCode = ErrorDirectory;
            return false;
        }

        if ((tagInfo.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            failureCode = ErrorStoppedOnReparsePoint;
            return false;
        }

        ulong? volumeSerial = NativeMethods.GetIdInfo(handle, 18, out var idInfo,
            (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>()) ? idInfo.VolumeSerialNumber : null;
        var collected = new List<WindowsBackupDirectoryEntry>();
        var bufferSize = InitialBufferSize;
        var restart = true;
        while (true)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var growBuffer = false;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Array.Clear(buffer, 0, bufferSize);
                    var success = NativeMethods.GetDirectoryInfo(handle, restart ? 20 : 19,
                        pinned.AddrOfPinnedObject(), (uint)bufferSize);
                    if (!success)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreFiles)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            entries = collected;
                            return true;
                        }

                        if (error is ErrorInsufficientBuffer or ErrorMoreData && bufferSize < MaximumBufferSize)
                        {
                            // A failed fetch can advance a driver's enumeration cursor.
                            // Restart the entire directory when growing; never drop a page.
                            collected.Clear();
                            restart = true;
                            growBuffer = true;
                            break;
                        }

                        failureCode = error;
                        return false;
                    }

                    if (!TryParseBuffer(buffer.AsSpan(0, bufferSize), volumeSerial,
                            out var page, cancellationToken))
                    {
                        failureCode = ErrorInvalidData;
                        return false;
                    }

                    collected.AddRange(page);
                    restart = false;
                }
            }
            finally
            {
                pinned.Free();
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (growBuffer)
            {
                bufferSize *= 2;
            }
        }
    }

    internal static bool TryParseBuffer(ReadOnlySpan<byte> buffer, ulong? volumeSerial,
        out IReadOnlyList<WindowsBackupDirectoryEntry> entries, CancellationToken cancellationToken = default)
    {
        entries = Array.Empty<WindowsBackupDirectoryEntry>();
        var parsed = new List<WindowsBackupDirectoryEntry>();
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < 0 || offset > buffer.Length - HeaderSize)
            {
                return false;
            }

            var current = buffer[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(current);
            var logical = BinaryPrimitives.ReadInt64LittleEndian(current[40..]);
            var allocation = BinaryPrimitives.ReadInt64LittleEndian(current[48..]);
            var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(current[60..]);
            if (logical < 0 || allocation < 0 || nameBytes == 0 || nameBytes > 65534
                || (nameBytes & 1) != 0 || nameBytes > current.Length - HeaderSize)
            {
                return false;
            }

            var units = new char[nameBytes / 2];
            var nameData = current.Slice(HeaderSize, (int)nameBytes);
            for (var i = 0; i < units.Length; i++)
            {
                units[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(nameData[(i * 2)..]);
                if (units[i] is '\0' or '\\' or '/')
                {
                    return false;
                }
            }

            if (!TryReadTime(current[8..], out var created)
                || !TryReadTime(current[24..], out var modified))
            {
                return false;
            }

            var name = new string(units); // Preserve unpaired UTF-16 surrogates.
            var low = BinaryPrimitives.ReadUInt64LittleEndian(current[72..]);
            var high = BinaryPrimitives.ReadUInt64LittleEndian(current[80..]);
            var identity = volumeSerial.HasValue && (low != 0 || high != 0)
                ? $"{volumeSerial.Value:X16}:{high:X16}{low:X16}"
                : null;
            if (name is not "." and not "..")
            {
                parsed.Add(new WindowsBackupDirectoryEntry(name,
                    (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(current[56..]),
                    logical, allocation, created, modified,
                    BinaryPrimitives.ReadUInt32LittleEndian(current[68..]), identity));
            }

            if (next == 0)
            {
                entries = parsed;
                return true;
            }

            if ((next & 7) != 0 || next < HeaderSize + nameBytes
                || next > current.Length - HeaderSize)
            {
                return false;
            }

            offset += (int)next;
        }
    }

    private static bool TryReadTime(ReadOnlySpan<byte> buffer, out DateTimeOffset? value)
        => TryReadTime(BinaryPrimitives.ReadInt64LittleEndian(buffer), out value);

    private static bool TryReadTime(long fileTime, out DateTimeOffset? value)
    {
        value = null;
        if (fileTime == 0)
        {
            return true;
        }

        try
        {
            value = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileBasicInfo
        {
            internal long CreationTime;
            internal long LastAccessTime;
            internal long LastWriteTime;
            internal long ChangeTime;
            internal uint Attributes;
        }

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
        internal struct FileAttributeTagInfo
        {
            internal uint Attributes;
            internal uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdInfo
        {
            internal ulong VolumeSerialNumber;
            internal ulong Low;
            internal ulong High;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint desiredAccess,
            uint shareMode, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDirectoryInfo(SafeFileHandle handle, int informationClass,
            IntPtr buffer, uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetBasicInfo(SafeFileHandle handle, int informationClass,
            out FileBasicInfo information, uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetStandardInfo(SafeFileHandle handle, int informationClass,
            out FileStandardInfo information, uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetAttributeTagInfo(SafeFileHandle handle, int informationClass,
            out FileAttributeTagInfo information, uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIdInfo(SafeFileHandle handle, int informationClass,
            out FileIdInfo information, uint size);
    }
}

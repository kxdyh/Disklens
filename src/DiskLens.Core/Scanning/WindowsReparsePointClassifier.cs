using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core.Scanning;

/// <summary>Distinguishes local data files from links without following or recalling their contents.</summary>
public static class WindowsReparsePointClassifier
{
    internal const uint WofTag = 0x80000017;
    private const uint CloudTag = 0x9000001A;
    private const uint CloudVariantMask = 0x0000F000;
    private const uint NameSurrogateBit = 0x20000000;

    /// <summary>Unknown or unreadable reparse files are skipped conservatively.</summary>
    public static bool ShouldSkipFile(FileInfo fileInfo)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);
        try
        {
            if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            return !TryReadTag(fileInfo.FullName, out var tag) || ShouldSkipReparseTag(tag);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    /// <summary>Only recognized local-data providers are safe to inspect without following a link.</summary>
    public static bool ShouldSkipReparseTag(uint tag) => !IsKnownNonLinkTag(tag);

    internal static bool IsKnownNonLinkTag(uint tag) =>
        (tag & NameSurrogateBit) == 0
        && (tag == WofTag || (tag & ~CloudVariantMask) == CloudTag);

    internal static bool TryReadTag(string path, out uint tag)
    {
        tag = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var handle = NativeMethods.CreateFile(
            ToNativePath(path), 0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3,
            0x00200000 | 0x00100000 | 0x02000000, IntPtr.Zero); // OPEN_REPARSE_POINT | OPEN_NO_RECALL | BACKUP_SEMANTICS
        if (handle.IsInvalid || !NativeMethods.GetAttributeTagInfo(handle, 9,
                out var information, (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInfo>()))
        {
            return false;
        }

        tag = information.ReparseTag;
        return true;
    }

    internal static string ToNativePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileAttributeTagInfo
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetAttributeTagInfo(SafeFileHandle handle, int informationClass,
            out FileAttributeTagInfo information, uint bufferSize);
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DiskLens.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Tests.Scanning;

public sealed class WindowsBackupPrivilegeScopeTests
{
    [Fact]
    public void Scope_should_enable_only_an_existing_privilege_and_leave_process_and_thread_identity_unchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            using var unsupported = WindowsBackupPrivilegeScope.TryEnable();
            Assert.False(unsupported.IsEnabled);
            Assert.Equal(50, unsupported.FailureCode);
            return;
        }

        if (TryOpenThreadToken(out var preexisting))
        {
            preexisting.Dispose();
            return; // Do not alter a test host's pre-existing impersonation.
        }

        Assert.True(NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x8, out var processToken));
        using (processToken)
        {
            var before = TokenInformation(processToken, 3);
            var ownsBackup = HasBackupPrivilege(before);
            using (var scope = WindowsBackupPrivilegeScope.TryEnable())
            {
                if (ownsBackup)
                {
                    Assert.True(scope.IsEnabled, $"Win32 error {scope.FailureCode}");
                    Assert.Null(scope.FailureCode);
                    Assert.True(TryOpenThreadToken(out var scanToken));
                    using (scanToken) Assert.True(HasBackupPrivilege(TokenInformation(scanToken, 3), requireEnabled: true));
                }
                else
                {
                    Assert.False(scope.IsEnabled);
                    Assert.Equal(1300, scope.FailureCode); // ERROR_NOT_ALL_ASSIGNED, no privilege grant.
                }
            }

            Assert.Equal(before, TokenInformation(processToken, 3));
            Assert.False(TryOpenThreadToken(out var after));
            var afterError = Marshal.GetLastWin32Error();
            after?.Dispose();
            Assert.Equal(1008, afterError); // ERROR_NO_TOKEN.
        }
    }

    [Fact]
    public void Scope_should_not_overwrite_an_existing_thread_impersonation_token()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (TryOpenThreadToken(out var preexisting))
        {
            preexisting.Dispose();
            return;
        }

        Assert.True(NativeMethods.ImpersonateSelf(2));
        try
        {
            Assert.True(TryOpenThreadToken(out var original));
            using (original)
            {
                var originalId = TokenInformation(original, 10).AsSpan(0, 8).ToArray(); // TOKEN_STATISTICS.TokenId.
                using (var scope = WindowsBackupPrivilegeScope.TryEnable())
                {
                    Assert.False(scope.IsEnabled);
                    Assert.Equal(183, scope.FailureCode);
                }

                Assert.True(TryOpenThreadToken(out var restored));
                using (restored)
                    Assert.Equal(originalId, TokenInformation(restored, 10).AsSpan(0, 8).ToArray());
            }
        }
        finally
        {
            Assert.True(NativeMethods.RevertToSelf());
        }
    }

    private static bool TryOpenThreadToken(out SafeAccessTokenHandle token) =>
        NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(), 0x8, true, out token);

    private static byte[] TokenInformation(SafeAccessTokenHandle token, int informationClass)
    {
        NativeMethods.GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var size);
        var pointer = Marshal.AllocHGlobal((int)size);
        try
        {
            Assert.True(NativeMethods.GetTokenInformation(token, informationClass, pointer, size, out _));
            var buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool HasBackupPrivilege(byte[] buffer, bool requireEnabled = false)
    {
        Assert.True(NativeMethods.LookupPrivilegeValue(null, "SeBackupPrivilege", out var backup));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        for (var i = 0; i < count; i++)
        {
            var entry = buffer.AsSpan(4 + i * 12, 12);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) == backup.Low
                && BinaryPrimitives.ReadInt32LittleEndian(entry[4..]) == backup.High)
            {
                return !requireEnabled || (BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) & 2) != 0;
            }
        }
        return false;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid { internal uint Low; internal int High; }

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenThreadToken(IntPtr thread, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImpersonateSelf(int level);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RevertToSelf();
        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
            IntPtr buffer, uint bufferLength, out uint returnLength);
    }
}

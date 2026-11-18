using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core.Scanning;

/// <summary>
/// Enables an existing backup privilege on a private impersonation token for the
/// current synchronous scan thread. This never grants privileges or edits policy.
/// Dispose on the creating thread; do not carry this scope across an await.
/// </summary>
public sealed class WindowsBackupPrivilegeScope : IDisposable
{
    private const int ErrorNotSupported = 50;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorNoToken = 1008;
    private readonly uint _threadId;
    private bool _ownsImpersonation;
    private bool _disposed;

    private WindowsBackupPrivilegeScope(uint threadId, int? failureCode = null)
    {
        _threadId = threadId;
        FailureCode = failureCode;
    }

    public bool IsEnabled { get; private set; }

    /// <summary>Win32 failure; 183 means an existing thread identity was left untouched.</summary>
    public int? FailureCode { get; private set; }

    public static WindowsBackupPrivilegeScope TryEnable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsBackupPrivilegeScope(0, ErrorNotSupported);
        }

        var scope = new WindowsBackupPrivilegeScope(NativeMethods.GetCurrentThreadId());
        // Never overwrite another component's impersonation, even for nested scans.
        if (NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(),
                NativeMethods.TokenQuery, true, out var existing))
        {
            existing.Dispose();
            scope.FailureCode = ErrorAlreadyExists;
            return scope;
        }

        var openError = Marshal.GetLastWin32Error();
        existing?.Dispose();
        if (openError != ErrorNoToken)
        {
            scope.FailureCode = openError;
            return scope;
        }

        if (!NativeMethods.ImpersonateSelf(2)) // SecurityImpersonation
        {
            scope.FailureCode = Marshal.GetLastWin32Error();
            return scope;
        }

        scope._ownsImpersonation = true;
        try
        {
            if (!NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(),
                    NativeMethods.TokenQuery | NativeMethods.TokenAdjustPrivileges, true, out var token))
            {
                scope.FailureCode = Marshal.GetLastWin32Error();
                token?.Dispose();
                return scope;
            }

            using (token)
            {
                if (!NativeMethods.LookupPrivilegeValue(null, "SeBackupPrivilege", out var luid))
                {
                    scope.FailureCode = Marshal.GetLastWin32Error();
                    return scope;
                }

                var privileges = new NativeMethods.TokenPrivileges
                {
                    Count = 1,
                    Luid = luid,
                    Attributes = 2, // SE_PRIVILEGE_ENABLED, never SE_PRIVILEGE_REMOVED.
                };
                var adjusted = NativeMethods.AdjustTokenPrivileges(token, false,
                    ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                var adjustmentError = Marshal.GetLastWin32Error();
                // TRUE can still report ERROR_NOT_ALL_ASSIGNED (1300).
                if (!adjusted || adjustmentError != 0)
                {
                    scope.FailureCode = adjustmentError;
                    return scope;
                }

                scope.IsEnabled = true;
                return scope;
            }
        }
        finally
        {
            if (!scope.IsEnabled)
            {
                scope.RevertOwnedIdentity();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsImpersonation && NativeMethods.GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("Dispose the backup privilege scope on its creating scan thread.");
        }

        RevertOwnedIdentity();
        IsEnabled = false;
        _disposed = true;
    }

    private void RevertOwnedIdentity()
    {
        if (!_ownsImpersonation)
        {
            return;
        }

        // The private token is discarded, so the process token and its privilege
        // attributes were never modified and need no AdjustTokenPrivileges undo.
        if (!NativeMethods.RevertToSelf())
        {
            var revertError = Marshal.GetLastWin32Error();
            if (!NativeMethods.SetThreadToken(IntPtr.Zero, IntPtr.Zero) || !HasNoThreadToken())
            {
                // A catchable exception would let Task.Run return an impersonating
                // worker to the pool. Never continue with an unknown security context.
                Environment.FailFast("Could not restore the scan thread identity.",
                    new Win32Exception(revertError));
            }
        }

        _ownsImpersonation = false;
    }

    private static bool HasNoThreadToken()
    {
        var hasToken = NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(),
            NativeMethods.TokenQuery, true, out var token);
        var error = Marshal.GetLastWin32Error();
        token?.Dispose();
        return !hasToken && error == ErrorNoToken;
    }

    private static class NativeMethods
    {
        internal const uint TokenQuery = 0x8;
        internal const uint TokenAdjustPrivileges = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid
        {
            internal uint LowPart;
            internal int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TokenPrivileges
        {
            internal uint Count;
            internal Luid Luid;
            internal uint Attributes;
        }

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

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

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadToken(IntPtr thread, IntPtr token);

        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState,
            uint bufferLength, IntPtr previousState, IntPtr returnLength);
    }
}

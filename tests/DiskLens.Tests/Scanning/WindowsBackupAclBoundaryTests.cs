using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Tests.Scanning;

/// <summary>
/// Uses only its own temporary fixture. The same test covers an unprivileged token
/// now and the backup-read path when deliberately run from an elevated test host.
/// </summary>
public sealed class WindowsBackupAclBoundaryTests
{
    private static readonly string CacheRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.BackupAcl.Tests");

    [Fact]
    public async Task A_directory_denied_to_the_current_user_should_be_backed_up_or_report_access_denied_without_token_leaks()
    {
        if (!OperatingSystem.IsWindows()) return;
        AssertNoThreadImpersonation();
        bool backupAvailable;
        int? backupFailure;
        using (var probe = WindowsBackupPrivilegeScope.TryEnable())
        {
            backupAvailable = probe.IsEnabled;
            backupFailure = probe.FailureCode;
        }
        AssertNoThreadImpersonation(); // The probe must end before ScanAsync starts.

        var fixture = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
        var restrictedPath = Path.Combine(fixture, "restricted");
        var filePath = Path.Combine(restrictedPath, "ordinary-256k.bin");
        var restricted = new DirectoryInfo(restrictedPath);
        byte[]? originalAcl = null;
        try
        {
            Directory.CreateDirectory(restrictedPath);
            await File.WriteAllBytesAsync(filePath, new byte[256 * 1024]);
            var expectedStorage = new WindowsFileStorageReader().Read(new FileInfo(filePath));
            Assert.True(expectedStorage.AllocatedBytes > 0);
            originalAcl = restricted.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            var deniedAcl = new DirectorySecurity();
            deniedAcl.SetSecurityDescriptorBinaryForm(originalAcl,
                AccessControlSections.Access);
            using var identity = WindowsIdentity.GetCurrent();
            var sid = Assert.IsType<SecurityIdentifier>(identity.User);
            deniedAcl.AddAccessRule(new FileSystemAccessRule(sid,
                FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            restricted.SetAccessControl(deniedAcl);

            // Backup privilege is not enabled here. The deny applies only to this
            // directory, not its child file, siblings or any user-owned real data.
            Assert.Throws<UnauthorizedAccessException>(() =>
                Directory.EnumerateFileSystemEntries(restrictedPath).ToArray());
            // GetFileAttributes may read the parent's cached directory entry,
            // so it is not an access-check oracle for FILE_READ_ATTRIBUTES.
            AssertNoThreadImpersonation();

            if (backupAvailable)
            {
                var direct = await ScanAndVerifyWorkerCleanupAsync(restrictedPath);
                AssertCompleteScan(direct, filePath, expectedStorage.AllocatedBytes);
                var fromParent = await ScanAndVerifyWorkerCleanupAsync(fixture);
                AssertCompleteScan(fromParent, filePath, expectedStorage.AllocatedBytes);
            }
            else
            {
                Assert.Equal(1300, backupFailure); // Token has no SeBackupPrivilege.
                var direct = await ScanAndVerifyWorkerCleanupAsync(restrictedPath);
                AssertDeniedPartialScan(direct, restrictedPath);
                Assert.Equal(restrictedPath, direct.Root.Path);
                var fromParent = await ScanAndVerifyWorkerCleanupAsync(fixture);
                AssertDeniedPartialScan(fromParent, restrictedPath);
            }

            AssertNoThreadImpersonation();
        }
        finally
        {
            // Restore the exact original DACL before any recursive deletion. Owner
            // and audit policy were neither requested nor modified by this test.
            if (originalAcl is not null)
            {
                var restoredAcl = new DirectorySecurity();
                restoredAcl.SetSecurityDescriptorBinaryForm(originalAcl, AccessControlSections.Access);
                restricted.SetAccessControl(restoredAcl);
                Assert.Equal(restoredAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    restricted.GetAccessControl(AccessControlSections.Access)
                        .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            }
            var resolvedFixture = Path.GetFullPath(fixture);
            var resolvedCache = Path.GetFullPath(CacheRoot) + Path.DirectorySeparatorChar;
            Assert.StartsWith(resolvedCache, resolvedFixture, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolvedFixture)) Directory.Delete(resolvedFixture, recursive: true);
        }
    }

    private static void AssertCompleteScan(ScanResult result, string filePath, long allocatedBytes)
    {
        Assert.True(result.BackupReadEnabled);
        Assert.False(result.IsPartial);
        Assert.Empty(result.Issues);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(256 * 1024, result.LogicalBytesDiscovered);
        Assert.Equal(allocatedBytes, result.BytesDiscovered);
        Assert.Equal(allocatedBytes, result.Root.SizeBytes);
        Assert.Equal(filePath, Assert.Single(result.Root.EnumerateFiles()).Path);
    }

    private static void AssertDeniedPartialScan(ScanResult result, string restrictedPath)
    {
        Assert.False(result.BackupReadEnabled);
        Assert.Equal(1300, result.BackupReadFailureCode);
        Assert.True(result.IsPartial);
        Assert.Equal(1, result.ScanIssueCount);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(restrictedPath, issue.Path);
        Assert.True(issue.IsDirectory);
        Assert.Equal(5, issue.NativeErrorCode);
        Assert.Contains(result.Root.DescendantsAndSelf(), node =>
            node.Path == restrictedPath && node.RiskTags.Contains("scan-error"));
        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(0, result.BytesDiscovered);
    }

    private static async Task<ScanResult> ScanAndVerifyWorkerCleanupAsync(string root)
    {
        using var completionRegistered = new ManualResetEventSlim();
        var progress = new CompletionGateProgress(completionRegistered);
        var scan = new DirectoryScanner().ScanAsync(new ScanSessionOptions(root), progress);
        var verified = scan.ContinueWith(completed =>
        {
            if (progress.WorkerThreadId != 0)
            {
                Assert.Equal(progress.WorkerThreadId, NativeMethods.GetCurrentThreadId());
            }
            AssertNoThreadImpersonation();
            return completed.GetAwaiter().GetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // The first progress callback holds the worker until this synchronous
        // completion check is registered, then checks after ScanInternal's using.
        completionRegistered.Set();
        return await verified;
    }

    private sealed class CompletionGateProgress(ManualResetEventSlim completionRegistered) : IProgress<ScanProgressSnapshot>
    {
        public uint WorkerThreadId { get; private set; }

        public void Report(ScanProgressSnapshot value)
        {
            WorkerThreadId = NativeMethods.GetCurrentThreadId();
            if (!completionRegistered.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The scan completion token check was not registered.");
        }
    }

    private static void AssertNoThreadImpersonation()
    {
        var opened = NativeMethods.OpenThreadToken(NativeMethods.GetCurrentThread(), 0x8, true, out var token);
        var error = Marshal.GetLastWin32Error();
        token?.Dispose();
        Assert.False(opened, "A scan or privilege probe leaked its impersonation token.");
        Assert.Equal(1008, error); // ERROR_NO_TOKEN.
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenThreadToken(IntPtr thread, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);
    }
}

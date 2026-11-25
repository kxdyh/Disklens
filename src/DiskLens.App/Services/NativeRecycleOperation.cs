using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DiskLens.Core.Policies;
using static DiskLens.App.Services.NativeRecycleInterop;

[assembly: InternalsVisibleTo("DiskLens.App.Tests")]

namespace DiskLens.App.Services;

public static class NativeRecycleOperation
{
    // This entry point runs only in the per-operation worker's STA thread.
    public static FileOperationResult Execute(FileOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            return new(FileOperationStatus.Failed, "文件操作必须运行在独立的 STA 工作线程中。");

        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathFullyQualified(request.Path))
                return new(FileOperationStatus.Failed, "文件路径必须为完整的绝对路径。");

            var path = Path.GetFullPath(request.Path);
            var policy = new DeletionPolicyService(new WindowsLocationPolicy()).Evaluate(path, request.IsDirectory);
            if (!policy.CanRecycle)
                return new(FileOperationStatus.Failed, policy.Reason);

            ThrowIfFailed(SHCreateItemFromParsingName(path, IntPtr.Zero, ShellItemInterfaceId, out item));
            var operationType = Type.GetTypeFromCLSID(FileOperationClassId, throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(operationType)!;
            ThrowIfFailed(operation.SetOperationFlags(GetOperationFlags()));
            ThrowIfFailed(operation.SetOwnerWindow(new IntPtr(request.OwnerWindowHandle)));
            var sink = new DeleteProgressSink(item, path);
            // The item-specific sink receives completion HRESULTs; queue success alone
            // never means that an item was actually recycled.
            ThrowIfFailed(operation.DeleteItem(item, sink));
            var performResult = operation.PerformOperations();
            var abortedResult = operation.GetAnyOperationsAborted(out var aborted);
            GC.KeepAlive(sink);
            return sink.State.Complete(performResult, abortedResult < 0 || aborted, InspectSourcePath(path));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(FileOperationStatus.Failed, exception.Message, exception.HResult);
        }
        finally
        {
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }

    // Windows owns progress, retry/skip/cancel, elevation and permanent-delete
    // confirmation. No silent, error-suppression or automatic-Yes flags are set.
    internal static uint GetOperationFlags() => RecycleOnDelete | AddUndoRecord | WantNukeWarning;

    // The copy engine can complete a directory move as one operation, then signal
    // that its children must not be processed separately. Pending/retry/ignored
    // HRESULTs are deliberately excluded even though they are non-negative.
    internal static bool IsCompletedDeleteStatus(int result) => result is
        0 or CopyEngineYes or DontProcessChildren or AlreadyDone;

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    internal enum SourcePathState { Missing, Present, Unknown }

    private static SourcePathState InspectSourcePath(string path)
    {
        try { _ = File.GetAttributes(path); return SourcePathState.Present; }
        catch (FileNotFoundException) { return SourcePathState.Missing; }
        catch (DirectoryNotFoundException) { return SourcePathState.Missing; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // File.Exists would also return false on access/network errors.
            // Those errors cannot prove that the selected item was removed.
            return SourcePathState.Unknown;
        }
    }

    internal sealed class DeleteProgressState
    {
        private int? _topLevelResult;
        private int? _lastItemFailure;
        private int? _finishResult;

        // A native confirmation may authorize permanent deletion. The observer
        // must not veto or replace that decision with an application dialog.
        internal int BeforeDelete(uint flags) => 0;

        internal void AfterDelete(int result, bool isTopLevelItem)
        {
            if (isTopLevelItem) _topLevelResult = result;
            if (!IsCompletedDeleteStatus(result)) _lastItemFailure = result;
        }

        internal void Finish(int result) => _finishResult = result;

        internal FileOperationResult Complete(int performResult, bool aborted, SourcePathState sourcePath)
        {
            var overallFailure = !IsCompletedDeleteStatus(performResult) ? performResult
                : _finishResult is { } finish && !IsCompletedDeleteStatus(finish) ? finish : (int?)null;
            var topLevelFailure = _topLevelResult is { } result && !IsCompletedDeleteStatus(result) ? result : (int?)null;
            var failure = overallFailure ?? topLevelFailure ?? _lastItemFailure;
            if (IsCancellation(overallFailure) || IsCancellation(topLevelFailure) || aborted)
                return new(FileOperationStatus.Cancelled, "Windows 文件操作已取消或跳过，未完成的内容已保留。", failure);

            // The selected item must have a real, final success callback and
            // actually be gone. Child successes or a queued operation are not
            // sufficient. A successful retry replaces earlier transient errors.
            if (overallFailure is null && _topLevelResult is { } completed
                && IsCompletedDeleteStatus(completed) && sourcePath == SourcePathState.Missing)
                return new(FileOperationStatus.Completed);

            if (failure is { } error)
                return new(FileOperationStatus.Failed, DescribeError(error), error);
            return new(FileOperationStatus.Failed, "Windows 未确认完整删除，请刷新后检查剩余项目。");
        }

        private static bool IsCancellation(int? result) => result is UserCancelled or EngineCancelled or Abort or UserIgnored;

        private static string DescribeError(int result)
        {
            var message = Marshal.GetExceptionForHR(result)?.Message ?? $"Windows 文件操作失败（0x{result:X8}）。";
            return message + " 如已完成部分操作，请刷新后检查剩余内容。";
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeleteProgressSink(IShellItem? targetItem, string targetPath) : IFileOperationProgressSink
    {
        internal DeleteProgressState State { get; } = new();
        public int StartOperations() => 0;
        public int FinishOperations(int result) { State.Finish(result); return 0; }
        public int PreDeleteItem(uint flags, IShellItem item) => State.BeforeDelete(flags);
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem)
        {
            State.AfterDelete(result, IsTopLevelItem(item));
            // Returning a failure here would cancel later native operations.
            // Shell must retain control of its own retry, skip and cancel flow.
            return 0;
        }

        private bool IsTopLevelItem(IShellItem item)
        {
            IntPtr name = IntPtr.Zero;
            try
            {
                if (ReferenceEquals(item, targetItem)) return true;
                if (targetItem is not null && item.Compare(targetItem, CanonicalComparison, out var order) >= 0 && order == 0)
                    return true;
                if (item.GetDisplayName(FileSystemPathName, out name) < 0) return false;
                var path = Marshal.PtrToStringUni(name);
                return string.Equals(path?.TrimEnd('\\', '/'), targetPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (COMException) { return false; }
            finally { if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name); }
        }
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string? templateName, uint attributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}

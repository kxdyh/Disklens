using System.Runtime.InteropServices;
using DiskLens.App.Services;
using static DiskLens.App.Services.NativeRecycleInterop;
using PathState = DiskLens.App.Services.NativeRecycleOperation.SourcePathState;

namespace DiskLens.App.Tests;

public sealed class NativeRecycleOperationTests
{
    private const int SharingViolation = unchecked((int)0x80270028);

    [Fact]
    public void NativeDialogsAreEnabledAndNeverAutomaticallyApproved()
    {
        var flags = NativeRecycleOperation.GetOperationFlags();
        Assert.NotEqual(0u, flags & RecycleOnDelete);
        Assert.NotEqual(0u, flags & AddUndoRecord);
        Assert.NotEqual(0u, flags & WantNukeWarning);
        Assert.Equal(0u, flags & (Silent | NoErrorUi | EarlyFailure | NoConfirmation));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(DeleteRecycleIfPossible)]
    [InlineData(uint.MaxValue)]
    public void PreDeleteObserverDoesNotVetoWindowsOwnConfirmation(uint flags)
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        Assert.Equal(0, state.BeforeDelete(flags));
    }

    [Fact]
    public void QueueSuccessOrChildSuccessCannotProveSelectedItemWasDeleted()
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        Assert.Equal(FileOperationStatus.Failed, state.Complete(0, false, PathState.Missing).Status);
        state.AfterDelete(0, isTopLevelItem: false);
        Assert.Equal(FileOperationStatus.Failed, state.Complete(0, false, PathState.Missing).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CopyEngineYes)]
    [InlineData(DontProcessChildren)]
    [InlineData(AlreadyDone)]
    public void RealTopLevelCompletionAndMissingSourceConfirmWindowsFinished(int result)
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(result, isTopLevelItem: true);
        state.Finish(0);
        // Both recycling and a permanent delete confirmed in Windows are final
        // deletions. The observer does not require a new Recycle Bin item.
        Assert.Equal(FileOperationStatus.Completed, state.Complete(0, false, PathState.Missing).Status);
    }

    [Theory]
    [InlineData((int)PathState.Present)]
    [InlineData((int)PathState.Unknown)]
    public void RemainingOrUnreadableSourceCannotRemoveTheEntireNode(int path)
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(0, isTopLevelItem: true);
        Assert.Equal(FileOperationStatus.Failed, state.Complete(0, false, (PathState)path).Status);
    }

    [Theory]
    [InlineData(SharingViolation)]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(RecycleSizeTooBig)]
    [InlineData(RecycleBinNotFound)]
    [InlineData(0x0027000B)] // Pending is not a completed deletion.
    [InlineData(0x00270010)] // Pending-delete is not completion either.
    public void UnresolvedTopLevelResultCannotBecomeSuccessOrAnAppFallback(int result)
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(result, isTopLevelItem: true);
        Assert.Equal(FileOperationStatus.Failed, state.Complete(0, false, PathState.Present).Status);
    }

    [Fact]
    public void WindowsRetrySuccessReplacesAnEarlierTransientFailure()
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(SharingViolation, isTopLevelItem: true);
        state.AfterDelete(SharingViolation, isTopLevelItem: false);
        state.AfterDelete(0, isTopLevelItem: true);
        state.Finish(0);
        Assert.Equal(FileOperationStatus.Completed, state.Complete(0, false, PathState.Missing).Status);
    }

    [Fact]
    public void PartialChildSuccessAndRemainingSourceDoNotRemoveTheNode()
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(0, isTopLevelItem: false);
        state.AfterDelete(SharingViolation, isTopLevelItem: false);
        Assert.NotEqual(FileOperationStatus.Completed, state.Complete(0, false, PathState.Present).Status);
    }

    [Theory]
    [InlineData(UserCancelled)]
    [InlineData(EngineCancelled)]
    [InlineData(Abort)]
    [InlineData(UserIgnored)]
    public void WindowsCancelAndSkipKeepTheNode(int result)
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(result, isTopLevelItem: true);
        Assert.Equal(FileOperationStatus.Cancelled, state.Complete(0, false, PathState.Present).Status);
    }

    [Fact]
    public void AbortedOverallOperationCannotClearAnEntireNode()
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(0, isTopLevelItem: true);
        Assert.Equal(FileOperationStatus.Cancelled, state.Complete(0, true, PathState.Missing).Status);
    }

    [Fact]
    public void FinalOverallFailureCannotBeHiddenByAnEarlierSuccessfulCallback()
    {
        var state = new NativeRecycleOperation.DeleteProgressState();
        state.AfterDelete(0, isTopLevelItem: true);
        state.Finish(SharingViolation);
        Assert.Equal(FileOperationStatus.Failed, state.Complete(0, false, PathState.Missing).Status);
    }

    [Fact]
    public void RootAndRelativePathsAreRejectedWithoutShellOperations()
    {
        var results = OnSta(() => new[]
        {
            NativeRecycleOperation.Execute(new(@"C:\", true)),
            NativeRecycleOperation.Execute(new(@"\\server\share\", true)),
            NativeRecycleOperation.Execute(new("relative.txt", false)),
        });
        Assert.All(results, result => Assert.Equal(FileOperationStatus.Failed, result.Status));
    }

    [Fact]
    public void SinkOnlyObservesNativeResultsAndAcceptsWindowsConfirmedPermanentDeletion()
    {
        var path = @"C:\DiskLensNativeObserverTests\target";
        var target = new TestShellItem(path);
        var (sink, state) = CreateSink(target, path);
        Assert.Equal(0, sink.PreDeleteItem(0, target));
        Assert.Equal(0, sink.PostDeleteItem(0, target, SharingViolation, null));
        Assert.Equal(0, sink.PostDeleteItem(0, target, 0, null));
        Assert.Equal(FileOperationStatus.Completed, state.Complete(0, false, PathState.Missing).Status);
    }

    [Fact]
    public void SinkDistinguishesAChildCallbackFromTheSelectedItem()
    {
        var path = @"C:\DiskLensNativeObserverTests\target";
        var (sink, state) = CreateSink(new TestShellItem(path), path);
        Assert.Equal(0, sink.PostDeleteItem(0, new TestShellItem(path + @"\child"), 0, null));
        Assert.Equal(FileOperationStatus.Failed, state.Complete(0, false, PathState.Missing).Status);
        // A fresh wrapper for the same canonical item is also recognized.
        Assert.Equal(0, sink.PostDeleteItem(0, new TestShellItem(path), 0, null));
        Assert.Equal(FileOperationStatus.Completed, state.Complete(0, false, PathState.Missing).Status);
    }

    [Fact]
    public void SinkCanBeExposedToNativeShellWithoutAnyFileOperation() => OnSta(() =>
    {
        var (sink, _) = CreateSink(new TestShellItem(@"C:\DiskLensNativeObserverTests\target"),
            @"C:\DiskLensNativeObserverTests\target");
#pragma warning disable SYSLIB1099
        var pointer = Marshal.GetComInterfaceForObject(sink, typeof(IFileOperationProgressSink));
#pragma warning restore SYSLIB1099
        Assert.NotEqual(IntPtr.Zero, pointer);
        Marshal.Release(pointer);
        return true;
    });

    private static (IFileOperationProgressSink Sink, NativeRecycleOperation.DeleteProgressState State)
        CreateSink(IShellItem target, string path)
    {
        var type = typeof(NativeRecycleOperation).GetNestedType("DeleteProgressSink",
            System.Reflection.BindingFlags.NonPublic)!;
        var sink = (IFileOperationProgressSink)Activator.CreateInstance(type, new object[] { target, path })!;
        var state = (NativeRecycleOperation.DeleteProgressState)type.GetProperty("State",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sink)!;
        return (sink, state);
    }

    private sealed class TestShellItem(string path) : IShellItem
    {
        public int BindToHandler(IntPtr bindContext, in Guid handler, in Guid interfaceId, out IntPtr result)
        { result = IntPtr.Zero; return unchecked((int)0x80004001); }
        public int GetParent(out IShellItem parent) { parent = null!; return unchecked((int)0x80004001); }
        public int GetDisplayName(uint displayName, out IntPtr name)
        { name = Marshal.StringToCoTaskMemUni(path); return 0; }
        public int GetAttributes(uint mask, out uint attributes) { attributes = 0; return 0; }
        public int Compare(IShellItem other, uint hint, out int order)
        {
            order = other is TestShellItem item ? string.Compare(path, item.Path, StringComparison.OrdinalIgnoreCase) : 1;
            return 0;
        }
        private string Path => path;
    }

    private static T OnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native observer test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
}

using System.IO;
using DiskLens.App.Services;

namespace DiskLens.App.Tests;

public sealed class RecycleBinServiceTests
{
    [Fact]
    public async Task Different_items_start_together_but_the_same_path_and_its_parent_or_child_are_rejected()
    {
        var transport = new ControlledTransport();
        var service = new RecycleBinService(transport);
        var first = service.RecycleAsync(@"C:\test\large", true);
        var second = service.RecycleAsync(@"C:\test\other", true);
        Assert.Equal(2, transport.Calls.Count);
        Assert.True(service.IsPathBusy(@"C:\test\large\nested"));
        Assert.True(service.IsPathBusy(@"C:\test"));
        Assert.False(service.IsPathBusy(@"C:\test\larger"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecycleAsync(@"c:\TEST\large\", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecycleAsync(@"C:\test", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecycleAsync(@"C:\test\large\nested", true));
        Assert.Equal(2, transport.Calls.Count);
        transport.Calls[1].Completion.SetResult(new(FileOperationStatus.Completed));
        await second;
        Assert.True(service.IsPathBusy(@"C:\test\large"));
        Assert.False(service.IsPathBusy(@"C:\test\other"));
        transport.Calls[0].Completion.SetResult(new(FileOperationStatus.Completed));
        await first;
        Assert.False(service.IsPathBusy(@"C:\test"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80270028))]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x80270037))]
    public async Task Windows_failure_never_submits_another_delete_request(int errorCode)
    {
        var transport = new ControlledTransport();
        var service = new RecycleBinService(transport);
        var task = service.RecycleAsync(@"C:\test\large", true);
        transport.Calls[0].Completion.SetResult(new(FileOperationStatus.Failed, "Windows operation failed.", errorCode));
        await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Single(transport.Calls);
        Assert.False(service.IsPathBusy(@"C:\test\large"));
    }

    [Theory]
    [InlineData(FileOperationStatus.Failed)]
    [InlineData((FileOperationStatus)99)]
    public async Task Ordinary_errors_and_invalid_success_results_do_not_offer_permanent_deletion(FileOperationStatus status)
    {
        var transport = new ControlledTransport();
        var service = new RecycleBinService(transport);
        var task = service.RecycleAsync(@"C:\test\item", false);
        transport.Calls[0].Completion.SetResult(new(status, "Access denied."));
        await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Single(transport.Calls);
        Assert.False(service.IsPathBusy(@"C:\test\item"));
    }

    [Fact]
    public async Task Failure_or_cancellation_releases_only_its_own_pending_path()
    {
        var transport = new ControlledTransport();
        var service = new RecycleBinService(transport);
        var changes = 0;
        service.StateChanged += (_, _) => Interlocked.Increment(ref changes);
        var first = service.RecycleAsync(@"C:\test\first", false);
        var second = service.RecycleAsync(@"C:\test\second", false);
        transport.Calls[0].Completion.SetException(new IOException("Worker exited."));
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.False(service.IsPathBusy(@"C:\test\first"));
        Assert.True(service.IsPathBusy(@"C:\test\second"));
        transport.Calls[1].Completion.SetResult(new(FileOperationStatus.Cancelled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        Assert.Equal(4, changes);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\")]
    public async Task File_system_roots_never_start_a_worker(string path)
    {
        var transport = new ControlledTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecycleBinService(transport).RecycleAsync(path, true));
        Assert.Empty(transport.Calls);
    }

    private sealed class ControlledTransport : IFileOperationTransport
    {
        public List<(FileOperationRequest Request, TaskCompletionSource<FileOperationResult> Completion)> Calls { get; } = [];
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((request, completion));
            return completion.Task;
        }
    }
}

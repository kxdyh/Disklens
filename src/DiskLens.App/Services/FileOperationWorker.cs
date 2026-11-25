using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.App.Services;

public static class FileOperationWorker
{
    public static bool IsWorkerInvocation(string[] args) => args.Length > 0 && args[0] == ProcessFileOperationTransport.WorkerArgument;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || !args[1].StartsWith("DiskLens.FileOperation.", StringComparison.Ordinal)
            || !int.TryParse(args[2], out var parentId)) return 2;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualParent) || actualParent != parentId) return 3;
            var request = await FileOperationWire.ReadAsync<FileOperationRequest>(pipe, startup.Token).ConfigureAwait(false);
            var completion = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(NativeRecycleOperation.Execute(request)); }
                catch (Exception ex) { completion.SetResult(new(FileOperationStatus.Failed, ex.Message, ex.HResult)); }
            }) { IsBackground = false, Name = "DiskLens Shell file operation" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var result = await completion.Task.ConfigureAwait(false);
            result = result with { WorkerProcessId = Environment.ProcessId };
            await FileOperationWire.WriteAsync(pipe, result).ConfigureAwait(false);
            return 0;
        }
        catch { return 4; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

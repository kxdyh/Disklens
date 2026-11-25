using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.App.Services;

/// <summary>Each item owns an independent Shell process, not a shared deletion queue.</summary>
public sealed class ProcessFileOperationTransport : IFileOperationTransport
{
    internal const string WorkerArgument = "--disklens-file-operation";

    public async Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { OwnerWindowHandle = GetOwnerWindowHandle() };
        var pipeName = "DiskLens.FileOperation." + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Application executable not found."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Unable to start file operation process.");
        var sent = false;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(45));
            var connection = pipe.WaitForConnectionAsync(handshake.Token);
            var exited = process.WaitForExitAsync(handshake.Token);
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) == exited && !pipe.IsConnected)
                throw new IOException("File operation process exited before connecting.");
            await connection.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != process.Id)
                throw new IOException("Unexpected file operation process connected.");
            cancellationToken.ThrowIfCancellationRequested();
            // A cancelled flush can still have delivered the whole request. From
            // the first write onward, never kill a worker that may be mutating.
            sent = true;
            await FileOperationWire.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            // Once Windows starts a mutation, do not kill it midway on UI cancellation.
            return await FileOperationWire.ReadAsync<FileOperationResult>(pipe).ConfigureAwait(false);
        }
        finally
        {
            if (!sent && !process.HasExited)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
            }
        }
    }

    private static long GetOwnerWindowHandle()
    {
        var application = Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted) return 0;
        long ReadHandle() => application.MainWindow is { } window
            ? new WindowInteropHelper(window).Handle.ToInt64() : 0;
        return application.Dispatcher.CheckAccess()
            ? ReadHandle() : application.Dispatcher.Invoke(ReadHandle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

internal static class FileOperationWire
{
    private const int MaximumMessageBytes = 131072;

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new IOException("File operation message is too large.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new IOException("Invalid file operation message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("Invalid file operation message.");
    }
}

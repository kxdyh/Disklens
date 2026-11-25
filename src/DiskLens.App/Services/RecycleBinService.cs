using System.IO;

namespace DiskLens.App.Services;

public sealed class RecycleBinService : IRecycleBinService
{
    private readonly IFileOperationTransport _transport;
    private readonly object _sync = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public RecycleBinService() : this(new ProcessFileOperationTransport()) { }
    public RecycleBinService(IFileOperationTransport transport) => _transport = transport;
    public event EventHandler? StateChanged;

    public bool IsPathBusy(string path)
    {
        var normalized = Normalize(path);
        lock (_sync) return _pending.Any(active => Overlaps(active, normalized));
    }

    public async Task RecycleAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        var decision = new Core.Policies.DeletionPolicyService(new Core.Policies.WindowsLocationPolicy()).Evaluate(normalized, isDirectory);
        if (!decision.CanRecycle) throw new InvalidOperationException(decision.Reason);
        lock (_sync)
        {
            if (_pending.Any(active => Overlaps(active, normalized)))
                throw new InvalidOperationException(Localization.L.T("该项目或其父子目录正在处理，请等待该任务完成。"));
            _pending.Add(normalized);
        }
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            var result = await _transport.ExecuteAsync(new(normalized, isDirectory), cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case FileOperationStatus.Completed:
                    return;
                case FileOperationStatus.Cancelled:
                    throw new OperationCanceledException(Localization.L.T("已取消文件操作。"));
                default:
                    throw new IOException(result.ErrorMessage ?? Localization.L.T("Windows 未能完成文件操作。"));
            }
        }
        finally
        {
            lock (_sync) _pending.Remove(normalized);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Overlaps(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

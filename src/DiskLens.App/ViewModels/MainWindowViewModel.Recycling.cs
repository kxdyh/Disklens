using System.IO;
using System.Windows.Threading;
using DiskLens.App.Services;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<string> _pendingRecyclePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _localRemovalGate = new(1, 1);
    private Dispatcher? _recycleDispatcher;

    public int PendingRecycleCount => _pendingRecyclePaths.Count;
    private bool HasPendingRecycles => PendingRecycleCount > 0;

    private void InitializeRecycling()
    {
        _recycleDispatcher = Dispatcher.CurrentDispatcher;
        _recycleBinService.StateChanged += RecycleService_OnStateChanged;
    }

    private void RecycleService_OnStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = _recycleDispatcher;
        if (_isDisposed || dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) RaiseCommandStateChanged();
        else _ = dispatcher.BeginInvoke(new Action(() => { if (!_isDisposed) RaiseCommandStateChanged(); }));
    }

    private static string NormalizeRecyclePath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private bool IsPendingRecyclePath(string path)
    {
        var target = NormalizeRecyclePath(path);
        return _pendingRecyclePaths.Any(pending => string.Equals(target, pending, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || pending.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyRecycleState()
    {
        OnPropertyChanged(nameof(PendingRecycleCount));
        RaiseCommandStateChanged();
        if (!HasPendingRecycles) _directoryChangeMonitor?.ResumePending();
    }

    private async Task RecycleAsync(object? parameter)
    {
        var node = ResolveTargetNode(parameter);
        if (node is null || !CanRecycleNode(node)) return;
        var path = NormalizeRecyclePath(node.Path);
        _pendingRecyclePaths.Add(path);
        _automaticRefreshCancellation?.Cancel();
        NotifyRecycleState();
        try
        {
            // Windows owns confirmation, progress, retry/skip and any permanent
            // deletion decision. Submit one Shell operation and wait for its result.
            StatusText = L.F($"Windows 正在处理文件：{node.Name}（进行中 {PendingRecycleCount} 项）");
            await _recycleBinService.RecycleAsync(node.Path, node.IsDirectory).ConfigureAwait(true);

            if (_isDisposed) return;
            var removed = await ApplyLocalRemovalAsync(node).ConfigureAwait(true);
            if (_isDisposed) return;
            StatusText = removed
                ? L.F($"Windows 文件操作已完成，并已从当前视图移除：{node.Name}")
                : L.F($"Windows 文件操作已完成，但未能在当前视图中定位该节点：{node.Name}");
        }
        catch (OperationCanceledException)
        {
            if (!_isDisposed) StatusText = L.T("文件操作已取消，未完成的内容已保留。");
        }
        catch (Exception ex)
        {
            if (!_isDisposed) StatusText = L.F($"文件操作未完成：{ex.Message}");
        }
        finally
        {
            _pendingRecyclePaths.Remove(path);
            if (!_isDisposed) NotifyRecycleState();
        }
    }
}

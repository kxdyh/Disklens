using System.Collections.ObjectModel;
using System.IO;
using DiskLens.App.Infrastructure;
using DiskLens.App.Models;
using DiskLens.App.Services;

namespace DiskLens.App.ViewModels;

/// <summary>Owns task lifetime and scan slots. All task mutations run on the UI context.</summary>
public sealed class ScanWorkspaceViewModel : ObservableObject, IDisposable
{
    public const int MaxConcurrentScans = 3;
    private readonly Func<AppSettings, string, MainWindowViewModel> _viewModelFactory;
    private readonly Func<MainWindowViewModel, string, CancellationToken, Task> _scanOperation;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSecretStore _secretStore;
    private readonly SemaphoreSlim _settingsWriteGate = new(1, 1);
    private readonly LinkedList<ScanTaskTabViewModel> _queuedTabs = new();
    private readonly Dictionary<ScanTaskTabViewModel, CancellationTokenSource> _taskCancellation = new();
    private readonly object _settingsSync = new();
    private (AppSettings Settings, string ApiKey) _sharedSettings;
    private ScanTaskTabViewModel _activeTab;
    private int _activeScanCount;
    private bool _isDrainingQueue;
    private bool _isDisposed;

    public ScanWorkspaceViewModel(
        Func<AppSettings, string, MainWindowViewModel> viewModelFactory,
        AppSettings settings,
        string apiKey,
        AppSettingsStore settingsStore,
        AppSecretStore secretStore,
        Func<MainWindowViewModel, string, CancellationToken, Task>? scanOperation = null)
    {
        _viewModelFactory = viewModelFactory;
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _sharedSettings = (settings, apiKey);
        _scanOperation = scanOperation ?? ((viewModel, path, token) => viewModel.RunOwnedScanAsync(path, token));
        HomeViewModel = viewModelFactory(settings, apiKey);
        HomeViewModel.AttachWorkspace(this, isHome: true);
        _activeTab = new ScanTaskTabViewModel(HomeViewModel, string.Empty, isHome: true) { IsSelected = true };
        TaskTabs = [_activeTab];
        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is ScanTaskTabViewModel tab) SelectTab(tab);
        });
        CloseTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is ScanTaskTabViewModel tab) CloseTab(tab);
        }, parameter => parameter is ScanTaskTabViewModel { CanClose: true });
    }

    public ObservableCollection<ScanTaskTabViewModel> TaskTabs { get; }
    public MainWindowViewModel HomeViewModel { get; }
    public ScanTaskTabViewModel ActiveTab => _activeTab;
    public MainWindowViewModel ActiveViewModel => _activeTab.ViewModel;
    public int ActiveScanCount => _activeScanCount;
    public int QueuedScanCount => _queuedTabs.Count;
    public RelayCommand SelectTabCommand { get; }
    public RelayCommand CloseTabCommand { get; }

    public Task StartScanAsync(string path, MainWindowViewModel? guideSource = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(L.T("所选扫描位置已不可用。"));
        }

        (AppSettings Settings, string ApiKey) shared;
        lock (_settingsSync)
        {
            _sharedSettings = (_sharedSettings.Settings with { RootPath = path }, _sharedSettings.ApiKey);
            shared = _sharedSettings;
        }
        var viewModel = _viewModelFactory(shared.Settings, shared.ApiKey);
        viewModel.AttachWorkspace(this, isHome: false);
        viewModel.PrepareQueuedScan(path);
        viewModel.ContinueScanGuide(guideSource?.TakePendingScanGuide() ?? false);
        var tab = new ScanTaskTabViewModel(viewModel, path, isHome: false);
        TaskTabs.Add(tab);
        _taskCancellation.Add(tab, new CancellationTokenSource());
        _queuedTabs.AddLast(tab);
        SelectTab(tab);
        OnPropertyChanged(nameof(QueuedScanCount));
        _ = PersistSettingsAsync();
        DrainQueue();
        return Task.CompletedTask;
    }

    public void SelectTab(ScanTaskTabViewModel tab)
    {
        if (_isDisposed || !TaskTabs.Contains(tab) || ReferenceEquals(tab, _activeTab)) return;
        _activeTab.ViewModel.IsScanNavigationNoticeOpen = false;
        _activeTab.IsSelected = false;
        _activeTab = tab;
        _activeTab.IsSelected = true;
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveViewModel));
    }

    public void CloseTab(ScanTaskTabViewModel tab)
    {
        if (_isDisposed || tab.IsHome || !TaskTabs.Contains(tab)) return;
        var index = TaskTabs.IndexOf(tab);
        if (ReferenceEquals(tab, _activeTab))
        {
            SelectTab(TaskTabs[index > 0 ? index - 1 : index + 1]);
        }
        TaskTabs.Remove(tab);
        CancelTab(tab);
        tab.Dispose();
        tab.ViewModel.Dispose();
    }

    public void CancelTask(MainWindowViewModel viewModel)
    {
        var tab = TaskTabs.FirstOrDefault(item => ReferenceEquals(item.ViewModel, viewModel));
        if (tab is not null) CancelTab(tab);
    }

    private void CancelTab(ScanTaskTabViewModel tab)
    {
        if (!_taskCancellation.TryGetValue(tab, out var cancellation)) return;
        cancellation.Cancel();
        if (_queuedTabs.Remove(tab))
        {
            _taskCancellation.Remove(tab);
            tab.State = ScanTaskState.Canceled;
            tab.ViewModel.FinishScheduledScan(ScanTaskState.Canceled);
            cancellation.Dispose();
            OnPropertyChanged(nameof(QueuedScanCount));
            tab.Complete();
        }
        // Running work keeps its slot until it acknowledges cancellation.
    }

    private void DrainQueue()
    {
        if (_isDisposed || _isDrainingQueue) return;
        _isDrainingQueue = true;
        try
        {
            while (_activeScanCount < MaxConcurrentScans && _queuedTabs.First is { } node)
            {
                var tab = node.Value;
                _queuedTabs.RemoveFirst();
                _activeScanCount++;
                tab.State = ScanTaskState.Scanning;
                tab.ViewModel.BeginScheduledScan();
                NotifyCountsChanged();
                _ = RunTabAsync(tab, _taskCancellation[tab]);
            }
        }
        finally
        {
            _isDrainingQueue = false;
        }
    }

    private async Task RunTabAsync(ScanTaskTabViewModel tab, CancellationTokenSource cancellation)
    {
        var state = ScanTaskState.Completed;
        string? error = null;
        try
        {
            await _scanOperation(tab.ViewModel, tab.Path, cancellation.Token).ConfigureAwait(true);
            state = cancellation.IsCancellationRequested ? ScanTaskState.Canceled : tab.ViewModel.LastScanOutcome;
        }
        catch (OperationCanceledException)
        {
            state = ScanTaskState.Canceled;
        }
        catch (Exception ex)
        {
            state = ScanTaskState.Failed;
            error = ex.Message;
        }
        finally
        {
            tab.ViewModel.FinishScheduledScan(state, error);
            tab.State = state;
            _taskCancellation.Remove(tab);
            cancellation.Dispose();
            _activeScanCount--;
            NotifyCountsChanged();
            DrainQueue();
            tab.Complete();
        }
    }

    private void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(ActiveScanCount));
        OnPropertyChanged(nameof(QueuedScanCount));
    }

    public Task<bool> SaveSharedSettingsAsync(MainWindowViewModel source)
    {
        if (_isDisposed || !TaskTabs.Any(tab => ReferenceEquals(tab.ViewModel, source)))
        {
            return Task.FromResult(true);
        }

        // Copy global settings synchronously, before the first await. A background
        // task can never later save its old root or overwrite a newer configuration.
        lock (_settingsSync)
        {
            _sharedSettings = (source.CaptureSettings() with { RootPath = _sharedSettings.Settings.RootPath }, source.ApiKey);
        }
        var shared = _sharedSettings;
        foreach (var tab in TaskTabs)
        {
            if (!ReferenceEquals(tab.ViewModel, source))
            {
                tab.ViewModel.ApplySharedSettings(shared.Settings, shared.ApiKey);
            }
        }
        return PersistSettingsAsync();
    }

    private async Task<bool> PersistSettingsAsync()
    {
        await _settingsWriteGate.WaitAsync().ConfigureAwait(true);
        try
        {
            (AppSettings Settings, string ApiKey) snapshot;
            lock (_settingsSync)
            {
                snapshot = _sharedSettings;
            }
            await _settingsStore.SaveAsync(snapshot.Settings).ConfigureAwait(true);
            await _secretStore.SaveApiKeyAsync(snapshot.ApiKey).ConfigureAwait(true);
            ReportSettingsError(null);
            return true;
        }
        catch (Exception ex)
        {
            ReportSettingsError(L.F($"设置保存失败：{ex.Message}"));
            return false;
        }
        finally
        {
            _settingsWriteGate.Release();
        }
    }

    private void ReportSettingsError(string? message)
    {
        foreach (var tab in TaskTabs)
        {
            tab.ViewModel.ReportWorkspaceSettingsError(message);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        foreach (var tab in TaskTabs.ToArray())
        {
            CancelTab(tab);
            tab.Dispose();
            tab.ViewModel.Dispose();
        }
        TaskTabs.Clear();
    }
}

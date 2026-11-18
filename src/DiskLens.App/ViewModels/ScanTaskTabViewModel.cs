using System.ComponentModel;
using System.IO;
using DiskLens.App.Infrastructure;

namespace DiskLens.App.ViewModels;

public enum ScanTaskState
{
    Home,
    Queued,
    Scanning,
    Completed,
    Partial,
    Canceled,
    Failed,
}

public sealed class ScanTaskTabViewModel : ObservableObject, IDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScanTaskState _state;
    private bool _isSelected;

    internal ScanTaskTabViewModel(MainWindowViewModel viewModel, string path, bool isHome)
    {
        ViewModel = viewModel;
        Path = path;
        IsHome = isHome;
        Title = isHome ? "Home" : GetTitle(path);
        _state = isHome ? ScanTaskState.Home : ScanTaskState.Queued;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (isHome)
        {
            _completion.TrySetResult();
        }
    }

    public MainWindowViewModel ViewModel { get; }
    public string Title { get; }
    public string Path { get; }
    public string RootLabel => Path;
    public bool IsHome { get; }
    public bool CanClose => !IsHome;
    public Task Completion => _completion.Task;
    public string ToolTipText => IsHome ? L.T("回到首页") : $"{Path}\n{StatusText}\n{ViewModel.ScanProgressDescriptionText}";
    public bool IsBusy => State is ScanTaskState.Queued or ScanTaskState.Scanning || ViewModel.IsAnalyzing;
    public double ProgressPercent => ViewModel.ScanProgressPercent;
    public bool IsComplete => State == ScanTaskState.Completed;
    public bool IsIndeterminate => State == ScanTaskState.Scanning && ViewModel.IsScanProgressIndeterminate;
    public string ProgressText => State switch
    {
        ScanTaskState.Home => string.Empty,
        ScanTaskState.Queued => L.T("排队中"),
        ScanTaskState.Scanning when IsIndeterminate => L.T("扫描中"),
        ScanTaskState.Partial => L.T("部分完成"),
        ScanTaskState.Canceled => L.T("已取消"),
        ScanTaskState.Failed => L.T("失败"),
        _ => $"{Math.Floor(ProgressPercent):0}%",
    };

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ScanTaskState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                NotifyStatusChanged();
            }
        }
    }

    public string StatusText => ViewModel.IsAnalyzing ? L.T("AI 分析中") : State switch
    {
        ScanTaskState.Home => L.T("首页"),
        ScanTaskState.Queued => L.T("排队中"),
        ScanTaskState.Scanning => L.T("扫描中"),
        ScanTaskState.Completed => L.T("已完成"),
        ScanTaskState.Partial => L.T("部分扫描"),
        ScanTaskState.Canceled => L.T("已取消"),
        _ => L.T("扫描失败"),
    };

    internal void Complete() => _completion.TrySetResult();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.IsAnalyzing))
        {
            NotifyStatusChanged();
        }
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(MainWindowViewModel.ScanProgressPercent) or nameof(MainWindowViewModel.IsScanProgressIndeterminate))
        {
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.ScanProgressDescriptionText))
        {
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    public void Dispose() => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private static string GetTitle(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

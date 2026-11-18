using System.Collections.Frozen;
using DiskLens.App.Infrastructure;
using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isReanalysisSelectionMode;
    private bool _aiSetupForReanalysis;
    private FileSystemNode? _reanalysisSelectionSnapshot;
    private IReadOnlySet<string> _reanalysisSelectedPaths = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public bool IsReanalysisSelectionMode => _isReanalysisSelectionMode;
    public int SelectedReanalysisCount => _reanalysisSelectedPaths.Count;
    public IReadOnlySet<string> ReanalysisSelectedPaths => _reanalysisSelectedPaths;
    public int ReanalysisSelectionRevision { get; private set; }
    public RelayCommand ToggleReanalysisSelectionCommand { get; private set; } = null!;
    public RelayCommand CancelReanalysisSelectionCommand { get; private set; } = null!;

    private void InitializeReanalysisSelection()
    {
        ReanalyzeWithoutMemoryCommand = new AsyncRelayCommand(ReanalyzeCheckedItemsAsync,
            () => ScanRoot is not null && !IsScanning && !IsAnalyzing && !IsRefreshing
                && !IsAiSetupOpen && (!IsReanalysisSelectionMode || SelectedReanalysisCount > 0));
        ToggleReanalysisSelectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is string path) ToggleReanalysisSelection(path);
        }, _ => IsReanalysisSelectionMode && !IsScanning && !IsAnalyzing && !IsRefreshing);
        CancelReanalysisSelectionCommand = new RelayCommand(CancelReanalysisSelection,
            () => IsReanalysisSelectionMode && !IsAnalyzing);
    }

    public bool IsReanalysisSelected(string path) => IsReanalysisSelectionMode
        && ReferenceEquals(_reanalysisSelectionSnapshot, ScanRoot) && _reanalysisSelectedPaths.Contains(path);

    public void ToggleReanalysisSelection(string path)
    {
        if (!IsReanalysisSelectionMode || IsScanning || IsAnalyzing || IsRefreshing
            || !ReferenceEquals(_reanalysisSelectionSnapshot, ScanRoot)
            || _scanNodeIndex.FindNode(path) is not { } node || IsAggregateNode(node)) return;
        var next = _reanalysisSelectedPaths.ToHashSet(StringComparer.Ordinal);
        if (!next.Add(path)) next.Remove(path);
        _reanalysisSelectedPaths = next.ToFrozenSet(StringComparer.Ordinal);
        NotifyReanalysisSelection();
    }

    private async Task ReanalyzeCheckedItemsAsync()
    {
        if (!IsReanalysisSelectionMode)
        {
            CancelSharedLookup();
            _reanalysisSelectionSnapshot = ScanRoot;
            _isReanalysisSelectionMode = true;
            NotifyReanalysisSelection();
            StatusText = L.T("请勾选要重新分析的文件或文件夹，再点击“重新分析”。将调用已配置的 API，可能产生费用。");
            return;
        }
        if (!ReferenceEquals(_reanalysisSelectionSnapshot, ScanRoot) || SelectedReanalysisCount == 0) return;
        if (!IsAiProviderConfigured)
        {
            OpenAiSetup();
            _aiSetupForReanalysis = true;
            OnPropertyChanged(nameof(AiSetupSaveButtonText));
            StatusText = L.T("请先配置分析服务。保存后请再次点击“重新分析”提交已勾选项目，API 调用可能产生费用。");
            return;
        }
        var selectedPaths = _reanalysisSelectedPaths.ToFrozenSet(StringComparer.Ordinal);
        await AnalyzeWithOptionsAsync(bypassMemory: true, selectedPaths: selectedPaths);
    }

    private void CancelReanalysisSelection()
    {
        if (IsAnalyzing) return;
        ResetReanalysisSelection();
        StatusText = L.T("已取消勾选，未提交重新分析。");
    }

    private void ResetReanalysisSelection()
    {
        _isReanalysisSelectionMode = false;
        _reanalysisSelectionSnapshot = null;
        _reanalysisSelectedPaths = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        NotifyReanalysisSelection();
    }

    private void NotifyReanalysisSelection()
    {
        ReanalysisSelectionRevision = unchecked(ReanalysisSelectionRevision + 1);
        OnPropertyChanged(nameof(IsReanalysisSelectionMode));
        OnPropertyChanged(nameof(SelectedReanalysisCount));
        OnPropertyChanged(nameof(ReanalysisSelectedPaths));
        OnPropertyChanged(nameof(ReanalysisSelectionRevision));
        ReanalyzeWithoutMemoryCommand?.RaiseCanExecuteChanged();
        ToggleReanalysisSelectionCommand?.RaiseCanExecuteChanged();
        CancelReanalysisSelectionCommand?.RaiseCanExecuteChanged();
        AnalyzeCommand?.RaiseCanExecuteChanged();
    }
}

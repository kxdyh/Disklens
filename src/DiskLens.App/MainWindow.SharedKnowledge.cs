using System.Windows;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.ViewModels;
using DiskLens.Core.Networking;

namespace DiskLens.App;

public partial class MainWindow
{
    public SharedKnowledgeRuntime? SharedRuntime { get; set; }
    private bool _sharedWindowOpen;

    internal void StartSharedRecommendation()
    {
        // Discovery is an explicit header action. Startup must never open a consent window.
    }

    private async void SharedKnowledge_OnClick(object sender, RoutedEventArgs e) => await ShowSharedKnowledgeEntryAsync();
    private async void SharedKnowledgeSettings_OnClick(object sender, RoutedEventArgs e) => await ShowSharedKnowledgeEntryAsync();

    private async void RematchSharedExperience_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.RematchCurrentSharedExperienceAsync();
    }

    private void StopSharedExperienceLookup_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.StopSharedExperienceLookup();
    }

    private async void RetrySharedExperienceLookup_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.RetrySharedExperienceLookupAsync();
    }

    private async void LocalAnalysisMemory_OnClick(object sender, RoutedEventArgs e)
    {
        if (SharedRuntime is null) return;
        try
        {
            var stats = await SharedRuntime.Memory.GetStatisticsAsync();
            if (LocalizedMessageBox.Show(this, L.F($"{stats}\n\n有效期 30 天；上限 20,000 条或 32 MiB。\n清理只移除本地分析缓存，不删除文件，也不撤回共享贡献。\n\n是否清理全部本地分析缓存？"),
                    L.T("本地分析缓存"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                await SharedRuntime.Memory.ClearAsync();
        }
        catch (Exception) { LocalizedMessageBox.Show(this, L.T("缓存管理暂时未完成，请稍后重试。"), L.T("本地分析缓存")); }
    }

    private async Task ShowSharedKnowledgeEntryAsync()
    {
        if (_sharedWindowOpen || SharedRuntime is null || DataContext is not MainWindowViewModel vm) return;
        _sharedWindowOpen = true;
        try
        {
            if (NetworkFailureClassifier.IsNetworkUnavailable(SharedRuntime.Endpoint.AbsoluteUri, SharedRuntime.NetworkAvailability))
            {
                await vm.HandleOfflineAsync();
                LocalizedMessageBox.Show(this, L.T(NetworkFailureClassifier.OfflineMessage), L.T("文件用途经验共享"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var consent = await SharedRuntime.Store.GetSnapshotAsync();
            if (consent.CanShare)
            {
                new SharedKnowledgeDisableWindow(SharedRuntime, vm.SelectedThemeId) { Owner = this }.ShowDialog();
            }
            else
            {
                vm.IsSettingsOpen = false;
                var dialog = new SharedKnowledgeWindow(SharedRuntime, vm.SelectedThemeId,
                    vm.ScanRoot is null ? null : vm.PreviewSharedScopeAsync,
                    vm.ScanRoot is null ? null : vm.ImportSharedHistoryAsync) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    // A newly enabled consent queues matching for any completed
                    // scan in the view model, including scans made before this dialog.
                    await vm.RefreshSharedConsentAsync();
                    if ((await SharedRuntime.Store.GetSnapshotAsync()).CanShare)
                        ShowSharingEnabledToast();
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { LocalizedMessageBox.Show(this, L.T("暂时无法读取功能设置，请稍后重试。"), L.T("文件用途经验共享")); }
        finally { _sharedWindowOpen = false; }
    }
}

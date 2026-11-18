using System.Windows;
using DiskLens.Core.Models;
using DiskLens.App.Localization;

namespace DiskLens.App;

public partial class MainWindow
{
    private void UsageInfo_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ToolTip: System.Windows.Controls.ToolTip tip } button)
        {
            tip.PlacementTarget = button;
            tip.IsOpen = !tip.IsOpen;
        }
    }

    private async Task<bool> ConfirmAiSubmissionAsync(AiAnalysisSubmissionPreview preview, CancellationToken cancellationToken)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || !IsLoaded) return false;
            var theme = (DataContext as ViewModels.MainWindowViewModel)?.SelectedThemeId ?? Models.ThemeCatalog.DefaultThemeId;
            var dialog = new AiAnalysisConfirmationWindow(preview, theme) { Owner = this };
            using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() =>
            { if (dialog.IsVisible) dialog.Close(); })));
            return dialog.ShowDialog() == true && !cancellationToken.IsCancellationRequested;
        });
    }

    private async Task<bool> ConfirmBudgetContinuationAsync(long maximumTokens, int unfinishedCount,
        CancellationToken cancellationToken)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || !IsLoaded) return false;
            var message = L.F($"本次预算不足，仍有 {unfinishedCount:N0} 项未完成。是否再开一轮，按当前最大 Token 上限 {maximumTokens:N0} Token 继续分析？已完成结果会从本地缓存复用。每轮都会使用一整份当前预算，累计用量可能超过单轮上限。上次超时或异常返回的请求是否已计费无法确认，重试可能重复计费。");
            return LocalizedMessageBox.Show(this, message, L.T("继续未完成分析"),
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes
                && !cancellationToken.IsCancellationRequested;
        });
    }
}

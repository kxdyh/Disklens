using System.Windows;
using System.Windows.Controls;
using DiskLens.App.Models;
using DiskLens.Core.Models;

namespace DiskLens.App;

public partial class AiAnalysisConfirmationWindow : Window
{
    public AiAnalysisSubmissionPreview Preview { get; }
    public string SummaryText => L.F($"候选共 {Preview.TotalCandidateCount} 项 · 需要 AI {Preview.Items.Count} 项 · 本地历史 {Preview.LocalCacheCount} 项 · 云端经验 {Preview.SharedExperienceCount} 项 · 读取不完整 {Preview.UnreadableCount} 项");
    public string BudgetNote => Preview.Items.Count == 0 ? L.T("本次没有需要调用模型的项目。")
        : !Preview.ProviderConfigured ? L.T("尚未配置 AI 服务。以上项目仍需分析，点击配置后再提交；本次尚未调用模型。")
        : L.T("实际提交受 Token 预算限制；API 调用可能产生费用。");
    public string ConfirmButtonText => Preview.Items.Count == 0 ? L.T("完成")
        : Preview.ProviderConfigured ? L.T("确认并开始") : L.T("配置 AI 服务");

    public AiAnalysisConfirmationWindow(AiAnalysisSubmissionPreview preview, string themeId)
    {
        Preview = preview;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/{ThemeCatalog.GetDictionaryPath(themeId)}") });
        InitializeComponent();
        WindowLanguage.Attach(this);
        WindowLanguage.Register(this, () => OnLanguageChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(L.Language))));
        DataContext = this;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 32);
        MaxWidth = Math.Max(460, SystemParameters.WorkArea.Width - 32);
        Loaded += (_, _) =>
        {
            if (Owner is not null)
            {
                MaxHeight = Math.Min(MaxHeight, Math.Max(480, Owner.ActualHeight - 32));
                MaxWidth = Math.Min(MaxWidth, Math.Max(460, Owner.ActualWidth - 32));
            }
        };
        ApplyFilter();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyFilter();

    private void Filter_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }
    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        var filtered = Preview.Items.Where(item =>
            (TypeFilter.SelectedIndex != 1 || item.IsDirectory) && (TypeFilter.SelectedIndex != 2 || !item.IsDirectory)
            && (search.Length == 0 || item.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(item => new SubmissionRow(item.Path, item.Name, FormatSize(item.SizeBytes))).ToArray();
        CandidateList.ItemsSource = filtered;
        FilterCountText.Text = L.F($"显示 {filtered.Length} / {Preview.Items.Count} 项 · 搜索与筛选仅用于查看，不改变提交范围");
        EmptyMessage.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB" : $"{bytes:N0} B";
    private void Confirm_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private sealed record SubmissionRow(string Path, string Name, string SizeText);
}

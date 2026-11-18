using System.Windows;
using System.Windows.Controls;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App;

public partial class AnnouncementsWindow : Window
{
    private readonly AnnouncementStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;
    private bool _busy;
    private bool _rendering;
    private string _statusKey = "正在加载公告…";

    internal AnnouncementsWindow(AnnouncementStore store, string themeId)
    {
        _store = store;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/{ThemeCatalog.GetDictionaryPath(themeId)}") });
        InitializeComponent();
        WindowLanguage.Attach(this);
        WindowLanguage.Register(this, Render);
        MaxWidth = Math.Max(650, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
        _store.Changed += Store_OnChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _store.Changed -= Store_OnChanged;
            _lifetime.Cancel();
        };
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy || _closed) return;
        _busy = true;
        _statusKey = "正在加载公告…";
        RefreshAnnouncementsButton.IsEnabled = false;
        Render();
        try
        {
            await _store.InitializeAsync(_lifetime.Token);
            if (_closed) return;
            Render();
            var result = await _store.RefreshAsync(_lifetime.Token);
            if (_closed) return;
            _statusKey = result.Succeeded ? "公告已更新。"
                : result.HasCachedContent ? "暂时无法更新公告，当前显示上次保存的内容。请稍后刷新。"
                : "暂时无法加载公告，请检查网络后点击刷新。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                RefreshAnnouncementsButton.IsEnabled = true;
                Render();
            }
        }
    }

    private void Store_OnChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (Dispatcher.CheckAccess()) Render();
        else _ = Dispatcher.InvokeAsync(() => { if (!_closed) Render(); });
    }

    private void Render()
    {
        if (_closed) return;
        var selectedId = (AnnouncementList.SelectedItem as AnnouncementRow)?.Item.Id;
        _rendering = true;
        try
        {
            StatusText.Text = L.T(_statusKey);
            var rows = _store.Feed.Items.Select(item => new AnnouncementRow(item)).ToArray();
            AnnouncementList.ItemsSource = rows;
            AnnouncementList.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selectedId) ?? rows.FirstOrDefault();
            RenderSelected();
        }
        finally { _rendering = false; }
        if (AnnouncementList.SelectedItem is AnnouncementRow selected) _ = MarkReadAsync(selected.Item);
    }

    private async void AnnouncementList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || _closed) return;
        RenderSelected();
        if (AnnouncementList.SelectedItem is AnnouncementRow selected) await MarkReadAsync(selected.Item);
    }

    private void RenderSelected()
    {
        var row = AnnouncementList.SelectedItem as AnnouncementRow;
        EmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        AnnouncementTitle.Text = row?.Title ?? "";
        AnnouncementDate.Text = row?.Date ?? "";
        // Text is intentionally never parsed as HTML, XAML, Markdown or executable links.
        AnnouncementBody.Text = row is null ? "" : L.IsEnglish && !string.IsNullOrWhiteSpace(row.Item.BodyEn)
            ? row.Item.BodyEn : row.Item.BodyZh;
    }

    private async Task MarkReadAsync(SharedAnnouncement item)
    {
        try { await _store.MarkReadAsync(item, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private sealed record AnnouncementRow(SharedAnnouncement Item)
    {
        public string Title => L.IsEnglish && !string.IsNullOrWhiteSpace(Item.TitleEn) ? Item.TitleEn : Item.TitleZh;
        public string Date => L.F($"发布于 {Item.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
    }
}

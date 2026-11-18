using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using DiskLens.App.Services;
using DiskLens.Core.Updates;

namespace DiskLens.App;

public partial class UpdateAvailableWindow : Window
{
    private readonly UpdateCheckCoordinator _coordinator;
    private readonly ReleaseManifest _release;
    private bool _isClosed;

    public UpdateAvailableWindow(ReleaseManifest release, UpdateCheckCoordinator coordinator)
    {
        InitializeComponent();
        WindowLanguage.Attach(this);
        _release = release;
        _coordinator = coordinator;
        MaxHeight = Math.Max(350, SystemParameters.WorkArea.Height - 56);
        VersionSummary.Text = $"{coordinator.CurrentVersionText}  →  {release.VersionText}";
        ReleaseDate.Text = L.F($"发布于 {release.PublishedAt:yyyy-MM-dd}");
        RefreshReleaseNotes();
        WindowLanguage.Register(this, () => OnLanguageChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(L.Language))));
        Loaded += (_, _) => OpenDownloadPageButton.Focus();
        Closed += (_, _) => _isClosed = true;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshReleaseNotes();
        ReleaseDate.Text = L.F($"发布于 {_release.PublishedAt:yyyy-MM-dd}");
        ActionError.Text = L.T(ActionError.Text);
    }
    private void RefreshReleaseNotes() => ReleaseNotes.ItemsSource = L.IsEnglish
        ? _release.NotesEn.Count > 0 ? _release.NotesEn : new[] { "English release notes are unavailable for this older release. Visit the download page for details." }
        : _release.Notes;

    private void Dismiss_OnClick(object sender, RoutedEventArgs e) => Close();

    private async void Ignore_OnClick(object sender, RoutedEventArgs e)
    {
        IgnoreUpdateButton.IsEnabled = false;
        var ignored = await _coordinator.IgnoreAsync(_release).ConfigureAwait(true);
        if (_isClosed) return;
        if (ignored) Close();
        else
        {
            ActionError.Text = L.T("未能保存忽略记录，请重试；也可以暂时关闭。");
            ActionError.Visibility = Visibility.Visible;
            IgnoreUpdateButton.IsEnabled = true;
        }
    }

    private void Download_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleaseManifest.OfficialDownloadPage.AbsoluteUri) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActionError.Text = L.T("无法打开默认浏览器，请手动访问 https://disklens.site/。");
            ActionError.Visibility = Visibility.Visible;
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;

namespace DiskLens.App;

public partial class MainWindow
{
    private AnnouncementStore? _announcementStore;
    private AnnouncementsWindow? _announcementsWindow;
    private readonly CancellationTokenSource _announcementLifetime = new();
    private DispatcherTimer? _announcementTimer;
    private bool _announcementsClosed;
    private bool _announcementRefreshRunning;

    private void InitializeAnnouncements()
    {
        if (_announcementStore is not null || SharedRuntime is null) return;
        _announcementStore = new AnnouncementStore(SharedRuntime.Client,
            Path.Combine(SharedRuntime.Store.StorageDirectory, "announcements-cache.json"));
        _announcementStore.Changed += Announcements_OnChanged;
        // Keep startup responsive and never open a dialog without a click.
        _ = RefreshAnnouncementsAtStartupAsync();
        _announcementTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _announcementTimer.Tick += async (_, _) =>
        {
            if (IsVisible) await RefreshAnnouncementsInBackgroundAsync();
        };
        _announcementTimer.Start();
        Closed += (_, _) =>
        {
            _announcementsClosed = true;
            _announcementTimer.Stop();
            _announcementLifetime.Cancel();
            _announcementStore.Changed -= Announcements_OnChanged;
            _announcementsWindow?.Close();
        };
    }

    private async Task RefreshAnnouncementsAtStartupAsync()
    {
        try
        {
            await _announcementStore!.InitializeAsync(_announcementLifetime.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), _announcementLifetime.Token);
            await RefreshAnnouncementsInBackgroundAsync();
        }
        catch (OperationCanceledException) when (_announcementLifetime.IsCancellationRequested) { }
    }

    private async Task RefreshAnnouncementsInBackgroundAsync()
    {
        if (_announcementRefreshRunning || _announcementsClosed || _announcementStore is null) return;
        _announcementRefreshRunning = true;
        try { await _announcementStore.RefreshAsync(_announcementLifetime.Token); }
        catch (OperationCanceledException) when (_announcementLifetime.IsCancellationRequested) { }
        finally { _announcementRefreshRunning = false; }
    }

    private void Announcements_OnChanged(object? sender, EventArgs e)
    {
        if (_announcementsClosed) return;
        void Apply()
        {
            if (!_announcementsClosed)
                AnnouncementUnreadDot.Visibility = _announcementStore?.HasUnread == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (Dispatcher.CheckAccess()) Apply();
        else _ = Dispatcher.InvokeAsync(Apply);
    }

    private void Announcements_OnClick(object sender, RoutedEventArgs e)
    {
        InitializeAnnouncements();
        if (_announcementStore is null) return;
        if (_announcementsWindow is not null) { _announcementsWindow.Activate(); return; }
        var theme = (DataContext as MainWindowViewModel)?.SelectedThemeId ?? ThemeCatalog.DefaultThemeId;
        _announcementsWindow = new AnnouncementsWindow(_announcementStore, theme) { Owner = this };
        _announcementsWindow.Closed += (_, _) => _announcementsWindow = null;
        _announcementsWindow.Show();
    }
}

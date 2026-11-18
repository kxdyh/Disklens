using System.Net.Http;
using System.Windows;
using DiskLens.App.Services;
using DiskLens.Core.Updates;

namespace DiskLens.App;

public partial class MainWindow
{
    private UpdateCheckCoordinator? _ownedUpdateCoordinator;
    private HttpClient? _ownedUpdateHttpClient;

    public static readonly DependencyProperty UpdateCoordinatorProperty = DependencyProperty.Register(
        nameof(UpdateCoordinator), typeof(UpdateCheckCoordinator), typeof(MainWindow));

    public UpdateCheckCoordinator? UpdateCoordinator
    {
        get => (UpdateCheckCoordinator?)GetValue(UpdateCoordinatorProperty);
        set => SetValue(UpdateCoordinatorProperty, value);
    }

    private async void CheckForUpdates_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await EnsureUpdateCoordinator().CheckAsync(manual: true).ConfigureAwait(true);
    }

    private UpdateCheckCoordinator EnsureUpdateCoordinator()
    {
        if (UpdateCoordinator is { } existing) return existing;
        if (_ownedUpdateCoordinator is { } owned)
        {
            UpdateCoordinator = owned;
            return owned;
        }

        // Preview and recording hosts construct the window without App.OnStartup.
        // Create their update service only when the user explicitly checks.
        _ownedUpdateHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownedUpdateCoordinator = new UpdateCheckCoordinator(new ReleaseFeedClient(_ownedUpdateHttpClient),
            new UpdatePreferenceStore(), typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 1, 0));
        _ownedUpdateCoordinator.UpdateAvailable += OwnedUpdateCoordinator_OnUpdateAvailable;
        Closed += MainWindow_ReleaseOwnedUpdateServices;
        UpdateCoordinator = _ownedUpdateCoordinator;
        return _ownedUpdateCoordinator;
    }

    private void OwnedUpdateCoordinator_OnUpdateAvailable(object? sender, ReleaseManifest release)
    {
        if (IsVisible && sender is UpdateCheckCoordinator coordinator)
            new UpdateAvailableWindow(release, coordinator) { Owner = this }.ShowDialog();
    }

    private void MainWindow_ReleaseOwnedUpdateServices(object? sender, EventArgs e)
    {
        Closed -= MainWindow_ReleaseOwnedUpdateServices;
        if (_ownedUpdateCoordinator is { } coordinator)
        {
            coordinator.UpdateAvailable -= OwnedUpdateCoordinator_OnUpdateAvailable;
            coordinator.Dispose();
            _ownedUpdateCoordinator = null;
        }
        _ownedUpdateHttpClient?.Dispose();
        _ownedUpdateHttpClient = null;
    }
}

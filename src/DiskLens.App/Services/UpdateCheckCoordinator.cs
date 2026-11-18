using System.Net.Http;
using DiskLens.App.Infrastructure;
using DiskLens.Core.Updates;

namespace DiskLens.App.Services;

/// <summary>One instance per application, independent of scan tab view models.</summary>
public sealed class UpdateCheckCoordinator : ObservableObject, IDisposable
{
    private readonly IReleaseFeedClient _feed;
    private readonly IUpdatePreferenceStore _preferences;
    private readonly Version _installedVersion;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _startupStarted;
    private bool _isChecking;
    private bool _disposed;
    private string _statusText = L.T("启动时自动检查新版，也可在此手动检查。");

    public UpdateCheckCoordinator(IReleaseFeedClient feed, IUpdatePreferenceStore preferences, Version installedVersion)
    {
        _feed = feed;
        _preferences = preferences;
        _installedVersion = installedVersion;
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(manual: true), () => !IsChecking && !_disposed);
    }

    public event EventHandler<ReleaseManifest>? UpdateAvailable;
    public AsyncRelayCommand CheckCommand { get; }
    public string CurrentVersionText => $"DiskLens {_installedVersion.ToString(3)}";
    public string CheckButtonText => IsChecking ? L.T("正在检查更新…") : L.T("检查更新");
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (!SetProperty(ref _isChecking, value)) return;
            OnPropertyChanged(nameof(CheckButtonText));
            CheckCommand.RaiseCanExecuteChanged();
        }
    }

    public Task CheckAtStartupAsync()
    {
        if (_startupStarted || _disposed) return Task.CompletedTask;
        _startupStarted = true;
        return CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (_disposed || IsChecking) return;
        var cancellationToken = _lifetime.Token;
        IsChecking = true;
        if (manual) StatusText = L.T("正在检查更新…");
        try
        {
            var ignored = await _preferences.LoadIgnoredVersionAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var release = await _feed.FetchAsync(cancellationToken).ConfigureAwait(true);
            if (_disposed) return;
            if (release is null)
            {
                if (manual) StatusText = L.T("暂时无法检查更新，请稍后重试或访问 disklens.site。");
                return;
            }
            if (ReleaseUpdatePolicy.ShouldOffer(release, _installedVersion, ignored, manual))
            {
                StatusText = L.F($"有新版本 {release.VersionText} 可用。");
                UpdateAvailable?.Invoke(this, release);
            }
            else StatusText = release.Version <= _installedVersion
                ? L.F($"当前已是最新版本：{_installedVersion.ToString(3)}")
                : L.F($"已忽略 {release.VersionText}；后续新版仍会提醒。");
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && manual) StatusText = L.T("更新检查超时，请稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (!_disposed && manual) StatusText = L.T("暂时无法检查更新，请稍后重试或访问 disklens.site。");
        }
        finally { IsChecking = false; }
    }

    public async Task<bool> IgnoreAsync(ReleaseManifest release)
    {
        if (_disposed) return false;
        try
        {
            await _preferences.SaveIgnoredVersionAsync(release.VersionText, _lifetime.Token).ConfigureAwait(true);
            if (_disposed) return false;
            StatusText = L.F($"已忽略 {release.VersionText}；后续新版仍会提醒。");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!_disposed) StatusText = L.T("未能保存忽略记录，请稍后重试。");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        CheckCommand.RaiseCanExecuteChanged();
        UpdateAvailable = null;
    }
}

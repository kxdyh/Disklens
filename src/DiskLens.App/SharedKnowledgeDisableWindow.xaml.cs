using System.Windows;
using DiskLens.App.Models;
using DiskLens.App.Services.SharedKnowledge;

namespace DiskLens.App;

public partial class SharedKnowledgeDisableWindow : Window
{
    private readonly SharedKnowledgeRuntime _runtime;
    private bool _busy;
    private bool _closed;
    private string? _statusKey;
    private bool _disabledLocally;

    public SharedKnowledgeDisableWindow(SharedKnowledgeRuntime runtime, string themeId)
    {
        _runtime = runtime;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/DiskLens.App;component/{ThemeCatalog.GetDictionaryPath(themeId)}") });
        InitializeComponent();
        WindowLanguage.Attach(this);
        WindowLanguage.Register(this, () => OnLanguageChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(L.Language))));
        MaxWidth = Math.Max(320, SystemParameters.WorkArea.Width - 24);
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => _closed = true;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_statusKey is not null) StatusText.Text = L.T(_statusKey);
        if (_disabledLocally) KeepEnabledButton.Content = L.T("知道了");
        if (_busy) DisableSharingButton.Content = L.T("正在关闭…");
        else if (_statusKey is not null && !_disabledLocally) DisableSharingButton.Content = L.T("重试关闭");
    }

    private void KeepEnabled_OnClick(object sender, RoutedEventArgs e) => Close();

    private async void Disable_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        KeepEnabledButton.IsEnabled = DisableSharingButton.IsEnabled = false;
        DisableSharingButton.Content = L.T("正在关闭…");
        try
        {
            var synced = await _runtime.DisableSharingAsync();
            _busy = false;
            if (_closed) return;
            if (synced) { Close(); return; }
            _statusKey = "本机功能已关闭，云端状态暂未同步。";
            _disabledLocally = true;
            StatusText.Text = L.T(_statusKey);
            StatusText.Visibility = Visibility.Visible;
            DisableSharingButton.Visibility = Visibility.Collapsed;
            KeepEnabledButton.Content = L.T("知道了");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            _statusKey = "关闭设置未能保存，请重试。";
            StatusText.Text = L.T(_statusKey);
            StatusText.Visibility = Visibility.Visible;
            DisableSharingButton.Content = L.T("重试关闭");
        }
        finally
        {
            _busy = false;
            KeepEnabledButton.IsEnabled = DisableSharingButton.IsEnabled = true;
        }
    }
}

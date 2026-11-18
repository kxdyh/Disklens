using System.Windows;
using System.Windows.Threading;

namespace DiskLens.App;

public partial class MainWindow
{
    private DispatcherTimer? _sharingEnabledToastTimer;

    private void ShowSharingEnabledToast()
    {
        SharingEnabledToastText.Text = L.T("文件用途经验共享已开启");
        SharingEnabledToast.Visibility = Visibility.Visible;
        if (_sharingEnabledToastTimer is null)
        {
            _sharingEnabledToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _sharingEnabledToastTimer.Tick += (_, _) =>
            {
                _sharingEnabledToastTimer.Stop();
                SharingEnabledToast.Visibility = Visibility.Collapsed;
            };
            Closed += (_, _) => _sharingEnabledToastTimer.Stop();
        }
        _sharingEnabledToastTimer.Stop();
        _sharingEnabledToastTimer.Start();
    }
}

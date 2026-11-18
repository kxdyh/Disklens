using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using DiskLens.App.Localization;

namespace DiskLens.App;

public partial class MainWindow
{
    private void RepositoryIssues_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/kxdyh/Disklens/issues")
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                L.T("暂时无法打开浏览器，请访问 github.com/kxdyh/Disklens/issues。"),
                L.T("帮助"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

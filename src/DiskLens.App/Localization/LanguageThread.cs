using System.Windows.Threading;

namespace DiskLens.App.Localization;

internal static class LanguageThread
{
    public static void Run(Dispatcher dispatcher, Action action)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || !dispatcher.Thread.IsAlive) return;
        void Refresh()
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(L.IsEnglish ? "en-US" : "zh-CN");
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture = culture;
            action();
        }
        if (dispatcher.CheckAccess()) { Refresh(); return; }
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished) Refresh();
            }), DispatcherPriority.DataBind);
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) { }
    }
}

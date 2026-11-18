using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DiskLens.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    private readonly System.Windows.Threading.Dispatcher _languageDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    protected ObservableObject() => PropertyChangedEventManager.AddHandler(L.Current, OnDisplayLanguageChanged, nameof(L.Language));

    private void OnDisplayLanguageChanged(object? sender, PropertyChangedEventArgs e) => RunOnLanguageThread(() => OnPropertyChanged(null));
    protected void RunOnLanguageThread(Action action) => LanguageThread.Run(_languageDispatcher, action);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

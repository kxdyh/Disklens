using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace DiskLens.App.Localization;

/// <summary>Refreshes programmatically created display labels; bound values keep their bindings.</summary>
public static class WindowLanguage
{
    private static readonly ConditionalWeakTable<Window, Subscription> Subscriptions = new();
    public static void Attach(Window window) => Subscriptions.GetValue(window, w => new Subscription(w));
    public static void Register(Window window, Action refresh) => Subscriptions.GetValue(window, w => new Subscription(w)).Callbacks.Add(refresh);
    private sealed class Subscription
    {
        private readonly Window _window;
        private bool _closed;
        public List<Action> Callbacks { get; } = [];
        public Subscription(Window window)
        {
            _window = window;
            window.Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.IsEnglish ? "en-US" : "zh-CN");
            PropertyChangedEventManager.AddHandler(L.Current, Changed, nameof(L.Language));
            window.Closed += (_, _) =>
            {
                _closed = true;
                Callbacks.Clear();
                PropertyChangedEventManager.RemoveHandler(L.Current, Changed, nameof(L.Language));
            };
        }
        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (_closed) return;
            LanguageThread.Run(_window.Dispatcher, () =>
            {
                if (_closed) return;
                _window.Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.IsEnglish ? "en-US" : "zh-CN");
                Refresh(_window, new HashSet<DependencyObject>());
                foreach (var callback in Callbacks.ToArray()) callback();
            });
        }
        private static void Refresh(DependencyObject obj, HashSet<DependencyObject> visited)
        {
            if (!visited.Add(obj)) return;
            void RefreshBinding(DependencyProperty property)
            {
                BindingOperations.GetMultiBindingExpression(obj, property)?.UpdateTarget();
                BindingOperations.GetBindingExpression(obj, property)?.UpdateTarget();
            }
            if (obj is TextBlock) RefreshBinding(TextBlock.TextProperty);
            if (obj is Run) RefreshBinding(Run.TextProperty);
            if (obj is ContentControl) RefreshBinding(ContentControl.ContentProperty);
            if (obj is HeaderedContentControl) RefreshBinding(HeaderedContentControl.HeaderProperty);
            if (obj is Window) RefreshBinding(Window.TitleProperty);
            if (obj is FrameworkElement) RefreshBinding(FrameworkElement.ToolTipProperty);
            if (obj is FrameworkElement e)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(e).OfType<DependencyObject>()) Refresh(child, visited);
                e.InvalidateVisual();
            }
            if (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) Refresh(VisualTreeHelper.GetChild(obj, i), visited);
        }
    }
}

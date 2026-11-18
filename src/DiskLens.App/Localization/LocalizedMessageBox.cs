using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskLens.App.Localization;

/// <summary>Application-owned confirmation buttons follow the app language, independently of Windows.</summary>
public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(string message, string caption = "", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) => Show(Application.Current?.MainWindow, message, caption, buttons, image, defaultResult);

    public static MessageBoxResult Show(Window? owner, string message, string caption = "", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new Window
        {
            Title = L.T(caption), MinWidth = 400, MaxWidth = 620, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, FontSize = 13,
        };
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        if (owner is not null) dialog.Resources.MergedDictionaries.Add(owner.Resources);
        dialog.SetResourceReference(Window.BackgroundProperty, "CardBackgroundBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        var text = new TextBlock { Text = L.T(message), TextWrapping = TextWrapping.Wrap, MaxWidth = 550, Margin = new Thickness(0, 0, 0, 24) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        panel.Children.Add(text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var labels = new List<(Button Button, string Chinese, string English)>();
        var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK
            : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        void Add(string zh, string en, MessageBoxResult choice, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button { Content = L.IsEnglish ? en : zh, MinWidth = 86, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = true; };
            labels.Add((button, zh, en));
            row.Children.Add(button);
        }
        if (buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
        {
            Add("是", "Yes", MessageBoxResult.Yes, isDefault: defaultResult == MessageBoxResult.Yes);
            Add("否", "No", MessageBoxResult.No, isDefault: defaultResult != MessageBoxResult.Yes, isCancel: buttons == MessageBoxButton.YesNo);
            if (buttons == MessageBoxButton.YesNoCancel) Add("取消", "Cancel", MessageBoxResult.Cancel, isCancel: true);
        }
        else
        {
            Add("确定", "OK", MessageBoxResult.OK, isDefault: true, isCancel: buttons == MessageBoxButton.OK);
            if (buttons == MessageBoxButton.OKCancel) Add("取消", "Cancel", MessageBoxResult.Cancel, isCancel: true);
        }
        panel.Children.Add(row);
        dialog.Content = panel;
        WindowLanguage.Register(dialog, () =>
        {
            dialog.Title = L.T(caption);
            text.Text = L.T(message);
            foreach (var label in labels) label.Button.Content = L.IsEnglish ? label.English : label.Chinese;
        });
        dialog.ShowDialog();
        return result;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace DiskLens.App.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension() { }
    public TextExtension(string source) => Source = source;
    [ConstructorArgument("source")]
    public string Source { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding("[" + Source + "]") { Source = L.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

public sealed class LocalizedTextConverter : IMultiValueConverter, IValueConverter
{
    public static LocalizedTextConverter Instance { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var value = values.Length > 0 ? values[0] : null;
        if (value == DependencyProperty.UnsetValue) return "";
        return value is string s ? L.T(s) : value ?? "";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string s ? L.T(s) : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Retains the original binding and refreshes its display text when the language changes.</summary>
public sealed class BindExtension : MarkupExtension
{
    public BindExtension() { }
    public BindExtension(string path) => Path = path;
    [ConstructorArgument("path")]
    public string Path { get; set; } = "";
    public string? ElementName { get; set; }
    public object? Source { get; set; }
    public string? Format { get; set; }
    public bool NodeName { get; set; }
    public RelativeSource? RelativeSource { get; set; }
    public object? FallbackValue { get; set; }
    public object? TargetNullValue { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var value = new Binding(Path);
        if (ElementName is not null) value.ElementName = ElementName;
        if (Source is not null) value.Source = Source;
        if (RelativeSource is not null) value.RelativeSource = RelativeSource;
        if (FallbackValue is not null) value.FallbackValue = FallbackValue;
        if (TargetNullValue is not null) value.TargetNullValue = TargetNullValue;
        var binding = new MultiBinding { Converter = NodeName ? new NodeConverter() : Format is null ? LocalizedTextConverter.Instance : new FormatConverter(Format), Mode = BindingMode.OneWay };
        binding.Bindings.Add(value);
        binding.Bindings.Add(new Binding(nameof(L.Language)) { Source = L.Current });
        return binding.ProvideValue(serviceProvider);
    }
    private sealed class NodeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => L.NodeName(values[0] as DiskLens.Core.Models.FileSystemNode);
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private sealed class FormatConverter(string format) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => string.Format(culture, L.T(format), values[0]);
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace DiskLens.App.Controls;

internal sealed class HomeArtworkRenderer
{
    private readonly Dictionary<string, Brush> _brushes = new();
    private readonly Dictionary<string, Geometry> _clips = new();
    private readonly Dictionary<string, double> _blurs = new();
    public Dictionary<string, List<FrameworkElement>> Groups { get; } = new();

    private static double Number(string? value, double fallback = 0) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static double N(XElement element, string name, double fallback = 0) => Number((string?)element.Attribute(name), fallback);
    private static string? A(XElement element, string name) => (string?)element.Attribute(name);
    private static Color ParseColor(string text) => (Color)ColorConverter.ConvertFromString(text)!;

    public Viewbox Convert(string svg)
    {
        _brushes.Clear();
        _clips.Clear();
        _blurs.Clear();
        Groups.Clear();

        var xml = XElement.Parse(svg);
        foreach (var definition in xml.Descendants("linearGradient"))
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(N(definition, "x1"), N(definition, "y1")),
                EndPoint = new Point(N(definition, "x2", 1), N(definition, "y2")),
            };
            foreach (var stop in definition.Elements("stop"))
            {
                var color = ParseColor(A(stop, "stop-color") ?? "#000000");
                color.A = (byte)(255 * N(stop, "stop-opacity", 1));
                gradient.GradientStops.Add(new GradientStop(color, N(stop, "offset")));
            }

            _brushes[A(definition, "id")!] = gradient;
        }

        foreach (var clip in xml.Descendants("clipPath"))
        {
            var shape = clip.Elements().First();
            _clips[A(clip, "id")!] = shape.Name.LocalName == "circle"
                ? new EllipseGeometry(new Point(N(shape, "cx"), N(shape, "cy")), N(shape, "r"), N(shape, "r"))
                : new RectangleGeometry(new Rect(N(shape, "x"), N(shape, "y"), N(shape, "width"), N(shape, "height")));
        }

        foreach (var filter in xml.Descendants("filter"))
        {
            _blurs[A(filter, "id")!] = N(filter.Element("feGaussianBlur")!, "stdDeviation") * 2;
        }

        var bounds = (A(xml, "viewBox") ?? "0 0 640 550").Split(' ').Select(value => Number(value)).ToArray();
        var canvas = new Canvas { Width = bounds[2], Height = bounds[3] };
        AddChildren(xml, canvas);
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }

    private Brush? Brush(string? value, string defaultColor = "#000000")
    {
        if (value == "none")
        {
            return null;
        }

        if (value?.StartsWith("url(#", StringComparison.Ordinal) == true)
        {
            return _brushes[value[5..^1]];
        }

        return new SolidColorBrush(ParseColor(value ?? defaultColor));
    }

    private void AddChildren(XElement parent, Canvas canvas)
    {
        foreach (var child in parent.Elements())
        {
            FrameworkElement? element = null;
            switch (child.Name.LocalName)
            {
                case "g":
                    element = new Canvas();
                    AddChildren(child, (Canvas)element);
                    break;
                case "path":
                    element = new Path { Data = Geometry.Parse(A(child, "d")!) };
                    break;
                case "polygon":
                    var coordinates = Regex.Split(A(child, "points")!.Trim(), @"[\s,]+")
                        .Select(value => Number(value)).ToArray();
                    var polygon = new Polygon();
                    for (var index = 0; index < coordinates.Length; index += 2)
                    {
                        polygon.Points.Add(new Point(coordinates[index], coordinates[index + 1]));
                    }

                    element = polygon;
                    break;
                case "rect":
                    element = new Rectangle
                    {
                        Width = N(child, "width"),
                        Height = N(child, "height"),
                        RadiusX = N(child, "rx"),
                        RadiusY = N(child, "rx"),
                    };
                    Canvas.SetLeft(element, N(child, "x"));
                    Canvas.SetTop(element, N(child, "y"));
                    break;
                case "circle":
                case "ellipse":
                    var radiusX = N(child, "rx", N(child, "r"));
                    var radiusY = N(child, "ry", N(child, "r"));
                    element = new Ellipse { Width = radiusX * 2, Height = radiusY * 2 };
                    Canvas.SetLeft(element, N(child, "cx") - radiusX);
                    Canvas.SetTop(element, N(child, "cy") - radiusY);
                    break;
                case "text":
                    element = new TextBlock
                    {
                        Text = child.Value,
                        FontSize = N(child, "font-size", 12),
                        FontFamily = new FontFamily(A(child, "font-family") ?? "Segoe UI"),
                        Foreground = Brush(A(child, "fill")),
                    };
                    Canvas.SetLeft(element, N(child, "x"));
                    Canvas.SetTop(element, N(child, "y") - ((TextBlock)element).FontSize * 1.05);
                    break;
            }

            if (element is null)
            {
                continue;
            }

            if (element is Shape shape)
            {
                var fill = Brush(A(child, "fill"));
                if (fill is not null)
                {
                    fill = fill.Clone();
                    fill.Opacity = N(child, "fill-opacity", 1);
                }

                shape.Fill = fill;
                var stroke = Brush(A(child, "stroke") ?? "none");
                if (stroke is not null)
                {
                    stroke = stroke.Clone();
                    stroke.Opacity = N(child, "stroke-opacity", 1);
                }

                shape.Stroke = stroke;
                shape.StrokeThickness = N(child, "stroke-width", 1);
                if (A(child, "stroke-linecap") == "round")
                {
                    shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
                }

                if (A(child, "stroke-linejoin") == "round")
                {
                    shape.StrokeLineJoin = PenLineJoin.Round;
                }
            }

            element.Opacity = N(child, "opacity", 1);
            if (A(child, "clip-path") is { } clip)
            {
                element.Clip = _clips[clip[5..^1]];
            }

            if (A(child, "filter") is { } filter)
            {
                element.Effect = new BlurEffect { Radius = _blurs[filter[5..^1]] };
            }

            var transforms = new TransformGroup();
            // SVG transforms use the parent coordinate system; WPF transforms
            // use the element's coordinates before Canvas.Left/Top are applied.
            var left = Canvas.GetLeft(element);
            var top = Canvas.GetTop(element);
            left = double.IsNaN(left) ? 0 : left;
            top = double.IsNaN(top) ? 0 : top;
            transforms.Children.Add(new TranslateTransform(left, top));
            if (A(child, "transform") is { } transform)
            {
                foreach (Match match in Regex.Matches(transform, @"(rotate|translate|scale|matrix)\(([^)]+)\)").Cast<Match>().Reverse())
                {
                    var values = Regex.Split(match.Groups[2].Value.Trim(), @"[\s,]+")
                        .Select(value => Number(value)).ToArray();
                    Transform item = match.Groups[1].Value switch
                    {
                        "rotate" => new RotateTransform(values[0], values.Length > 1 ? values[1] : 0, values.Length > 2 ? values[2] : 0),
                        "translate" => new TranslateTransform(values[0], values.Length > 1 ? values[1] : 0),
                        "scale" => new ScaleTransform(values[0], values.Length > 1 ? values[1] : values[0]),
                        _ => new MatrixTransform(values[0], values[1], values[2], values[3], values[4], values[5]),
                    };
                    transforms.Children.Add(item);
                }
            }

            transforms.Children.Add(new TranslateTransform(-left, -top));
            element.RenderTransform = transforms;
            if (A(child, "class") is { } classes)
            {
                foreach (var name in classes.Split(' '))
                {
                    if (!Groups.TryGetValue(name, out var items))
                    {
                        Groups[name] = items = [];
                    }

                    items.Add(element);
                    if (name == "sweep")
                    {
                        element.Opacity = 0;
                    }
                }
            }

            canvas.Children.Add(element);
        }
    }
}

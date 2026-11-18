using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskLens.App.Models;
using Path = System.Windows.Shapes.Path;

namespace DiskLens.App.Controls;

public sealed class HomeLandingControl : UserControl
{
    private sealed record MovingArtworkPart(FrameworkElement Element, TranslateTransform Offset, double BaseOpacity);

    public static readonly DependencyProperty StartScanCommandProperty = DependencyProperty.Register(
        nameof(StartScanCommand), typeof(ICommand), typeof(HomeLandingControl), new PropertyMetadata(null, OnCommandsChanged));

    public static readonly DependencyProperty BrowseFolderCommandProperty = DependencyProperty.Register(
        nameof(BrowseFolderCommand), typeof(ICommand), typeof(HomeLandingControl), new PropertyMetadata(null, OnCommandsChanged));

    private readonly Dictionary<string, List<MovingArtworkPart>> _movingArtwork = [];
    private HomeArtworkRenderer? _artworkRenderer;
    private Button? _primaryButton;
    private Button? _folderButton;
    private Border? _frame;
    private Viewbox? _artwork;
    private StackPanel? _headline;
    private TextBlock? _description;
    private TranslateTransform? _primaryButtonOffset;
    private TranslateTransform? _folderButtonOffset;
    private Vector _targetPrimaryOffset;
    private Vector _targetFolderOffset;
    private Vector _primaryOffset;
    private Vector _folderOffset;
    private double _targetArtworkProgress;
    private double _artworkProgress;
    private TimeSpan? _lastRenderTime;
    private bool _renderingSubscribed;
    private bool _reduceMotion;
    private int _variant = 3;

    public ICommand? StartScanCommand
    {
        get => (ICommand?)GetValue(StartScanCommandProperty);
        set => SetValue(StartScanCommandProperty, value);
    }

    public ICommand? BrowseFolderCommand
    {
        get => (ICommand?)GetValue(BrowseFolderCommandProperty);
        set => SetValue(BrowseFolderCommandProperty, value);
    }

    public int CurrentVariant => _variant;

    public Button? PrimaryButton => _primaryButton;

    public Button? FolderButton => _folderButton;

    public void ApplyMotionPreference(bool reduceMotion)
    {
        _reduceMotion = reduceMotion;
        if (reduceMotion)
        {
            AdvancePointerMotion(1);
        }
    }

    public HomeLandingControl()
    {
        System.ComponentModel.PropertyChangedEventManager.AddHandler(L.Current, OnLanguageChanged, nameof(L.Language));
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        SizeChanged += OnSizeChanged;
        MouseMove += OnPointerMoved;
        MouseLeave += OnPointerLeftControl;
    }

    public void ApplyTheme(string? themeId, bool reduceMotion)
    {
        StopPointerMotion();
        _reduceMotion = reduceMotion;
        _targetPrimaryOffset = _targetFolderOffset = _primaryOffset = _folderOffset = default;
        _targetArtworkProgress = _artworkProgress = 0;
        _variant = ThemeCatalog.Normalize(themeId) switch
        {
            "01" => 1,
            "02" => 2,
            "03" => 3,
            _ => 3,
        };
        Build();
        ApplyArtworkProgress(0);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => LanguageThread.Run(Dispatcher, RefreshLanguage);

    private void RefreshLanguage()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || !Dispatcher.CheckAccess()
            || TryFindResource("PrimaryTextBrush") is not Brush) return;
        StopPointerMotion();
        Build();
        ApplyArtworkProgress(0);
    }

    private void Build()
    {
        _movingArtwork.Clear();
        var foreground = ResourceBrush("PrimaryTextBrush");
        var muted = ResourceBrush("SecondaryTextBrush");
        var accent = ResourceBrush("AccentBrush");
        var titleAccent = _variant == 3 ? ResourceBrush("TileUserMossBrush") : accent;
        var highlight = ResourceBrush("SelectionBorderBrush");
        var frame = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(_variant == 3 ? 50 : 62, 30, 40, 30),
            ClipToBounds = true,
        };
        frame.SetResourceReference(BackgroundProperty, "MainSurfaceBrush");
        _frame = frame;
        Content = frame;

        var stage = new Grid();
        stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.Child = stage;

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(47, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(53, GridUnitType.Star) });
        stage.Children.Add(body);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(copy);
        if (_variant != 3)
        {
            var eyebrow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 22),
            };
            eyebrow.Children.Add(new Rectangle
            {
                Width = 24,
                Height = 1,
                Fill = accent,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            eyebrow.Children.Add(Text(_variant == 1 ? L.T("看见空间的每一层") : L.T("你的磁盘，有迹可循"), 12, muted));
            copy.Children.Add(eyebrow);
        }

        var fontSize = _variant == 3 ? 76 : _variant == 1 ? 68 : 62;
        _headline = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        var lines = _variant switch
        {
            2 => new[] { L.T("让每一份空间，"), L.T("都有迹可循。") },
            3 => new[] { L.T("把磁盘，"), L.T("看个明白。") },
            _ => new[] { L.T("空间去哪了，"), L.T("一眼就知道。") },
        };
        for (var index = 0; index < lines.Length; index++)
        {
            var title = Text(lines[index], fontSize, index == 0 ? foreground : titleAccent);
            if (_variant == 3 && index == 1)
            {
                title.Text = string.Empty;
                title.Inlines.Add(new Run(L.T("看个")) { Foreground = foreground });
                title.Inlines.Add(new Run(L.T("明白。")) { Foreground = titleAccent });
            }

            title.LineHeight = fontSize * (_variant == 3 ? 1.13 : 1.25);
            title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            _headline.Children.Add(title);
        }

        copy.Children.Add(_headline);

        var description = _variant switch
        {
            2 => L.T("扫描看清空间占用，AI 辅助判断用途与风险，\n整理哪些内容，由你决定。"),
            3 => L.T("本地空间地图，一眼找到大占用。\nAI 辅助判断用途与风险，再决定怎样整理。"),
            _ => L.T("把磁盘或文件夹，变成清晰的空间地图。\n再用 AI 辅助判断文件用途与清理风险。"),
        };
        _description = Text(description, _variant == 1 ? 16 : 15, muted);
        _description.LineHeight = 29;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.MaxWidth = 430;
        copy.Children.Add(_description);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _primaryButton = CreateButton(_variant switch
        {
            2 => L.T("开始一次扫描"),
            3 => L.T("扫描磁盘"),
            _ => L.T("选择磁盘开始"),
        }, primary: true, foreground, highlight);
        _primaryButton.Command = StartScanCommand;
        _folderButton = CreateButton(L.T("选择文件夹"), primary: false, foreground, highlight);
        _folderButton.Command = BrowseFolderCommand;
        actions.Children.Add(_primaryButton);
        actions.Children.Add(_folderButton);
        _primaryButtonOffset = (TranslateTransform)_primaryButton.RenderTransform;
        _folderButtonOffset = (TranslateTransform)_folderButton.RenderTransform;

        if (_variant == 3)
        {
            var actionBar = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            actionBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actionBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actionBar.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = ResourceBrush("DividerBrush"),
                Margin = new Thickness(0, 0, 0, 25),
            });
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(actions, 1);
            actionBar.Children.Add(actions);
            Grid.SetRow(actionBar, 1);
            stage.Children.Add(actionBar);
        }
        else
        {
            actions.Margin = new Thickness(0, 30, 0, 0);
            copy.Children.Add(actions);
        }

        var artGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-12, 0, -8, 0),
        };
        var svg = ReadArtwork(_variant);
        if (_variant == 2)
        {
            svg = RecolorPaperArtwork(svg);
        }

        _artworkRenderer = new HomeArtworkRenderer();
        _artwork = _artworkRenderer.Convert(svg);
        _artwork.MaxHeight = _variant == 3 ? 430 : 520;
        PrepareArtworkMotion();
        artGrid.Children.Add(_artwork);
        Grid.SetColumn(artGrid, 1);
        body.Children.Add(artGrid);
    }

    private string RecolorPaperArtwork(string svg)
    {
        var accent = ColorFromBrush("AccentBrush");
        var lightAccent = ColorFromBrush("SelectionBorderBrush");
        return svg
            .Replace("#305FDD", accent, StringComparison.OrdinalIgnoreCase)
            .Replace("#416CD2", accent, StringComparison.OrdinalIgnoreCase)
            .Replace("#3260C7", accent, StringComparison.OrdinalIgnoreCase)
            .Replace("#3F6FCE", accent, StringComparison.OrdinalIgnoreCase)
            .Replace("#5F88E4", lightAccent, StringComparison.OrdinalIgnoreCase)
            .Replace("#92B3EE", lightAccent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadArtwork(int variant)
    {
        var resourceName = $"DiskLens.App.Resources.Home.0{variant}.svg";
        using var stream = typeof(HomeLandingControl).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing home illustration {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string ColorFromBrush(string key) => ((SolidColorBrush)ResourceBrush(key)).Color.ToString();

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private TextBlock Text(string value, double size, Brush foreground)
    {
        return new TextBlock
        {
            Text = value,
            FontSize = size,
            Foreground = foreground,
            FontFamily = FontFamily,
            FontWeight = FontWeights.Normal,
        };
    }

    private Button CreateButton(string label, bool primary, Brush foreground, Brush highlight)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!primary)
        {
            var folder = new Path
            {
                Data = Geometry.Parse("M2 7 L2 4 Q2 2 4 2 L9 2 L11 4 L19 4 Q21 4 21 6 L21 17 Q21 19 19 19 L4 19 Q2 19 2 17 Z"),
                Stroke = foreground,
                StrokeThickness = 1.3,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 9, 0),
            };
            content.Children.Add(folder);
        }

        content.Children.Add(Text(label, 14, primary ? ResourceBrush("PrimaryButtonTextBrush") : foreground));
        if (primary)
        {
            content.Children.Add(new Path
            {
                Data = Geometry.Parse("M0 5 H20 M14 0 L20 5 L14 10"),
                Stroke = ResourceBrush("PrimaryButtonTextBrush"),
                StrokeThickness = 1.6,
                Width = 21,
                Height = 12,
                Margin = new Thickness(22, -2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var button = new Button
        {
            Content = content,
            Height = 54,
            MinWidth = primary ? 164 : 0,
            Padding = new Thickness(primary ? 23 : 12, 0, primary ? 23 : 12, 0),
            Margin = new Thickness(primary ? 0 : 12, 0, 0, 0),
            Cursor = Cursors.Hand,
            Foreground = primary ? ResourceBrush("PrimaryButtonTextBrush") : foreground,
            Background = primary ? ResourceBrush("PrimaryButtonBrush") : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = FontFamily,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Focusable = true,
            RenderTransform = new TranslateTransform(),
        };
        AutomationProperties.SetName(button, label);
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.Name = "Surface";
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(primary && _variant == 3 ? 8 : primary ? 27 : 7));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        surface.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, primary ? .88 : 1d));
        if (!primary)
        {
            hover.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("CardSubtleBackgroundBrush"), "Surface"));
        }

        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .78));
        template.Triggers.Add(pressed);
        var focused = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, highlight, "Surface"));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Surface"));
        template.Triggers.Add(focused);
        button.Template = template;
        return button;
    }

    private void PrepareArtworkMotion()
    {
        if (_artworkRenderer is not { } renderer)
        {
            return;
        }

        foreach (var groupName in new[] { "platform", "optic", "paper", "note-card", "cube", "sweep" })
        {
            if (!renderer.Groups.TryGetValue(groupName, out var elements))
            {
                continue;
            }

            var parts = new List<MovingArtworkPart>(elements.Count);
            foreach (var element in elements)
            {
                if (element.RenderTransform is not TransformGroup transforms)
                {
                    transforms = new TransformGroup();
                    element.RenderTransform = transforms;
                }

                var offset = new TranslateTransform();
                transforms.Children.Add(offset);
                var opacity = groupName == "sweep" ? 1 : element.Opacity;
                parts.Add(new MovingArtworkPart(element, offset, opacity));
            }

            _movingArtwork[groupName] = parts;
        }
    }

    private void OnPointerMoved(object sender, MouseEventArgs e)
    {
        SetPointerTargets(e.GetPosition(this));
        if (_reduceMotion)
        {
            AdvancePointerMotion(1);
            return;
        }

        SubscribeToRendering();
    }

    private void OnPointerLeftControl(object sender, MouseEventArgs e)
    {
        _targetPrimaryOffset = _targetFolderOffset = default;
        if (_reduceMotion)
        {
            AdvancePointerMotion(1);
            return;
        }

        SubscribeToRendering();
    }

    private void SetPointerTargets(Point pointer)
    {
        _targetPrimaryOffset = ButtonOffsetNearPointer(_primaryButton, pointer);
        _targetFolderOffset = ButtonOffsetNearPointer(_folderButton, pointer);

        if (_artwork is null || _artwork.ActualWidth <= 0 || _artwork.ActualHeight <= 0)
        {
            return;
        }

        var bounds = _artwork.TransformToAncestor(this).TransformBounds(new Rect(_artwork.RenderSize));
        if (bounds.Contains(pointer) && bounds.Width > 0)
        {
            _targetArtworkProgress = Math.Clamp((pointer.X - bounds.Left) / bounds.Width, 0, 1);
        }
    }

    private Vector ButtonOffsetNearPointer(Button? button, Point pointer)
    {
        if (button is null || button.ActualWidth <= 0 || button.ActualHeight <= 0)
        {
            return default;
        }

        var bounds = button.TransformToAncestor(this).TransformBounds(new Rect(button.RenderSize));
        var closest = new Point(Math.Clamp(pointer.X, bounds.Left, bounds.Right), Math.Clamp(pointer.Y, bounds.Top, bounds.Bottom));
        var distance = (pointer - closest).Length;
        var influence = SmoothStep(1 - distance / 260);
        if (influence <= 0)
        {
            return default;
        }

        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var direction = pointer - center;
        if (direction.Length > 0)
        {
            direction.Normalize();
        }

        return new Vector(direction.X * 3.2 * influence, direction.Y * 2.2 * influence - 3.4 * influence);
    }

    private void SubscribeToRendering()
    {
        if (_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _renderingSubscribed = true;
        _lastRenderTime = null;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame)
        {
            return;
        }

        var delta = _lastRenderTime is { } previous
            ? Math.Clamp((frame.RenderingTime - previous).TotalSeconds, 1d / 240, 1d / 20)
            : 1d / 60;
        _lastRenderTime = frame.RenderingTime;
        AdvancePointerMotion(delta);
    }

    private void AdvancePointerMotion(double deltaSeconds)
    {
        var amount = _reduceMotion ? 1 : 1 - Math.Exp(-24 * Math.Clamp(deltaSeconds, 0, 1));
        _primaryOffset += (_targetPrimaryOffset - _primaryOffset) * amount;
        _folderOffset += (_targetFolderOffset - _folderOffset) * amount;
        _artworkProgress += (_targetArtworkProgress - _artworkProgress) * amount;

        if (VectorNear(_primaryOffset, _targetPrimaryOffset) && VectorNear(_folderOffset, _targetFolderOffset)
            && Math.Abs(_artworkProgress - _targetArtworkProgress) < .001)
        {
            _primaryOffset = _targetPrimaryOffset;
            _folderOffset = _targetFolderOffset;
            _artworkProgress = _targetArtworkProgress;
            StopPointerMotion();
        }

        if (_primaryButtonOffset is { } primary)
        {
            primary.X = _primaryOffset.X;
            primary.Y = _primaryOffset.Y;
        }

        if (_folderButtonOffset is { } folder)
        {
            folder.X = _folderOffset.X;
            folder.Y = _folderOffset.Y;
        }

        ApplyArtworkProgress(_artworkProgress);
    }

    private void StopPointerMotion()
    {
        if (_renderingSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingSubscribed = false;
        }

        _lastRenderTime = null;
    }

    private void ApplyArtworkProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        SetStaggeredGroup("cube", progress, 14, .055, .52, 1);
        SetStaggeredGroup("paper", progress, 18, .12, .64, 1);
        SetStaggeredGroup("note-card", progress, 13, 0, .7, 0);

        SetPulsedGroup("platform", progress, 8, 3);
        SetPulsedGroup("optic", progress, 7, 8);

        if (_movingArtwork.TryGetValue("sweep", out var sweep))
        {
            var beamOpacity = Math.Pow(Math.Sin(Math.PI * progress), .8);
            foreach (var part in sweep)
            {
                part.Offset.X = -36 + progress * 560;
                part.Offset.Y = 0;
                part.Element.Opacity = part.BaseOpacity * beamOpacity;
            }
        }
    }

    private void SetStaggeredGroup(string name, double progress, double lift, double stagger, double span, double horizontal)
    {
        if (!_movingArtwork.TryGetValue(name, out var parts))
        {
            return;
        }

        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var stage = SmoothStep((progress - index * stagger) / span);
            part.Offset.X = Math.Sin(2 * Math.PI * progress) * horizontal * (index % 2 == 0 ? 1 : -1);
            part.Offset.Y = -lift * Math.Sin(Math.PI * stage);
            part.Element.Opacity = part.BaseOpacity;
        }
    }

    private void SetPulsedGroup(string name, double progress, double lift, double horizontal)
    {
        if (!_movingArtwork.TryGetValue(name, out var parts))
        {
            return;
        }

        var liftAmount = Math.Sin(Math.PI * progress);
        var drift = Math.Sin(2 * Math.PI * progress);
        foreach (var part in parts)
        {
            part.Offset.X = drift * horizontal;
            part.Offset.Y = -liftAmount * lift;
            part.Element.Opacity = part.BaseOpacity;
        }
    }

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static bool VectorNear(Vector left, Vector right) => (left - right).Length < .01;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_frame is null || _headline is null || _description is null || _artwork is null || e.NewSize.Width <= 0)
        {
            return;
        }

        var small = e.NewSize.Width < 1000;
        _frame.Padding = new Thickness(small ? 32 : _variant == 3 ? 50 : 62, 26, small ? 24 : 40, 26);
        foreach (var title in _headline.Children.OfType<TextBlock>())
        {
            title.FontSize = small ? _variant == 3 ? 54 : 42 : _variant == 3 ? 76 : _variant == 1 ? 68 : 62;
            title.LineHeight = title.FontSize * (_variant == 3 ? 1.13 : 1.25);
        }

        _description.FontSize = small ? 14 : _variant == 1 ? 16 : 15;
        _artwork.MaxHeight = Math.Max(220, Math.Min(_variant == 3 ? 430 : 520, e.NewSize.Height - (_variant == 3 ? 175 : 92)));
    }

    private static void OnCommandsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not HomeLandingControl home)
        {
            return;
        }

        if (e.Property == StartScanCommandProperty && home._primaryButton is not null)
        {
            home._primaryButton.Command = (ICommand?)e.NewValue;
        }

        if (e.Property == BrowseFolderCommandProperty && home._folderButton is not null)
        {
            home._folderButton.Command = (ICommand?)e.NewValue;
        }
    }
}

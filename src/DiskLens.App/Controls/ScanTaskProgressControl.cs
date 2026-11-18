using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiskLens.App.Controls;

/// <summary>Theme-aware task progress. Completion changes only the status, never the path.</summary>
public sealed class ScanTaskProgressControl : Grid
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(ScanTaskProgressControl), new PropertyMetadata(0d, OnStateChanged));
    public static readonly DependencyProperty RootLabelProperty = DependencyProperty.Register(
        nameof(RootLabel), typeof(string), typeof(ScanTaskProgressControl), new PropertyMetadata(string.Empty, OnStateChanged));
    public static readonly DependencyProperty ProgressTextProperty = DependencyProperty.Register(
        nameof(ProgressText), typeof(string), typeof(ScanTaskProgressControl), new PropertyMetadata("0%", OnStateChanged));
    public static readonly DependencyProperty IsCompleteProperty = DependencyProperty.Register(
        nameof(IsComplete), typeof(bool), typeof(ScanTaskProgressControl), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(ScanTaskProgressControl), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty IsMotionEnabledProperty = DependencyProperty.Register(
        nameof(IsMotionEnabled), typeof(bool), typeof(ScanTaskProgressControl), new PropertyMetadata(true, OnStateChanged));

    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly ScaleTransform _doneScale = new(1, 1);
    private readonly TranslateTransform _doneOffset = new();
    private readonly TranslateTransform _scanOffset = new();
    private readonly Border _scanIndicator = new() { Height = 2, Width = 32, Opacity = .8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly TextBlock _root = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _percent = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _done = new() { Text = "done", FontSize = 13, FontWeight = FontWeights.SemiBold, Opacity = 0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, RenderTransformOrigin = new Point(.5, .5) };
    private bool _wasComplete;
    private bool _indicatorAnimating;

    public ScanTaskProgressControl()
    {
        IsHitTestVisible = false;
        var fill = new Border { Opacity = .24, RenderTransform = _fillScale };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        Children.Add(fill);
        _scanIndicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _scanIndicator.RenderTransform = _scanOffset;
        Children.Add(_scanIndicator);
        var labels = new Grid { Margin = new Thickness(10, 0, 32, 0) };
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        _root.Margin = new Thickness(0, 0, 6, 0);
        labels.Children.Add(_root);
        Grid.SetColumn(_percent, 1);
        Grid.SetColumn(_done, 1);
        labels.Children.Add(_percent);
        labels.Children.Add(_done);
        Children.Add(labels);
        foreach (var label in new[] { _root, _percent, _done })
            label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        var doneTransform = new TransformGroup();
        doneTransform.Children.Add(_doneScale);
        doneTransform.Children.Add(_doneOffset);
        _done.RenderTransform = doneTransform;
        SizeChanged += (_, _) =>
        {
            Clip = new RectangleGeometry(new Rect(RenderSize), 5, 5);
            _indicatorAnimating = false;
            UpdateIndeterminate(IsLoaded && IsMotionEnabled);
        };
        Loaded += (_, _) => UpdateState(animate: false);
        Unloaded += (_, _) => StopAnimations();
    }

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public string RootLabel { get => (string)GetValue(RootLabelProperty); set => SetValue(RootLabelProperty, value); }
    public string ProgressText { get => (string)GetValue(ProgressTextProperty); set => SetValue(ProgressTextProperty, value); }
    public bool IsComplete { get => (bool)GetValue(IsCompleteProperty); set => SetValue(IsCompleteProperty, value); }
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public bool IsMotionEnabled { get => (bool)GetValue(IsMotionEnabledProperty); set => SetValue(IsMotionEnabledProperty, value); }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ScanTaskProgressControl)sender;
        control.UpdateState(control.IsLoaded && control.IsMotionEnabled);
    }

    private void UpdateState(bool animate)
    {
        UpdateRootLabel();
        _percent.Text = ProgressText;
        UpdateIndeterminate(animate);
        var target = Math.Clamp(double.IsFinite(Percent) ? Percent : 0, 0, 100) / 100;
        var current = _fillScale.ScaleX;
        _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _fillScale.ScaleX = target;
        if (animate && Math.Abs(current - target) > .0001)
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        if (IsComplete && !_wasComplete && animate)
        {
            _percent.Opacity = 1;
            _done.Opacity = 0;
            _percent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { BeginTime = TimeSpan.FromMilliseconds(300) });
            _done.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = TimeSpan.FromMilliseconds(410) });
            var lift = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(410) };
            lift.KeyFrames.Add(new EasingDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            lift.KeyFrames.Add(new EasingDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            lift.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            _doneOffset.BeginAnimation(TranslateTransform.YProperty, lift);
            foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
                _doneScale.BeginAnimation(property, new DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(340)) { BeginTime = TimeSpan.FromMilliseconds(410), EasingFunction = new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut } });
        }
        else if (!IsComplete || !animate)
        {
            StopStatusAnimations();
            _percent.Opacity = IsComplete ? 0 : 1;
            _done.Opacity = IsComplete ? 1 : 0;
        }
        _wasComplete = IsComplete;
    }

    private void UpdateRootLabel()
    {
        var path = RootLabel ?? string.Empty;
        // Keep the distinctive folder name visible when several tasks share
        // a long parent path. The full root stays available in the tab tooltip.
        if (path.Length > 16)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var leaf = System.IO.Path.GetFileName(trimmed);
            var drive = System.IO.Path.GetPathRoot(path) ?? string.Empty;
            if (!string.IsNullOrEmpty(leaf) && trimmed.Length > drive.TrimEnd('\\', '/').Length)
                path = $"{drive}…\\{leaf}";
        }
        _root.Text = path;
    }

    private void UpdateIndeterminate(bool animate)
    {
        var unknown = IsIndeterminate && !IsComplete;
        _scanIndicator.Visibility = unknown ? Visibility.Visible : Visibility.Collapsed;
        if (unknown && animate && ActualWidth > 0)
        {
            if (_indicatorAnimating) return;
            _scanOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-32, ActualWidth, TimeSpan.FromMilliseconds(1800)) { RepeatBehavior = RepeatBehavior.Forever });
            _indicatorAnimating = true;
        }
        else
        {
            _scanOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _scanOffset.X = Math.Max(0, (ActualWidth - 32) / 2);
            _indicatorAnimating = false;
        }
    }

    private void StopStatusAnimations()
    {
        _percent.BeginAnimation(OpacityProperty, null);
        _done.BeginAnimation(OpacityProperty, null);
        _doneOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _doneScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _doneScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void StopAnimations()
    {
        _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scanOffset.BeginAnimation(TranslateTransform.XProperty, null);
        _indicatorAnimating = false;
        StopStatusAnimations();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiskLens.App.Controls;

public sealed class ScanRunnerControl : FrameworkElement
{
    private const double TickIntervalMilliseconds = 33;
    private const double Gravity = 2100;
    private const double JumpVelocity = -720;
    private const double GroundOffset = 34;
    private const double PlayerSize = 28;
    private const double BaseSpeed = 280;
    private const double ObstacleSpacingMinimum = 190;
    private const double ObstacleSpacingVariance = 160;
    private const double ObstacleSpacingSpeedFactor = 0.22;
    private const string ReadyHintText = "点击开始游戏后即可起跳躲避文件方块";
    private const string RestartLabel = "重新开始";

    private static readonly Brush SkyBrush = Freeze(new LinearGradientBrush(
        Color.FromRgb(245, 249, 253),
        Color.FromRgb(233, 241, 248),
        new Point(0, 0),
        new Point(1, 1)));
    private static readonly Brush GroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(201, 214, 226)));
    private static readonly Brush PlayerBrush = Freeze(new SolidColorBrush(Color.FromRgb(122, 146, 170)));
    private static readonly Brush ObstacleBrush = Freeze(new SolidColorBrush(Color.FromRgb(155, 175, 194)));
    private static readonly Brush CloudBrush = Freeze(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)));
    private static readonly Brush OverlayBrush = Freeze(new SolidColorBrush(Color.FromArgb(214, 248, 251, 254)));
    private static readonly Brush OverlayTitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 23, 42)));
    private static readonly Brush OverlayBodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(93, 109, 126)));
    private static readonly Brush ButtonBrush = Freeze(new SolidColorBrush(Color.FromRgb(144, 171, 196)));
    private static readonly Brush ButtonTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 255, 255)));
    private static readonly Pen HorizonPen = CreatePen(Color.FromRgb(184, 198, 211), 1.2);
    private static readonly Pen OverlayOutlinePen = CreatePen(Color.FromRgb(213, 225, 236), 1);
    private static readonly Pen ButtonOutlinePen = CreatePen(Color.FromRgb(136, 160, 183), 1);
    private static readonly Typeface TitleTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface BodyTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface ButtonTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(ScanRunnerControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsActiveChanged));

    public static readonly DependencyProperty ThemeSkyBrushProperty = RegisterThemeBrush(nameof(ThemeSkyBrush), SkyBrush);
    public static readonly DependencyProperty ThemeGroundBrushProperty = RegisterThemeBrush(nameof(ThemeGroundBrush), GroundBrush);
    public static readonly DependencyProperty ThemePlayerBrushProperty = RegisterThemeBrush(nameof(ThemePlayerBrush), PlayerBrush);
    public static readonly DependencyProperty ThemeObstacleBrushProperty = RegisterThemeBrush(nameof(ThemeObstacleBrush), ObstacleBrush);
    public static readonly DependencyProperty ThemeOverlayBrushProperty = RegisterThemeBrush(nameof(ThemeOverlayBrush), OverlayBrush);
    public static readonly DependencyProperty ThemeTextBrushProperty = RegisterThemeBrush(nameof(ThemeTextBrush), OverlayTitleBrush);
    public static readonly DependencyProperty ThemeSecondaryTextBrushProperty = RegisterThemeBrush(nameof(ThemeSecondaryTextBrush), OverlayBodyBrush);
    public static readonly DependencyProperty ThemeMutedTextBrushProperty = RegisterThemeBrush(nameof(ThemeMutedTextBrush), OverlayBodyBrush);
    public static readonly DependencyProperty ThemeButtonBrushProperty = RegisterThemeBrush(nameof(ThemeButtonBrush), ButtonBrush);
    public static readonly DependencyProperty ThemeButtonTextBrushProperty = RegisterThemeBrush(nameof(ThemeButtonTextBrush), ButtonTextBrush);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Random _random = new();
    private readonly List<Obstacle> _obstacles = [];
    private readonly List<Cloud> _clouds = [];
    private RunnerState _currentState = RunnerState.Ready;
    private long _lastTickMilliseconds;
    private double _playerY;
    private double _playerVelocity;
    private int _score;
    private int _bestScore;
    private int _lastRunScore;
    private bool _hasInitializedWorld;
    private Rect _callToActionRect = Rect.Empty;

    public ScanRunnerControl()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(TickIntervalMilliseconds),
            DispatcherPriority.Background,
            (_, _) => AdvanceFrame(),
            Dispatcher);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush ThemeSkyBrush { get => (Brush)GetValue(ThemeSkyBrushProperty); set => SetValue(ThemeSkyBrushProperty, value); }
    public Brush ThemeGroundBrush { get => (Brush)GetValue(ThemeGroundBrushProperty); set => SetValue(ThemeGroundBrushProperty, value); }
    public Brush ThemePlayerBrush { get => (Brush)GetValue(ThemePlayerBrushProperty); set => SetValue(ThemePlayerBrushProperty, value); }
    public Brush ThemeObstacleBrush { get => (Brush)GetValue(ThemeObstacleBrushProperty); set => SetValue(ThemeObstacleBrushProperty, value); }
    public Brush ThemeOverlayBrush { get => (Brush)GetValue(ThemeOverlayBrushProperty); set => SetValue(ThemeOverlayBrushProperty, value); }
    public Brush ThemeTextBrush { get => (Brush)GetValue(ThemeTextBrushProperty); set => SetValue(ThemeTextBrushProperty, value); }
    public Brush ThemeSecondaryTextBrush { get => (Brush)GetValue(ThemeSecondaryTextBrushProperty); set => SetValue(ThemeSecondaryTextBrushProperty, value); }
    public Brush ThemeMutedTextBrush { get => (Brush)GetValue(ThemeMutedTextBrushProperty); set => SetValue(ThemeMutedTextBrushProperty, value); }
    public Brush ThemeButtonBrush { get => (Brush)GetValue(ThemeButtonBrushProperty); set => SetValue(ThemeButtonBrushProperty, value); }
    public Brush ThemeButtonTextBrush { get => (Brush)GetValue(ThemeButtonTextBrushProperty); set => SetValue(ThemeButtonTextBrushProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(ThemeSkyBrush, null, bounds, 18, 18);

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        EnsureWorld();

        foreach (var cloud in _clouds)
        {
            DrawCloud(drawingContext, cloud);
        }

        var groundY = GetGroundY();
        drawingContext.DrawLine(HorizonPen, new Point(0, groundY), new Point(ActualWidth, groundY));
        drawingContext.DrawRectangle(ThemeGroundBrush, null, new Rect(0, groundY, ActualWidth, Math.Max(0, ActualHeight - groundY)));

        foreach (var obstacle in _obstacles)
        {
            var rect = new Rect(obstacle.X, groundY - obstacle.Height, obstacle.Width, obstacle.Height);
            drawingContext.DrawRoundedRectangle(ThemeObstacleBrush, null, rect, 6, 6);
        }

        var playerRect = new Rect(42, _playerY, PlayerSize, PlayerSize);
        drawingContext.DrawRoundedRectangle(ThemePlayerBrush, null, playerRect, 8, 8);
        drawingContext.DrawEllipse(Brushes.White, null, new Point(playerRect.X + 19, playerRect.Y + 10), 2.2, 2.2);

        DrawHud(drawingContext);

        if (!IsActive)
        {
            _callToActionRect = Rect.Empty;
            return;
        }

        switch (_currentState)
        {
            case RunnerState.Ready:
                DrawOverlay(
                    drawingContext,
                    L.T("扫描跳跃小游戏"),
                    ReadyHintText,
                    L.T("开始游戏"));
                break;
            case RunnerState.GameOver:
                DrawOverlay(
                    drawingContext,
                    L.F($"本轮结束 · 得分 {_lastRunScore}"),
                    L.T("你撞上了文件方块，点击“重新开始”再试一次。"),
                    RestartLabel);
                break;
            default:
                _callToActionRect = Rect.Empty;
                break;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (!IsActive)
        {
            return;
        }

        var clickPoint = e.GetPosition(this);
        if (_currentState == RunnerState.Running)
        {
            TryJump();
            e.Handled = true;
            return;
        }

        if (!_callToActionRect.Contains(clickPoint))
        {
            return;
        }

        if (_currentState == RunnerState.Ready)
        {
            StartGame();
            e.Handled = true;
            return;
        }

        if (_currentState == RunnerState.GameOver)
        {
            RestartGame();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!IsActive)
        {
            return;
        }

        if (_currentState == RunnerState.Running)
        {
            if (e.Key == Key.Space || e.Key == Key.Up)
            {
                TryJump();
                e.Handled = true;
            }

            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        if (_currentState == RunnerState.Ready)
        {
            StartGame();
            e.Handled = true;
            return;
        }

        if (_currentState == RunnerState.GameOver)
        {
            RestartGame();
            e.Handled = true;
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ScanRunnerControl)d).HandleIsActiveChanged((bool)e.NewValue);
    }

    private void HandleIsActiveChanged(bool isActive)
    {
        _timer.Stop();

        if (isActive)
        {
            _hasInitializedWorld = false;
            _obstacles.Clear();
            _clouds.Clear();
            _score = 0;
            _lastRunScore = 0;
            _playerVelocity = 0;
            _currentState = RunnerState.Ready;
            _lastTickMilliseconds = _stopwatch.ElapsedMilliseconds;
            InvalidateVisual();
            Focus();
            return;
        }

        _currentState = RunnerState.Ready;
        _callToActionRect = Rect.Empty;
        InvalidateVisual();
    }

    private void StartGame()
    {
        if (!IsActive)
        {
            return;
        }

        EnsureWorld();
        ResetWorld();
        _lastRunScore = 0;
        _currentState = RunnerState.Running;
        _lastTickMilliseconds = _stopwatch.ElapsedMilliseconds;
        _timer.Start();
        Focus();
        InvalidateVisual();
    }

    private void RestartGame()
    {
        StartGame();
    }

    private void AdvanceFrame()
    {
        if (!IsActive || _currentState != RunnerState.Running)
        {
            return;
        }

        EnsureWorld();

        var now = _stopwatch.ElapsedMilliseconds;
        var deltaSeconds = Math.Clamp((now - _lastTickMilliseconds) / 1000d, 0.01, 0.05);
        _lastTickMilliseconds = now;

        var groundY = GetGroundY();
        _playerVelocity += Gravity * deltaSeconds;
        _playerY += _playerVelocity * deltaSeconds;
        var maxPlayerY = groundY - PlayerSize;
        if (_playerY >= maxPlayerY)
        {
            _playerY = maxPlayerY;
            _playerVelocity = 0;
        }

        var speed = BaseSpeed + Math.Min(160, _score * 0.8);
        for (var index = _obstacles.Count - 1; index >= 0; index--)
        {
            var obstacle = _obstacles[index];
            obstacle.X -= speed * deltaSeconds;
            if (obstacle.X + obstacle.Width < -8)
            {
                _obstacles.RemoveAt(index);
                continue;
            }

            if (IntersectsPlayer(obstacle, groundY))
            {
                HandleGameOver();
                return;
            }
        }

        for (var index = _clouds.Count - 1; index >= 0; index--)
        {
            var cloud = _clouds[index];
            cloud.X -= cloud.Speed * deltaSeconds;
            if (cloud.X + cloud.Width < -10)
            {
                _clouds.RemoveAt(index);
            }
        }

        SpawnCloudIfNeeded();
        SpawnObstacleIfNeeded(speed);

        _score += Math.Max(1, (int)Math.Round(deltaSeconds * 18));
        _bestScore = Math.Max(_bestScore, _score);
        InvalidateVisual();
    }

    private void HandleGameOver()
    {
        _timer.Stop();
        _lastRunScore = _score;
        _bestScore = Math.Max(_bestScore, _score);
        _currentState = RunnerState.GameOver;
        InvalidateVisual();
    }

    private void TryJump()
    {
        if (!IsActive || _currentState != RunnerState.Running)
        {
            return;
        }

        EnsureWorld();
        var groundY = GetGroundY();
        var maxPlayerY = groundY - PlayerSize;
        if (_playerY >= maxPlayerY - 1)
        {
            _playerVelocity = JumpVelocity;
        }
    }

    private void EnsureWorld()
    {
        if (ActualWidth < 160 || ActualHeight < 120)
        {
            return;
        }

        if (_hasInitializedWorld)
        {
            return;
        }

        ResetWorld();
    }

    private void ResetWorld()
    {
        _obstacles.Clear();
        _clouds.Clear();
        _score = 0;
        _playerVelocity = 0;
        _playerY = GetGroundY() - PlayerSize;
        _hasInitializedWorld = true;

        for (var index = 0; index < 3; index++)
        {
            _clouds.Add(CreateCloud(initial: true));
        }

        _obstacles.Add(CreateObstacle(ActualWidth * 0.8));
    }

    private void SpawnObstacleIfNeeded(double speed)
    {
        if (_obstacles.Count == 0)
        {
            _obstacles.Add(CreateObstacle(ActualWidth + 40));
            return;
        }

        var last = _obstacles[^1];
        var spacing = ObstacleSpacingMinimum + (_random.NextDouble() * ObstacleSpacingVariance) + (speed * ObstacleSpacingSpeedFactor);
        if (last.X < ActualWidth - spacing)
        {
            _obstacles.Add(CreateObstacle(ActualWidth + 24));
        }
    }

    private void SpawnCloudIfNeeded()
    {
        if (_clouds.Count == 0 || _clouds[^1].X < ActualWidth - 180)
        {
            _clouds.Add(CreateCloud(initial: false));
        }
    }

    private Obstacle CreateObstacle(double x)
    {
        var width = 18 + _random.NextDouble() * 16;
        var height = 20 + _random.NextDouble() * 24;
        return new Obstacle(x, width, height);
    }

    private Cloud CreateCloud(bool initial)
    {
        var width = 52 + _random.NextDouble() * 38;
        var height = 16 + _random.NextDouble() * 10;
        var x = initial ? (_random.NextDouble() * Math.Max(120, ActualWidth - width)) : ActualWidth + 20;
        var y = 18 + (_random.NextDouble() * Math.Max(12, (ActualHeight * 0.26) - height));
        var speed = 16 + _random.NextDouble() * 18;
        return new Cloud(x, y, width, height, speed);
    }

    private bool IntersectsPlayer(Obstacle obstacle, double groundY)
    {
        var playerRect = new Rect(42, _playerY, PlayerSize, PlayerSize);
        var obstacleRect = new Rect(obstacle.X, groundY - obstacle.Height, obstacle.Width, obstacle.Height);
        return playerRect.IntersectsWith(obstacleRect);
    }

    private double GetGroundY()
    {
        return Math.Max(72, ActualHeight - GroundOffset);
    }

    private void DrawCloud(DrawingContext drawingContext, Cloud cloud)
    {
        var centerY = cloud.Y + (cloud.Height * 0.5);
        drawingContext.DrawEllipse(CloudBrush, null, new Point(cloud.X + 16, centerY), 14, 8);
        drawingContext.DrawEllipse(CloudBrush, null, new Point(cloud.X + 30, centerY - 4), 18, 10);
        drawingContext.DrawEllipse(CloudBrush, null, new Point(cloud.X + 48, centerY), 14, 8);
    }

    private void DrawHud(DrawingContext drawingContext)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var scoreText = CreateText(L.F($"得分 {_score}"), ButtonTypeface, 13, ThemeSecondaryTextBrush, dpi);
        drawingContext.DrawText(scoreText, new Point(Math.Max(12, ActualWidth - 110), 12));

        var bestText = CreateText(L.F($"最高 {_bestScore}"), ButtonTypeface, 12, ThemeMutedTextBrush, dpi);
        drawingContext.DrawText(bestText, new Point(14, 12));
    }

    private void DrawOverlay(DrawingContext drawingContext, string title, string message, string buttonText)
    {
        var panelWidth = Math.Max(1, Math.Min(ActualWidth - 24, 264));
        var textWidth = Math.Max(1, panelWidth - 32);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var titleText = CreateText(title, TitleTypeface, 16, ThemeTextBrush, dpi, textWidth);
        var bodyText = CreateText(message, BodyTypeface, 11.5, ThemeSecondaryTextBrush, dpi, textWidth);
        const double buttonHeight = 36;
        var panelHeight = Math.Min(Math.Max(1, ActualHeight - 24), 16 + titleText.Height + 8 + bodyText.Height + 12 + buttonHeight + 14);
        var panelRect = new Rect(
            Math.Max(0, (ActualWidth - panelWidth) / 2),
            Math.Max(0, (ActualHeight - panelHeight) / 2),
            panelWidth,
            panelHeight);

        drawingContext.DrawRoundedRectangle(ThemeOverlayBrush, OverlayOutlinePen, panelRect, 12, 12);

        drawingContext.DrawText(titleText, new Point(panelRect.X + 16, panelRect.Y + 16));
        var bodyY = panelRect.Y + 16 + titleText.Height + 8;
        bodyText.MaxTextHeight = Math.Max(1, panelRect.Bottom - buttonHeight - 26 - bodyY);
        bodyText.Trimming = TextTrimming.CharacterEllipsis;
        drawingContext.DrawText(bodyText, new Point(panelRect.X + 16, bodyY));

        var buttonWidth = Math.Max(1, Math.Min(148, panelWidth - 24));
        _callToActionRect = new Rect(
            panelRect.X + ((panelRect.Width - buttonWidth) / 2),
            panelRect.Bottom - buttonHeight - 16,
            buttonWidth,
            buttonHeight);

        drawingContext.DrawRoundedRectangle(ThemeButtonBrush, ButtonOutlinePen, _callToActionRect, 8, 8);

        var buttonLabel = CreateText(buttonText, ButtonTypeface, 13, ThemeButtonTextBrush, dpi);
        var labelX = _callToActionRect.X + ((_callToActionRect.Width - buttonLabel.Width) / 2);
        var labelY = _callToActionRect.Y + ((_callToActionRect.Height - buttonLabel.Height) / 2);
        drawingContext.DrawText(buttonLabel, new Point(labelX, labelY));
    }

    private static FormattedText CreateText(
        string text,
        Typeface typeface,
        double size,
        Brush brush,
        double pixelsPerDip,
        double? maxTextWidth = null)
    {
        var formattedText = new FormattedText(
            L.T(text),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            pixelsPerDip);

        if (maxTextWidth is double width)
        {
            formattedText.MaxTextWidth = width;
        }

        return formattedText;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static DependencyProperty RegisterThemeBrush(string propertyName, Brush defaultValue)
    {
        return DependencyProperty.Register(
            propertyName,
            typeof(Brush),
            typeof(ScanRunnerControl),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private enum RunnerState
    {
        Ready,
        Running,
        GameOver
    }

    private sealed class Obstacle
    {
        public Obstacle(double x, double width, double height)
        {
            X = x;
            Width = width;
            Height = height;
        }

        public double X { get; set; }
        public double Width { get; }
        public double Height { get; }
    }

    private sealed class Cloud
    {
        public Cloud(double x, double y, double width, double height, double speed)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Speed = speed;
        }

        public double X { get; set; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public double Speed { get; }
    }
}

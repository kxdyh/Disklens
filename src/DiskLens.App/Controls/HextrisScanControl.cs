using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiskLens.App.Controls;

public sealed class HextrisScanControl : FrameworkElement
{
    private const double TickIntervalMilliseconds = 16;
    private const double BaseFallSpeed = 76;
    private const double RotationStepDegrees = 60;
    private const double RotationSmoothing = 14;
    private const double CoreRadius = 44;
    private const double RingGap = 10;
    private const double SegmentDepth = 24;
    private const int LaneCount = 6;
    private const int MatchThreshold = 3;
    private const int MaxLaneHeight = 6;
    private const string StartLabel = "Start Game";
    private const string RestartLabel = "Restart";

    private static readonly Color[] Palette =
    [
        Color.FromRgb(244, 73, 63),
        Color.FromRgb(250, 197, 24),
        Color.FromRgb(27, 205, 127),
        Color.FromRgb(47, 150, 221),
    ];

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(239, 243, 246)));
    private static readonly Brush OuterHexBrush = Freeze(new SolidColorBrush(Color.FromRgb(206, 213, 220)));
    private static readonly Brush CoreBrush = Freeze(new SolidColorBrush(Color.FromRgb(43, 59, 78)));
    private static readonly Brush HudLabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(194, 203, 212)));
    private static readonly Brush HudValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(52, 72, 93)));
    private static readonly Brush OverlayBrush = Freeze(new SolidColorBrush(Color.FromArgb(220, 248, 251, 253)));
    private static readonly Brush OverlayTitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 23, 42)));
    private static readonly Brush OverlayBodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(92, 108, 124)));
    private static readonly Brush ButtonBrush = Freeze(new SolidColorBrush(Color.FromRgb(59, 130, 246)));
    private static readonly Brush ButtonTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 255, 255)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 198, 30)));
    private static readonly Pen OuterHexPen = CreatePen(Color.FromRgb(52, 152, 219), 3.2);
    private static readonly Pen OverlayPen = CreatePen(Color.FromRgb(214, 225, 235), 1);
    private static readonly Pen ButtonPen = CreatePen(Color.FromRgb(37, 99, 235), 1);
    private static readonly Typeface LightTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiboldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(HextrisScanControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsActiveChanged));

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Random _random = new();
    private readonly List<FallingPiece> _fallingPieces = [];
    private readonly List<int>[] _laneStacks =
        Enumerable.Range(0, LaneCount)
            .Select(_ => new List<int>())
            .ToArray();
    private GameState _currentState = GameState.Ready;
    private Rect _callToActionRect = Rect.Empty;
    private long _lastTickMilliseconds;
    private int _rotationSteps;
    private double _currentRotationDegrees;
    private int _score;
    private int _bestScore;
    private int _lastRunScore;

    public HextrisScanControl()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(TickIntervalMilliseconds),
            DispatcherPriority.Render,
            (_, _) => AdvanceFrame(),
            Dispatcher);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(BackgroundBrush, null, bounds, 18, 18);

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2 + 8);
        var outerRadius = Math.Min(ActualWidth, ActualHeight) * 0.36;
        var playRadius = outerRadius * 0.62;

        DrawDecorativeAccents(drawingContext, center, outerRadius);
        DrawHud(drawingContext);
        DrawOuterHexagon(drawingContext, center, playRadius);
        DrawAttachedSegments(drawingContext, center);
        DrawFallingPieces(drawingContext, center);
        DrawCore(drawingContext, center);

        if (!IsActive)
        {
            _callToActionRect = Rect.Empty;
            return;
        }

        switch (_currentState)
        {
            case GameState.Ready:
                DrawOverlay(
                    drawingContext,
                    center,
                    "Hextris Scan Break",
                    "Rotate the center with A / D or Left / Right. Match 3 colors to clear.",
                    StartLabel);
                break;
            case GameState.GameOver:
                DrawOverlay(
                    drawingContext,
                    center,
                    $"Game Over - { _lastRunScore }",
                    "Stacks reached the core. Restart and keep the scan break going.",
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

        var point = e.GetPosition(this);
        if (_currentState == GameState.Running)
        {
            RotateRight();
            e.Handled = true;
            return;
        }

        if (!_callToActionRect.Contains(point))
        {
            return;
        }

        if (_currentState == GameState.Ready)
        {
            StartGame();
            e.Handled = true;
            return;
        }

        if (_currentState == GameState.GameOver)
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

        if (_currentState == GameState.Running)
        {
            if (e.Key == Key.A || e.Key == Key.Left)
            {
                RotateLeft();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.D || e.Key == Key.Right)
            {
                RotateRight();
                e.Handled = true;
                return;
            }

            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Space)
        {
            return;
        }

        if (_currentState == GameState.Ready)
        {
            StartGame();
            e.Handled = true;
            return;
        }

        if (_currentState == GameState.GameOver)
        {
            RestartGame();
            e.Handled = true;
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HextrisScanControl)d).HandleIsActiveChanged((bool)e.NewValue);
    }

    private void HandleIsActiveChanged(bool isActive)
    {
        _timer.Stop();
        _callToActionRect = Rect.Empty;

        if (isActive)
        {
            ResetBoard();
            _currentState = GameState.Ready;
            _lastTickMilliseconds = _stopwatch.ElapsedMilliseconds;
            Focus();
            InvalidateVisual();
            return;
        }

        _currentState = GameState.Ready;
        InvalidateVisual();
    }

    private void StartGame()
    {
        if (!IsActive)
        {
            return;
        }

        ResetBoard();
        _currentState = GameState.Running;
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
        if (!IsActive || _currentState != GameState.Running)
        {
            return;
        }

        var now = _stopwatch.ElapsedMilliseconds;
        var deltaSeconds = Math.Clamp((now - _lastTickMilliseconds) / 1000d, 0.008, 0.04);
        _lastTickMilliseconds = now;

        UpdateRotation(deltaSeconds);
        SpawnWaveIfNeeded();
        UpdateFallingPieces(deltaSeconds);
        InvalidateVisual();
    }

    private void UpdateRotation(double deltaSeconds)
    {
        var targetRotation = _rotationSteps * RotationStepDegrees;
        _currentRotationDegrees = Lerp(_currentRotationDegrees, targetRotation, Math.Min(1, deltaSeconds * RotationSmoothing));
    }

    private void SpawnWaveIfNeeded()
    {
        if (_fallingPieces.Count > 0 && _fallingPieces[^1].Distance > 170)
        {
            return;
        }

        var count = _random.NextDouble() > 0.72 ? 2 : 1;
        var usedLanes = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var worldLane = _random.Next(0, LaneCount);
            while (!usedLanes.Add(worldLane))
            {
                worldLane = _random.Next(0, LaneCount);
            }

            _fallingPieces.Add(new FallingPiece(worldLane, _random.Next(0, Palette.Length), 320 + (_random.NextDouble() * 90)));
        }
    }

    private void UpdateFallingPieces(double deltaSeconds)
    {
        var speed = BaseFallSpeed + Math.Min(90, _score * 0.18);
        for (var index = _fallingPieces.Count - 1; index >= 0; index--)
        {
            var piece = _fallingPieces[index];
            piece.Distance -= speed * deltaSeconds;

            var targetLane = NormalizeLane(piece.WorldLane - _rotationSteps);
            var laneDepth = _laneStacks[targetLane].Count * SegmentDepth;
            var attachmentDistance = RingGap + CoreRadius + laneDepth + (SegmentDepth * 0.45);
            if (piece.Distance > attachmentDistance)
            {
                continue;
            }

            _fallingPieces.RemoveAt(index);
            _laneStacks[targetLane].Add(piece.ColorIndex);
            _score += 4;
            _bestScore = Math.Max(_bestScore, _score);
            TryResolveMatches();

            if (_laneStacks[targetLane].Count >= MaxLaneHeight)
            {
                EnterGameOver();
                return;
            }
        }
    }

    private void TryResolveMatches()
    {
        var clearedAny = true;
        while (clearedAny)
        {
            clearedAny = false;
            var visited = new bool[LaneCount];

            for (var start = 0; start < LaneCount; start++)
            {
                if (visited[start] || _laneStacks[start].Count == 0)
                {
                    continue;
                }

                var colorIndex = _laneStacks[start][^1];
                var run = new List<int> { start };
                visited[start] = true;

                for (var offset = 1; offset < LaneCount; offset++)
                {
                    var lane = (start + offset) % LaneCount;
                    if (_laneStacks[lane].Count == 0 || _laneStacks[lane][^1] != colorIndex)
                    {
                        break;
                    }

                    run.Add(lane);
                    visited[lane] = true;
                }

                if (run.Count < MatchThreshold)
                {
                    continue;
                }

                foreach (var lane in run)
                {
                    _laneStacks[lane].RemoveAt(_laneStacks[lane].Count - 1);
                }

                _score += run.Count * 18;
                _bestScore = Math.Max(_bestScore, _score);
                clearedAny = true;
                break;
            }
        }
    }

    private void EnterGameOver()
    {
        _timer.Stop();
        _currentState = GameState.GameOver;
        _lastRunScore = _score;
        _bestScore = Math.Max(_bestScore, _score);
        InvalidateVisual();
    }

    private void RotateLeft()
    {
        _rotationSteps--;
    }

    private void RotateRight()
    {
        _rotationSteps++;
    }

    private void ResetBoard()
    {
        foreach (var lane in _laneStacks)
        {
            lane.Clear();
        }

        _fallingPieces.Clear();
        _score = 0;
        _lastRunScore = 0;
        _rotationSteps = 0;
        _currentRotationDegrees = 0;
    }

    private void DrawHud(DrawingContext drawingContext)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var highScoreLabel = CreateText("HIGH SCORE", LightTypeface, 12.5, HudLabelBrush, dpi);
        drawingContext.DrawText(highScoreLabel, new Point((ActualWidth - highScoreLabel.Width) / 2, 6));

        var highScoreValue = CreateText(_bestScore.ToString(CultureInfo.InvariantCulture), LightTypeface, 26, HudValueBrush, dpi);
        drawingContext.DrawText(highScoreValue, new Point((ActualWidth - highScoreValue.Width) / 2, 20));

        var scoreValue = CreateText(_score.ToString(CultureInfo.InvariantCulture), SemiboldTypeface, 22, Brushes.White, dpi);
        var center = new Point(ActualWidth / 2, ActualHeight / 2 + 8);
        drawingContext.DrawText(scoreValue, new Point(center.X - (scoreValue.Width / 2), center.Y - (scoreValue.Height / 2) + 2));
    }

    private void DrawDecorativeAccents(DrawingContext drawingContext, Point center, double outerRadius)
    {
        DrawSlash(drawingContext, new Point(center.X - outerRadius - 88, center.Y - 18), 160, -28);
        DrawSlash(drawingContext, new Point(center.X + outerRadius + 88, center.Y + 18), 160, 28);
    }

    private void DrawSlash(DrawingContext drawingContext, Point center, double length, double rotationDegrees)
    {
        drawingContext.PushTransform(new TranslateTransform(center.X, center.Y));
        drawingContext.PushTransform(new RotateTransform(rotationDegrees));
        drawingContext.DrawRoundedRectangle(AccentBrush, null, new Rect(-12, -length / 2, 24, length), 8, 8);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void DrawOuterHexagon(DrawingContext drawingContext, Point center, double radius)
    {
        var geometry = CreateHexagonGeometry(center, radius, 0);
        drawingContext.DrawGeometry(OuterHexBrush, OuterHexPen, geometry);
    }

    private void DrawCore(DrawingContext drawingContext, Point center)
    {
        var geometry = CreateHexagonGeometry(center, CoreRadius, _currentRotationDegrees);
        drawingContext.DrawGeometry(CoreBrush, null, geometry);
    }

    private void DrawAttachedSegments(DrawingContext drawingContext, Point center)
    {
        for (var lane = 0; lane < LaneCount; lane++)
        {
            for (var depth = 0; depth < _laneStacks[lane].Count; depth++)
            {
                var geometry = CreateSegmentGeometry(center, lane, depth, SegmentDepth, RingGap + CoreRadius, _currentRotationDegrees);
                drawingContext.DrawGeometry(CreateColorBrush(_laneStacks[lane][depth]), null, geometry);
            }
        }
    }

    private void DrawFallingPieces(DrawingContext drawingContext, Point center)
    {
        foreach (var piece in _fallingPieces.OrderByDescending(item => item.Distance))
        {
            var geometry = CreateSegmentGeometry(center, piece.WorldLane, 0, SegmentDepth, piece.Distance, 0);
            drawingContext.DrawGeometry(CreateColorBrush(piece.ColorIndex), null, geometry);
        }
    }

    private void DrawOverlay(DrawingContext drawingContext, Point center, string title, string body, string buttonText)
    {
        var panelWidth = Math.Min(ActualWidth - 56, 330);
        var panelHeight = 144d;
        var panelRect = new Rect(
            center.X - (panelWidth / 2),
            center.Y - (panelHeight / 2),
            panelWidth,
            panelHeight);

        drawingContext.DrawRoundedRectangle(OverlayBrush, OverlayPen, panelRect, 18, 18);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var titleText = CreateText(title, BoldTypeface, 20, OverlayTitleBrush, dpi, panelRect.Width - 44);
        drawingContext.DrawText(titleText, new Point(panelRect.X + 22, panelRect.Y + 18));

        var bodyText = CreateText(body, LightTypeface, 12.5, OverlayBodyBrush, dpi, panelRect.Width - 44);
        drawingContext.DrawText(bodyText, new Point(panelRect.X + 22, panelRect.Y + 54));

        _callToActionRect = new Rect(panelRect.X + ((panelRect.Width - 148) / 2), panelRect.Bottom - 48, 148, 34);
        drawingContext.DrawRoundedRectangle(ButtonBrush, ButtonPen, _callToActionRect, 16, 16);
        var buttonTextBlock = CreateText(buttonText, SemiboldTypeface, 13.5, ButtonTextBrush, dpi);
        drawingContext.DrawText(
            buttonTextBlock,
            new Point(
                _callToActionRect.X + ((_callToActionRect.Width - buttonTextBlock.Width) / 2),
                _callToActionRect.Y + ((_callToActionRect.Height - buttonTextBlock.Height) / 2)));
    }

    private Geometry CreateSegmentGeometry(Point center, int lane, int depthIndex, double segmentDepth, double startDistance, double baseRotationDegrees)
    {
        var angleDegrees = (lane * RotationStepDegrees) - 90 + baseRotationDegrees;
        var angleRadians = angleDegrees * Math.PI / 180d;
        var normal = new Vector(Math.Cos(angleRadians), Math.Sin(angleRadians));
        var tangent = new Vector(-normal.Y, normal.X);

        var innerDistance = startDistance + (depthIndex * segmentDepth);
        var outerDistance = innerDistance + segmentDepth;
        var innerHalfWidth = Math.Max(12, innerDistance * 0.36);
        var outerHalfWidth = Math.Max(innerHalfWidth + 4, outerDistance * 0.36);

        var innerCenter = center + (normal * innerDistance);
        var outerCenter = center + (normal * outerDistance);

        var p1 = innerCenter - (tangent * innerHalfWidth);
        var p2 = innerCenter + (tangent * innerHalfWidth);
        var p3 = outerCenter + (tangent * outerHalfWidth);
        var p4 = outerCenter - (tangent * outerHalfWidth);

        var figure = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(p2, true));
        figure.Segments.Add(new LineSegment(p3, true));
        figure.Segments.Add(new LineSegment(p4, true));

        return Freeze(new PathGeometry([figure]));
    }

    private static Geometry CreateHexagonGeometry(Point center, double radius, double rotationDegrees)
    {
        var points = new Point[LaneCount];
        for (var side = 0; side < LaneCount; side++)
        {
            var angleDegrees = (side * RotationStepDegrees) - 90 + rotationDegrees;
            var angleRadians = angleDegrees * Math.PI / 180d;
            points[side] = new Point(
                center.X + (Math.Cos(angleRadians) * radius),
                center.Y + (Math.Sin(angleRadians) * radius));
        }

        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        for (var index = 1; index < points.Length; index++)
        {
            figure.Segments.Add(new LineSegment(points[index], true));
        }

        return Freeze(new PathGeometry([figure]));
    }

    private static int NormalizeLane(int lane)
    {
        var value = lane % LaneCount;
        return value < 0 ? value + LaneCount : value;
    }

    private static Brush CreateColorBrush(int colorIndex)
    {
        var normalized = NormalizeColorIndex(colorIndex);
        return Freeze(new SolidColorBrush(Palette[normalized]));
    }

    private static int NormalizeColorIndex(int colorIndex)
    {
        var value = colorIndex % Palette.Length;
        return value < 0 ? value + Palette.Length : value;
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }

    private static FormattedText CreateText(
        string text,
        Typeface typeface,
        double fontSize,
        Brush brush,
        double pixelsPerDip,
        double? maxWidth = null)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            pixelsPerDip);

        if (maxWidth is double width)
        {
            formattedText.MaxTextWidth = width;
            formattedText.Trimming = TextTrimming.CharacterEllipsis;
        }

        return formattedText;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private enum GameState
    {
        Ready,
        Running,
        GameOver
    }

    private sealed class FallingPiece
    {
        public FallingPiece(int worldLane, int colorIndex, double distance)
        {
            WorldLane = worldLane;
            ColorIndex = colorIndex;
            Distance = distance;
        }

        public int WorldLane { get; }
        public int ColorIndex { get; }
        public double Distance { get; set; }
    }
}

using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.Core.Layout;
using DiskLens.Core.Models;

namespace DiskLens.App.Controls;

public sealed class TreemapControl : FrameworkElement
{
    private const double ScanAnimationDurationMilliseconds = 150;
    private const double ZoomAnimationDurationMilliseconds = 220;
    private const double LabelFadeStartProgress = 0.78;
    private const double DirectoryChromeFadeStartProgress = 0.9;
    private const double DirectoryHeaderBandHeight = 34;
    private const double DirectoryContentInset = 4;
    private const double DirectoryChromeMinimumWidth = 96;
    private const double DirectoryChromeMinimumHeight = 64;
    private const double ClickFeedbackDurationMilliseconds = 320;
    private const double ClickFeedbackPeakOpacity = 0.2;
    private static readonly Color CanvasColor = Color.FromRgb(245, 248, 251);
    private static readonly Color[] NeutralPalette =
    [
        Color.FromRgb(240, 244, 248),
        Color.FromRgb(236, 241, 246),
        Color.FromRgb(232, 238, 244),
        Color.FromRgb(228, 235, 242),
        Color.FromRgb(242, 245, 249),
    ];
    private static readonly Brush CanvasBrush = CreateCanvasBrush();
    private static readonly Brush[] IdlePaletteBrushes = CreatePaletteBrushes(alpha: 225);
    private static readonly Brush[] ScanningPaletteBrushes = CreatePaletteBrushes(alpha: 180);
    private static readonly Brush LowRiskFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(220, 252, 231)));
    private static readonly Brush ReviewRiskFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(254, 249, 195)));
    private static readonly Brush HighRiskFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(254, 226, 226)));
    private static readonly Brush ProtectedRiskFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(254, 202, 202)));
    private static readonly Brush ClickFeedbackBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen OverviewDirectoryPen = CreateFrozenPen(Color.FromRgb(24, 24, 27), 1.35);
    private static readonly Pen SelectedPen = CreateFrozenPen(Color.FromRgb(155, 171, 188), 1.45);
    private static readonly Pen HoverDirectoryPen = CreateFrozenPen(Color.FromRgb(194, 206, 219), 1.05);
    private static readonly Pen HoverFilePen = CreateFrozenPen(Color.FromRgb(194, 206, 219), 0.9);
    private static readonly Pen ScanningPen = CreateFrozenPen(Color.FromRgb(206, 214, 223), 1);
    private static readonly Pen PrimaryDirectoryPen = CreateFrozenPen(Color.FromRgb(213, 221, 229), 1.2);
    private static readonly Pen NestedDirectoryPen = CreateFrozenPen(Color.FromRgb(213, 221, 229), 1.05);
    private static readonly Pen FilePen = CreateFrozenPen(Color.FromRgb(213, 221, 229), 0.9);

    public static readonly DependencyProperty RootNodeProperty =
        DependencyProperty.Register(
            nameof(RootNode),
            typeof(FileSystemNode),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedNodeProperty =
        DependencyProperty.Register(
            nameof(SelectedNode),
            typeof(FileSystemNode),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ZoomIntoCommandProperty =
        DependencyProperty.Register(
            nameof(ZoomIntoCommand),
            typeof(ICommand),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ScanNavigationBlockedCommandProperty =
        DependencyProperty.Register(
            nameof(ScanNavigationBlockedCommand), typeof(ICommand), typeof(TreemapControl),
            new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsReanalysisSelectionModeProperty = DependencyProperty.Register(
        nameof(IsReanalysisSelectionMode), typeof(bool), typeof(TreemapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReanalysisSelectionRevisionProperty = DependencyProperty.Register(
        nameof(ReanalysisSelectionRevision), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReanalysisSelectedPathsProperty = DependencyProperty.Register(
        nameof(ReanalysisSelectedPaths), typeof(IReadOnlySet<string>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ToggleReanalysisSelectionCommandProperty = DependencyProperty.Register(
        nameof(ToggleReanalysisSelectionCommand), typeof(ICommand), typeof(TreemapControl), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty MaxDepthProperty =
        DependencyProperty.Register(
            nameof(MaxDepth),
            typeof(int),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxVisibleChildrenPerRegionProperty =
        DependencyProperty.Register(
            nameof(MaxVisibleChildrenPerRegion),
            typeof(int),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsScanningProperty =
        DependencyProperty.Register(
            nameof(IsScanning),
            typeof(bool),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompactionLevelProperty =
        DependencyProperty.Register(
            nameof(CompactionLevel),
            typeof(int),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(58, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UseOverviewChromeProperty =
        DependencyProperty.Register(
            nameof(UseOverviewChrome),
            typeof(bool),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EnableViewportTransitionsProperty =
        DependencyProperty.Register(
            nameof(EnableViewportTransitions),
            typeof(bool),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(true, OnEnableViewportTransitionsChanged));

    public static readonly DependencyProperty PurposeRevealsProperty = DependencyProperty.Register(
        nameof(PurposeReveals), typeof(TreemapPurposeRevealBatch), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPurposeRevealsChanged));

    public static readonly DependencyProperty ShowSharedExperienceAnalysisProperty = DependencyProperty.Register(
        nameof(ShowSharedExperienceAnalysis), typeof(bool), typeof(TreemapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnSharedAnalysisVisibilityChanged));

    public static readonly DependencyProperty ThemeCanvasBrushProperty = RegisterThemeBrush(nameof(ThemeCanvasBrush));
    public static readonly DependencyProperty ThemeDirectoryBrushProperty = RegisterThemeBrush(nameof(ThemeDirectoryBrush));
    public static readonly DependencyProperty ThemeFileBrushProperty = RegisterThemeBrush(nameof(ThemeFileBrush));
    public static readonly DependencyProperty ThemeAggregateBrushProperty = RegisterThemeBrush(nameof(ThemeAggregateBrush));
    public static readonly DependencyProperty ThemeLabelBrushProperty = RegisterThemeBrush(nameof(ThemeLabelBrush));
    public static readonly DependencyProperty ThemeMutedLabelBrushProperty = RegisterThemeBrush(nameof(ThemeMutedLabelBrush));
    public static readonly DependencyProperty ThemeBorderBrushProperty = RegisterThemeBrush(nameof(ThemeBorderBrush));

    private readonly TreemapDisplayPlanner _displayPlanner = new();
    private IReadOnlyList<TreemapRectangle> _rectangles = [];
    private FileSystemNode? _hoveredNode;
    private IReadOnlySet<string> _visibleContainerPaths = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, RiskSummary> _riskSummaries = new Dictionary<string, RiskSummary>(StringComparer.Ordinal);
    private TreemapLayoutRequest? _appliedLayoutRequest;
    private TreemapLayoutRequest? _queuedLayoutRequest;
    private int _layoutRequestVersion;
    private readonly Stopwatch _animationStopwatch = Stopwatch.StartNew();
    private IReadOnlyDictionary<string, TreemapRectangle> _animationFromRectangles = new Dictionary<string, TreemapRectangle>(StringComparer.Ordinal);
    private IReadOnlyList<TreemapRectangle> _exitingRectangles = [];
    private long _animationStartedAtMilliseconds = -1;
    private double _animationDurationMilliseconds = ScanAnimationDurationMilliseconds;
    private bool _isViewportTransition;
    private bool _isAnimating;
    private bool _isRenderingSubscribed;
    private bool _isPurposeRevealRendering;
    private FileSystemNode? _clickFeedbackNode;
    private long _clickFeedbackStartedAtMilliseconds = -1;
    private Brush? _purposeAccentSource;
    private Brush _purposeBandBrush = Freeze(new SolidColorBrush(Color.FromRgb(224, 239, 201)));
    private DispatcherTimer? _hoverToolTipTimer;
    private bool _hoverToolTipWaitingToOpen;

    public TreemapControl()
    {
        // Every tile is drawn by this single input element. WPF's automatic
        // tooltip owner does not change when the pointer enters another tile.
        ToolTipService.SetIsEnabled(this, false);
        PropertyChangedEventManager.AddHandler(L.Current, OnHoverLanguageChanged, nameof(L.Language));
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) InvalidateVisual();
            else PauseHiddenViewAnimations();
        };
        Unloaded += (_, _) => PauseHiddenViewAnimations();
    }

    public FileSystemNode? RootNode
    {
        get => (FileSystemNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    public FileSystemNode? SelectedNode
    {
        get => (FileSystemNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public ICommand? ZoomIntoCommand
    {
        get => (ICommand?)GetValue(ZoomIntoCommandProperty);
        set => SetValue(ZoomIntoCommandProperty, value);
    }

    public ICommand? ScanNavigationBlockedCommand
    {
        get => (ICommand?)GetValue(ScanNavigationBlockedCommandProperty);
        set => SetValue(ScanNavigationBlockedCommandProperty, value);
    }

    public bool IsReanalysisSelectionMode
    {
        get => (bool)GetValue(IsReanalysisSelectionModeProperty);
        set => SetValue(IsReanalysisSelectionModeProperty, value);
    }
    public int ReanalysisSelectionRevision
    {
        get => (int)GetValue(ReanalysisSelectionRevisionProperty);
        set => SetValue(ReanalysisSelectionRevisionProperty, value);
    }
    public IReadOnlySet<string>? ReanalysisSelectedPaths
    {
        get => (IReadOnlySet<string>?)GetValue(ReanalysisSelectedPathsProperty);
        set => SetValue(ReanalysisSelectedPathsProperty, value);
    }
    public ICommand? ToggleReanalysisSelectionCommand
    {
        get => (ICommand?)GetValue(ToggleReanalysisSelectionCommandProperty);
        set => SetValue(ToggleReanalysisSelectionCommandProperty, value);
    }

    public int MaxDepth
    {
        get => (int)GetValue(MaxDepthProperty);
        set => SetValue(MaxDepthProperty, value);
    }

    public int MaxVisibleChildrenPerRegion
    {
        get => (int)GetValue(MaxVisibleChildrenPerRegionProperty);
        set => SetValue(MaxVisibleChildrenPerRegionProperty, value);
    }

    public bool IsScanning
    {
        get => (bool)GetValue(IsScanningProperty);
        set => SetValue(IsScanningProperty, value);
    }

    public int CompactionLevel
    {
        get => (int)GetValue(CompactionLevelProperty);
        set => SetValue(CompactionLevelProperty, value);
    }

    public bool UseOverviewChrome
    {
        get => (bool)GetValue(UseOverviewChromeProperty);
        set => SetValue(UseOverviewChromeProperty, value);
    }

    public bool EnableViewportTransitions
    {
        get => (bool)GetValue(EnableViewportTransitionsProperty);
        set => SetValue(EnableViewportTransitionsProperty, value);
    }

    public TreemapPurposeRevealBatch? PurposeReveals
    {
        get => (TreemapPurposeRevealBatch?)GetValue(PurposeRevealsProperty);
        set => SetValue(PurposeRevealsProperty, value);
    }

    // Bound to the existing sharing consent; this is not a separate user preference.
    public bool ShowSharedExperienceAnalysis
    {
        get => (bool)GetValue(ShowSharedExperienceAnalysisProperty);
        set => SetValue(ShowSharedExperienceAnalysisProperty, value);
    }

    public Brush? ThemeCanvasBrush { get => (Brush?)GetValue(ThemeCanvasBrushProperty); set => SetValue(ThemeCanvasBrushProperty, value); }
    public Brush? ThemeDirectoryBrush { get => (Brush?)GetValue(ThemeDirectoryBrushProperty); set => SetValue(ThemeDirectoryBrushProperty, value); }
    public Brush? ThemeFileBrush { get => (Brush?)GetValue(ThemeFileBrushProperty); set => SetValue(ThemeFileBrushProperty, value); }
    public Brush? ThemeAggregateBrush { get => (Brush?)GetValue(ThemeAggregateBrushProperty); set => SetValue(ThemeAggregateBrushProperty, value); }
    public Brush? ThemeLabelBrush { get => (Brush?)GetValue(ThemeLabelBrushProperty); set => SetValue(ThemeLabelBrushProperty, value); }
    public Brush? ThemeMutedLabelBrush { get => (Brush?)GetValue(ThemeMutedLabelBrushProperty); set => SetValue(ThemeMutedLabelBrushProperty, value); }
    public Brush? ThemeBorderBrush { get => (Brush?)GetValue(ThemeBorderBrushProperty); set => SetValue(ThemeBorderBrushProperty, value); }

    public bool IsPointInteractive(Point point) => !IsViewportAnimationActive() && HitTestNode(point) is not null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        drawingContext.DrawRectangle(ThemeCanvasBrush ?? CanvasBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (RootNode is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            PausePurposeReveals();
            return;
        }

        EnsureLayout();
        var renderRectangles = GetRenderRectangles();
        SynchronizePurposeReveals(renderRectangles);
        var transitionProgress = _isAnimating && _isViewportTransition
            ? GetAnimationProgress()
            : 1;

        if (RenderExitingRectangles(transitionProgress) && _exitingRectangles.Count > 0)
        {
            foreach (var rectangle in _exitingRectangles)
            {
                DrawRectangle(
                    drawingContext,
                    rectangle,
                    isSelected: false,
                    opacity: 1 - transitionProgress,
                    transitionProgress: transitionProgress,
                    isExiting: true);
            }
        }

        for (var index = 0; index < renderRectangles.Count; index++)
        {
            var rectangle = renderRectangles[index];
            var opacity = GetRectangleOpacity(rectangle, transitionProgress);
            DrawRectangle(
                drawingContext,
                rectangle,
                isSelected: IsSameNode(rectangle.Node, SelectedNode),
                opacity: opacity,
                transitionProgress: transitionProgress);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var previousHoveredNode = _hoveredNode;
        _hoveredNode = IsViewportAnimationActive() ? null : HitTestNode(e.GetPosition(this));

        if (!ReferenceEquals(previousHoveredNode, _hoveredNode))
        {
            RefreshHoveredToolTip();
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredNode is null)
        {
            return;
        }

        _hoveredNode = null;
        RefreshHoveredToolTip();
        InvalidateVisual();
    }

    private void OnHoverLanguageChanged(object? sender, PropertyChangedEventArgs e) =>
        LanguageThread.Run(Dispatcher, () =>
        {
            if (_hoveredNode is not null) RefreshHoveredToolTip(ToolTip is System.Windows.Controls.ToolTip { IsOpen: true });
        });

    private void RefreshHoveredToolTip(bool showImmediately = false)
    {
        CloseHoveredToolTip();
        ToolTip = CreateNodeToolTip(_hoveredNode);
        if (ToolTip is not System.Windows.Controls.ToolTip || !IsVisible || !IsEnabled || IsViewportAnimationActive()) return;
        _hoverToolTipTimer ??= CreateHoverToolTipTimer();
        _hoverToolTipWaitingToOpen = true;
        if (showImmediately) OnHoverToolTipTimerTick(this, EventArgs.Empty);
        else
        {
            _hoverToolTipTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ToolTipService.GetInitialShowDelay(this)));
            _hoverToolTipTimer.Start();
        }
    }

    private DispatcherTimer CreateHoverToolTipTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        timer.Tick += OnHoverToolTipTimerTick;
        return timer;
    }

    private void OnHoverToolTipTimerTick(object? sender, EventArgs e)
    {
        _hoverToolTipTimer?.Stop();
        if (!_hoverToolTipWaitingToOpen)
        {
            CloseHoveredToolTip();
            return;
        }
        _hoverToolTipWaitingToOpen = false;
        if (ToolTip is not System.Windows.Controls.ToolTip tooltip || _hoveredNode is null || !IsVisible || !IsEnabled || IsViewportAnimationActive()) return;
        tooltip.IsOpen = true;
        _hoverToolTipTimer!.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ToolTipService.GetShowDuration(this)));
        _hoverToolTipTimer.Start();
    }

    private void CloseHoveredToolTip()
    {
        _hoverToolTipTimer?.Stop();
        _hoverToolTipWaitingToOpen = false;
        if (ToolTip is System.Windows.Controls.ToolTip tooltip) tooltip.IsOpen = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CloseHoveredToolTip();
        _hoveredNode = null; // A subsequent move within this tile can start a new dwell.
        var hitNode = HitTestNode(e.GetPosition(this));
        Focus();
        if (ShowScanNavigationNotice(hitNode))
        {
            e.Handled = true;
            return;
        }
        if (IsViewportAnimationActive())
        {
            e.Handled = true;
            return;
        }

        SelectNode(hitNode);
        if (IsReanalysisSelectionMode)
        {
            if (e.ClickCount == 1) ToggleReanalysisNode(hitNode);
            e.Handled = hitNode is not null;
            return;
        }
        // Overflow is a paging control, not an actual directory. Its first click
        // retains paging; the second down in a double click must not page twice.
        if (hitNode is not null && (DisplayNodeMetadata.IsOverflow(hitNode) ? e.ClickCount == 1 : e.ClickCount == 2))
        {
            EnterDirectory(hitNode);
        }
        e.Handled = hitNode is not null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space) || e.IsRepeat)
        {
            return;
        }

        var node = SelectedNode ?? _hoveredNode ?? _rectangles.FirstOrDefault()?.Node;
        if (ShowScanNavigationNotice(node))
        {
            e.Handled = true;
            return;
        }
        if (node is null || IsViewportAnimationActive())
        {
            return;
        }

        SelectNode(node);
        if (IsReanalysisSelectionMode)
        {
            ToggleReanalysisNode(node);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            EnterDirectory(node);
        }
        e.Handled = true;
    }

    private void SelectNode(FileSystemNode? hitNode)
    {
        if (!ReferenceEquals(SelectedNode, hitNode))
        {
            // Keep the two-way SelectedNode binding alive after every click.
            SetCurrentValue(SelectedNodeProperty, hitNode);
            InvalidateVisual();
        }

        if (hitNode is not null)
        {
            StartClickFeedback(hitNode);
        }
    }

    private void ToggleReanalysisNode(FileSystemNode? node)
    {
        // The overflow tile pages a collection; it must never submit hidden files.
        if (node is null || DisplayNodeMetadata.IsOverflow(node)) return;
        if (ToggleReanalysisSelectionCommand?.CanExecute(node.Path) == true)
            ToggleReanalysisSelectionCommand.Execute(node.Path);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        CloseHoveredToolTip();
        _hoveredNode = null;
        var hitNode = HitTestNode(e.GetPosition(this));
        if (ShowScanNavigationNotice(hitNode))
        {
            e.Handled = true;
            return;
        }
        if (!IsViewportAnimationActive()) SelectNode(hitNode);
    }

    private bool ShowScanNavigationNotice(FileSystemNode? node)
    {
        if (!IsScanning) return false;
        if (ScanNavigationBlockedCommand?.CanExecute(node) == true)
            ScanNavigationBlockedCommand.Execute(node);
        return true;
    }

    private void EnterDirectory(FileSystemNode node)
    {
        if (node.IsDirectory && ZoomIntoCommand?.CanExecute(node) == true)
            ZoomIntoCommand.Execute(node);
    }

    private void EnsureLayout()
    {
        var request = CreateLayoutRequest();
        if (request is null || MatchesLayoutRequest(_appliedLayoutRequest, request))
        {
            return;
        }

        QueueLayoutRequest(request);
    }

    private TreemapLayoutRequest? CreateLayoutRequest()
    {
        if (RootNode is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return null;
        }

        return new TreemapLayoutRequest(
            RootNode,
            ActualWidth,
            ActualHeight,
            Math.Clamp(MaxDepth, 1, 12),
            Math.Max(8, MaxVisibleChildrenPerRegion),
            Math.Clamp(CompactionLevel, 0, 100),
            UseOverviewChrome,
            IsScanning,
            EnableViewportTransitions,
            ShowSharedExperienceAnalysis);
    }

    private void QueueLayoutRequest(TreemapLayoutRequest request)
    {
        if (MatchesLayoutRequest(_queuedLayoutRequest, request))
        {
            return;
        }

        _queuedLayoutRequest = request;
        var requestVersion = Interlocked.Increment(ref _layoutRequestVersion);
        _hoveredNode = null;
        RefreshHoveredToolTip();

        _ = Task.Run(() => BuildPreparedLayout(request))
            .ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (requestVersion == _layoutRequestVersion)
                        {
                            _queuedLayoutRequest = null;
                            InvalidateVisual();
                        }
                    }, DispatcherPriority.Background);
                    return;
                }

                Dispatcher.InvokeAsync(
                    () => ApplyPreparedLayout(requestVersion, task.Result),
                    DispatcherPriority.Background);
            }, TaskScheduler.Default);
    }

    private PreparedLayout BuildPreparedLayout(TreemapLayoutRequest request)
    {
        var displayPlan = _displayPlanner.Plan(
            request.RootNode,
            new TreemapDisplayPlannerOptions(
                ViewportWidth: request.ViewportWidth,
                ViewportHeight: request.ViewportHeight,
                MaxDepth: request.MaxDepth,
                MaxVisibleChildrenPerRegion: request.MaxVisibleChildrenPerRegion,
                CompactionLevel: request.CompactionLevel,
                IsScanning: request.IsScanning,
                IsOverviewMode: request.UseOverviewChrome));

        return new PreparedLayout(
            request,
            displayPlan.Rectangles,
            BuildVisibleContainerPaths(displayPlan.Rectangles),
            BuildRiskSummaries(displayPlan.DisplayRoot, request.ShowSharedExperienceAnalysis));
    }

    private void ApplyPreparedLayout(int requestVersion, PreparedLayout preparedLayout)
    {
        if (requestVersion != _layoutRequestVersion
            || !MatchesLayoutRequest(_queuedLayoutRequest, preparedLayout.Request))
        {
            return;
        }

        var previousRectangles = _isAnimating && _isViewportTransition
            ? GetRenderRectangles()
            : _rectangles;
        if (EnableViewportTransitions
            && preparedLayout.Request.EnableViewportTransitions
            && previousRectangles.Count > 0
            && preparedLayout.Rectangles.Count > 0
            && !ReferenceEquals(_appliedLayoutRequest?.RootNode, preparedLayout.Request.RootNode))
        {
            StartAnimation(
                previousRectangles,
                preparedLayout.Rectangles,
                preparedLayout.Request.IsScanning ? ScanAnimationDurationMilliseconds : ZoomAnimationDurationMilliseconds,
                isViewportTransition: !preparedLayout.Request.IsScanning);
        }
        else
        {
            StopAnimation();
        }

        _rectangles = preparedLayout.Rectangles;
        _visibleContainerPaths = preparedLayout.VisibleContainerPaths;
        _riskSummaries = preparedLayout.RiskSummaries;
        _appliedLayoutRequest = preparedLayout.Request;
        _queuedLayoutRequest = null;
        InvalidateVisual();
    }

    private static bool MatchesLayoutRequest(TreemapLayoutRequest? left, TreemapLayoutRequest? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return ReferenceEquals(left.RootNode, right.RootNode)
               && Math.Abs(left.ViewportWidth - right.ViewportWidth) < 0.1
               && Math.Abs(left.ViewportHeight - right.ViewportHeight) < 0.1
               && left.MaxDepth == right.MaxDepth
               && left.MaxVisibleChildrenPerRegion == right.MaxVisibleChildrenPerRegion
               && left.CompactionLevel == right.CompactionLevel
               && left.UseOverviewChrome == right.UseOverviewChrome
               && left.EnableViewportTransitions == right.EnableViewportTransitions
               && left.ShowSharedExperienceAnalysis == right.ShowSharedExperienceAnalysis
               && left.IsScanning == right.IsScanning;
    }

    private IReadOnlyList<TreemapRectangle> GetRenderRectangles()
    {
        if (!_isAnimating || _rectangles.Count == 0)
        {
            return _rectangles;
        }

        var progress = GetAnimationProgress();
        if (progress >= 1)
        {
            StopAnimation();
            return _rectangles;
        }

        var animatedRectangles = new TreemapRectangle[_rectangles.Count];
        for (var index = 0; index < _rectangles.Count; index++)
        {
            var rectangle = _rectangles[index];
            animatedRectangles[index] =
                _animationFromRectangles.TryGetValue(rectangle.Node.Path, out var previousRectangle)
                    ? InterpolateRectangle(previousRectangle, rectangle, progress)
                    : rectangle;
        }

        return animatedRectangles;
    }

    private void StartAnimation(
        IReadOnlyList<TreemapRectangle> fromRectangles,
        IReadOnlyList<TreemapRectangle> toRectangles,
        double durationMilliseconds,
        bool isViewportTransition)
    {
        _animationFromRectangles = TreemapAnimationSourceResolver.ResolveSourceRectangleMap(fromRectangles, toRectangles);
        var toPaths = toRectangles
            .Select(rectangle => rectangle.Node.Path)
            .ToHashSet(StringComparer.Ordinal);
        _exitingRectangles = fromRectangles
            .Where(rectangle => !toPaths.Contains(rectangle.Node.Path))
            .ToArray();

        _animationStartedAtMilliseconds = _animationStopwatch.ElapsedMilliseconds;
        _animationDurationMilliseconds = durationMilliseconds;
        _isViewportTransition = isViewportTransition;
        if (_isAnimating)
        {
            return;
        }

        _isAnimating = true;
        EnsureRenderingSubscription();
    }

    private void StopAnimation()
    {
        _isAnimating = false;
        _exitingRectangles = [];
        _isViewportTransition = false;
        StopRenderingSubscriptionIfIdle();
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (_isAnimating && GetAnimationProgress() >= 1)
        {
            StopAnimation();
        }

        if (_clickFeedbackNode is not null
            && _animationStopwatch.ElapsedMilliseconds - _clickFeedbackStartedAtMilliseconds >= ClickFeedbackDurationMilliseconds)
        {
            _clickFeedbackNode = null;
            _clickFeedbackStartedAtMilliseconds = -1;
        }

        if (_isPurposeRevealRendering)
        {
            if (CanAnimatePurposeReveals())
            {
                PurposeReveals!.AdvanceTo(_animationStopwatch.Elapsed.TotalMilliseconds);
                _isPurposeRevealRendering = PurposeReveals.HasRunningReveals;
            }
            else PausePurposeReveals();
        }

        StopRenderingSubscriptionIfIdle();

        InvalidateVisual();
    }

    private void EnsureRenderingSubscription()
    {
        if (_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += OnCompositionTargetRendering;
        _isRenderingSubscribed = true;
    }

    private void StopRenderingSubscriptionIfIdle()
    {
        if (_isAnimating || _isPurposeRevealRendering || _clickFeedbackNode is not null || !_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _isRenderingSubscribed = false;
    }

    private bool CanAnimatePurposeReveals() => PurposeReveals is not null && IsVisible
        && EnableViewportTransitions && !IsScanning && !IsViewportAnimationActive()
        && ReferenceEquals(_appliedLayoutRequest?.RootNode, RootNode);

    private void SynchronizePurposeReveals(IReadOnlyList<TreemapRectangle> rectangles)
    {
        if (PurposeReveals is null) return;
        if (!EnableViewportTransitions)
        {
            PurposeReveals.CompleteAll();
            _isPurposeRevealRendering = false;
            return;
        }
        if (!CanAnimatePurposeReveals())
        {
            PausePurposeReveals();
            return;
        }

        PurposeReveals.AdvanceTo(_animationStopwatch.Elapsed.TotalMilliseconds);
        PurposeReveals.RevealVisible(rectangles.OrderBy(rectangle => rectangle.Y).ThenBy(rectangle => rectangle.X)
            .Select(rectangle => new KeyValuePair<string, string>(rectangle.Node.Path, GetVisibleNodePurpose(rectangle.Node))));
        _isPurposeRevealRendering = PurposeReveals.HasRunningReveals;
        if (_isPurposeRevealRendering) EnsureRenderingSubscription();
        else StopRenderingSubscriptionIfIdle();
    }

    private void PausePurposeReveals()
    {
        PurposeReveals?.Pause();
        _isPurposeRevealRendering = false;
        StopRenderingSubscriptionIfIdle();
    }

    private void PauseHiddenViewAnimations()
    {
        _hoveredNode = null;
        RefreshHoveredToolTip();
        PausePurposeReveals();
        _clickFeedbackNode = null;
        _clickFeedbackStartedAtMilliseconds = -1;
        StopAnimation();
    }

    private static void OnPurposeRevealsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((TreemapPurposeRevealBatch?)e.OldValue)?.Pause();
        var control = (TreemapControl)sender;
        control.PausePurposeReveals();
        if (!control.EnableViewportTransitions) control.PurposeReveals?.CompleteAll();
    }

    private static void OnSharedAnalysisVisibilityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (TreemapControl)sender;
        // A popup already on screen must not retain the previous cloud purpose.
        control.RefreshHoveredToolTip(control.ToolTip is System.Windows.Controls.ToolTip { IsOpen: true });
    }

    private double GetAnimationProgress()
    {
        if (_animationStartedAtMilliseconds < 0)
        {
            return 1;
        }

        var elapsed = _animationStopwatch.ElapsedMilliseconds - _animationStartedAtMilliseconds;
        var rawProgress = Math.Clamp(elapsed / _animationDurationMilliseconds, 0, 1);
        return 1 - Math.Pow(1 - rawProgress, 3);
    }

    private static TreemapRectangle InterpolateRectangle(TreemapRectangle from, TreemapRectangle to, double progress)
    {
        return to with
        {
            X = Lerp(from.X, to.X, progress),
            Y = Lerp(from.Y, to.Y, progress),
            Width = Lerp(from.Width, to.Width, progress),
            Height = Lerp(from.Height, to.Height, progress),
        };
    }

    private void DrawRectangle(
        DrawingContext drawingContext,
        TreemapRectangle rectangle,
        bool isSelected,
        double opacity = 1,
        double transitionProgress = 1,
        bool isExiting = false)
    {
        if (opacity <= 0)
        {
            return;
        }

        var bounds = new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        var radius = GetCornerRadius(rectangle, isSelected);
        var brush = CreateBrush(rectangle);
        var pen = CreatePen(rectangle, isSelected);

        var pushedOpacity = false;
        if (opacity < 0.999)
        {
            drawingContext.PushOpacity(opacity);
            pushedOpacity = true;
        }

        if (radius <= 0.01)
        {
            drawingContext.DrawRectangle(brush, pen, bounds);
        }
        else
        {
            drawingContext.DrawRoundedRectangle(
                brush,
                pen,
                bounds,
                radius,
                radius);
        }

        DrawClickFeedback(drawingContext, rectangle, bounds, radius, opacity);
        DrawDirectoryChrome(drawingContext, rectangle, bounds, radius, isSelected, transitionProgress);
        DrawDirectoryRiskIndicator(drawingContext, rectangle, bounds);
        DrawLabel(drawingContext, rectangle, bounds, radius, isSelected, transitionProgress, isExiting);
        if (!isExiting) DrawReanalysisCheck(drawingContext, rectangle, bounds);

        if (pushedOpacity)
        {
            drawingContext.Pop();
        }
    }

    private void DrawReanalysisCheck(DrawingContext context, TreemapRectangle rectangle, Rect bounds)
    {
        if (!CanRenderReanalysisCheck(rectangle.Node, bounds)) return;
        var isChecked = ReanalysisSelectedPaths?.Contains(rectangle.Node.Path) == true;
        var box = new Rect(bounds.Right - 25, bounds.Y + 7, 18, 18);
        var ink = ThemeLabelBrush ?? Brushes.WhiteSmoke;
        var fill = isChecked ? ink : ThemeCanvasBrush ?? CanvasBrush;
        context.DrawRoundedRectangle(fill, new Pen(ink, 1.4), box, 4, 4);
        if (!isChecked) return;
        var tick = new StreamGeometry();
        using (var path = tick.Open())
        {
            path.BeginFigure(new Point(box.X + 4, box.Y + 9), false, false);
            path.LineTo(new Point(box.X + 7.5, box.Y + 12.5), true, false);
            path.LineTo(new Point(box.X + 14, box.Y + 5), true, false);
        }
        tick.Freeze();
        context.DrawGeometry(null, new Pen(ThemeDirectoryBrush ?? LowRiskFillBrush, 2), tick);
    }

    private bool CanRenderReanalysisCheck(FileSystemNode node, Rect bounds) =>
        IsReanalysisSelectionMode && !DisplayNodeMetadata.IsOverflow(node)
        && bounds.Width >= 38 && bounds.Height >= 42 && !HasVisibleChildren(node.Path);

    private void DrawLabel(
        DrawingContext drawingContext,
        TreemapRectangle rectangle,
        Rect bounds,
        double radius,
        bool isSelected,
        double transitionProgress,
        bool isExiting)
    {
        var hasSourceRectangle = isExiting
                                 || TreemapTransitionPresentation.HasExactSourceRectangle(rectangle.Node, _animationFromRectangles);
        var labelOpacity = IsViewportAnimationActive() && !isExiting
            ? TreemapTransitionPresentation.GetOpacity(transitionProgress, LabelFadeStartProgress, hasSourceRectangle)
            : 1;
        var isOverflow = DisplayNodeMetadata.IsOverflow(rectangle.Node);
        if (!ShouldRenderLabel(bounds, labelOpacity)
            && !(isOverflow && labelOpacity > 0 && bounds.Width >= 20 && bounds.Height >= 20))
        {
            return;
        }

        drawingContext.PushOpacity(labelOpacity);
        drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));

        try
        {
            if (isOverflow)
            {
                DrawOverflowLabel(drawingContext, rectangle.Node, bounds);
                return;
            }

            // A parent with visible children only owns its reserved bottom title band.
            // This also applies while scanning, when the decorative chrome is hidden.
            var labelBounds = CanRenderReanalysisCheck(rectangle.Node, bounds)
                ? new Rect(bounds.X, bounds.Y + 25, bounds.Width, bounds.Height - 25)
                : bounds;
            if (rectangle.Node.IsDirectory && HasVisibleChildren(rectangle.Node.Path))
            {
                if (bounds.Width < DirectoryChromeMinimumWidth || bounds.Height < DirectoryChromeMinimumHeight)
                {
                    return;
                }

                labelBounds = new Rect(bounds.X, bounds.Bottom - DirectoryHeaderBandHeight,
                    bounds.Width, DirectoryHeaderBandHeight);
            }

            var labelLayout = TreemapLabelPlanner.Plan(labelBounds.Width, labelBounds.Height);
            if (labelLayout is null)
            {
                return;
            }

            var padding = labelLayout.Padding;
            var labelWidth = Math.Max(0, labelBounds.Width - (padding * 2));
            var labelHeight = Math.Max(0, labelBounds.Height - (padding * 2));
            var contentBounds = new Rect(labelBounds.X + padding, labelBounds.Y + padding, labelWidth, labelHeight);
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var labelBrush = GetVisibleAnalysis(rectangle.Node) is null
                ? ThemeLabelBrush ?? CreateLabelBrush(isSelected, strong: true)
                : ThemeMutedLabelBrush ?? CreateLabelBrush(isSelected, strong: true);
            // Keep glyph overhang inside the block's padding, rather than clipping
            // fallback CJK strokes or Latin descenders at the logical line box.
            var inkBounds = contentBounds;
            inkBounds.Inflate(1.5, 1.5);
            drawingContext.PushClip(new RectangleGeometry(inkBounds));
            try
            {
                // CJK fallback glyphs may overhang their advance box slightly.
                var textBounds = contentBounds;
                textBounds.Inflate(-1, 0);
                DrawNodeLabelWithPurpose(drawingContext, rectangle.Node, textBounds, labelLayout, labelBrush, pixelsPerDip);
            }
            finally
            {
                drawingContext.Pop();
            }
        }
        finally
        {
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }

    private void DrawNodeLabelWithPurpose(DrawingContext context, FileSystemNode node, Rect bounds,
        TreemapLabelLayout layout, Brush brush, double pixelsPerDip)
    {
        var purpose = GetVisibleNodePurpose(node);
        if (purpose.Length > 0)
        {
            var purposeFontSize = Math.Clamp(layout.FontSize, 12, 13);
            var purposeText = CreateFormattedText(purpose, purposeFontSize, FontWeights.Medium,
                TryFindResource("AccentTextBrush") as Brush ?? Brushes.DarkSlateGray,
                Math.Max(1, bounds.Width - 12), pixelsPerDip);
            var name = CreateFormattedText(node.Name, layout.FontSize, FontWeights.SemiBold, brush,
                Math.Max(1, bounds.Width), pixelsPerDip);
            var size = CreateFormattedText(FormatBytes(node.SizeBytes), layout.FontSize,
                FontWeights.SemiBold, brush, 0, pixelsPerDip);
            var minimumLabelHeight = bounds.Width >= size.WidthIncludingTrailingWhitespace + 6 + layout.FontSize * 2
                ? Math.Max(name.Height, size.Height)
                : name.Height + 2 + size.Height;
            var band = TreemapLabelPlanner.PlanPurposeBand(bounds.Width, bounds.Height,
                minimumLabelHeight, purposeText.Height);
            if (band is not null)
            {
                // Each row owns a separate rectangle; trimming is measured by WPF's
                // glyph formatter, not guessed from a fixed character count.
                var nameAndSize = new Rect(bounds.X, bounds.Y, bounds.Width, band.LabelHeight);
                DrawNodeLabel(context, node, nameAndSize, layout, brush, pixelsPerDip);
                var purposeBounds = new Rect(bounds.X, bounds.Y + band.BandTop, bounds.Width, band.BandHeight);
                var revealProgress = PurposeReveals?.GetProgress(node.Path, purpose) ?? 1;
                context.PushClip(new RectangleGeometry(purposeBounds));
                context.PushOpacity(revealProgress);
                context.PushTransform(new TranslateTransform(0, (1 - revealProgress) * 5));
                context.DrawRoundedRectangle(GetPurposeBandBrush(), null, purposeBounds, 3, 3);
                context.DrawText(purposeText, new Point(purposeBounds.Left + band.TextInset,
                    purposeBounds.Top + (purposeBounds.Height - purposeText.Height) / 2));
                context.Pop();
                context.Pop();
                context.Pop();
                return;
            }
        }

        // Tiny tiles retain their existing name/capacity layout. The hover card
        // still exposes the complete short purpose without overlapping neighbours.
        DrawNodeLabel(context, node, bounds, layout, brush, pixelsPerDip);
    }

    private Brush GetPurposeBandBrush()
    {
        var accent = TryFindResource("AccentBrush") as SolidColorBrush;
        if (accent is not null && !ReferenceEquals(accent, _purposeAccentSource))
        {
            _purposeAccentSource = accent;
            static byte Lighten(byte channel) => (byte)Math.Round(channel + (255 - channel) * .65);
            _purposeBandBrush = Freeze(new SolidColorBrush(Color.FromRgb(
                Lighten(accent.Color.R), Lighten(accent.Color.G), Lighten(accent.Color.B))));
        }
        return _purposeBandBrush;
    }

    private static string GetNodePurpose(FileSystemNode node)
    {
        var analysis = node.Analysis;
        return analysis is null || analysis.LooksLikeFallback() || DisplayNodeMetadata.IsOverflow(node)
            ? string.Empty : L.Purpose(analysis).Trim();
    }

    private AiAnalysisResult? GetVisibleAnalysis(FileSystemNode node) =>
        IsAnalysisVisible(node.Analysis, ShowSharedExperienceAnalysis) ? node.Analysis : null;

    private string GetVisibleNodePurpose(FileSystemNode node) =>
        IsAnalysisVisible(node.Analysis, ShowSharedExperienceAnalysis) ? GetNodePurpose(node) : string.Empty;

    private static bool IsAnalysisVisible(AiAnalysisResult? analysis, bool showSharedExperience) =>
        analysis is not null && (analysis.Origin != AiAnalysisResultOrigin.SharedExperience || showSharedExperience);

    private static void DrawNodeLabel(DrawingContext context, FileSystemNode node, Rect bounds,
        TreemapLabelLayout layout, Brush brush, double pixelsPerDip)
    {
        const double lineGap = 2;
        // Measure size independently, before truncating the name. A long name must
        // never consume the capacity's space or ellipsize away its unit.
        var size = CreateFormattedText(FormatBytes(node.SizeBytes), layout.FontSize,
            FontWeights.SemiBold, brush, 0, pixelsPerDip);
        if (size.WidthIncludingTrailingWhitespace > bounds.Width)
        {
            size = CreateFormattedText(FormatCompactBytes(node.SizeBytes), layout.FontSize,
                FontWeights.SemiBold, brush, 0, pixelsPerDip);
        }

        if (size.WidthIncludingTrailingWhitespace > bounds.Width || size.Height > bounds.Height)
        {
            var fittedFontSize = layout.FontSize * Math.Min(
                bounds.Width / size.WidthIncludingTrailingWhitespace, bounds.Height / size.Height);
            size = CreateFormattedText(FormatCompactBytes(node.SizeBytes), fittedFontSize,
                FontWeights.SemiBold, brush, 0, pixelsPerDip);
        }

        var name = CreateFormattedText(node.Name, layout.FontSize, FontWeights.SemiBold,
            brush, bounds.Width, pixelsPerDip);
        var twoLines = layout.MaxLineCount >= 2 && name.Height + lineGap + size.Height <= bounds.Height;
        if (twoLines)
        {
            var height = name.Height + lineGap + size.Height;
            var top = node.IsDirectory ? bounds.Bottom - height : bounds.Top;
            context.DrawText(name, new Point(bounds.Left, top));
            context.DrawText(size, new Point(bounds.Left, top + name.Height + lineGap));
            return;
        }

        // A shallow block has one row: reserve the right-hand size first, then
        // use any remaining width for an ellipsized name. Very narrow blocks show size.
        var nameWidth = bounds.Width - size.WidthIncludingTrailingWhitespace - 6;
        if (nameWidth >= layout.FontSize * 2 && name.Height <= bounds.Height)
        {
            name = CreateFormattedText(node.Name, layout.FontSize, FontWeights.SemiBold,
                brush, nameWidth, pixelsPerDip);
            var rowHeight = Math.Max(name.Height, size.Height);
            var rowTop = node.IsDirectory ? bounds.Bottom - rowHeight : bounds.Top;
            context.DrawText(name, new Point(bounds.Left, rowTop));
            context.DrawText(size, new Point(bounds.Right - size.WidthIncludingTrailingWhitespace, rowTop));
        }
        else
        {
            var sizeTop = node.IsDirectory ? bounds.Bottom - size.Height : bounds.Top;
            context.DrawText(size, new Point(bounds.Left, sizeTop));
        }
    }

    private void DrawOverflowLabel(DrawingContext context, FileSystemNode node, Rect bounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var brush = ThemeLabelBrush ?? CreateLabelBrush(false, strong: true);
        var text = CreateFormattedText("…", Math.Min(32, bounds.Height * 0.45), FontWeights.SemiBold,
            brush, Math.Max(1, bounds.Width - 8), dpi, 1);
        var showCount = bounds.Width >= 90 && bounds.Height >= 76;
        var top = bounds.Y + (bounds.Height - text.Height) / 2 - (showCount ? 9 : 0);
        context.DrawText(text, new Point(bounds.X + (bounds.Width - text.Width) / 2, top));
        if (showCount)
        {
            var count = CreateFormattedText(L.F($"其余 {node.Children.Count:N0} 项"), 10, FontWeights.Normal,
                brush, bounds.Width - 12, dpi, 1);
            context.DrawText(count, new Point(bounds.X + (bounds.Width - count.Width) / 2, top + text.Height + 2));
        }
    }

    private FileSystemNode? HitTestNode(Point point)
    {
        var hitTestRectangles = IsViewportAnimationActive()
            ? GetRenderRectangles()
            : _rectangles;
        for (var index = hitTestRectangles.Count - 1; index >= 0; index--)
        {
            var rectangle = hitTestRectangles[index];
            if (point.X >= rectangle.X
                && point.X <= rectangle.X + rectangle.Width
                && point.Y >= rectangle.Y
                && point.Y <= rectangle.Y + rectangle.Height)
            {
                return rectangle.Node;
            }
        }

        return null;
    }

    private void StartClickFeedback(FileSystemNode node)
    {
        if (!EnableViewportTransitions)
        {
            return;
        }

        _clickFeedbackNode = node;
        _clickFeedbackStartedAtMilliseconds = _animationStopwatch.ElapsedMilliseconds;
        EnsureRenderingSubscription();
    }

    private void DrawClickFeedback(
        DrawingContext drawingContext,
        TreemapRectangle rectangle,
        Rect bounds,
        double radius,
        double itemOpacity)
    {
        if (itemOpacity <= 0
            || _clickFeedbackNode is null
            || !string.Equals(_clickFeedbackNode.Path, rectangle.Node.Path, StringComparison.Ordinal))
        {
            return;
        }

        var elapsed = _animationStopwatch.ElapsedMilliseconds - _clickFeedbackStartedAtMilliseconds;
        if (elapsed < 0 || elapsed >= ClickFeedbackDurationMilliseconds)
        {
            return;
        }

        var progress = elapsed / ClickFeedbackDurationMilliseconds;
        var opacity = progress < 0.26
            ? ClickFeedbackPeakOpacity * (progress / 0.26)
            : ClickFeedbackPeakOpacity * (1 - ((progress - 0.26) / 0.74));
        if (opacity <= 0)
        {
            return;
        }

        drawingContext.PushOpacity(opacity);
        drawingContext.DrawRoundedRectangle(ClickFeedbackBrush, null, bounds, radius, radius);
        drawingContext.Pop();
    }

    private static void OnEnableViewportTransitionsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TreemapControl control || e.NewValue is not false)
        {
            return;
        }

        // Honor reduced motion immediately, even if a layout request is still being prepared.
        control._clickFeedbackNode = null;
        control._clickFeedbackStartedAtMilliseconds = -1;
        control.PurposeReveals?.CompleteAll();
        control._isPurposeRevealRendering = false;
        control.StopAnimation();
        control.InvalidateVisual();
    }

    private static Brush CreateCanvasBrush()
    {
        return Freeze(new LinearGradientBrush(
            Color.FromArgb(255, CanvasColor.R, CanvasColor.G, CanvasColor.B),
            Color.FromRgb(238, 243, 248),
            new Point(0, 0),
            new Point(1, 1)));
    }

    private Brush CreateBrush(TreemapRectangle rectangle)
    {
        if (rectangle.Node.RiskTags.Contains(DisplayNodeMetadata.AggregateRiskTag))
        {
            return ThemeAggregateBrush ?? IdlePaletteBrushes[0];
        }

        return rectangle.Node.IsDirectory
            ? ThemeDirectoryBrush ?? (IsScanning ? ScanningPaletteBrushes[0] : IdlePaletteBrushes[0])
            : ThemeFileBrush ?? (IsScanning ? ScanningPaletteBrushes[1] : IdlePaletteBrushes[1]);
    }

    private void DrawDirectoryChrome(
        DrawingContext drawingContext,
        TreemapRectangle rectangle,
        Rect bounds,
        double radius,
        bool isSelected,
        double transitionProgress)
    {
        if (!ShouldRenderDirectoryChrome(rectangle, bounds))
        {
            return;
        }

        var hasSourceRectangle = TreemapTransitionPresentation.HasExactSourceRectangle(rectangle.Node, _animationFromRectangles);
        var chromeOpacity = IsViewportAnimationActive()
            ? TreemapTransitionPresentation.GetOpacity(transitionProgress, DirectoryChromeFadeStartProgress, hasSourceRectangle)
            : 1;
        if (chromeOpacity <= 0)
        {
            return;
        }

        drawingContext.PushOpacity(chromeOpacity);
        drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));

        try
        {
            var headerRect = new Rect(
                bounds.X + 1,
                bounds.Bottom - Math.Min(Math.Max(0, bounds.Height - 2), DirectoryHeaderBandHeight),
                Math.Max(0, bounds.Width - 2),
                Math.Min(Math.Max(0, bounds.Height - 2), DirectoryHeaderBandHeight));

            drawingContext.DrawRectangle(CreateDirectoryHeaderBrush(rectangle), null, headerRect);

            var separatorY = bounds.Bottom - DirectoryHeaderBandHeight;
            drawingContext.DrawLine(
                CreateDirectoryHeaderPen(rectangle, isSelected),
                new Point(bounds.X + 2, separatorY),
                new Point(bounds.Right - 2, separatorY));

            var contentRect = CreateDirectoryContentRect(bounds);
            if (contentRect.Width <= 0 || contentRect.Height <= 0)
            {
                return;
            }

            var contentRadius = Math.Max(0, radius - 1.4);
            var contentPen = CreateDirectoryContentPen(rectangle, isSelected);
            if (contentRadius <= 0.01)
            {
                drawingContext.DrawRectangle(null, contentPen, contentRect);
            }
            else
            {
                drawingContext.DrawRoundedRectangle(null, contentPen, contentRect, contentRadius, contentRadius);
            }
        }
        finally
        {
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }

    private Pen CreatePen(TreemapRectangle rectangle, bool isSelected)
    {
        if (isSelected)
        {
            return new Pen(ThemeBorderBrush ?? SelectedPen.Brush, SelectedPen.Thickness + 0.45);
        }

        if (UseOverviewChrome && rectangle.Node.IsDirectory && rectangle.Depth <= 1)
        {
            return OverviewDirectoryPen;
        }

        if (ReferenceEquals(rectangle.Node, _hoveredNode) && !IsViewportAnimationActive())
        {
            return rectangle.Node.IsDirectory ? HoverDirectoryPen : HoverFilePen;
        }

        if (IsScanning)
        {
            return ScanningPen;
        }

        if (rectangle.Node.IsDirectory)
        {
            return rectangle.Depth <= 1 ? PrimaryDirectoryPen : NestedDirectoryPen;
        }

        return FilePen;
    }

    private Brush CreateDirectoryHeaderBrush(TreemapRectangle rectangle)
    {
        var baseAlpha = IsScanning ? 68 : 94;
        if (rectangle.Depth <= 1)
        {
            baseAlpha += 10;
        }

        return new SolidColorBrush(Color.FromArgb((byte)Math.Min(140, baseAlpha), 255, 255, 255));
    }

    private Pen CreateDirectoryHeaderPen(TreemapRectangle rectangle, bool isSelected)
    {
        var color = isSelected
            ? Color.FromRgb(188, 198, 208)
            : rectangle.Depth <= 1
                ? Color.FromRgb(205, 214, 222)
                : Color.FromRgb(215, 223, 230);

        return new Pen(new SolidColorBrush(color), rectangle.Depth <= 1 ? 1.0 : 0.85);
    }

    private Pen CreateDirectoryContentPen(TreemapRectangle rectangle, bool isSelected)
    {
        var color = isSelected
            ? Color.FromRgb(192, 202, 212)
            : rectangle.Depth <= 1
                ? Color.FromRgb(208, 216, 224)
                : Color.FromRgb(220, 226, 232);

        var thickness = rectangle.Depth <= 1 ? 0.95 : 0.75;
        return new Pen(new SolidColorBrush(color), thickness);
    }

    private void DrawDirectoryRiskIndicator(DrawingContext drawingContext, TreemapRectangle rectangle, Rect bounds)
    {
        if (!rectangle.Node.IsDirectory
            || bounds.Width < 120
            || bounds.Height < 84
            || _appliedLayoutRequest?.ShowSharedExperienceAnalysis != ShowSharedExperienceAnalysis
            || !_riskSummaries.TryGetValue(rectangle.Node.Path, out var summary)
            || summary.Total == 0)
        {
            return;
        }

        var center = new Point(bounds.Right - 18, bounds.Y + 18);
        const double outerRadius = 9;
        const double innerRadius = 5.4;
        var startAngle = -90d;

        foreach (var segment in summary.GetSegments())
        {
            var sweep = 360d * (segment.Count / (double)summary.Total);
            if (sweep <= 0.01)
            {
                continue;
            }

            var geometry = CreateRingSegmentGeometry(center, innerRadius, outerRadius, startAngle, sweep);
            drawingContext.DrawGeometry(segment.Brush, null, geometry);
            startAngle += sweep;
        }
    }

    private Brush? CreateRiskFillBrush(FileSystemNode node)
    {
        var analysis = GetVisibleAnalysis(node);
        if (node.IsDirectory || analysis is null)
        {
            return null;
        }

        return analysis.RiskLevel switch
        {
            RiskLevel.Low => LowRiskFillBrush,
            RiskLevel.Review => ReviewRiskFillBrush,
            RiskLevel.High => HighRiskFillBrush,
            RiskLevel.Protected => ProtectedRiskFillBrush,
            _ => null,
        };
    }

    private static Brush CreateLabelBrush(bool isSelected, bool strong)
    {
        return strong
            ? new SolidColorBrush(isSelected ? Color.FromRgb(57, 73, 89) : Color.FromRgb(88, 105, 123))
            : new SolidColorBrush(isSelected ? Color.FromRgb(89, 102, 116) : Color.FromRgb(120, 135, 151));
    }

    private bool IsViewportAnimationActive()
    {
        return _isAnimating && _isViewportTransition;
    }

    private static bool ShouldRenderLabel(Rect bounds, double labelOpacity)
    {
        return labelOpacity > 0 && TreemapLabelPlanner.Plan(bounds.Width, bounds.Height) is not null;
    }

    private bool ShouldRenderDirectoryChrome(TreemapRectangle rectangle, Rect bounds)
    {
        if (IsScanning
            || !rectangle.Node.IsDirectory
            || bounds.Width < DirectoryChromeMinimumWidth
            || bounds.Height < DirectoryChromeMinimumHeight)
        {
            return false;
        }

        return HasVisibleChildren(rectangle.Node.Path);
    }

    private bool RenderExitingRectangles(double transitionProgress)
    {
        return TreemapTransitionPresentation.ShouldRenderExitingRectangles(
            IsViewportAnimationActive(),
            transitionProgress);
    }

    private bool HasVisibleChildren(string path)
    {
        return _visibleContainerPaths.Contains(path);
    }

    private double GetRectangleOpacity(TreemapRectangle rectangle, double transitionProgress)
    {
        if (!_isAnimating || !_isViewportTransition)
        {
            return 1;
        }

        return _animationFromRectangles.ContainsKey(rectangle.Node.Path)
            ? 1
            : transitionProgress;
    }

    private static string FormatCompactBytes(long bytes)
    {
        string[] units = ["B", "K", "M", "G", "T", "P", "E"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.CurrentUICulture) + units[unit];
    }

    private object? CreateNodeToolTip(FileSystemNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var card = new Border
        {
            MaxWidth = 480,
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(.5),
            CornerRadius = new CornerRadius(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var content = new StackPanel();
        content.Children.Add(CreateToolTipTextBlock(
            DisplayNodeMetadata.IsOverflow(node) ? L.F($"其余 {node.Children.Count:N0} 项  点击继续查看") : node.Name,
            fontSize: 14,
            fontWeight: FontWeights.SemiBold,
            foregroundResource: "PrimaryTextBrush"));
        content.Children.Add(CreateToolTipTextBlock(
            FormatBytes(node.SizeBytes),
            margin: new Thickness(0, 4, 0, 9),
            fontSize: 12,
            fontWeight: FontWeights.SemiBold,
            foregroundResource: "SecondaryTextBrush"));

        var purpose = GetVisibleNodePurpose(node);
        if (purpose.Length > 0)
        {
            content.Children.Add(CreateToolTipTextBlock(purpose,
                margin: new Thickness(0, 0, 0, 12), fontSize: 13,
                fontWeight: FontWeights.Medium, foregroundResource: "PrimaryTextBrush"));
        }

        var pathLabel = CreateToolTipTextBlock(
            L.T("完整路径"),
            fontSize: 12,
            fontWeight: FontWeights.SemiBold,
            foregroundResource: "MutedTextBrush");
        content.Children.Add(pathLabel);

        var path = CreateToolTipTextBlock(
            DisplayNodeMetadata.GetOverflowParentPath(node) ?? node.Path,
            margin: new Thickness(0, 3, 0, 0),
            fontSize: 12,
            fontWeight: FontWeights.Normal,
            foregroundResource: "SecondaryTextBrush");
        var pathViewer = new ScrollViewer
        {
            MaxHeight = 156,
            Margin = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = path,
        };
        content.Children.Add(pathViewer);

        card.Child = content;
        return new ToolTip
        {
            Content = card,
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            VerticalOffset = 18,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HasDropShadow = false,
            MaxWidth = 480,
        };
    }

    private static TextBlock CreateToolTipTextBlock(
        string text,
        Thickness margin = default,
        double fontSize = 12,
        FontWeight fontWeight = default,
        string foregroundResource = "SecondaryTextBrush")
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Margin = margin,
            FontSize = fontSize,
            FontWeight = fontWeight == default ? FontWeights.Normal : fontWeight,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
        return textBlock;
    }

    private static DependencyProperty RegisterThemeBrush(string propertyName)
    {
        return DependencyProperty.Register(
            propertyName,
            typeof(Brush),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    private static bool IsSameNode(FileSystemNode left, FileSystemNode? right)
    {
        return right is not null && string.Equals(left.Path, right.Path, StringComparison.Ordinal);
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }

    private static FormattedText CreateFormattedText(
        string text,
        double fontSize,
        FontWeight fontWeight,
        Brush brush,
        double maxWidth,
        double pixelsPerDip,
        int maxLineCount = 1)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, fontWeight, FontStretches.Normal),
            fontSize,
            brush,
            pixelsPerDip)
        {
            MaxLineCount = Math.Max(1, maxLineCount),
            MaxTextWidth = maxWidth,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        return formattedText;
    }

    private double GetCornerRadius(TreemapRectangle rectangle, bool isSelected)
    {
        if (IsScanning)
        {
            return 0;
        }

        if (rectangle.Depth <= 1)
        {
            return isSelected ? 4 : 3;
        }

        return isSelected ? 2 : 0;
    }

    private static Rect CreateDirectoryContentRect(Rect bounds)
    {
        return new Rect(
            bounds.X + DirectoryContentInset,
            bounds.Y + DirectoryContentInset,
            Math.Max(0, bounds.Width - (DirectoryContentInset * 2)),
            Math.Max(0, bounds.Height - (DirectoryContentInset * 2)));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 23;
            foreach (var ch in value)
            {
                hash = (hash * 31) + ch;
            }

            return hash & int.MaxValue;
        }
    }

    private static IReadOnlySet<string> BuildVisibleContainerPaths(IReadOnlyList<TreemapRectangle> rectangles)
    {
        var containerPaths = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new Stack<TreemapRectangle>();

        // Layout emits parents before their displayed descendants. Physical paths can
        // still be related when a conclusion filter displays them as independent peers.
        // Only actual layout nesting may reserve a directory header or suppress labels.
        foreach (var rectangle in rectangles)
        {
            while (ancestors.Count > 0 && ancestors.Peek().Depth >= rectangle.Depth)
            {
                ancestors.Pop();
            }
            if (ancestors.TryPeek(out var parent)) containerPaths.Add(parent.Node.Path);
            ancestors.Push(rectangle);
        }

        return containerPaths;
    }

    private static IReadOnlyDictionary<string, RiskSummary> BuildRiskSummaries(FileSystemNode root, bool showSharedExperience)
    {
        var map = new Dictionary<string, RiskSummary>(StringComparer.Ordinal);
        BuildRiskSummaries(root, map, showSharedExperience);
        return map;
    }

    private static RiskSummary BuildRiskSummaries(FileSystemNode node, IDictionary<string, RiskSummary> map, bool showSharedExperience)
    {
        if (DisplayNodeMetadata.IsOverflow(node))
        {
            return RiskSummary.Empty;
        }

        var summary = !IsAnalysisVisible(node.Analysis, showSharedExperience)
            ? RiskSummary.Empty
            : RiskSummary.From(node.Analysis!.RiskLevel);

        foreach (var child in node.Children)
        {
            summary = summary.Add(BuildRiskSummaries(child, map, showSharedExperience));
        }

        if (node.IsDirectory)
        {
            map[node.Path] = summary;
        }

        return summary;
    }

    private static string? GetDisplayParentPath(string path)
    {
        var aggregateMarkerIndex = path.LastIndexOf(DisplayNodeMetadata.AggregatePathMarker, StringComparison.Ordinal);
        if (aggregateMarkerIndex >= 0)
        {
            var aggregateParentPath = NormalizeDisplayPath(path[..aggregateMarkerIndex]);
            return string.IsNullOrWhiteSpace(aggregateParentPath) || string.Equals(aggregateParentPath, path, StringComparison.Ordinal)
                ? null
                : aggregateParentPath;
        }

        var parentPath = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var normalizedRoot = NormalizeDisplayPath(root);
            return string.Equals(normalizedRoot, path, StringComparison.Ordinal)
                ? null
                : normalizedRoot;
        }

        var normalizedParentPath = NormalizeDisplayPath(parentPath);
        return string.Equals(normalizedParentPath, path, StringComparison.Ordinal)
            ? null
            : normalizedParentPath;
    }

    private static string NormalizeDisplayPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmedPath = path.Trim();
        var normalizedPath = trimmedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(trimmedPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return normalizedPath;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? root
            : normalizedPath;
    }

    private static Geometry CreateRingSegmentGeometry(Point center, double innerRadius, double outerRadius, double startAngleDegrees, double sweepDegrees)
    {
        var startOuter = PointOnCircle(center, outerRadius, startAngleDegrees);
        var endOuter = PointOnCircle(center, outerRadius, startAngleDegrees + sweepDegrees);
        var startInner = PointOnCircle(center, innerRadius, startAngleDegrees + sweepDegrees);
        var endInner = PointOnCircle(center, innerRadius, startAngleDegrees);
        var isLargeArc = sweepDegrees > 180;

        var figure = new PathFigure
        {
            StartPoint = startOuter,
            IsFilled = true,
            IsClosed = true,
        };
        figure.Segments.Add(new ArcSegment(endOuter, new Size(outerRadius, outerRadius), 0, isLargeArc, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(startInner, true));
        figure.Segments.Add(new ArcSegment(endInner, new Size(innerRadius, innerRadius), 0, isLargeArc, SweepDirection.Counterclockwise, true));

        return Freeze(new PathGeometry([figure]));
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + (Math.Cos(radians) * radius),
            center.Y + (Math.Sin(radians) * radius));
    }

    private static Brush[] CreatePaletteBrushes(byte alpha)
    {
        var brushes = new Brush[NeutralPalette.Length];
        for (var index = 0; index < NeutralPalette.Length; index++)
        {
            var baseColor = NeutralPalette[index];
            brushes[index] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)));
        }

        return brushes;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        return Freeze(new Pen(Freeze(new SolidColorBrush(color)), thickness));
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class TreemapLayoutRequest
    {
        public TreemapLayoutRequest(
            FileSystemNode rootNode,
            double viewportWidth,
            double viewportHeight,
            int maxDepth,
            int maxVisibleChildrenPerRegion,
            int compactionLevel,
            bool useOverviewChrome,
            bool isScanning,
            bool enableViewportTransitions,
            bool showSharedExperienceAnalysis)
        {
            RootNode = rootNode;
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
            MaxDepth = maxDepth;
            MaxVisibleChildrenPerRegion = maxVisibleChildrenPerRegion;
            CompactionLevel = compactionLevel;
            UseOverviewChrome = useOverviewChrome;
            IsScanning = isScanning;
            EnableViewportTransitions = enableViewportTransitions;
            ShowSharedExperienceAnalysis = showSharedExperienceAnalysis;
        }

        public FileSystemNode RootNode { get; }

        public double ViewportWidth { get; }

        public double ViewportHeight { get; }

        public int MaxDepth { get; }

        public int MaxVisibleChildrenPerRegion { get; }

        public int CompactionLevel { get; }

        public bool UseOverviewChrome { get; }

        public bool IsScanning { get; }

        public bool EnableViewportTransitions { get; }

        public bool ShowSharedExperienceAnalysis { get; }
    }

    private sealed class PreparedLayout
    {
        public PreparedLayout(
            TreemapLayoutRequest request,
            IReadOnlyList<TreemapRectangle> rectangles,
            IReadOnlySet<string> visibleContainerPaths,
            IReadOnlyDictionary<string, RiskSummary> riskSummaries)
        {
            Request = request;
            Rectangles = rectangles;
            VisibleContainerPaths = visibleContainerPaths;
            RiskSummaries = riskSummaries;
        }

        public TreemapLayoutRequest Request { get; }

        public IReadOnlyList<TreemapRectangle> Rectangles { get; }

        public IReadOnlySet<string> VisibleContainerPaths { get; }

        public IReadOnlyDictionary<string, RiskSummary> RiskSummaries { get; }
    }

    private sealed record RiskSummary(int Low, int Review, int High, int Protected)
    {
        public static RiskSummary Empty { get; } = new(0, 0, 0, 0);

        public int Total => Low + Review + High + Protected;

        public static RiskSummary From(RiskLevel riskLevel)
        {
            return riskLevel switch
            {
                RiskLevel.Low => new RiskSummary(1, 0, 0, 0),
                RiskLevel.High => new RiskSummary(0, 0, 1, 0),
                RiskLevel.Protected => new RiskSummary(0, 0, 0, 1),
                RiskLevel.Review => new RiskSummary(0, 1, 0, 0),
                _ => Empty,
            };
        }

        public RiskSummary Add(RiskSummary other)
        {
            return new RiskSummary(
                Low + other.Low,
                Review + other.Review,
                High + other.High,
                Protected + other.Protected);
        }

        public IEnumerable<(int Count, Brush Brush)> GetSegments()
        {
            if (High > 0)
            {
                yield return (High, HighRiskFillBrush);
            }

            if (Protected > 0)
            {
                yield return (Protected, ProtectedRiskFillBrush);
            }

            if (Review > 0)
            {
                yield return (Review, ReviewRiskFillBrush);
            }

            if (Low > 0)
            {
                yield return (Low, LowRiskFillBrush);
            }
        }
    }
}

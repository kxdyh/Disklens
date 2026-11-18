using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiskLens.App;

/// <summary>A purely illustrative narrative. Only anonymous metadata cards travel; local folders stay on their desks.</summary>
public sealed class SharedKnowledgeFlowIllustration : FrameworkElement
{
    private static readonly DependencyProperty SceneProgressProperty = DependencyProperty.Register(
        "SceneProgress", typeof(double), typeof(SharedKnowledgeFlowIllustration),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Dictionary<(string, double, Brush, bool), FormattedText> _textCache = [];
    private readonly TranslateTransform _leftTransform = new();
    private readonly TranslateTransform _cloudTransform = new();
    private readonly TranslateTransform _archiveTransform = new();
    private readonly TranslateTransform _rightTransform = new();
    private readonly TranslateTransform _sourceCardTransform = new();
    private readonly TranslateTransform _destinationCardTransform = new();
    private readonly TranslateTransform _packetTransform = new();
    private readonly ScaleTransform _checkTransform = new();
    private readonly TranslateTransform _checkPosition = new();
    private Palette? _palette;
    private Assets? _assets;
    private AnimationClock? _motionClock;
    private bool _hasCompleted;
    private bool _listening;
    private double _assetWidth;
    private double _assetDpi;

    public SharedKnowledgeFlowIllustration()
    {
        PropertyChangedEventManager.AddHandler(L.Current, OnLanguageChanged, nameof(L.Language));
        Focusable = false;
        UpdateAutomationName();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    private void UpdateAutomationName()
    {
        AutomationProperties.SetName(this,
            L.T("文件用途经验共享示意动画：他人的 AI 先分析文件夹用途；去除私人路径后的匿名信息与结论卡汇入云端；校验和匹配后，另一人的文件夹旁显示用途说明。文件夹本体不上传。未来合格的 AI 结论继续帮助他人。"));
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => LanguageThread.Run(Dispatcher, RefreshLanguage);

    private void RefreshLanguage()
    {
        UpdateAutomationName();
        _assets = null;
        _textCache.Clear();
        InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _palette = null;
        _assets = null;
        _textCache.Clear();
        UpdateMotion();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_listening) SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _listening = false;
        StopMotion();
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or null or "")) return;
        if (Dispatcher.CheckAccess()) UpdateMotion();
        else Dispatcher.BeginInvoke(new Action(UpdateMotion));
    }

    private void UpdateMotion()
    {
        var visible = IsLoaded && IsVisible;
        if (visible && !_listening)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            _listening = true;
        }
        else if (!visible && _listening)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            _listening = false;
        }
        if (!visible)
        {
            StopMotion();
            return;
        }
        if (!SystemParameters.ClientAreaAnimation)
        {
            CompleteMotion();
            return;
        }
        if (_motionClock is not null || _hasCompleted) return;
        var animation = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(6)))
        { RepeatBehavior = new RepeatBehavior(1), FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(animation, 60);
        _motionClock = animation.CreateClock();
        _motionClock.Completed += OnMotionCompleted;
        ApplyAnimationClock(SceneProgressProperty, _motionClock, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopMotion()
    {
        if (_motionClock is not null) CompleteMotion();
    }

    private void OnMotionCompleted(object? sender, EventArgs e) => CompleteMotion();

    private void CompleteMotion()
    {
        _hasCompleted = true;
        var clock = _motionClock;
        _motionClock = null;
        if (clock is not null)
        {
            clock.Completed -= OnMotionCompleted;
            ApplyAnimationClock(SceneProgressProperty, null);
            clock.Controller?.Remove();
        }
        SetValue(SceneProgressProperty, 1d);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var palette = _palette ??= CreatePalette();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_assets is null || Math.Abs(_assetWidth - ActualWidth) > .1 || _assetDpi != dpi)
        {
            _assetWidth = ActualWidth;
            _assetDpi = dpi;
            _textCache.Clear();
            _assets = BuildAssets(palette);
        }
        var assets = _assets;
        var phase = (double)GetValue(SceneProgressProperty);
        var left = ActualWidth * .16;
        var middle = ActualWidth * .5;
        var right = ActualWidth * .84;
        const double sceneY = 110;

        // Labels are a stable reference while the actual story enters, travels, validates, and arrives.
        context.DrawDrawing(assets.Labels);
        var leftEnter = Range(phase, 0, .09);
        DrawAt(context, assets.Contributor, _leftTransform, left, sceneY + 16 * (1 - leftEnter), leftEnter);

        var cloudEnter = Range(phase, .24, .36);
        DrawAt(context, assets.Cloud, _cloudTransform, middle, sceneY - 12 * (1 - cloudEnter), cloudEnter);

        var sourceEnter = Range(phase, .09, .19);
        var sourceOpacity = sourceEnter * (1 - .5 * Range(phase, .27, .4) * (1 - Range(phase, .78, .88)));
        DrawAt(context, assets.SourcePurpose, _sourceCardTransform, left, sceneY + 90 + 9 * (1 - sourceEnter), sourceOpacity);

        // An anonymous path/category packet is generated separately from the stationary folder illustration.
        if (phase is >= .2 and <= .45)
        {
            var travel = Range(phase, .23, .43);
            var position = Bezier(new Point(left + 23, sceneY + 106), new Point(left + 91, sceneY + 61),
                new Point(middle - 104, sceneY - 21), new Point(middle - 4, sceneY + 17), travel);
            var opacity = Range(phase, .2, .24) * (1 - Range(phase, .4, .45));
            DrawAt(context, assets.AnonymousPacket, _packetTransform, position.X, position.Y, opacity);
        }

        var archived = Range(phase, .4, .5);
        DrawAt(context, assets.Archive, _archiveTransform, middle, sceneY + 11 * (1 - archived), archived);
        var validated = Range(phase, .47, .54);
        if (validated > 0)
        {
            _checkPosition.X = middle + 68;
            _checkPosition.Y = sceneY + 93;
            var scale = 1 + .13 * Math.Sin(validated * Math.PI);
            _checkTransform.ScaleX = _checkTransform.ScaleY = scale;
            context.PushOpacity(validated);
            context.PushTransform(_checkPosition);
            context.PushTransform(_checkTransform);
            context.DrawDrawing(assets.Validation);
            context.Pop(); context.Pop(); context.Pop();
        }

        var rightEnter = Range(phase, .54, .65);
        DrawAt(context, assets.Receiver, _rightTransform, right + 14 * (1 - rightEnter), sceneY, rightEnter);
        if (phase is >= .59 and <= .84)
        {
            var travel = Range(phase, .61, .81);
            var position = Bezier(new Point(middle + 24, sceneY + 37), new Point(middle + 108, sceneY - 13),
                new Point(right - 104, sceneY + 49), new Point(right - 7, sceneY + 103), travel);
            var opacity = Range(phase, .59, .64) * (1 - Range(phase, .8, .84));
            DrawAt(context, assets.MatchPacket, _packetTransform, position.X, position.Y, opacity);
        }
        var arrived = Range(phase, .78, .88);
        DrawAt(context, assets.DestinationPurpose, _destinationCardTransform,
            right, sceneY + 90 + 8 * (1 - arrived), arrived);
        context.PushOpacity(Range(phase, .87, .94));
        context.DrawDrawing(assets.FutureLoop);
        context.Pop();
    }

    private Assets BuildAssets(Palette palette)
    {
        var left = ActualWidth * .16;
        var middle = ActualWidth * .5;
        var right = ActualWidth * .84;
        return new Assets(
            Drawing(context =>
            {
                Label(context, L.T("他人已分析"), left, 18, palette.Text, 18, true);
                Label(context, L.T("文件用途经验共享"), middle, 18, palette.Text, 18, true);
                Label(context, L.T("你直接参考"), right, 18, palette.Text, 18, true);
                Label(context, L.T("匿名公共路径与结论"), (left + middle) / 2, 49, palette.Muted, 12);
                Label(context, L.T("匹配后复用"), (middle + right) / 2, 49, palette.Muted, 12);
            }),
            Drawing(context => DrawContributor(context, palette)),
            Drawing(context => DrawCloud(context, palette)),
            Drawing(context => DrawArchive(context, palette)),
            Drawing(context => DrawReceiver(context, palette)),
            Drawing(context => DrawPurpose(context, palette, false)),
            Drawing(context => DrawPurpose(context, palette, true)),
            Drawing(context => DrawPacket(context, palette, false)),
            Drawing(context => DrawPacket(context, palette, true)),
            Drawing(context =>
            {
                context.DrawEllipse(palette.SuccessFill, null, new Point(0, 0), 18, 18);
                Check(context, -10, -8, palette.Success, 2.5, 1.4);
            }),
            Drawing(context =>
            {
                var curve = new StreamGeometry();
                using (var geometry = curve.Open())
                {
                    geometry.BeginFigure(new Point(right, 270), false, false);
                    geometry.BezierTo(new Point(right, 302), new Point(left, 302), new Point(left, 270), true, false);
                }
                context.DrawGeometry(null, Stroke(palette.Arc, 1), curve);
                context.DrawLine(Stroke(palette.Arc, 1), new Point(left, 270), new Point(left - 5, 278));
                context.DrawLine(Stroke(palette.Arc, 1), new Point(left, 270), new Point(left + 5, 278));
                Label(context, L.T("未来，你的合格 AI 结论也可以继续帮助他人"), ActualWidth / 2, 303, palette.Muted, 12);
            }));
    }

    private static DrawingGroup Drawing(Action<DrawingContext> draw)
    {
        var group = new DrawingGroup();
        using (var context = group.Open()) draw(context);
        group.Freeze();
        return group;
    }

    private static void DrawAt(DrawingContext context, DrawingGroup drawing, TranslateTransform transform,
        double x, double y, double opacity)
    {
        if (opacity <= 0) return;
        transform.X = x;
        transform.Y = y;
        context.PushOpacity(opacity);
        context.PushTransform(transform);
        context.DrawDrawing(drawing);
        context.Pop(); context.Pop();
    }

    private static void DrawContributor(DrawingContext context, Palette palette)
    {
        // People and local folder enter as separate depth layers, then stay put throughout the transfer.
        Workstation(context, 30, -17, .68, palette.Sage, palette);
        Workstation(context, -33, -4, .8, palette.Person, palette);
        Workstation(context, 2, 14, 1, palette.Accent, palette);
        context.DrawEllipse(palette.CardBack, null, new Point(-37, 80), 47, 8);
        Folder(context, -73, 42, 58, 39, palette);
    }

    private static void Workstation(DrawingContext context, double x, double y, double scale, Brush clothing, Palette palette)
    {
        context.PushTransform(new TranslateTransform(x, y));
        context.PushTransform(new ScaleTransform(scale, scale));
        context.DrawEllipse(palette.Skin, null, new Point(0, -17), 12, 14);
        var hair = new StreamGeometry();
        using (var geometry = hair.Open())
        {
            geometry.BeginFigure(new Point(-12, -17), true, true);
            geometry.BezierTo(new Point(-15, -38), new Point(13, -38), new Point(13, -17), true, false);
            geometry.BezierTo(new Point(5, -20), new Point(0, -26), new Point(-9, -19), true, false);
        }
        context.DrawGeometry(palette.Ink, null, hair);
        context.DrawRoundedRectangle(clothing, null, new Rect(-25, 0, 50, 37), 17, 17);
        context.DrawRoundedRectangle(palette.Ink, null, new Rect(-35, 12, 70, 43), 5, 5);
        context.DrawRoundedRectangle(palette.Screen, null, new Rect(-30, 17, 60, 32), 2, 2);
        context.DrawRectangle(palette.ScreenLine, null, new Rect(-22, 25, 25, 3));
        context.DrawRectangle(palette.ScreenLine, null, new Rect(-22, 33, 40, 3));
        context.DrawRoundedRectangle(palette.Ink, null, new Rect(-41, 54, 82, 5), 2, 2);
        context.Pop(); context.Pop();
    }

    private void DrawPurpose(DrawingContext context, Palette palette, bool received)
    {
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-81, 5, 174, 65), 8, 8);
        context.DrawRoundedRectangle(palette.Card, null, new Rect(-87, 0, 174, 65), 8, 8);
        Folder(context, -71, 11, 26, 21, palette);
        Text(context, "文件夹", -32, 10, palette.Text, 14, true);
        Text(context, L.T("用途：缓存与临时数据"), -71, 39, palette.Muted, 12);
        if (received)
        {
            context.DrawEllipse(palette.SuccessFill, null, new Point(72, 0), 12, 12);
            Check(context, 65, -6, palette.Success, 1.7);
        }
    }

    private static void DrawCloud(DrawingContext context, Palette palette)
    {
        var cloud = new StreamGeometry();
        using (var geometry = cloud.Open())
        {
            geometry.BeginFigure(new Point(-78, 41), true, true);
            geometry.BezierTo(new Point(-109, 39), new Point(-108, -3), new Point(-74, -7), true, false);
            geometry.BezierTo(new Point(-78, -39), new Point(-35, -52), new Point(-9, -29), true, false);
            geometry.BezierTo(new Point(17, -55), new Point(70, -41), new Point(66, -11), true, false);
            geometry.BezierTo(new Point(108, -10), new Point(113, 39), new Point(78, 41), true, false);
        }
        context.DrawGeometry(palette.Cloud, null, cloud);
        context.DrawEllipse(palette.CloudHighlight, null, new Point(-26, -13), 25, 14);
    }

    private void DrawArchive(DrawingContext context, Palette palette)
    {
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-64, 10, 145, 89), 9, 9);
        context.DrawRoundedRectangle(palette.Card, null, new Rect(-76, 23, 145, 89), 9, 9);
        Text(context, L.T("文件夹用途"), -58, 36, palette.Text, 14, true);
        Text(context, L.T("缓存与临时数据"), -58, 64, palette.Muted, 12);
        context.DrawRoundedRectangle(palette.LineSoft, null, new Rect(-58, 91, 92, 3), 1, 1);
        Label(context, L.T("合格结论归档"), 0, 126, palette.Muted, 12);
    }

    private static void DrawReceiver(DrawingContext context, Palette palette)
    {
        // A local disk and folder are fixed assets. They never ride a transfer path.
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-68, -17, 145, 97), 12, 12);
        context.DrawRoundedRectangle(palette.Disk, null, new Rect(-77, -26, 145, 97), 12, 12);
        context.DrawEllipse(palette.DiskInner, null, new Point(-17, 19), 34, 34);
        context.DrawEllipse(null, Stroke(palette.LineSoft, 1), new Point(-17, 19), 26, 26);
        context.DrawEllipse(palette.Card, null, new Point(-17, 19), 7, 7);
        var arm = new StreamGeometry();
        using (var geometry = arm.Open())
        {
            geometry.BeginFigure(new Point(39, 47), true, true);
            geometry.LineTo(new Point(-5, 10), true, false);
            geometry.LineTo(new Point(3, 6), true, false);
            geometry.LineTo(new Point(47, 36), true, false);
        }
        context.DrawGeometry(palette.Muted, null, arm);
        context.DrawEllipse(palette.Ink, null, new Point(41, 41), 6, 6);
        context.DrawEllipse(palette.Line, null, new Point(-62, -12), 2, 2);
        context.DrawEllipse(palette.Line, null, new Point(53, -12), 2, 2);
        Folder(context, -76, 42, 58, 39, palette);
    }

    private void DrawPacket(DrawingContext context, Palette palette, bool match)
    {
        // The moving object is a text-only summary. A folder icon is deliberately absent.
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-58, -27, 122, 64), 8, 8);
        context.DrawRoundedRectangle(palette.Card, null, new Rect(-62, -32, 122, 64), 8, 8);
        context.DrawRoundedRectangle(palette.SuccessFill, null, new Rect(-49, -20, 17, 17), 4, 4);
        if (match) Check(context, -47, -17, palette.Success, 1.4, .8);
        else
        {
            context.DrawRoundedRectangle(null, Stroke(palette.Success, 1.2), new Rect(-46, -16, 11, 9), 2, 2);
            context.DrawGeometry(null, Stroke(palette.Success, 1.2), LockHandle());
        }
        Text(context, match ? L.T("匹配的用途") : L.T("匿名路径"), -23, -21, palette.Text, 12, true);
        Text(context, L.T("缓存与临时数据"), -49, 5, palette.Muted, 12);
    }

    private static StreamGeometry LockHandle()
    {
        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            context.BeginFigure(new Point(-44, -16), false, false);
            context.BezierTo(new Point(-44, -23), new Point(-37, -23), new Point(-37, -16), true, false);
        }
        return shape;
    }

    private static void Folder(DrawingContext context, double x, double y, double width, double height, Palette palette)
    {
        context.DrawRoundedRectangle(palette.FolderBack, null, new Rect(x, y, width * .52, height * .33), 3, 3);
        context.DrawRoundedRectangle(palette.FolderBack, null, new Rect(x, y + height * .19, width, height * .72), 4, 4);
        context.DrawRoundedRectangle(palette.Folder, null, new Rect(x, y + height * .35, width, height * .65), 4, 4);
    }

    private static void Check(DrawingContext context, double x, double y, Brush brush, double thickness, double scale = 1)
    {
        var check = new StreamGeometry();
        using (var geometry = check.Open())
        {
            geometry.BeginFigure(new Point(x, y + 6 * scale), false, false);
            geometry.LineTo(new Point(x + 5 * scale, y + 11 * scale), true, false);
            geometry.LineTo(new Point(x + 14 * scale, y), true, false);
        }
        context.DrawGeometry(null, Stroke(brush, thickness), check);
    }

    private static Pen Stroke(Brush brush, double thickness)
        => new(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private static double Range(double phase, double from, double to)
    {
        var value = Math.Clamp((phase - from) / (to - from), 0, 1);
        return value * value * value * (value * (value * 6 - 15) + 10);
    }

    private static Point Bezier(Point from, Point controlOne, Point controlTwo, Point to, double progress)
    {
        var inverse = 1 - progress;
        return new Point(inverse * inverse * inverse * from.X + 3 * inverse * inverse * progress * controlOne.X
            + 3 * inverse * progress * progress * controlTwo.X + progress * progress * progress * to.X,
            inverse * inverse * inverse * from.Y + 3 * inverse * inverse * progress * controlOne.Y
            + 3 * inverse * progress * progress * controlTwo.Y + progress * progress * progress * to.Y);
    }

    private void Label(DrawingContext context, string text, double x, double y, Brush brush, double size, bool strong = false)
    {
        var formatted = Format(text, brush, size, strong);
        context.DrawText(formatted, new Point(x - formatted.Width / 2, y));
    }

    private void Text(DrawingContext context, string text, double x, double y, Brush brush, double size, bool strong = false)
        => context.DrawText(Format(text, brush, size, strong), new Point(x, y));

    private FormattedText Format(string text, Brush brush, double size, bool strong)
    {
        text = L.T(text);
        var key = (text, size, brush, strong);
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        var weight = strong ? TryFindResource("DialogStrongWeight") is FontWeight current ? current : FontWeights.Medium : FontWeights.Normal;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _textCache[key] = formatted;
        return formatted;
    }

    private Palette CreatePalette()
    {
        var dark = TryFindResource("MainSurfaceBrush") is SolidColorBrush surface && surface.Color.R < 80;
        return new Palette(TryFindResource("PrimaryTextBrush") as Brush ?? Brush("#24362B"),
            TryFindResource("SecondaryTextBrush") as Brush ?? Brush("#566259"),
            Brush(dark ? "#8BC6A6" : "#527A64"), Brush(dark ? "#344B3E" : "#DDEAE0"),
            Brush(dark ? "#415D4B" : "#ECF3EC"), Brush(dark ? "#2E3430" : "#FFFFFF"),
            Brush(dark ? "#242925" : "#E6EAE5"), Brush(dark ? "#3D4D40" : "#D9E1D6"),
            Brush(dark ? "#526654" : "#BBCDBD"), Brush(dark ? "#A8C8B2" : "#2D4A37"),
            Brush(dark ? "#607666" : "#CBDBCE"), Brush(dark ? "#26392E" : "#D8EBDF"),
            Brush(dark ? "#9FD0AD" : "#356548"), Brush(dark ? "#A98C67" : "#D7B382"),
            Brush(dark ? "#557661" : "#9AB79D"), Brush(dark ? "#81977B" : "#B3C6AC"),
            Brush(dark ? "#18221B" : "#465749"), Brush(dark ? "#91AE96" : "#DDE8DC"),
            Brush(dark ? "#59735D" : "#86A48D"), Brush(dark ? "#B79660" : "#DFC08A"),
            Brush(dark ? "#E1BE79" : "#F0D59F"), Brush(dark ? "#687D6D" : "#91A395"));
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed record Assets(DrawingGroup Labels, DrawingGroup Contributor, DrawingGroup Cloud, DrawingGroup Archive,
        DrawingGroup Receiver, DrawingGroup SourcePurpose, DrawingGroup DestinationPurpose, DrawingGroup AnonymousPacket,
        DrawingGroup MatchPacket, DrawingGroup Validation, DrawingGroup FutureLoop);
    private sealed record Palette(Brush Text, Brush Muted, Brush Accent, Brush Cloud, Brush CloudHighlight, Brush Card,
        Brush CardBack, Brush Disk, Brush DiskInner, Brush Line, Brush LineSoft, Brush SuccessFill, Brush Success,
        Brush Skin, Brush Person, Brush Sage, Brush Ink, Brush Screen, Brush ScreenLine, Brush FolderBack, Brush Folder, Brush Arc);
}

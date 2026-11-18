using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiskLens.App;

/// <summary>Two illustrative scenes for file-purpose experience sharing, independent of real scan progress.</summary>
public sealed class SharedKnowledgeBenefitsIllustration : FrameworkElement
{
    private const string ScanTitle = "扫描完毕马上了解文件用途";
    private const string ContributionTitle = "未来的某一天你的AI分析结论也能在像帮助你一样帮助他们";
    private static readonly DependencyProperty AnimationProgressProperty = DependencyProperty.Register(
        "AnimationProgress", typeof(double), typeof(SharedKnowledgeBenefitsIllustration),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Dictionary<(string, double, Brush, bool, double), FormattedText> _textCache = [];
    private readonly TranslateTransform[] _transforms = Enumerable.Range(0, 22).Select(_ => new TranslateTransform()).ToArray();
    private readonly ScaleTransform _completedScale = new();
    private Palette? _palette;
    private Assets? _assets;
    private bool _listening;
    private AnimationClock? _motionClock;
    private bool _hasCompleted;
    private Size _assetSize;
    private double _assetDpi;

    public SharedKnowledgeBenefitsIllustration()
    {
        PropertyChangedEventManager.AddHandler(L.Current, OnLanguageChanged, nameof(L.Language));
        Focusable = false;
        UpdateAutomationName();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => LanguageThread.Run(Dispatcher, RefreshLanguage);

    private void RefreshLanguage()
    {
        UpdateAutomationName();
        _assets = null;
        _textCache.Clear();
        InvalidateVisual();
    }
    private void UpdateAutomationName() => AutomationProperties.SetName(this,
        L.T("文件用途经验共享示意动画。") + L.T(ScanTitle) + ". " + L.T(ContributionTitle)
        + L.T("。动画不表示实际扫描或上传进度。"));

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
        var animation = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(7)))
        { RepeatBehavior = new RepeatBehavior(1), FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(animation, 60);
        _motionClock = animation.CreateClock();
        _motionClock.Completed += OnMotionCompleted;
        ApplyAnimationClock(AnimationProgressProperty, _motionClock, HandoffBehavior.SnapshotAndReplace);
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
            ApplyAnimationClock(AnimationProgressProperty, null);
            clock.Controller?.Remove();
        }
        SetValue(AnimationProgressProperty, 1d);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var palette = _palette ??= CreatePalette();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_assets is null || _assetSize != RenderSize || _assetDpi != dpi)
        {
            _assetSize = RenderSize;
            _assetDpi = dpi;
            _textCache.Clear();
            _assets = BuildAssets(palette);
        }
        var assets = _assets;
        var phase = (double)GetValue(AnimationProgressProperty);
        var scanY = Math.Min(80, ActualHeight * .2);
        var bottomY = assets.ContributionY;
        var sourceX = ActualWidth * .13;
        var cloudX = ActualWidth * .43;
        context.DrawDrawing(assets.Titles);

        DrawAt(context, assets.Scanner, 0, ActualWidth * .105, scanY + 4 * (1 - Progress(phase, 0, .05)), Progress(phase, 0, .05));
        var done = Progress(phase, .03, .09);
        _completedScale.ScaleX = _completedScale.ScaleY = 1 + .1 * Math.Sin(done * Math.PI);
        SetTransform(1, ActualWidth * .105 + 28, scanY + 15);
        context.PushOpacity(done);
        context.PushTransform(_transforms[1]);
        context.PushTransform(_completedScale);
        context.DrawDrawing(assets.Complete);
        context.Pop(); context.Pop(); context.Pop();
        for (var i = 0; i < 3; i++)
        {
            var x = ActualWidth * (.34 + i * .24);
            DrawAt(context, assets.Files[i], 2 + i, x, scanY, Progress(phase, .025 + i * .025, .09 + i * .025));
            var reveal = Progress(phase, .1 + i * .055, .19 + i * .055);
            DrawAt(context, assets.Purposes[i], 5 + i, x, scanY + 59 + 8 * (1 - reveal), reveal);
        }

        var contributor = Progress(phase, .28, .36);
        DrawAt(context, assets.Contributor, 8, sourceX - 10 * (1 - contributor), bottomY, contributor);
        DrawAt(context, assets.SourceConclusion, 9, sourceX, bottomY + 51 + 5 * (1 - contributor), contributor);
        var cloud = Progress(phase, .36, .44);
        DrawAt(context, assets.Cloud, 10, cloudX, bottomY - 6 * (1 - cloud), cloud);
        DrawPacket(context, assets.Packet, 11, phase, .36, .53,
            new Point(sourceX + 8, bottomY + 62), new Point(cloudX - 6, bottomY + 17));
        var archived = Progress(phase, .5, .58);
        DrawAt(context, assets.Archived, 12, cloudX, bottomY + 9 * (1 - archived), archived);
        for (var i = 0; i < 3; i++)
        {
            var recipientX = ActualWidth * (.67 + i * .125);
            var recipientY = bottomY + (i == 1 ? -7 : 7);
            DrawAt(context, assets.Recipients[i], 13 + i, recipientX, recipientY,
                Progress(phase, .55 + i * .035, .63 + i * .035));
            DrawPacket(context, assets.MatchPacket, 16 + i, phase, .58 + i * .07, .74 + i * .07,
                new Point(cloudX + 12, bottomY + 21), new Point(recipientX, recipientY + 42));
            var understood = Progress(phase, .72 + i * .07, .79 + i * .07);
            DrawAt(context, assets.ReceivedPurpose, 19 + i, recipientX, recipientY + 47 + 4 * (1 - understood), understood);
        }
    }

    private void DrawPacket(DrawingContext context, DrawingGroup packet, int transform, double phase, double fromPhase,
        double toPhase, Point from, Point to)
    {
        if (phase < fromPhase || phase > toPhase) return;
        var progress = Progress(phase, fromPhase, toPhase);
        var inverse = 1 - progress;
        var control = new Point((from.X + to.X) / 2, Math.Min(from.Y, to.Y) - 30);
        var x = inverse * inverse * from.X + 2 * inverse * progress * control.X + progress * progress * to.X;
        var y = inverse * inverse * from.Y + 2 * inverse * progress * control.Y + progress * progress * to.Y;
        var opacity = Progress(phase, fromPhase, fromPhase + .025) * (1 - Progress(phase, toPhase - .025, toPhase));
        DrawAt(context, packet, transform, x, y, opacity);
    }

    private Assets BuildAssets(Palette palette)
    {
        var lowerTitleY = Math.Max(181, ActualHeight * .48);
        // Deliberate two-line typesetting keeps the exact long headline readable at 740 DIP.
        var longTitle = Format(ContributionTitle.Replace("AI分析结论", "AI分析结论\n", StringComparison.Ordinal),
            palette.Text, 18, true, ActualWidth - 24);
        longTitle.TextAlignment = TextAlignment.Center;
        var contributionY = lowerTitleY + longTitle.Height + 42;
        var titles = Drawing(context =>
        {
            Label(context, ScanTitle, ActualWidth / 2, 2, palette.Text, 18, true);
            context.DrawText(longTitle, new Point(12, lowerTitleY));
        });
        var files = new[]
        {
            Drawing(context => DrawFile(context, "Cache", false, palette)),
            Drawing(context => DrawFile(context, "Logs", false, palette)),
            Drawing(context => DrawFile(context, "setup.exe", true, palette))
        };
        var purposes = new[]
        {
            Drawing(context => Purpose(context, L.T("缓存与临时数据"), 150, palette)),
            Drawing(context => Purpose(context, L.T("运行日志"), 150, palette)),
            Drawing(context => Purpose(context, L.T("软件安装程序"), 150, palette))
        };
        var recipients = new[]
        {
            Drawing(context => PersonComputer(context, palette.Accent, palette, .76)),
            Drawing(context => PersonComputer(context, palette.PersonTwo, palette, .76)),
            Drawing(context => PersonComputer(context, palette.PersonThree, palette, .76))
        };
        return new Assets(titles, contributionY,
            Drawing(context => DrawScanner(context, palette)),
            Drawing(context =>
            {
                context.DrawEllipse(palette.SuccessFill, null, new Point(), 15, 15);
                Check(context, -8, -6, palette.Success, 2.2);
            }), files, purposes,
            Drawing(context => PersonComputer(context, palette.Accent, palette, 1)),
            Drawing(context =>
            {
                context.DrawRoundedRectangle(palette.Card, null, new Rect(-66, -4, 132, 36), 7, 7);
                Check(context, -52, 7, palette.Success, 1.6);
                Text(context, L.T("你的用途结论"), -30, 4, palette.Text, 12, true);
            }),
            Drawing(context => DrawCloud(context, palette)),
            Drawing(context => DrawConclusionPacket(context, palette, false)),
            Drawing(context => DrawConclusionPacket(context, palette, true)),
            Drawing(context =>
            {
                context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-41, -8, 90, 54), 7, 7);
                context.DrawRoundedRectangle(palette.Card, null, new Rect(-49, 0, 90, 54), 7, 7);
                context.DrawRoundedRectangle(palette.Line, null, new Rect(-34, 12, 48, 3), 1, 1);
                context.DrawRoundedRectangle(palette.LineSoft, null, new Rect(-34, 24, 35, 3), 1, 1);
                context.DrawEllipse(palette.SuccessFill, null, new Point(34, 42), 12, 12);
                Check(context, 27, 36, palette.Success, 1.7);
            }), recipients,
            Drawing(context => Purpose(context, L.T("用途已知"), 85, palette)));
    }

    private static DrawingGroup Drawing(Action<DrawingContext> draw)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) draw(context);
        drawing.Freeze();
        return drawing;
    }

    private void DrawAt(DrawingContext context, DrawingGroup asset, int index, double x, double y, double opacity)
    {
        if (opacity <= 0) return;
        SetTransform(index, x, y);
        context.PushOpacity(opacity);
        context.PushTransform(_transforms[index]);
        context.DrawDrawing(asset);
        context.Pop(); context.Pop();
    }

    private void SetTransform(int index, double x, double y)
    {
        _transforms[index].X = x;
        _transforms[index].Y = y;
    }

    private void DrawScanner(DrawingContext context, Palette palette)
    {
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-43, -29, 86, 62), 9, 9);
        context.DrawRoundedRectangle(palette.Disk, null, new Rect(-47, -35, 86, 62), 9, 9);
        context.DrawEllipse(palette.DiskInner, null, new Point(-12, -4), 22, 22);
        context.DrawEllipse(null, Stroke(palette.Line, 1), new Point(-12, -4), 15, 15);
        context.DrawEllipse(palette.Card, null, new Point(-12, -4), 5, 5);
        context.DrawLine(Stroke(palette.Muted, 3), new Point(20, 15), new Point(-4, -7));
        Label(context, L.T("扫描完毕"), -3, 48, palette.Muted, 12);
    }

    private void DrawFile(DrawingContext context, string name, bool executable, Palette palette)
    {
        context.DrawEllipse(palette.Ground, null, new Point(0, 34), 52, 7);
        if (executable)
        {
            context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-29, -35, 58, 64), 6, 6);
            context.DrawRoundedRectangle(palette.Card, null, new Rect(-33, -39, 58, 64), 6, 6);
            context.DrawRoundedRectangle(palette.SuccessFill, null, new Rect(-19, -25, 30, 29), 5, 5);
            context.DrawLine(Stroke(palette.Success, 1.5), new Point(-10, -14), new Point(1, -8));
            context.DrawLine(Stroke(palette.Success, 1.5), new Point(1, -8), new Point(-10, -2));
        }
        else Folder(context, -40, -32, 80, 57, palette);
        Label(context, name, 0, 31, palette.Text, 12);
    }

    private void Purpose(DrawingContext context, string purpose, double width, Palette palette)
    {
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-width / 2 + 3, 4, width, 31), 7, 7);
        context.DrawRoundedRectangle(palette.Card, null, new Rect(-width / 2, 0, width, 31), 7, 7);
        context.DrawEllipse(palette.SuccessFill, null, new Point(-width / 2 + 16, 15), 8, 8);
        Check(context, -width / 2 + 11, 11, palette.Success, 1.3, .72);
        var text = Format(purpose, palette.Text, 12);
        context.DrawText(text, new Point(-width / 2 + 31, 6));
    }

    private static void PersonComputer(DrawingContext context, Brush shirt, Palette palette, double scale)
    {
        context.PushTransform(new ScaleTransform(scale, scale));
        context.DrawEllipse(palette.Skin, null, new Point(-1, -26), 11, 12);
        context.DrawEllipse(palette.Ink, null, new Point(-1, -33), 12, 6);
        context.DrawRoundedRectangle(shirt, null, new Rect(-22, -12, 44, 31), 15, 15);
        context.DrawRoundedRectangle(palette.Ink, null, new Rect(-31, 3, 62, 37), 5, 5);
        context.DrawRoundedRectangle(palette.Screen, null, new Rect(-26, 7, 52, 26), 2, 2);
        context.DrawRectangle(palette.ScreenLine, null, new Rect(-18, 13, 22, 3));
        context.DrawRectangle(palette.ScreenLine, null, new Rect(-18, 20, 31, 3));
        context.DrawRoundedRectangle(palette.Ink, null, new Rect(-37, 42, 74, 4), 2, 2);
        context.Pop();
    }

    private void DrawCloud(DrawingContext context, Palette palette)
    {
        var cloud = new StreamGeometry();
        using (var geometry = cloud.Open())
        {
            geometry.BeginFigure(new Point(-75, 30), true, true);
            geometry.BezierTo(new Point(-99, 28), new Point(-98, -12), new Point(-67, -13), true, false);
            geometry.BezierTo(new Point(-68, -42), new Point(-33, -53), new Point(-9, -29), true, false);
            geometry.BezierTo(new Point(17, -56), new Point(66, -38), new Point(62, -8), true, false);
            geometry.BezierTo(new Point(99, -8), new Point(102, 29), new Point(72, 30), true, false);
        }
        context.DrawGeometry(palette.Cloud, null, cloud);
        context.DrawEllipse(palette.CloudHighlight, null, new Point(-22, -12), 24, 13);
        context.DrawEllipse(palette.SuccessFill, null, new Point(41, 12), 19, 19);
        Check(context, 30, 4, palette.Success, 2.2, 1.2);
        Label(context, L.T("校验归档"), 0, 70, palette.Muted, 12);
    }

    private void DrawConclusionPacket(DrawingContext context, Palette palette, bool match)
    {
        context.DrawRoundedRectangle(palette.CardBack, null, new Rect(-61, -26, 120, 57), 8, 8);
        context.DrawRoundedRectangle(palette.Card, null, new Rect(-67, -32, 120, 57), 8, 8);
        context.DrawRoundedRectangle(palette.SuccessFill, null, new Rect(-52, -18, 18, 18), 4, 4);
        Check(context, -49, -15, palette.Success, 1.4, .82);
        Text(context, match ? L.T("用途结论") : L.T("我的结论"), -26, -21, palette.Text, 12, true);
        Text(context, L.T("缓存与临时数据"), -52, 4, palette.Muted, 12);
    }

    private static void Folder(DrawingContext context, double x, double y, double width, double height, Palette palette)
    {
        context.DrawRoundedRectangle(palette.FolderBack, null, new Rect(x, y, width * .52, height * .3), 4, 4);
        context.DrawRoundedRectangle(palette.FolderBack, null, new Rect(x, y + height * .18, width, height * .72), 5, 5);
        context.DrawRoundedRectangle(palette.Folder, null, new Rect(x, y + height * .34, width, height * .66), 5, 5);
    }

    private static void Check(DrawingContext context, double x, double y, Brush brush, double thickness, double scale = 1)
    {
        var shape = new StreamGeometry();
        using (var geometry = shape.Open())
        {
            geometry.BeginFigure(new Point(x, y + 6 * scale), false, false);
            geometry.LineTo(new Point(x + 5 * scale, y + 11 * scale), true, false);
            geometry.LineTo(new Point(x + 14 * scale, y), true, false);
        }
        context.DrawGeometry(null, Stroke(brush, thickness), shape);
    }

    private static Pen Stroke(Brush brush, double thickness)
        => new(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private void Label(DrawingContext context, string text, double x, double y, Brush ink, double size, bool strong = false)
    {
        var formatted = Format(text, ink, size, strong);
        context.DrawText(formatted, new Point(x - formatted.Width / 2, y));
    }

    private void Text(DrawingContext context, string text, double x, double y, Brush ink, double size, bool strong = false)
        => context.DrawText(Format(text, ink, size, strong), new Point(x, y));

    private FormattedText Format(string text, Brush ink, double size, bool strong = false, double? maxWidth = null)
    {
        text = L.T(text);
        var key = (text + (maxWidth?.ToString(CultureInfo.InvariantCulture) ?? ""), size, ink, strong, maxWidth ?? 0);
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        var weight = strong && TryFindResource("DialogStrongWeight") is FontWeight current ? current : FontWeights.Normal;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth is > 0) formatted.MaxTextWidth = maxWidth.Value;
        _textCache[key] = formatted;
        return formatted;
    }

    private static double Progress(double value, double from, double to)
    {
        var progress = Math.Clamp((value - from) / (to - from), 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private Palette CreatePalette()
    {
        var dark = TryFindResource("MainSurfaceBrush") is SolidColorBrush surface && surface.Color.R < 80;
        return new Palette(
            Text: TryFindResource("PrimaryTextBrush") as Brush ?? Brush("#24362B"),
            Muted: TryFindResource("SecondaryTextBrush") as Brush ?? Brush("#566259"),
            Accent: Brush(dark ? "#9ACBAD" : "#50795F"),
            Ground: Brush(dark ? "#202620" : "#E5ECE5"),
            Card: Brush(dark ? "#303B33" : "#FFFFFF"),
            CardBack: Brush(dark ? "#232B24" : "#DEE6DC"),
            Disk: Brush(dark ? "#34453A" : "#F1F5EF"),
            DiskInner: Brush(dark ? "#607C68" : "#B3C6B4"),
            Line: Brush(dark ? "#8BA691" : "#51735A"),
            LineSoft: Brush(dark ? "#556A59" : "#C9D8C9"),
            SuccessFill: Brush(dark ? "#284832" : "#DDEFE0"),
            Success: Brush(dark ? "#AFE0B8" : "#356548"),
            Skin: Brush(dark ? "#B79C7A" : "#DBC39D"),
            PersonTwo: Brush(dark ? "#86A88D" : "#668B70"),
            PersonThree: Brush(dark ? "#789987" : "#85A98B"),
            Ink: Brush(dark ? "#1A2520" : "#43564A"),
            Screen: Brush(dark ? "#90B5A0" : "#DDEADE"),
            ScreenLine: Brush(dark ? "#66866E" : "#9EBAA2"),
            FolderBack: Brush(dark ? "#9E8253" : "#B99961"),
            Folder: Brush(dark ? "#D3B06E" : "#F2D8A0"),
            Cloud: Brush(dark ? "#344B3D" : "#DEEBDF"),
            CloudHighlight: Brush(dark ? "#4B6853" : "#EEF5EB"));
    }

    private sealed record Palette(Brush Text, Brush Muted, Brush Accent, Brush Ground, Brush Card, Brush CardBack,
        Brush Disk, Brush DiskInner, Brush Line, Brush LineSoft, Brush SuccessFill, Brush Success, Brush Skin,
        Brush PersonTwo, Brush PersonThree, Brush Ink, Brush Screen, Brush ScreenLine, Brush FolderBack, Brush Folder,
        Brush Cloud, Brush CloudHighlight);
    private sealed record Assets(DrawingGroup Titles, double ContributionY, DrawingGroup Scanner, DrawingGroup Complete,
        DrawingGroup[] Files, DrawingGroup[] Purposes, DrawingGroup Contributor, DrawingGroup SourceConclusion,
        DrawingGroup Cloud, DrawingGroup Packet, DrawingGroup MatchPacket, DrawingGroup Archived,
        DrawingGroup[] Recipients, DrawingGroup ReceivedPurpose);
}

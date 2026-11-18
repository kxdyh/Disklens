using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiskLens.App.Controls;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;
using Path = System.IO.Path;

namespace DiskLens.App.Tests;

// This deliberately never calls Window.Show, EnsureHandle, input APIs or UI Automation.
// All images are RenderTargetBitmap captures of the real production content tree.
[Collection("Isolated AI settings")]
public sealed partial class WindowLayoutRegressionTests
{
    private static readonly string OutputDirectory = Environment.GetEnvironmentVariable("DISKLENS_OFFSCREEN_OUTPUT")
        ?? Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS") ?? Path.GetTempPath(), "offscreen-layout");

    [Fact]
    public void Real_window_layout_and_bound_controls_work_without_showing_a_window() => OnSta(() =>
    {
        Directory.CreateDirectory(OutputDirectory);
        var observations = new Dictionary<string, object>();
        var failures = new List<string>();
        void Check(bool condition, string description)
        {
            observations[description] = condition;
            if (!condition) failures.Add(description);
        }

        using (var scene = new Scene(AppSettings.Default() with { ThemeId = "02" }))
        {
            var window = scene.Window;
            var root = Layout(window, 1280, 800);
            var tabBar = Named<FrameworkElement>(window, "TaskTabBar");
            var header = Named<FrameworkElement>(window, "AppHeader");
            var home = Named<Button>(window, "HomeTaskTab");
            Check(Bounds(tabBar, root).Bottom <= Bounds(header, root).Top,
                "1280: task tabs and application header do not overlap");
            Check(!scene.Workspace.CloseTabCommand.CanExecute(scene.Workspace.TaskTabs[0]),
                "Home tab cannot be closed");
            var landing = Named<HomeLandingControl>(window, "HomeLanding");
            Save(Render(root), "01-home-theme02-1280x800.png");
            var artwork = (Viewbox)Field(landing, "_artwork")!;
            var artworkCanvas = (Canvas)artwork.Child;
            var renderer = Field(landing, "_artworkRenderer")!;
            var groups = (Dictionary<string, List<FrameworkElement>>)renderer.GetType().GetProperty("Groups")!.GetValue(renderer)!;
            var caption = groups["order-caption"].Single();
            var paper = groups["paper"];
            var middlePaper = Descendants<Rectangle>(groups["paper-middle"].Single()).First();
            var expectedMiddleBounds = new RotateTransform(7, 328, 235).TransformBounds(new Rect(219, 115, 218, 240));
            var actualMiddleBounds = Bounds(middlePaper, artworkCanvas);
            observations["middle-paper-svg-transform"] = new
            {
                expectedSvgBounds = expectedMiddleBounds.ToString(),
                actualWpfBounds = actualMiddleBounds.ToString(),
            };
            // Production enables layout rounding; allow one physical pixel at the inherited DPI.
            Check(Math.Abs(expectedMiddleBounds.Top - actualMiddleBounds.Top) <= 1 / VisualTreeHelper.GetDpi(artworkCanvas).DpiScaleY,
                "SVG paper rotation uses SVG global center after conversion to WPF local coordinates");
            var samples = new List<BitmapSource>();
            foreach (var progress in new[] { 0d, .25, .5, .75, 1d })
            {
                Invoke(landing, "ApplyArtworkProgress", progress);
                Layout(window, 1280, 800);
                var captionBounds = VisualTreeHelper.GetDescendantBounds(caption);
                captionBounds = caption.TransformToAncestor(artworkCanvas).TransformBounds(captionBounds);
                var captionGeometry = new RectangleGeometry(captionBounds);
                var intersectedArea = paper.Sum(part =>
                {
                    var rectangle = Descendants<Rectangle>(part).First();
                    return Geometry.Combine(captionGeometry, GeometryInAncestor(rectangle, artworkCanvas),
                        GeometryCombineMode.Intersect, Transform.Identity).GetArea();
                });
                var topPaper = paper.Min(part =>
                {
                    var rectangle = Descendants<Rectangle>(part).First();
                    return Bounds(rectangle, artworkCanvas).Top;
                });
                observations[$"artwork-progress-{progress:F2}"] = new
                {
                    captionBottom = captionBounds.Bottom,
                    paperTop = topPaper,
                    clearance = topPaper - captionBounds.Bottom,
                    actualShapeIntersectionArea = intersectedArea,
                };
                Check(intersectedArea < .01,
                    $"Theme02 progress {progress:F2}: caption and arrow do not overlap paper geometry");
                samples.Add(Crop(Render(root), Bounds(artwork, root)));
            }
            Save(Montage(samples, columns: 3), "02-home-theme02-five-motion-samples.png");

            for (var index = 0; index < 5; index++) scene.AddCompletedTask("已完成任务-" + index);
            root = Layout(window, 1024, 640);
            var tabsScroll = Descendants<ScrollViewer>(tabBar).Single();
            var fixedHomeBounds = Bounds(home, root);
            tabsScroll.ScrollToRightEnd();
            Layout(window, 1024, 640);
            tabsScroll.ScrollToHorizontalOffset(tabsScroll.ScrollableWidth);
            Layout(window, 1024, 640);
            Check(Bounds(home, root) == fixedHomeBounds, "1024: Home stays fixed outside the task scroller");
            observations["offscreen-scroll-limitation"] = "An unshown WPF ScrollViewer reports ExtentWidth=0, so no desktop scrolling or active overflow-tab reveal is claimed.";
            scene.Workspace.SelectTab(scene.Workspace.TaskTabs[1]);
            Layout(window, 1024, 640);
            Check(Bounds(tabBar, root).Bottom <= Bounds(header, root).Top,
                "1024: task tabs and application header do not overlap");
            var back = Named<Button>(window, "WorkbenchBackButton");
            var toRoot = Named<Button>(window, "WorkbenchRootButton");
            var focusBack = Named<Button>(window, "FocusModeBackButton");
            var focusRoot = Named<Button>(window, "FocusModeRootButton");
            var scanRootPath = scene.Workspace.ActiveViewModel.CurrentRoot!.Path;
            var child = scene.Workspace.ActiveViewModel.CurrentRoot!.Children[0];
            scene.Workspace.ActiveViewModel.ZoomIntoNodeCommand.Execute(child);
            Layout(window, 1024, 640);
            Check(back.IsEnabled && Fits(back, root), "1024: first-folder Back is enabled and inside content");
            Check(toRoot.IsEnabled && Fits(toRoot, root), "1024: Root house button enables on the first folder entry");
            Check(Bounds(back, root).Right + 6 <= Bounds(toRoot, root).Left,
                "1024: workbench Back and Root buttons have a visible gap");
            Save(Render(root), "03-workspace-1024x640.png");
            Execute(back);
            Check(!scene.Workspace.ActiveViewModel.CanZoomOut, "1024: bound Back returns to scan root");
            var navigationVm = scene.Workspace.ActiveViewModel;
            navigationVm.ZoomIntoNodeCommand.Execute(child);
            navigationVm.ZoomIntoNodeCommand.Execute(child.Children[0]);
            Layout(window, 1024, 640);
            Execute(toRoot);
            Check(navigationVm.CurrentRoot?.Path == scanRootPath && !back.IsEnabled && !toRoot.IsEnabled,
                "1024: Root house button returns from two folder levels to this scan root and disables both buttons");
            navigationVm.ZoomIntoNodeCommand.Execute(child);
            navigationVm.ZoomIntoNodeCommand.Execute(child.Children[0]);
            Layout(window, 1024, 640);
            Check(focusBack.IsEnabled && focusRoot.IsEnabled && ReferenceEquals(focusRoot.Command, navigationVm.ResetZoomCommand),
                "Focus navigation binds the real enabled Back and ResetZoom commands");
            // Measure the declared focus buttons without invoking the OS fullscreen controller.
            var focusBackBinding = BindingOperations.GetBindingBase(focusBack, UIElement.VisibilityProperty)!;
            var focusRootBinding = BindingOperations.GetBindingBase(focusRoot, UIElement.VisibilityProperty)!;
            focusBack.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            focusRoot.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
            Layout(window, 1024, 640);
            var focusBackBounds = Bounds(focusBack, root);
            var focusRootBounds = Bounds(focusRoot, root);
            Check(Fits(focusRoot, root) && focusBackBounds.Right + 6 <= focusRootBounds.Left,
                "1024: declared focus Back and Root buttons fit with a visible gap");
            observations["focus-button-layout-only"] = new { back = focusBackBounds.ToString(), root = focusRootBounds.ToString() };
            Execute(focusRoot);
            Check(navigationVm.CurrentRoot?.Path == scanRootPath && !focusBack.IsEnabled && !focusRoot.IsEnabled,
                "Focus Root button returns to this scan root through the actual bound command");
            BindingOperations.SetBinding(focusBack, UIElement.VisibilityProperty, focusBackBinding);
            BindingOperations.SetBinding(focusRoot, UIElement.VisibilityProperty, focusRootBinding);
            var newTask = Descendants<Button>(tabBar).Single(button => Equals(button.Content, "+"));
            Check(newTask.IsEnabled && Fits(newTask, root), "1024: New task stays enabled and inside content");
            var currentVm = scene.Workspace.ActiveViewModel;
            Execute(newTask);
            Check(currentVm.IsScanLocationPickerOpen, "1024: New task opens location picker through its bound command");
            currentVm.CloseScanLocationPickerCommand.Execute(null);
            var closeButtons = Descendants<Button>(tabBar).Where(button => Equals(button.Content, "×")).ToArray();
            var activeClose = closeButtons.Single(button => ReferenceEquals(button.CommandParameter, scene.Workspace.ActiveTab));
            Check(activeClose.IsEnabled && Fits(activeClose, root), "1024: active task Close stays enabled and inside content");
            var activeCloseBounds = Bounds(activeClose, root);
            observations["task-scroll"] = new { tabsScroll.ExtentWidth, tabsScroll.ViewportWidth, tabsScroll.ScrollableWidth, tabsScroll.HorizontalOffset };
            var closedTab = scene.Workspace.ActiveTab;
            Execute(activeClose);
            Check(!scene.Workspace.TaskTabs.Contains(closedTab), "1024: bound Close removes only its task");
            Check(scene.Workspace.TaskTabs[0].IsHome, "1024: Home remains the first tab after closing");
            root = Layout(window, 1024, 640);
            observations["workspace-1024-bounds"] = new
            {
                tabs = Bounds(tabBar, root).ToString(), header = Bounds(header, root).ToString(),
                home = Bounds(home, root).ToString(), back = Bounds(back, root).ToString(),
                newTask = Bounds(newTask, root).ToString(), close = activeCloseBounds.ToString(),
            };

            scene.Workspace.SelectTab(scene.Workspace.TaskTabs[1]);
            var active = scene.Workspace.ActiveViewModel;
            Invoke(active, "BeginScheduledScan");
            active.ShowScanNavigationNoticeCommand.Execute(null);
            root = Layout(window, 1024, 640);
            var overlay = Named<FrameworkElement>(window, "ScanNavigationNoticeOverlay");
            Check(overlay.Visibility == Visibility.Visible, "Scan navigation notice opens from production VM notification");
            Save(Render(root), "05-scanning-navigation-notice-1024x640.png");
            var dismiss = Descendants<Button>(overlay).Single(button => Equals(button.Content, "知道了"));
            Execute(dismiss);
            Layout(window, 1024, 640);
            Check(!active.IsScanNavigationNoticeOpen && overlay.Visibility == Visibility.Collapsed,
                "Scan navigation notice closes through its bound button command");
            Invoke(active, "FinishScheduledScan", ScanTaskState.Completed, null!);
        }

        var settingsSamples = new List<BitmapSource>();
        foreach (var configured in new[] { false, true })
        {
            var settings = AppSettings.Default() with
            {
                AiConnectionProfileId = configured ? AiConnectionOption.DeepSeekId : AiConnectionOption.DisabledId,
                ProviderKind = configured ? AiProviderKind.OpenAiCompatible : AiProviderKind.Disabled,
                AiEndpoint = configured ? AiConnectionOption.DeepSeekEndpoint : string.Empty,
                AiModel = configured ? AiConnectionOption.DeepSeekModel : string.Empty,
                AiReasoningEffort = configured ? AiConnectionOption.DeepSeekReasoningEffort : string.Empty,
            };
            using var scene = new Scene(settings);
            var vm = scene.Workspace.ActiveViewModel;
            vm.SelectSettingsSectionCommand.Execute("connection");
            vm.OpenSettingsCommand.Execute(null);
            var root = Layout(scene.Window, 1280, 800);
            var action = Named<Button>(scene.Window, "ConfigureAiConnectionButton");
            var fields = Named<StackPanel>(scene.Window, "AiConnectionFields");
            Check(action.Visibility == (configured ? Visibility.Collapsed : Visibility.Visible),
                $"Settings configured={configured}: setup action visibility is correct");
            Check(fields.Visibility == (configured ? Visibility.Visible : Visibility.Collapsed),
                $"Settings configured={configured}: connection fields visibility is correct");
            if (configured)
            {
                var labels = Descendants<TextBlock>(fields).Select(text => text.Text).ToArray();
                foreach (var label in new[] { "分析模型渠道", "接口地址", "模型名称", "API 密钥", "思考强度（接口支持时生效）" })
                    Check(labels.Contains(label), "Configured DeepSeek uses Chinese field label: " + label);
                Check(vm.IsDeepSeekConnection, "Configured settings keep the DeepSeek selection");
            }
            var target = configured ? (FrameworkElement)fields : action;
            var scroll = Ancestor<ScrollViewer>(target);
            Check(ReferenceEquals(scroll, Named<ScrollViewer>(scene.Window, "SettingsContentScrollViewer")),
                $"Settings configured={configured}: connection controls use the current category scroll area");
            Check(vm.SettingsSection == "connection" && target.ActualHeight > 0,
                $"Settings configured={configured}: connection category content is laid out");
            root = Layout(scene.Window, 1280, 800);
            var panel = Named<FrameworkElement>(scene.Window, "SettingsPanel");
            settingsSamples.Add(Crop(Render(root), Bounds(panel, root)));
        }
        Save(Montage(settingsSamples, columns: 2), "04-settings-fresh-and-deepseek.png");
        SaveProgressSamples(Check, observations);
        observations["limitations"] = "Offscreen production WPF layout only. No visible window, OS input, actual scan, AI network request, focus/fullscreen OS transition, or manual desktop experience.";
        File.WriteAllText(Path.Combine(OutputDirectory, "layout-observations.json"),
            JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    });

    private sealed class Scene : IDisposable
    {
        private readonly string? _previousData = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        private readonly string _data = Path.Combine(OutputDirectory, "isolated-data-" + Guid.NewGuid().ToString("N"));
        public ScanWorkspaceViewModel Workspace { get; }
        public MainWindow Window { get; }

        public Scene(AppSettings settings)
        {
            Directory.CreateDirectory(_data);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _data);
            var store = new AppSettingsStore();
            var secrets = new AppSecretStore();
            settings = settings with { RootPath = _data, HasCompletedExplorerGuide = true, IsReduceMotionEnabled = true };
            Workspace = new ScanWorkspaceViewModel((configuration, key) =>
            {
                var policy = new WindowsLocationPolicy();
                return new MainWindowViewModel(new DirectoryScanner(), new WindowsFolderPickerService(),
                    new DeletionPolicyService(policy), new ExplorerFileLocationService(), new RecycleBinService(),
                    new AiAnalysisService([], new AiCandidateBuilder(policy), new AiBatchPlanner()),
                    store, secrets, configuration, key);
            }, settings, string.Empty, store, secrets, (vm, path, _) =>
            {
                ApplySnapshot(vm, Tree(path));
                return Task.CompletedTask;
            });
            Window = new MainWindow { DataContext = Workspace.ActiveViewModel };
        }

        public void AddCompletedTask(string name)
        {
            var directory = Path.Combine(_data, name);
            Directory.CreateDirectory(directory);
            Complete(Workspace.StartScanAsync(directory));
        }

        public void Dispose()
        {
            Complete(Workspace.SaveSharedSettingsAsync(Workspace.ActiveViewModel));
            Window.Close();
            Workspace.Dispose();
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousData);
        }
    }

    private static FileSystemNode Tree(string path)
    {
        var file = new FileSystemNode(Path.Combine(path, "下载", "归档", "演示归档.zip"), "演示归档.zip", ".zip",
            4L * 1024 * 1024 * 1024, FileAttributes.Normal, null, null, false, false, [], []);
        var archive = new FileSystemNode(Path.Combine(path, "下载", "归档"), "归档", string.Empty, file.SizeBytes,
            FileAttributes.Directory, null, null, true, false, [], [file]);
        var folder = new FileSystemNode(Path.Combine(path, "下载"), "下载", string.Empty, file.SizeBytes,
            FileAttributes.Directory, null, null, true, false, [], [archive]);
        return new FileSystemNode(path, Path.GetFileName(path), string.Empty, file.SizeBytes,
            FileAttributes.Directory, null, null, true, false, [], [folder]);
    }

    private static void ApplySnapshot(MainWindowViewModel vm, FileSystemNode root) =>
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, null, null]);

    private static void SaveProgressSamples(Action<bool, string> check, Dictionary<string, object> observations)
    {
        var samples = new List<BitmapSource>();
        foreach (var theme in new[] { "01", "02", "03" })
        {
            var host = new Grid { Width = 500, Height = 116 };
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/DiskLens.App;component/Resources/Themes/Theme{theme}.xaml"),
            });
            var stack = new StackPanel { Margin = new Thickness(18, 10, 18, 12) };
            host.Children.Add(stack);
            var heading = new TextBlock { Text = $"主题 {theme} · 50% / done", FontSize = 14, Margin = new Thickness(0, 0, 0, 12) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            stack.Children.Add(heading);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(row);
            foreach (var complete in new[] { false, true })
            {
                var progress = new ScanTaskProgressControl
                {
                    Width = 218, Height = 36, RootLabel = @"C:\", Percent = 50,
                    ProgressText = "50%", IsMotionEnabled = false,
                };
                progress.IsComplete = complete;
                var border = new Border { Child = progress, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0) };
                border.SetResourceReference(Border.BackgroundProperty, "CardSubtleBackgroundBrush");
                border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                row.Children.Add(border);
                var fill = (ScaleTransform)Field(progress, "_fillScale")!;
                var rootLabel = (TextBlock)Field(progress, "_root")!;
                var done = (TextBlock)Field(progress, "_done")!;
                var percent = (TextBlock)Field(progress, "_percent")!;
                check(rootLabel.Text == @"C:\", $"Theme {theme} complete={complete}: progress preserves the root label");
                check(Math.Abs(fill.ScaleX - .5) < .001,
                    $"Theme {theme} complete={complete}: progress preserves measured half-tab coverage");
                check(done.Opacity == (complete ? 1 : 0) && percent.Opacity == (complete ? 0 : 1),
                    $"Theme {theme} complete={complete}: status switches between percent and done");
            }
            for (var pass = 0; pass < 3; pass++)
            {
                host.Measure(new Size(500, 116));
                host.Arrange(new Rect(0, 0, 500, 116));
                host.UpdateLayout();
                Pump();
            }
            samples.Add(Render(host));
        }
        observations["progress-samples"] = "Three themes; production ScanTaskProgressControl rendered at 50% and complete. Static end states only, not a desktop animation or input test.";
        Save(Montage(samples, columns: 1), "06-task-progress-three-themes-50pct-and-done.png");
    }

    private static FrameworkElement Layout(MainWindow window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        for (var pass = 0; pass < 3; pass++)
        {
            Pump();
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        return root;
    }

    private static BitmapSource Render(FrameworkElement root)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)root.FindResource("WindowBackgroundBrush"), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                Stretch = Stretch.Fill,
            }, null, new Rect(0, 0, width, height));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static BitmapSource Crop(BitmapSource source, Rect rect)
    {
        var left = Math.Max(0, (int)Math.Floor(rect.Left));
        var top = Math.Max(0, (int)Math.Floor(rect.Top));
        var image = new CroppedBitmap(source, new Int32Rect(left, top,
            Math.Min(source.PixelWidth - left, (int)Math.Ceiling(rect.Right) - left),
            Math.Min(source.PixelHeight - top, (int)Math.Ceiling(rect.Bottom) - top)));
        image.Freeze();
        return image;
    }

    private static BitmapSource Montage(IReadOnlyList<BitmapSource> images, int columns)
    {
        var width = images.Max(image => image.PixelWidth);
        var height = images.Max(image => image.PixelHeight);
        var rows = (images.Count + columns - 1) / columns;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width * columns, height * rows));
            for (var index = 0; index < images.Count; index++)
                drawing.DrawImage(images[index], new Rect(index % columns * width, index / columns * height,
                    images[index].PixelWidth, images[index].PixelHeight));
        }
        var output = new RenderTargetBitmap(width * columns, height * rows, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        output.Freeze();
        return output;
    }

    private static void Save(BitmapSource image, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(OutputDirectory, name));
        encoder.Save(stream);
    }

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
    private static Geometry GeometryInAncestor(Shape element, Visual ancestor)
    {
        var geometry = element.RenderedGeometry.Clone();
        var transform = element.TransformToAncestor(ancestor);
        var origin = transform.Transform(new Point());
        var xAxis = transform.Transform(new Point(1, 0)) - origin;
        var yAxis = transform.Transform(new Point(0, 1)) - origin;
        var matrix = geometry.Transform.Value;
        matrix.Append(new Matrix(xAxis.X, xAxis.Y, yAxis.X, yAxis.Y, origin.X, origin.Y));
        geometry.Transform = new MatrixTransform(matrix);
        return geometry;
    }
    private static bool Fits(FrameworkElement element, FrameworkElement root) =>
        element.Visibility == Visibility.Visible && new Rect(root.RenderSize).Contains(Bounds(element, root)) && element.ActualWidth > 0 && element.ActualHeight > 0;
    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        throw new InvalidOperationException("Missing ancestor " + typeof(T).Name);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Execute(Button button)
    {
        Assert.True(button.IsEnabled);
        Assert.NotNull(button.Command);
        Assert.True(button.Command.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
        Pump();
    }

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Offscreen fixture save timed out.");
            Pump();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                action();
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Offscreen WPF layout test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

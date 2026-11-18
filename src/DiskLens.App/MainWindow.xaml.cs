using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiskLens.App.Controls;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Visualization;

namespace DiskLens.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan OverlayEnterDuration = TimeSpan.FromMilliseconds(210);
    private static readonly TimeSpan OverlayExitDuration = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan FocusTransitionDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan TreemapLayoutFadeOutDuration = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan TreemapLayoutFadeInDuration = TimeSpan.FromMilliseconds(190);
    private const double FocusToggleTopMargin = 8;
    private const double TaskTabStripHeight = 44;
    private const double WorkAreaMargin = 24;
    private const double MinimumUsableWidth = 920;
    private const double MinimumUsableHeight = 560;
    private INotifyPropertyChanged? _trackedViewModel;
    private string? _appliedThemeId;
    private readonly Dictionary<FrameworkElement, DispatcherTimer> _overlayExitTimers = [];
    private readonly Dictionary<FrameworkElement, bool> _overlayDesiredVisibility = [];
    private DispatcherTimer? _modalBackdropExitTimer;
    private bool _isApplyingFocusTransition;
    private int _focusTransitionVersion;
    private int _lastExplorerGuideStep = -1;
    private bool? _lastExplorerGuideMotionEnabled;
    private IInputElement? _focusBeforeExplorerGuide;
    private bool _isExplorerGuideFocusSession;
    private bool _isRestoringGuideFocus;
    private IInputElement? _focusBeforeModal;
    private FrameworkElement? _activeModalPanel;
    private readonly WindowFullscreenController _fullscreenController;
    private ScanWorkspaceViewModel? _workspace;

    public MainWindow()
    {
        InitializeComponent();
        WindowLanguage.Attach(this);
        InitializeOfficialDistribution();
        ConfigureInitialWindowBounds();
        _fullscreenController = new WindowFullscreenController(this);
        DataContextChanged += MainWindow_OnDataContextChanged;
        Activated += (_, _) => SetAutomaticRefreshActive(true);
        Deactivated += (_, _) => SetAutomaticRefreshActive(false);
        Closed += (_, _) =>
        {
            SetAutomaticRefreshActive(false);
            DetachViewModelNotifications(_trackedViewModel);
            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= Workspace_OnPropertyChanged;
                _workspace.Dispose();
            }
        };
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        SetAutomaticRefreshActive(IsVisible && IsActive);
        if (DataContext is MainWindowViewModel viewModel)
        {
            ApiKeyPasswordBox.Password = viewModel.ApiKey;
            DeepSeekApiKeyPasswordBox.Password = viewModel.AiSetupApiKey;
            CustomApiKeyPasswordBox.Password = viewModel.AiSetupApiKey;
            ApplyTheme(viewModel.SelectedThemeId);
        }

        AttachViewModelNotifications(DataContext as MainWindowViewModel);
        SynchronizeOverlayVisibility(DataContext as MainWindowViewModel);
        ApplyWorkbenchResponsiveLayout();
        if (DataContext is MainWindowViewModel focusViewModel)
        {
            UpdateFocusModeVisuals(focusViewModel.IsFocusMode, animate: false);
        }

        UpdateExplorerGuideLayout(animateCard: false);
        SynchronizeExplorerGuideFocus(DataContext as MainWindowViewModel);
        StartSharedRecommendation();
        InitializeAnnouncements();
        if (DataContext is MainWindowViewModel modalViewModel)
        {
            SynchronizeModalFocus(modalViewModel);
        }
    }

    private void ApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.ApiKey = passwordBox.Password;
        }
    }

    private void AiSetupApiKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.AiSetupApiKey = passwordBox.Password;
        }
    }

    private void OfficialDocumentation_OnRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(e.Uri.Host, "api-docs.deepseek.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Uri.Host, "platform.deepseek.com", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                LocalizedMessageBox.Show(this, L.T("无法打开浏览器。请检查系统默认浏览器设置后重试。"),
                    L.T("官方说明"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        e.Handled = true;
    }

    private void SettingsBackdrop_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.CloseSettingsCommand.CanExecute(null))
        {
            viewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ModalBackdrop_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.IsSettingsOpen)
        {
            viewModel.CloseSettingsCommand.Execute(null);
        }

        // Keep modal clicks from reaching controls underneath, even when the active dialog
        // deliberately does not close from a backdrop click.
        e.Handled = true;
    }

    private void OverlayExitShield_OnInput(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (!viewModel.IsExplorerGuideOpen)
        {
            var modal = GetActiveModalPanel(viewModel);
            if (modal is null && viewModel.Workspace is { } workspace && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.Tab)
                {
                    var index = workspace.TaskTabs.IndexOf(workspace.ActiveTab);
                    var direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                    workspace.SelectTabCommand.Execute(workspace.TaskTabs[(index + direction + workspace.TaskTabs.Count) % workspace.TaskTabs.Count]);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.W)
                {
                    if (workspace.CloseTabCommand.CanExecute(workspace.ActiveTab))
                        workspace.CloseTabCommand.Execute(workspace.ActiveTab);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.T)
                {
                    if (viewModel.ScanCommand.CanExecute(null)) viewModel.ScanCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D1)
                {
                    workspace.SelectTabCommand.Execute(workspace.TaskTabs[0]);
                    e.Handled = true;
                    return;
                }
            }
            if (modal is not null && Keyboard.FocusedElement is DependencyObject focused)
            {
                var comboBox = FindVisualAncestor<ComboBox>(focused) ?? FindOpenComboBox(modal);
                if (comboBox?.IsDropDownOpen == true)
                {
                    if (e.Key == Key.Escape)
                    {
                        comboBox.IsDropDownOpen = false;
                        e.Handled = true;
                    }
                    return;
                }

                if (!IsDescendantOf(focused, modal) && e.Key != Key.Escape)
                {
                    FocusFirstModalControl(modal);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Escape)
            {
                if (viewModel.IsScanReportOpen)
                {
                    viewModel.CloseScanReportCommand.Execute(null);
                    e.Handled = true;
                }
                else if (viewModel.IsScanNavigationNoticeOpen)
                {
                    viewModel.CloseScanNavigationNoticeCommand.Execute(null);
                    e.Handled = true;
                }
                else if (viewModel.IsScanLocationPickerOpen)
                {
                    viewModel.CloseScanLocationPickerCommand.Execute(null);
                    e.Handled = true;
                }
                else if (viewModel.IsHelpOpen)
                {
                    viewModel.CloseHelpCommand.Execute(null);
                    e.Handled = true;
                }
                else if (viewModel.IsAiSetupOpen)
                {
                    viewModel.CloseAiSetupCommand.Execute(null);
                    e.Handled = true;
                }
                else if (viewModel.IsSettingsOpen)
                {
                    viewModel.CloseSettingsCommand.Execute(null);
                    e.Handled = true;
                }
                else if (viewModel.IsFocusMode)
                {
                    viewModel.ExitFocusMode();
                    e.Handled = true;
                }
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.DismissExplorerGuideCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.SystemKey == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            MoveExplorerGuideFocus(viewModel, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back || (e.Key == Key.Home && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            e.Handled = true;
            return;
        }

        var isActivationKey = e.Key is Key.Enter or Key.Space;
        var isAllowedFocus = IsAllowedExplorerGuideFocus(Keyboard.FocusedElement as DependencyObject, viewModel);
        if (isActivationKey && !isAllowedFocus)
        {
            e.Handled = true;
        }
        else if (!isAllowedFocus)
        {
            e.Handled = true;
        }
    }

    private void ExplorerMetricsBar_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ExplorerDisplayControls is null || VolumeUsageMetrics is null || e.NewSize.Width <= 0)
        {
            return;
        }

        var isCompact = e.NewSize.Width < 720;
        Grid.SetRow(ExplorerDisplayControls, isCompact ? 1 : 0);
        Grid.SetColumn(ExplorerDisplayControls, isCompact ? 0 : 2);
        Grid.SetColumnSpan(ExplorerDisplayControls, isCompact ? 3 : 1);
        ExplorerDisplayControls.HorizontalAlignment = HorizontalAlignment.Right;
        ExplorerDisplayControls.Margin = new Thickness(0, isCompact ? 10 : 0, 0, 0);
        VolumeUsageMetrics.Margin = new Thickness(0, 0, isCompact ? 0 : 20, 0);
    }

    private void WorkbenchGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel focusViewModel && focusViewModel.IsFocusMode)
        {
            if (!_isApplyingFocusTransition)
            {
                ApplyFocusLayout();
            }

            UpdateExplorerGuideLayout(animateCard: false);
            return;
        }

        ApplyWorkbenchResponsiveLayout(e.NewSize.Width);

        UpdateExplorerGuideLayout(animateCard: false);
    }

    private void ApplyWorkbenchResponsiveLayout(double? widthOverride = null)
    {
        if (WorkbenchGrid.RowDefinitions.Count < 2 || WorkbenchGrid.ColumnDefinitions.Count < 2)
        {
            return;
        }

        var width = widthOverride ?? WorkbenchGrid.ActualWidth;
        var hasWorkspace = DataContext is MainWindowViewModel { HasScanWorkspace: true };
        ExplorerNavigationBar.Visibility = hasWorkspace ? Visibility.Visible : Visibility.Collapsed;
        ExplorerMetricsBar.Visibility = hasWorkspace ? Visibility.Visible : Visibility.Collapsed;
        DetailsWorkspace.Visibility = DataContext is MainWindowViewModel { HasScanResult: true, IsScanning: false }
            ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(TreemapWorkspace, hasWorkspace ? 1 : 2);
        WorkbenchGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(hasWorkspace ? (width < 980 ? 300 : 340) : 0);
        WorkbenchGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.RowDefinitions[1].Height = new GridLength(0, GridUnitType.Star);

        Grid.SetRow(DetailsWorkspace, 0);
        Grid.SetColumn(DetailsWorkspace, 1);
        DetailsWorkspace.Margin = new Thickness(16, 0, 0, 0);
    }

    private void FocusModeToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.IsFocusModeToggleInteractive)
        {
            viewModel.ToggleFocusMode();
            e.Handled = true;
        }
    }

    private void UpdateFocusModeVisuals(bool isFocusMode, bool animate)
    {
        _isApplyingFocusTransition = true;
        var transitionVersion = ++_focusTransitionVersion;

        _fullscreenController.SetFullscreen(isFocusMode);
        SetFocusTogglePlacement();
        AnimateFocusToggle(isFocusMode, animate);
        var chrome = new FrameworkElement[]
        {
            AppHeader,
            ExplorerNavigationBar,
            ExplorerMetricsBar,
            DetailsWorkspace,
            AppFooter,
        };

        if (isFocusMode)
        {
            var transitionElements = chrome.Append(TreemapWorkspace).ToArray();
            AnimateChromeOpacity(transitionElements, 0, animate, () =>
            {
                if (transitionVersion != _focusTransitionVersion
                    || DataContext is not MainWindowViewModel currentViewModel
                    || !currentViewModel.IsFocusMode)
                {
                    return;
                }

                MainContentGrid.Margin = new Thickness(0, 64 + TaskTabStripHeight, 0, 0);
                AppHeader.Visibility = Visibility.Collapsed;
                AppFooter.Visibility = Visibility.Collapsed;
                ContentHeaderRow.Height = new GridLength(0);
                ContentFooterRow.Height = new GridLength(0);
                ContentWorkbenchRow.Height = new GridLength(1, GridUnitType.Star);
                ExplorerNavigationBar.Visibility = Visibility.Collapsed;
                ExplorerMetricsBar.Visibility = Visibility.Collapsed;
                DetailsWorkspace.Visibility = Visibility.Collapsed;
                WorkbenchGrid.Margin = new Thickness(0);
                WorkbenchGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Star);
                WorkbenchGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                WorkbenchGrid.RowDefinitions[1].Height = new GridLength(0, GridUnitType.Star);
                Grid.SetRow(TreemapWorkspace, 0);
                Grid.SetColumn(TreemapWorkspace, 0);
                TreemapWorkspace.Padding = new Thickness(4);
                TreemapView.Margin = new Thickness(4);
                SetFocusTogglePlacement();
                AnimateTreemapWorkspace(show: true, animate, () => CompleteFocusTransition(transitionVersion));
            });
            return;
        }

        AnimateTreemapWorkspace(show: false, animate, () =>
        {
            if (transitionVersion != _focusTransitionVersion
                || DataContext is not MainWindowViewModel currentViewModel
                || currentViewModel.IsFocusMode)
            {
                return;
            }

            MainContentGrid.Margin = new Thickness(18, 18 + TaskTabStripHeight, 18, 18);
            WorkbenchGrid.Margin = new Thickness(0, 18, 0, 14);
            TreemapWorkspace.Padding = new Thickness(14);
            TreemapView.Margin = new Thickness(10);
            AppHeader.Visibility = Visibility.Visible;
            AppFooter.Visibility = Visibility.Visible;
            ContentHeaderRow.Height = GridLength.Auto;
            ContentFooterRow.Height = GridLength.Auto;
            ContentWorkbenchRow.Height = new GridLength(1, GridUnitType.Star);
            ExplorerNavigationBar.Visibility = Visibility.Visible;
            ExplorerMetricsBar.Visibility = Visibility.Visible;
            DetailsWorkspace.Visibility = Visibility.Visible;
            ApplyWorkbenchResponsiveLayout();
            SetFocusTogglePlacement();
            foreach (var element in chrome)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = 0;
            }

            var remaining = 2;
            void FinishRestore()
            {
                if (--remaining == 0)
                {
                    CompleteFocusTransition(transitionVersion);
                }
            }

            AnimateChromeOpacity(chrome, 1, animate, FinishRestore);
            AnimateTreemapWorkspace(show: true, animate, FinishRestore);
        });
    }

    private void AnimateTreemapWorkspace(bool show, bool animate, Action? completed = null)
    {
        var targetOpacity = show ? 1d : 0d;
        var targetScale = show ? 1d : 0.985d;
        TreemapWorkspace.IsHitTestVisible = show && !animate;
        TreemapWorkspace.BeginAnimation(OpacityProperty, null);
        TreemapWorkspaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TreemapWorkspaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (show && animate)
        {
            TreemapWorkspaceScale.ScaleX = 0.985;
            TreemapWorkspaceScale.ScaleY = 0.985;
        }

        if (!animate)
        {
            TreemapWorkspace.Opacity = targetOpacity;
            TreemapWorkspaceScale.ScaleX = targetScale;
            TreemapWorkspaceScale.ScaleY = targetScale;
            TreemapWorkspace.IsHitTestVisible = show;
            completed?.Invoke();
            return;
        }

        var duration = show ? TreemapLayoutFadeInDuration : TreemapLayoutFadeOutDuration;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };
        opacityAnimation.Completed += (_, _) =>
        {
            TreemapWorkspace.Opacity = targetOpacity;
            TreemapWorkspaceScale.ScaleX = targetScale;
            TreemapWorkspaceScale.ScaleY = targetScale;
            TreemapWorkspace.IsHitTestVisible = show;
            completed?.Invoke();
        };
        TreemapWorkspace.BeginAnimation(OpacityProperty, opacityAnimation);
        TreemapWorkspaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        });
        TreemapWorkspaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private void CompleteFocusTransition(int transitionVersion)
    {
        if (transitionVersion == _focusTransitionVersion)
        {
            _isApplyingFocusTransition = false;
            UpdateExplorerGuideLayout(animateCard: false);
        }
    }

    private void ApplyFocusLayout()
    {
        MainContentGrid.Margin = new Thickness(0, 64 + TaskTabStripHeight, 0, 0);
        WorkbenchGrid.Margin = new Thickness(0);
        ContentHeaderRow.Height = new GridLength(0);
        ContentFooterRow.Height = new GridLength(0);
        ContentWorkbenchRow.Height = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Star);
        WorkbenchGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.RowDefinitions[1].Height = new GridLength(0, GridUnitType.Star);
        SetFocusTogglePlacement();
    }

    private void SetFocusTogglePlacement()
    {
        var isFocusMode = DataContext is MainWindowViewModel { IsFocusMode: true };
        var host = isFocusMode ? RootLayout : WorkbenchFocusToggleHost;
        if (!ReferenceEquals(FocusModeToggleButton.Parent, host))
        {
            FocusModeToggleButton.BeginAnimation(MarginProperty, null);
            FocusModeToggleButton.Margin = new Thickness(0);
            if (FocusModeToggleButton.Parent is Panel previousHost)
            {
                previousHost.Children.Remove(FocusModeToggleButton);
            }

            host.Children.Add(FocusModeToggleButton);
        }

        Grid.SetRow(FocusModeToggleButton, 0);
        Grid.SetRowSpan(FocusModeToggleButton, 1);
        FocusModeToggleButton.HorizontalAlignment = HorizontalAlignment.Center;
        FocusModeToggleButton.VerticalAlignment = isFocusMode ? VerticalAlignment.Top : VerticalAlignment.Center;
    }

    private void AnimateFocusToggle(bool isFocusMode, bool animate)
    {
        FocusModeToggleButton.ToolTip = isFocusMode ? L.T("退出全屏并返回工作台（Esc）") : L.T("全屏浏览文件块");
        FocusModeEntryGlyph.Visibility = isFocusMode ? Visibility.Collapsed : Visibility.Visible;
        FocusModeExitGlyph.Visibility = isFocusMode ? Visibility.Visible : Visibility.Collapsed;
        FocusModeToggleButton.Width = isFocusMode ? 52 : 40;
        FocusModeToggleButton.Height = isFocusMode ? 52 : 40;

        var targetMargin = new Thickness(0, isFocusMode ? FocusToggleTopMargin + TaskTabStripHeight : 0, 0, 0);
        var currentMargin = FocusModeToggleButton.Margin;
        FocusModeToggleButton.BeginAnimation(MarginProperty, null);
        FocusModeToggleButton.Margin = currentMargin;
        if (!animate)
        {
            FocusModeToggleButton.Margin = targetMargin;
            return;
        }

        var animation = new ThicknessAnimation(currentMargin, targetMargin, FocusTransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        FocusModeToggleButton.BeginAnimation(MarginProperty, animation);
    }

    private void AnimateChromeOpacity(
        IReadOnlyList<FrameworkElement> elements,
        double target,
        bool animate,
        Action? completed = null)
    {
        var duration = animate ? FocusTransitionDuration : TimeSpan.Zero;
        var remaining = elements.Count;
        if (remaining == 0)
        {
            completed?.Invoke();
            return;
        }

        foreach (var element in elements)
        {
            element.BeginAnimation(OpacityProperty, null);
            if (!animate)
            {
                element.Opacity = target;
                if (--remaining == 0)
                {
                    completed?.Invoke();
                }

                continue;
            }

            var animation = new DoubleAnimation
            {
                To = target,
                Duration = duration,
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.Completed += (_, _) =>
            {
                element.Opacity = target;
                if (--remaining == 0)
                {
                    completed?.Invoke();
                }
            };
            element.BeginAnimation(OpacityProperty, animation);
        }
    }

    private void VisualTile_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || sender is not FrameworkElement element
            || element.DataContext is not VisualExplorerItem item)
        {
            return;
        }

        if (viewModel.IsScanning)
        {
            if (viewModel.ShowScanNavigationNoticeCommand.CanExecute(null))
                viewModel.ShowScanNavigationNoticeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        viewModel.SelectedNode = item.DisplayNode;
        var isPagingClick = DiskLens.Core.Models.DisplayNodeMetadata.IsOverflow(item.DisplayNode) && e.ClickCount == 1;
        var isDirectoryDoubleClick = !DiskLens.Core.Models.DisplayNodeMetadata.IsOverflow(item.DisplayNode) && e.ClickCount == 2;
        if ((isPagingClick || isDirectoryDoubleClick) && viewModel.ZoomIntoNodeCommand.CanExecute(item.DisplayNode))
        {
            viewModel.ZoomIntoNodeCommand.Execute(item.DisplayNode);
        }
        e.Handled = true;
    }

    private void MainWindow_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        (e.OldValue as MainWindowViewModel)?.SetAutomaticRefreshActive(false);
        DetachViewModelNotifications(e.OldValue as INotifyPropertyChanged);
        AttachViewModelNotifications(e.NewValue as MainWindowViewModel);
        if (e.NewValue is MainWindowViewModel viewModel)
        {
            viewModel.ConfirmAiSubmissionAsync = ConfirmAiSubmissionAsync;
            viewModel.ConfirmBudgetContinuationAsync = ConfirmBudgetContinuationAsync;
            AttachWorkspace(viewModel.Workspace);
            ApiKeyPasswordBox.Password = viewModel.ApiKey;
            DeepSeekApiKeyPasswordBox.Password = viewModel.AiSetupApiKey;
            CustomApiKeyPasswordBox.Password = viewModel.AiSetupApiKey;
            ApplyTheme(viewModel.SelectedThemeId);
            SynchronizeOverlayVisibility(viewModel);
            UpdateFocusModeVisuals(viewModel.IsFocusMode, animate: false);
            SynchronizeModalFocus(viewModel);
            viewModel.SetAutomaticRefreshActive(IsVisible && IsActive);
        }

        UpdateExplorerGuideLayout(animateCard: false);
    }

    private void AttachWorkspace(ScanWorkspaceViewModel? workspace)
    {
        if (ReferenceEquals(_workspace, workspace)) return;
        if (_workspace is not null) _workspace.PropertyChanged -= Workspace_OnPropertyChanged;
        _workspace = workspace;
        if (_workspace is not null) _workspace.PropertyChanged += Workspace_OnPropertyChanged;
    }

    private void Workspace_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanWorkspaceViewModel.ActiveViewModel) && _workspace is not null)
        {
            if (DataContext is MainWindowViewModel previous)
            {
                previous.IsScanNavigationNoticeOpen = false;
                previous.CloseScanReportCommand.Execute(null);
            }
            _activeModalPanel = null;
            _focusBeforeModal = null;
            DataContext = _workspace.ActiveViewModel;
            ApplyWorkbenchResponsiveLayout();
            var selectedTab = _workspace.ActiveTab;
            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_workspace?.ActiveTab, selectedTab)
                    && TaskTabItems.ItemContainerGenerator.ContainerFromItem(selectedTab) is FrameworkElement container)
                    container.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }

    private void SetAutomaticRefreshActive(bool active)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.SetAutomaticRefreshActive(active && IsVisible);
    }

    private void TaskTabsScroller_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TaskTabsScroller.ScrollableWidth <= 0) return;
        TaskTabsScroller.ScrollToHorizontalOffset(TaskTabsScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void SettingsComboBox_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var comboBox = FindVisualAncestor<ComboBox>(e.OriginalSource as DependencyObject)
            ?? FindVisualAncestor<ComboBox>(e.Source as DependencyObject);
        if (comboBox is null || comboBox.IsDropDownOpen) return;

        // A closed, focused ComboBox changes its selection on wheel input by
        // default. In settings the wheel should scroll the page instead.
        e.Handled = true;
        var page = FindVisualAncestor<ScrollViewer>(comboBox);
        page?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = page,
        });
    }

    private void AttachViewModelNotifications(MainWindowViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(_trackedViewModel, viewModel))
        {
            return;
        }

        DetachViewModelNotifications(_trackedViewModel);
        _trackedViewModel = viewModel;
        _trackedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
    }

    private void DetachViewModelNotifications(INotifyPropertyChanged? previousViewModel)
    {
        if (previousViewModel is null)
        {
            return;
        }

        previousViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        if (ReferenceEquals(_trackedViewModel, previousViewModel))
        {
            _trackedViewModel = null;
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ApiKey) && sender is MainWindowViewModel keyViewModel
            && !string.Equals(ApiKeyPasswordBox.Password, keyViewModel.ApiKey, StringComparison.Ordinal))
            ApiKeyPasswordBox.Password = keyViewModel.ApiKey;
        if (sender is MainWindowViewModel setupViewModel
            && e.PropertyName is nameof(MainWindowViewModel.AiSetupApiKey) or nameof(MainWindowViewModel.AiSetupTitle))
        {
            if (!string.Equals(DeepSeekApiKeyPasswordBox.Password, setupViewModel.AiSetupApiKey, StringComparison.Ordinal))
            {
                DeepSeekApiKeyPasswordBox.Password = setupViewModel.AiSetupApiKey;
            }

            if (!string.Equals(CustomApiKeyPasswordBox.Password, setupViewModel.AiSetupApiKey, StringComparison.Ordinal))
            {
                CustomApiKeyPasswordBox.Password = setupViewModel.AiSetupApiKey;
            }
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SelectedThemeId)
            && sender is MainWindowViewModel viewModel)
        {
            ApplyTheme(viewModel.SelectedThemeId);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsReduceMotionEnabled)
            && sender is MainWindowViewModel motionViewModel)
        {
            HomeLanding.ApplyMotionPreference(motionViewModel.IsReduceMotionEnabled);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.CurrentRoot)
            or nameof(MainWindowViewModel.VisibleAnalysisItems)
            or nameof(MainWindowViewModel.HasVisibleAnalysisItems))
        {
            Dispatcher.InvokeAsync(ScrollAnalysisResultsToTop, DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedNode)
            or nameof(MainWindowViewModel.SelectedAnalysisItem))
        {
            Dispatcher.InvokeAsync(ScrollSelectedDetailsToTop, DispatcherPriority.Background);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsFocusMode)
            && sender is MainWindowViewModel focusViewModel)
        {
            UpdateFocusModeVisuals(focusViewModel.IsFocusMode, animate: focusViewModel.IsViewportMotionEnabled);
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsScanning) or nameof(MainWindowViewModel.HasScanWorkspace))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (DataContext is MainWindowViewModel currentViewModel && !currentViewModel.IsFocusMode)
                {
                    ApplyWorkbenchResponsiveLayout();
                }
            }, DispatcherPriority.Loaded);
        }

        if (sender is MainWindowViewModel overlayViewModel)
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.IsSettingsOpen):
                    SetOverlayOpenState(SettingsOverlay, overlayViewModel.IsSettingsOpen);
                    break;
                case nameof(MainWindowViewModel.IsHelpOpen):
                    SetOverlayOpenState(HelpOverlay, overlayViewModel.IsHelpOpen);
                    break;
                case nameof(MainWindowViewModel.IsScanLocationPickerOpen):
                    SetOverlayOpenState(ScanLocationPickerOverlay, overlayViewModel.IsScanLocationPickerOpen);
                    break;
                case nameof(MainWindowViewModel.IsAiSetupOpen):
                    SetOverlayOpenState(AiSetupOverlay, overlayViewModel.IsAiSetupOpen);
                    break;
                case nameof(MainWindowViewModel.IsScanReportOpen):
                    SetOverlayOpenState(ScanReportOverlay, overlayViewModel.IsScanReportOpen);
                    break;
                case nameof(MainWindowViewModel.IsScanNavigationNoticeOpen):
                    SetOverlayOpenState(ScanNavigationNoticeOverlay, overlayViewModel.IsScanNavigationNoticeOpen);
                    break;
                case nameof(MainWindowViewModel.IsExplorerGuideOpen):
                    SetOverlayOpenState(ExplorerGuideOverlay, overlayViewModel.IsExplorerGuideOpen);
                    SynchronizeExplorerGuideFocus(overlayViewModel);
                    break;
                case nameof(MainWindowViewModel.IsViewportMotionEnabled) when !overlayViewModel.IsViewportMotionEnabled:
                    ResetOverlayMotion();
                    foreach (var overlay in new FrameworkElement[]
                             {
                                 SettingsOverlay,
                                 HelpOverlay,
                                 ScanLocationPickerOverlay,
                                 AiSetupOverlay,
                                 ScanReportOverlay,
                                 ScanNavigationNoticeOverlay,
                                 ExplorerGuideOverlay,
                             })
                    {
                        CancelOverlayExit(overlay);
                        overlay.Opacity = 1;
                        overlay.Visibility = GetOverlayDesiredVisibility(overlay) ? Visibility.Visible : Visibility.Collapsed;
                        overlay.IsHitTestVisible = GetOverlayDesiredVisibility(overlay);
                    }

                    UpdateFocusModeVisuals(overlayViewModel.IsFocusMode, animate: false);

                    break;
            }

            if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsOpen)
                or nameof(MainWindowViewModel.IsHelpOpen)
                or nameof(MainWindowViewModel.IsScanLocationPickerOpen)
                or nameof(MainWindowViewModel.IsAiSetupOpen)
                or nameof(MainWindowViewModel.IsScanReportOpen)
                or nameof(MainWindowViewModel.IsScanNavigationNoticeOpen)
                or nameof(MainWindowViewModel.IsExplorerGuideOpen))
            {
                UpdateModalBackdrop(overlayViewModel, animate: !overlayViewModel.IsExplorerGuideOpen);
                SynchronizeModalFocus(overlayViewModel);
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.IsViewportMotionEnabled)
                     && !overlayViewModel.IsViewportMotionEnabled)
            {
                UpdateModalBackdrop(overlayViewModel, animate: false);
            }
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsExplorerGuideOpen)
            or nameof(MainWindowViewModel.ExplorerGuideStep)
            or nameof(MainWindowViewModel.IsViewportMotionEnabled)
            or nameof(MainWindowViewModel.IsScanLocationPickerOpen))
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ExplorerGuideStep)
                && DataContext is MainWindowViewModel guideViewModel)
            {
                KeepExplorerGuideFocusInside(guideViewModel);
            }

            Dispatcher.InvokeAsync(
                () => UpdateExplorerGuideLayout(animateCard: e.PropertyName == nameof(MainWindowViewModel.ExplorerGuideStep)),
                DispatcherPriority.Loaded);
        }
    }

    private void SynchronizeOverlayVisibility(MainWindowViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        SetOverlayOpenState(SettingsOverlay, viewModel.IsSettingsOpen, animate: false);
        SetOverlayOpenState(HelpOverlay, viewModel.IsHelpOpen, animate: false);
        SetOverlayOpenState(ScanLocationPickerOverlay, viewModel.IsScanLocationPickerOpen, animate: false);
        SetOverlayOpenState(AiSetupOverlay, viewModel.IsAiSetupOpen, animate: false);
        SetOverlayOpenState(ScanReportOverlay, viewModel.IsScanReportOpen, animate: false);
        SetOverlayOpenState(ScanNavigationNoticeOverlay, viewModel.IsScanNavigationNoticeOpen, animate: false);
        SetOverlayOpenState(ExplorerGuideOverlay, viewModel.IsExplorerGuideOpen, animate: false);
        UpdateModalBackdrop(viewModel, animate: false);
    }

    private void UpdateModalBackdrop(MainWindowViewModel viewModel, bool animate = true)
    {
        var shouldBeVisible = !viewModel.IsExplorerGuideOpen
                              && (viewModel.IsSettingsOpen || viewModel.IsHelpOpen || viewModel.IsScanLocationPickerOpen || viewModel.IsAiSetupOpen || viewModel.IsScanNavigationNoticeOpen || viewModel.IsScanReportOpen);
        var shouldAnimate = animate && viewModel.IsViewportMotionEnabled;
        CancelModalBackdropExit();

        var wasVisible = ModalBackdropLayer.Visibility == Visibility.Visible;
        var currentOpacity = wasVisible
            ? StopAndReadCurrentValue(ModalBackdropLayer, OpacityProperty)
            : 0;

        if (shouldBeVisible)
        {
            ModalBackdropLayer.Visibility = Visibility.Visible;
            ModalBackdropLayer.IsHitTestVisible = true;
            if (!shouldAnimate)
            {
                ModalBackdropLayer.Opacity = 1;
                return;
            }

            var from = wasVisible ? Math.Clamp(currentOpacity, 0, 1) : 0;
            ModalBackdropLayer.Opacity = from;
            ModalBackdropLayer.BeginAnimation(
                OpacityProperty,
                CreateOverlayOpacityAnimation(from, 1, OverlayEnterDuration));
            return;
        }

        if (!wasVisible)
        {
            ModalBackdropLayer.IsHitTestVisible = false;
            ModalBackdropLayer.Visibility = Visibility.Collapsed;
            ModalBackdropLayer.Opacity = 1;
            return;
        }

        ModalBackdropLayer.IsHitTestVisible = true;
        if (!shouldAnimate)
        {
            ModalBackdropLayer.BeginAnimation(OpacityProperty, null);
            ModalBackdropLayer.Opacity = 1;
            ModalBackdropLayer.Visibility = Visibility.Collapsed;
            return;
        }

        ModalBackdropLayer.BeginAnimation(
            OpacityProperty,
            CreateOverlayOpacityAnimation(currentOpacity, 0, OverlayExitDuration));
        _modalBackdropExitTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = OverlayExitDuration,
        };
        _modalBackdropExitTimer.Tick += (_, _) =>
        {
            CancelModalBackdropExit();
            if (DataContext is MainWindowViewModel currentViewModel
                && (currentViewModel.IsSettingsOpen || currentViewModel.IsHelpOpen || currentViewModel.IsScanLocationPickerOpen || currentViewModel.IsAiSetupOpen || currentViewModel.IsScanNavigationNoticeOpen || currentViewModel.IsScanReportOpen))
            {
                return;
            }

            ModalBackdropLayer.BeginAnimation(OpacityProperty, null);
            ModalBackdropLayer.Opacity = 1;
            ModalBackdropLayer.Visibility = Visibility.Collapsed;
            ModalBackdropLayer.IsHitTestVisible = false;
        };
        _modalBackdropExitTimer.Start();
        UpdateOverlayExitShield();
    }

    private void CancelModalBackdropExit()
    {
        if (_modalBackdropExitTimer is null)
        {
            return;
        }

        _modalBackdropExitTimer.Stop();
        _modalBackdropExitTimer = null;
        UpdateOverlayExitShield();
    }

    private void SetOverlayOpenState(FrameworkElement overlay, bool isOpen, bool animate = true)
    {
        _overlayDesiredVisibility[overlay] = isOpen;
        CancelOverlayExit(overlay);

        var shouldAnimate = animate
                            && DataContext is MainWindowViewModel viewModel
                            && viewModel.IsViewportMotionEnabled;
        if (isOpen)
        {
            var wasVisible = overlay.Visibility == Visibility.Visible;
            var currentOpacity = overlay.Opacity;
            overlay.Visibility = Visibility.Visible;
            overlay.IsHitTestVisible = true;
            overlay.BeginAnimation(OpacityProperty, null);
            var fromOpacity = wasVisible && shouldAnimate ? Math.Clamp(currentOpacity, 0, 1) : 0;
            overlay.Opacity = shouldAnimate ? fromOpacity : 1;
            var panel = GetOverlayPanel(overlay);
            if (panel is not null)
            {
                SetPanelMotion(panel, entering: true, shouldAnimate, continueFromCurrent: wasVisible);
            }

            if (shouldAnimate)
            {
                overlay.BeginAnimation(OpacityProperty, CreateOverlayOpacityAnimation(fromOpacity, 1, TimeSpan.FromMilliseconds(170)));
            }

            return;
        }

        if (overlay.Visibility != Visibility.Visible)
        {
            overlay.IsHitTestVisible = false;
            overlay.Visibility = Visibility.Collapsed;
            return;
        }

        // Release input and backdrop hit testing as soon as the command changes state.
        overlay.IsHitTestVisible = false;
        if (!shouldAnimate)
        {
            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Opacity = 1;
            ResetPanelMotion(overlay);
            overlay.Visibility = Visibility.Collapsed;
            return;
        }

        var closingPanel = GetOverlayPanel(overlay);
        if (closingPanel is not null)
        {
            SetPanelMotion(closingPanel, entering: false, animate: true, continueFromCurrent: true);
        }

        overlay.BeginAnimation(OpacityProperty, CreateOverlayOpacityAnimation(overlay.Opacity, 0, OverlayExitDuration));
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = OverlayExitDuration,
        };
        timer.Tick += (_, _) =>
        {
            if (!_overlayExitTimers.TryGetValue(overlay, out var activeTimer)
                || !ReferenceEquals(activeTimer, timer))
            {
                return;
            }

            timer.Stop();
            _overlayExitTimers.Remove(overlay);
            if (GetOverlayDesiredVisibility(overlay))
            {
                UpdateOverlayExitShield();
                return;
            }

            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Opacity = 1;
            ResetPanelMotion(overlay);
            overlay.Visibility = Visibility.Collapsed;
            UpdateOverlayExitShield();
        };
        _overlayExitTimers[overlay] = timer;
        timer.Start();
        UpdateOverlayExitShield();
    }

    private void CancelOverlayExit(FrameworkElement overlay)
    {
        if (_overlayExitTimers.Remove(overlay, out var timer))
        {
            timer.Stop();
            UpdateOverlayExitShield();
        }
    }

    private void UpdateOverlayExitShield()
    {
        var isExiting = _overlayExitTimers.Count > 0 || _modalBackdropExitTimer is not null;
        OverlayExitShield.Visibility = isExiting ? Visibility.Visible : Visibility.Collapsed;
        OverlayExitShield.IsHitTestVisible = isExiting;
    }

    private bool GetOverlayDesiredVisibility(FrameworkElement overlay)
    {
        return _overlayDesiredVisibility.TryGetValue(overlay, out var isOpen) && isOpen;
    }

    private FrameworkElement? GetOverlayPanel(FrameworkElement overlay)
    {
        return overlay switch
        {
            _ when overlay.Name == nameof(SettingsOverlay) => SettingsPanel,
            _ when overlay.Name == nameof(HelpOverlay) => HelpPanel,
            _ when overlay.Name == nameof(ScanLocationPickerOverlay) => LocationPickerTarget,
            _ when overlay.Name == nameof(AiSetupOverlay) => AiSetupPanel,
            _ when overlay.Name == nameof(ScanReportOverlay) => ScanReportPanel,
            _ when overlay.Name == nameof(ScanNavigationNoticeOverlay) => ScanNavigationNoticePanel,
            _ when overlay.Name == nameof(ExplorerGuideOverlay) => GuideInstructionCard,
            _ => null,
        };
    }

    private static void SetPanelMotion(FrameworkElement panel, bool entering, bool animate, bool continueFromCurrent)
    {
        var transform = FindTranslateTransform(panel.RenderTransform);
        var scale = FindScaleTransform(panel.RenderTransform);
        if (transform is null)
        {
            return;
        }

        var isHorizontalEntry = string.Equals(panel.Tag as string, "X", StringComparison.Ordinal);
        var horizontal = isHorizontalEntry;
        var offset = horizontal ? 14d : 9d;
        var property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var duration = entering ? OverlayEnterDuration : OverlayExitDuration;
        var currentX = StopAndReadCurrentValue(transform, TranslateTransform.XProperty);
        var currentY = StopAndReadCurrentValue(transform, TranslateTransform.YProperty);
        var currentMotion = horizontal ? currentX : currentY;
        var currentScaleX = scale is null ? 1 : StopAndReadCurrentValue(scale, ScaleTransform.ScaleXProperty);
        var currentScaleY = scale is null ? 1 : StopAndReadCurrentValue(scale, ScaleTransform.ScaleYProperty);

        if (!animate)
        {
            transform.X = 0;
            transform.Y = 0;
            if (scale is not null)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }

            return;
        }

        if (horizontal)
        {
            transform.Y = 0;
        }
        else
        {
            transform.X = 0;
        }

        var from = entering && !continueFromCurrent ? offset : currentMotion;
        var to = entering ? 0 : (horizontal ? -offset * 0.45 : -offset * 0.45);
        transform.SetValue(property, to);
        transform.BeginAnimation(property, new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        });
        if (scale is not null)
        {
            var scaleFromX = continueFromCurrent || !entering ? currentScaleX : 0.985;
            var scaleFromY = continueFromCurrent || !entering ? currentScaleY : 0.985;
            var scaleTo = entering ? 1 : 0.99;
            scale.ScaleX = scaleTo;
            scale.ScaleY = scaleTo;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scaleFromX, scaleTo, duration)
            {
                EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop,
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scaleFromY, scaleTo, duration)
            {
                EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    private static double StopAndReadCurrentValue(DependencyObject target, DependencyProperty property)
    {
        var currentValue = (double)target.GetValue(property);
        if (target is UIElement uiElement)
        {
            uiElement.BeginAnimation(property, null);
        }
        else if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, null);
        }

        target.SetValue(property, currentValue);
        return currentValue;
    }

    private void ResetPanelMotion(FrameworkElement overlay)
    {
        var panel = GetOverlayPanel(overlay);
        if (panel is null)
        {
            return;
        }

        var transform = FindTranslateTransform(panel.RenderTransform);
        if (transform is not null)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = 0;
            transform.Y = 0;
        }

        var scale = FindScaleTransform(panel.RenderTransform);
        if (scale is not null)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    private static DoubleAnimation CreateOverlayOpacityAnimation(double from, double to, TimeSpan duration)
    {
        return new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
    }

    private void ResetOverlayMotion()
    {
        foreach (var overlay in new UIElement[]
                 {
                     SettingsOverlay,
                     HelpOverlay,
                     ScanLocationPickerOverlay,
                     AiSetupOverlay,
                     ScanReportOverlay,
                     ScanNavigationNoticeOverlay,
                     ExplorerGuideOverlay,
                     SettingsPanel,
                     HelpPanel,
                     LocationPickerTarget,
                     AiSetupPanel,
                     ScanReportPanel,
                     ScanNavigationNoticePanel,
                     GuideInstructionCard,
                 })
        {
            overlay.BeginAnimation(OpacityProperty, null);
            overlay.Opacity = 1;
            if (overlay is FrameworkElement panel)
            {
                var transform = FindTranslateTransform(panel.RenderTransform);
                if (transform is not null)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    transform.X = 0;
                    transform.Y = 0;
                }

                var scale = FindScaleTransform(panel.RenderTransform);
                if (scale is not null)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
            }
        }
    }

    private static TranslateTransform? FindTranslateTransform(Transform transform)
    {
        return transform is TransformGroup group
            ? group.Children.OfType<TranslateTransform>().FirstOrDefault()
            : transform as TranslateTransform;
    }

    private static ScaleTransform? FindScaleTransform(Transform transform)
    {
        return transform is TransformGroup group
            ? group.Children.OfType<ScaleTransform>().FirstOrDefault()
            : transform as ScaleTransform;
    }

    private void ExplorerGuideOverlay_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateExplorerGuideLayout(animateCard: false);
    }

    private void UpdateExplorerGuideLayout(bool animateCard)
    {
        var isLocationStep = DataContext is MainWindowViewModel guideViewModel
                             && guideViewModel.IsExplorerGuideOpen
                             && guideViewModel.ExplorerGuideStep == 1;
        // Keep the instructions outside the disk picker, including its footer actions.
        LocationPickerTarget.Margin = isLocationStep ? new Thickness(24, 16, 24, 180) : new Thickness(24);
        GuideInstructionCard.MaxWidth = isLocationStep ? 660 : 460;
        GuideInstructionCard.Width = isLocationStep
            ? Math.Min(660, Math.Max(280, ExplorerGuideOverlay.ActualWidth - 56))
            : 460;
        GuideInstructionCard.HorizontalAlignment = isLocationStep ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsExplorerGuideOpen)
        {
            GuideFocusRing.BeginAnimation(OpacityProperty, null);
            GuideFocusRing.Opacity = 0.9;
            _lastExplorerGuideStep = -1;
            _lastExplorerGuideMotionEnabled = null;
            return;
        }

        FrameworkElement target = viewModel.ExplorerGuideStep switch
        {
            0 => NewScanButton,
            1 => LocationPickerTarget,
            _ => FocusModeToggleButton,
        };
        if (!target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0
            || GuideMaskCanvas.ActualWidth <= 0 || GuideMaskCanvas.ActualHeight <= 0)
        {
            CoverGuideSurface();
            return;
        }

        Rect bounds;
        try
        {
            var targetInOverlay = target.TransformToVisual(ExplorerGuideOverlay);
            var topLeft = targetInOverlay.Transform(new Point(0, 0));
            var bottomRight = targetInOverlay.Transform(new Point(target.ActualWidth, target.ActualHeight));
            bounds = new Rect(topLeft, bottomRight);
        }
        catch (InvalidOperationException)
        {
            CoverGuideSurface();
            return;
        }

        bounds.Intersect(new Rect(0, 0, GuideMaskCanvas.ActualWidth, GuideMaskCanvas.ActualHeight));
        if (bounds.IsEmpty)
        {
            CoverGuideSurface();
            return;
        }

        SetGuideMask(GuideMaskTop, 0, 0, GuideMaskCanvas.ActualWidth, bounds.Top);
        SetGuideMask(GuideMaskLeft, 0, bounds.Top, bounds.Left, bounds.Height);
        SetGuideMask(GuideMaskRight, bounds.Right, bounds.Top, GuideMaskCanvas.ActualWidth - bounds.Right, bounds.Height);
        SetGuideMask(GuideMaskBottom, 0, bounds.Bottom, GuideMaskCanvas.ActualWidth, GuideMaskCanvas.ActualHeight - bounds.Bottom);

        const double ringPadding = 5;
        GuideFocusRing.Width = bounds.Width + ringPadding * 2;
        GuideFocusRing.Height = bounds.Height + ringPadding * 2;
        Canvas.SetLeft(GuideFocusRing, bounds.Left - ringPadding);
        Canvas.SetTop(GuideFocusRing, bounds.Top - ringPadding);

        var guideTargetChanged = _lastExplorerGuideStep != viewModel.ExplorerGuideStep;
        var motionSettingChanged = _lastExplorerGuideMotionEnabled != viewModel.IsViewportMotionEnabled;
        if (guideTargetChanged || motionSettingChanged)
        {
            GuideFocusRing.BeginAnimation(OpacityProperty, null);
            if (viewModel.IsViewportMotionEnabled)
            {
                GuideFocusRing.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 0.74,
                    To = 0.96,
                    Duration = TimeSpan.FromMilliseconds(1250),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
            }
            else
            {
                GuideFocusRing.Opacity = 0.9;
            }

            _lastExplorerGuideStep = viewModel.ExplorerGuideStep;
            _lastExplorerGuideMotionEnabled = viewModel.IsViewportMotionEnabled;
        }

        GuideInstructionCard.BeginAnimation(OpacityProperty, null);
        GuideInstructionCard.Opacity = viewModel.IsViewportMotionEnabled && animateCard ? 0 : 1;
        if (viewModel.IsViewportMotionEnabled && animateCard)
        {
            GuideInstructionCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        }
    }

    private void CoverGuideSurface()
    {
        var width = GuideMaskCanvas.ActualWidth;
        var height = GuideMaskCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        SetGuideMask(GuideMaskTop, 0, 0, width, height);
        SetGuideMask(GuideMaskLeft, 0, 0, 0, 0);
        SetGuideMask(GuideMaskRight, width, 0, 0, 0);
        SetGuideMask(GuideMaskBottom, 0, height, 0, 0);
        GuideFocusRing.BeginAnimation(OpacityProperty, null);
        GuideFocusRing.Opacity = 0;
        _lastExplorerGuideStep = -1;
        _lastExplorerGuideMotionEnabled = null;
    }

    private FrameworkElement GetExplorerGuideTarget(MainWindowViewModel viewModel)
    {
        return viewModel.ExplorerGuideStep switch
        {
            0 => NewScanButton,
            1 => LocationPickerTarget,
            _ => FocusModeToggleButton,
        };
    }

    private FrameworkElement? GetActiveModalPanel(MainWindowViewModel viewModel) =>
        viewModel.IsScanReportOpen ? ScanReportPanel :
        viewModel.IsScanNavigationNoticeOpen ? ScanNavigationNoticePanel :
        viewModel.IsScanLocationPickerOpen ? LocationPickerTarget :
        viewModel.IsAiSetupOpen ? AiSetupPanel :
        viewModel.IsHelpOpen ? HelpPanel : viewModel.IsSettingsOpen ? SettingsPanel : null;

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = current is Visual
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }

    private static ComboBox? FindOpenComboBox(DependencyObject root)
    {
        if (root is ComboBox { IsDropDownOpen: true } combo)
        {
            return combo;
        }
        if (root is not Visual)
        {
            return null;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindOpenComboBox(VisualTreeHelper.GetChild(root, index)) is { } open)
            {
                return open;
            }
        }
        return null;
    }

    private static void FocusFirstModalControl(FrameworkElement panel)
    {
        var controls = new List<UIElement>();
        CollectFocusableElements(panel, controls);
        controls.FirstOrDefault()?.Focus();
    }

    private void SynchronizeModalFocus(MainWindowViewModel viewModel)
    {
        if (viewModel.IsExplorerGuideOpen)
        {
            return;
        }
        var panel = GetActiveModalPanel(viewModel);
        if (ReferenceEquals(panel, _activeModalPanel))
        {
            return;
        }
        if (panel is not null)
        {
            if (_activeModalPanel is null)
            {
                _focusBeforeModal = Keyboard.FocusedElement;
            }
            _activeModalPanel = panel;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (ReferenceEquals(DataContext, viewModel) && ReferenceEquals(GetActiveModalPanel(viewModel), panel))
                {
                    FocusFirstModalControl(panel);
                }
            }));
        }
        else
        {
            _activeModalPanel = null;
            var previous = _focusBeforeModal;
            _focusBeforeModal = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (ReferenceEquals(DataContext, viewModel) && GetActiveModalPanel(viewModel) is null && !viewModel.IsExplorerGuideOpen)
                {
                    RestoreFocus(previous);
                }
            }));
        }
    }

    private void SynchronizeExplorerGuideFocus(MainWindowViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }

        if (viewModel.IsExplorerGuideOpen)
        {
            if (!_isExplorerGuideFocusSession && !_isRestoringGuideFocus)
            {
                _focusBeforeExplorerGuide = Keyboard.FocusedElement;
                _isExplorerGuideFocusSession = true;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!viewModel.IsExplorerGuideOpen)
                {
                    return;
                }

                if (!IsAllowedExplorerGuideFocus(Keyboard.FocusedElement as DependencyObject, viewModel))
                {
                    SkipGuideButton.Focus();
                }
            }));
            return;
        }

        if (!_isExplorerGuideFocusSession)
        {
            return;
        }

        var previousFocus = _focusBeforeExplorerGuide;
        _focusBeforeExplorerGuide = null;
        _isExplorerGuideFocusSession = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RestoreFocus(previousFocus)));
    }

    private void MoveExplorerGuideFocus(MainWindowViewModel viewModel, bool reverse)
    {
        var candidates = GetExplorerGuideFocusCandidates(viewModel);
        if (candidates.Count == 0)
        {
            SkipGuideButton.Focus();
            return;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        var index = candidates.FindIndex(candidate => ReferenceEquals(candidate, focused));
        if (index < 0)
        {
            candidates[reverse && candidates.Count > 1 ? candidates.Count - 2 : 0].Focus();
            return;
        }

        if (ReferenceEquals(candidates[index], SkipGuideButton))
        {
            (reverse && candidates.Count > 1 ? candidates[^2] : candidates[0]).Focus();
            return;
        }

        if (reverse)
        {
            (index == 0 ? SkipGuideButton : candidates[index - 1]).Focus();
        }
        else
        {
            (index + 1 >= candidates.Count ? SkipGuideButton : candidates[index + 1]).Focus();
        }
    }

    private List<UIElement> GetExplorerGuideFocusCandidates(MainWindowViewModel viewModel)
    {
        var target = GetExplorerGuideTarget(viewModel);
        var candidates = new List<UIElement>();
        if (IsFocusableTarget(target))
        {
            candidates.Add(target);
        }

        if (viewModel.ExplorerGuideStep == 1)
        {
            CollectFocusableElements(LocationPickerTarget, candidates);
            candidates = candidates.Distinct().ToList();
        }

        candidates.Add(GuideDoNotShowAgainCheckBox);
        candidates.Add(SkipGuideButton);
        return candidates;
    }

    private static bool IsFocusableTarget(UIElement element)
    {
        var isTabStop = element is Control control
            ? control.IsTabStop
            : KeyboardNavigation.GetIsTabStop(element);
        return element.IsVisible && element.IsEnabled && element.Focusable && isTabStop;
    }

    private static void CollectFocusableElements(DependencyObject root, ICollection<UIElement> results)
    {
        if (root is not Visual)
        {
            return;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is UIElement element && IsFocusableTarget(element))
            {
                results.Add(element);
            }

            CollectFocusableElements(child, results);
        }
    }

    private bool IsAllowedExplorerGuideFocus(DependencyObject? focused, MainWindowViewModel viewModel)
    {
        return ReferenceEquals(focused, SkipGuideButton)
               || IsDescendantOf(focused, GuideDoNotShowAgainCheckBox)
               || IsDescendantOf(focused, GetExplorerGuideTarget(viewModel));
    }

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject? ancestor)
    {
        for (var current = child; current is not null; current = current is Visual
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void KeepExplorerGuideFocusInside(MainWindowViewModel viewModel)
    {
        if (IsAllowedExplorerGuideFocus(Keyboard.FocusedElement as DependencyObject, viewModel))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (viewModel.IsExplorerGuideOpen
                && !IsAllowedExplorerGuideFocus(Keyboard.FocusedElement as DependencyObject, viewModel))
            {
                SkipGuideButton.Focus();
            }
        }));
    }

    private void RestoreFocus(IInputElement? previousFocus)
    {
        _isRestoringGuideFocus = true;
        try
        {
            if (previousFocus is UIElement previousElement
                && previousElement.IsVisible
                && previousElement.IsEnabled
                && previousElement.IsHitTestVisible
                && previousElement.Focusable
                && previousElement.Focus())
            {
                return;
            }

            if (NewScanButton.IsVisible && NewScanButton.IsEnabled)
            {
                NewScanButton.Focus();
            }
            else if (TreemapView.IsVisible)
            {
                TreemapView.Focus();
            }
        }
        finally
        {
            _isRestoringGuideFocus = false;
        }
    }

    private static void SetGuideMask(Rectangle rectangle, double left, double top, double width, double height)
    {
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
    }

    private void ApplyTheme(string? themeId)
    {
        var normalizedThemeId = ThemeCatalog.Normalize(themeId);
        if (string.Equals(_appliedThemeId, normalizedThemeId, StringComparison.Ordinal))
        {
            return;
        }

        var mergedDictionaries = Resources.MergedDictionaries;
        for (var index = mergedDictionaries.Count - 1; index >= 0; index--)
        {
            var source = mergedDictionaries[index].Source?.OriginalString;
            if (source?.Contains("/Resources/Themes/Theme", StringComparison.OrdinalIgnoreCase) == true)
            {
                mergedDictionaries.RemoveAt(index);
            }
        }

        var dictionaryPath = ThemeCatalog.GetDictionaryPath(normalizedThemeId);
        var sourceUri = new Uri($"pack://application:,,,/DiskLens.App;component/{dictionaryPath}", UriKind.Absolute);
        mergedDictionaries.Add(new ResourceDictionary { Source = sourceUri });
        // A tooltip has a separate popup resource tree. Give it the selected
        // theme explicitly so it never flashes the system's yellow tooltip.
        if (AiUsageInfoButton?.ToolTip is ToolTip usageTip)
        {
            usageTip.Resources.MergedDictionaries.Clear();
            usageTip.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = sourceUri });
        }
        _appliedThemeId = normalizedThemeId;
        if (HomeLanding is not null)
        {
            var reduceMotion = DataContext is MainWindowViewModel viewModel && viewModel.IsReduceMotionEnabled;
            HomeLanding.ApplyTheme(normalizedThemeId, reduceMotion);
        }
    }

    private void ScrollAnalysisResultsToTop()
    {
        if (FindVisualChild<ScrollViewer>(AnalysisResultsListBox) is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToTop();
        }
    }

    private void ScrollSelectedDetailsToTop()
    {
        SelectedDetailsScrollViewer.ScrollToTop();
    }

    private static TChild? FindVisualChild<TChild>(DependencyObject? root)
        where TChild : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TChild typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualChild<TChild>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void ConfigureInitialWindowBounds()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        var safeMinWidth = Math.Min(MinWidth, Math.Max(MinimumUsableWidth, workArea.Width - (WorkAreaMargin * 2)));
        var safeMinHeight = Math.Min(MinHeight, Math.Max(MinimumUsableHeight, workArea.Height - (WorkAreaMargin * 2)));
        var safeMaxWidth = Math.Max(safeMinWidth, workArea.Width - WorkAreaMargin);
        var safeMaxHeight = Math.Max(safeMinHeight, workArea.Height - WorkAreaMargin);

        MinWidth = safeMinWidth;
        MinHeight = safeMinHeight;

        // Limit the initial size only. Windows uses the current work area when maximizing.
        Width = Math.Clamp(Width, safeMinWidth, safeMaxWidth);
        Height = Math.Clamp(Height, safeMinHeight, safeMaxHeight);
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }
}

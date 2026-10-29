using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media.Animation;
using AgentSpeed.Core;
using AgentSpeed.Interop;
using AgentSpeed.Orb;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Perf;

namespace AgentSpeed.Dashboard;

/// <summary>
/// Flyout above the taskbar near the notification area (DESIGN section 4). The window is sized from code, never by
/// SizeToContent: at rest it hugs its content, and while the agents list opens or closes one spring frame sets the
/// viewport height and the window rectangle together (a single SetWindowPos with the taskbar-side edge fixed), before
/// that frame is laid out and drawn.
/// </summary>
internal sealed partial class DashboardWindow : Window
{
    private const double FlyoutWidth = 368;
    private const double ScreenMargin = 12;

    /// <summary>Height differences below this (DIPs) are float noise; layout-rounded heights move by whole pixels.</summary>
    private const double HeightTolerance = 0.01;

    private readonly DashboardViewModel _model;
    private readonly HeightSpring _heights = new();
    private readonly RowFade _fade;
    private nint _hwnd;
    private TaskbarEdge _edge = TaskbarEdge.Bottom;
    private RECT _work;
    private double _scale = 1;
    private bool _placing;
    private RectD _placedRect;
    private double _placedHeight;
    private double _chrome;
    private bool _suppressRange;
    private bool _toggling;
    private bool _collapsing;
    private double _collapseFromHeight;
    private double _collapseFromOffset;

    public DashboardWindow(DashboardViewModel viewModel, Func<double> clock)
    {
        ScoreCards = new PerfCardPresenter(Dispatcher,
            () => new PerfCardAppearance(viewModel.CapsuleAppearance.Dark, viewModel.CapsuleAppearance.HighContrast, viewModel.ReduceMotion,
                viewModel.TransparencyEnabled),
            () => viewModel.LanguageCode);
        viewModel.AgentsUpdated += (_, _) => ScoreCards.Refresh();
        InitializeComponent();
        DataContext = viewModel;
        _model = viewModel;
        _fade = new RowFade(AgentsItems);
        _heights.Stepped += OnHeightsStepped;
        _heights.Settled += OnHeightsSettled;
        AgentsViewport.Height = viewModel.AgentsViewportHeight;
        viewModel.PropertyChanged += OnModelChanged;
        TurnChevron(animate: false);
        UpdateScrollBars();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _heights.Finish();
                _fade.Finish();
            }
        };
        Hero = new OrbView(clock)
        {
            Margin = new Thickness(-28),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        HeroHost.Children.Add(Hero);
        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => Dismiss();
        LayoutUpdated += OnLayoutUpdated;
        DpiChanged += (_, e) =>
        {
            _scale = e.NewDpi.DpiScaleX;
            _placedRect = default;
            Dispatcher.BeginInvoke(() => Place(_placedHeight));
        };
    }

    /// <summary>The agent rows' score cards (DESIGN 12): beside the panel, toward the screen's centre.</summary>
    public PerfCardPresenter ScoreCards { get; }

    public event EventHandler? SettingsRequested;

    public event EventHandler? SourcesRequested;

    /// <summary>A source row (that source) or the Today header's history button (null, all sources) was clicked.</summary>
    public event EventHandler<SourceKind?>? HistoryRequested;

    public event EventHandler? QuitRequested;

    public event EventHandler<bool>? OrbToggled;

    public event EventHandler<int>? RangeChanged;

    /// <summary>The agents list was expanded (true) or collapsed (false) by the user.</summary>
    public event EventHandler<bool>? AgentsExpandedChanged;

    /// <summary>The update pill was clicked.</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>The hero's unit was clicked: switch <c>settings.liveUnit</c> (DESIGN 9).</summary>
    public event EventHandler? UnitToggleRequested;

    /// <summary>A Network row was clicked: Usage History on the traffic metric with that source (DESIGN 10).</summary>
    public event EventHandler<SourceKind>? TrafficHistoryRequested;

    /// <summary>The Network section's helper button: Settings → 網路流量, never the administrator prompt itself.</summary>
    public event EventHandler? TrafficSettingsRequested;

    /// <summary>The last minute of this computer's traffic for the Network sparkline (once per second).</summary>
    public void SetTrafficChart(IReadOnlyList<Core.Traffic.TrafficPoint> history, bool bits, string summary)
    {
        TrafficChart.ReduceMotion = _model.ReduceMotion;
        TrafficChart.SetData(history, bits, summary);
    }

    public OrbView Hero { get; }

    public long LastHiddenTick { get; private set; }

    public long LastShownTick { get; private set; }

    public bool IsShown => IsVisible;

    public void SetRange(int seconds)
    {
        _suppressRange = true;
        try
        {
            (seconds switch { 60 => Range60, 300 => Range300, _ => Range120 }).IsChecked = true;
        }
        finally
        {
            _suppressRange = false;
        }
    }

    public void SetOrbVisible(bool visible) => OrbToggle.IsChecked = visible;

    public void SetPreviewing(bool previewing) =>
        PreviewBadge.Visibility = previewing ? Visibility.Visible : Visibility.Collapsed;

    public LiveChart LiveChart => Chart;

    /// <summary>Shows the footer pill (<c>update.available.short</c>) when <paramref name="text"/> is set.</summary>
    public void SetUpdate(string? text)
    {
        UpdatePill.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePillText.Text = text ?? string.Empty;
        System.Windows.Automation.AutomationProperties.SetName(UpdatePill, text ?? string.Empty);
    }

    /// <summary>The live reporting indicator was clicked: Settings → API.</summary>
    public event EventHandler? ReportSettingsRequested;

    /// <summary>The footer's boards button: the app answers with <see cref="ShowBoardsMenu"/>.</summary>
    public event EventHandler? BoardsMenuRequested;

    /// <summary>Opens the boards' menu above the footer button, inside the flyout (so it stays open).</summary>
    public void ShowBoardsMenu(System.Windows.Controls.ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.PlacementTarget = BoardsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>The one-time live reporting invitation above the hero (DESIGN 11).</summary>
    public ReportInvitationCard ReportInvitation => Invitation;

    /// <summary>
    /// The footer's live reporting glyph (DESIGN 11, Dashboard footer): shown while reporting is on, at 40 % while paused, with a
    /// 5 DIP amber dot and <c>report.indicator.attention</c> when the status needs the user.
    /// </summary>
    public void SetReportIndicator(bool shown, bool paused, bool attention, string label)
    {
        ReportIndicator.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ReportGlyph.Opacity = paused ? 0.4 : 1;
        ReportAttention.Visibility = attention ? Visibility.Visible : Visibility.Collapsed;
        ReportIndicator.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(ReportIndicator, label);
    }

    private void OnReportIndicator(object sender, RoutedEventArgs e) => ReportSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnBoardsMenu(object sender, RoutedEventArgs e) => BoardsMenuRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The one-time <c>update.done</c> line after an update; null hides it.</summary>
    public void SetUpdateDone(string? text)
    {
        UpdateDone.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateDoneText.Text = text ?? string.Empty;
    }

    /// <summary>Shows the flyout on the monitor containing <paramref name="anchor"/> (physical pixels).</summary>
    public void ShowNear(POINT anchor, bool reduceMotion)
    {
        var monitor = Monitors.FromPoint(anchor) ?? Monitors.Primary();
        if (monitor is null)
        {
            return;
        }

        _work = monitor.WorkArea;
        _scale = monitor.Scale;
        _edge = TaskbarEdgeFor(monitor);
        MaxHeight = Math.Max(240, monitor.WorkArea.Height / _scale - 2 * ScreenMargin);

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        _heights.Finish();
        _fade.Finish();
        UpdateScrollBars();
        // The sections get the same constraint from the outer scroller (full width, unbounded height), so this measure
        // stays valid for the next layout pass and works before the window has ever been shown.
        Sections.Measure(new Size(FlyoutWidth, double.PositiveInfinity));
        Place(FlyoutHeights.Fit(Sections.DesiredSize.Height, MaxHeight), raise: true);

        if (!IsVisible)
        {
            Show();
        }

        Place(_placedHeight, raise: true);
        LastShownTick = Environment.TickCount64;
        User32.SetForegroundWindow(_hwnd);
        Activate();
        Focus();
        Animate(reduceMotion);
    }

    public void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        InfoPopup.IsOpen = false;
        ScoreCards.Hide();
        LastHiddenTick = Environment.TickCount64;
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (InfoPopup.IsOpen)
            {
                InfoPopup.IsOpen = false;
            }
            else
            {
                Dismiss();
            }

            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 hides the flyout; it is reused for the app's lifetime.
        if (!_allowClose)
        {
            e.Cancel = true;
            Dismiss();
        }

        base.OnClosing(e);
    }

    private bool _allowClose;

    public void CloseForShutdown()
    {
        _allowClose = true;
        ScoreCards.Dispose();
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.SetCornerPreference(_hwnd, DWMWCP.ROUND);
        WindowEffects.RemoveCaptionButtons(_hwnd);
        ApplyBackground();
    }

    /// <summary>Mica where supported (applied by the Fluent theme); a solid surface elsewhere.</summary>
    public void ApplyBackground()
    {
        var backdrop = WindowEffects.SupportsSystemBackdrop && !SystemParameters.HighContrast;
        if (backdrop)
        {
            Root.ClearValue(Border.BackgroundProperty);
            Root.Background = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            Root.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        }
    }

    /// <summary>
    /// Moves and sizes the window in one call, keeping the edge against the taskbar on the same pixel, so a height
    /// change never shows an intermediate frame (grown away from the taskbar, then moved back).
    /// </summary>
    private void Place(double heightDip, bool raise = false)
    {
        if (_hwnd == 0 || _placing || heightDip <= 0 || _work.IsEmpty)
        {
            return;
        }

        _placedHeight = heightDip;
        var work = new RectD(_work.Left, _work.Top, _work.Width, _work.Height);
        var rect = PlacementMath.Flyout(work, _edge, FlyoutWidth * _scale, FlyoutHeights.ToPixels(heightDip, _scale),
            ScreenMargin * _scale);
        if (rect == _placedRect && !raise)
        {
            return;
        }

        _placing = true;
        try
        {
            _placedRect = rect;
            var flags = raise ? SWP.NOACTIVATE : SWP.NOACTIVATE | SWP.NOZORDER | SWP.NOOWNERZORDER;
            User32.SetWindowPos(_hwnd, HWND.TOPMOST, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, flags);
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>
    /// At rest the window follows its content's natural height (the sections are measured with unbounded height and a
    /// fixed width, so the window size never feeds back into it). While the agents spring runs it owns the window, and
    /// a content change elsewhere only moves its target.
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_placing || _hwnd == 0 || !IsVisible)
        {
            return;
        }

        var natural = Sections.DesiredSize.Height;
        if (natural <= 0)
        {
            return;
        }

        if (_heights.IsRunning)
        {
            var chrome = natural - AgentsViewport.ActualHeight;
            if (Math.Abs(chrome - _chrome) > HeightTolerance)
            {
                _chrome = chrome;
                _heights.Retarget(FlyoutHeights.For(_chrome, _heights.Target.Viewport, MaxHeight), MaxHeight);
            }

            return;
        }

        var fit = FlyoutHeights.Fit(natural, MaxHeight);
        if (Math.Abs(fit - _placedHeight) > HeightTolerance)
        {
            Place(fit);
        }
    }

    private void Animate(bool reduceMotion)
    {
        var duration = TimeSpan.FromMilliseconds(reduceMotion ? 120 : 260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        if (!reduceMotion)
        {
            var from = _edge switch
            {
                TaskbarEdge.Top => -14,
                TaskbarEdge.Left or TaskbarEdge.Right => 0,
                _ => 14,
            };
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
        }
        else
        {
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            Slide.Y = 0;
        }
    }

    private static TaskbarEdge TaskbarEdgeFor(MonitorInfo monitor)
    {
        var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        if (Shell32.SHAppBarMessage(ABM.GETTASKBARPOS, ref data) != 0)
        {
            var taskbarMonitor = Monitors.FromRect(data.rc);
            if (taskbarMonitor is null || taskbarMonitor.Handle == monitor.Handle)
            {
                return data.uEdge switch
                {
                    ABE.LEFT => TaskbarEdge.Left,
                    ABE.TOP => TaskbarEdge.Top,
                    ABE.RIGHT => TaskbarEdge.Right,
                    _ => TaskbarEdge.Bottom,
                };
            }
        }

        return TaskbarEdge.Bottom;
    }

    private void OnRangeChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressRange || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var seconds))
        {
            return;
        }

        RangeChanged?.Invoke(this, seconds);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardViewModel.AgentsViewportHeight) when !_toggling && !_collapsing:
                if (_heights.IsRunning)
                {
                    _heights.Retarget(FlyoutHeights.For(_chrome, _model.AgentsViewportHeight, MaxHeight), MaxHeight);
                }
                else
                {
                    // The window follows in the same frame (OnLayoutUpdated).
                    AgentsViewport.Height = _model.AgentsViewportHeight;
                }

                break;
            case nameof(DashboardViewModel.CollapsedAgentsHeight) when _collapsing:
                _heights.Retarget(FlyoutHeights.For(_chrome, _model.CollapsedAgentsHeight, MaxHeight), MaxHeight);
                break;
            case nameof(DashboardViewModel.AgentsExpanded):
                TurnChevron(animate: IsVisible && !_model.ReduceMotion);
                break;
            case nameof(DashboardViewModel.AgentsScroll):
                UpdateScrollBars();
                break;
            case nameof(DashboardViewModel.HeroIsMoney) when IsVisible:
                SlideHeroValue();
                break;
            case nameof(DashboardViewModel.CurrencyCode) when IsVisible:
                CrossfadeMoney();
                break;
        }
    }

    private void OnAgentsToggle(object sender, RoutedEventArgs e)
    {
        _fade.Finish();
        var transition = AgentsDisclosure.TransitionFor(IsVisible, _model.ReduceMotion);
        var expand = !_model.AgentsExpanded;
        var from = new FlyoutHeights(_heights.IsRunning ? _heights.Current.Viewport : AgentsViewport.Height, _placedHeight);
        var offset = AgentsViewport.VerticalOffset;

        // Both values come from the last layout pass, so their difference is everything but the viewport.
        var chrome = Sections.DesiredSize.Height - AgentsViewport.ActualHeight;
        _toggling = true;
        try
        {
            if (expand)
            {
                _collapsing = false;
                _model.AgentsExpanded = true;
                _model.HoldAllAgents = false;
            }
            else
            {
                // Rows stay until the list has closed over them (or faded out), so it never leaves a gap.
                _model.HoldAllAgents = transition != DisclosureTransition.Instant;
                _model.AgentsExpanded = false;
            }
        }
        finally
        {
            _toggling = false;
        }

        switch (transition)
        {
            case DisclosureTransition.Spring:
                _chrome = chrome;
                _collapsing = !expand;
                _collapseFromHeight = from.Viewport;
                _collapseFromOffset = offset;
                var target = expand ? _model.AgentsViewportHeight : _model.CollapsedAgentsHeight;
                _heights.AnimateTo(from, FlyoutHeights.For(chrome, target, MaxHeight), MaxHeight);
                break;
            case DisclosureTransition.Crossfade when expand:
                AgentsViewport.Height = _model.AgentsViewportHeight;
                var (first, count) = AgentsDisclosure.ChangingRows(_model.CollapsedAgentCount, _model.Agents.Count);
                _fade.FadeIn(first, count);
                break;
            case DisclosureTransition.Crossfade:
                var (leaving, leavingCount) = AgentsDisclosure.ChangingRows(_model.CollapsedAgentCount, _model.Agents.Count);
                _fade.FadeOut(leaving, leavingCount, () => CompleteFadedCollapse(scrolled: offset > 0.5));
                break;
            default:
                AgentsViewport.ScrollToVerticalOffset(0);
                AgentsViewport.Height = _model.AgentsViewportHeight;
                break;
        }

        UpdateScrollBars();
        AgentsExpandedChanged?.Invoke(this, expand);
    }

    /// <summary>Reduce Motion: the leaving rows have faded, so the list closes at once.</summary>
    private void CompleteFadedCollapse(bool scrolled)
    {
        AgentsViewport.ScrollToVerticalOffset(0);
        _model.HoldAllAgents = false;
        if (scrolled && !_model.AgentsExpanded)
        {
            // The first rows were scrolled away; they fade in where the faded rows were.
            _fade.FadeIn(0, _model.CollapsedAgentCount);
        }

        UpdateScrollBars();
    }

    private void OnHeightsStepped(object? sender, FlyoutHeights frame)
    {
        // Viewport first, then the window: the resize lays out the new viewport height in the same pass.
        var heights = frame.SnapToPixels(_scale);
        AgentsViewport.Height = heights.Viewport;
        if (_collapsing)
        {
            AgentsViewport.ScrollToVerticalOffset(AgentsDisclosure.CollapseOffset(
                _collapseFromOffset, _collapseFromHeight, _heights.Target.Viewport, heights.Viewport));
        }

        Place(heights.Window);
    }

    private void OnHeightsSettled(object? sender, EventArgs e)
    {
        if (_collapsing)
        {
            _collapsing = false;
            AgentsViewport.ScrollToVerticalOffset(0);
            _model.HoldAllAgents = false;
        }

        UpdateScrollBars();
    }

    /// <summary>
    /// Scroll bars show only at rest, and neither takes width from the content (see the scroll viewer styles), so
    /// switching them never re-lays out the rows.
    /// </summary>
    private void UpdateScrollBars()
    {
        var resting = !_heights.IsRunning;
        AgentsViewport.VerticalScrollBarVisibility = _model.AgentsScroll && resting ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        Scroller.VerticalScrollBarVisibility = resting ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    private void TurnChevron(bool animate)
    {
        var angle = _model.AgentsExpanded ? 180 : 0;
        if (animate)
        {
            var turn = new DoubleAnimation(angle, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            AgentsChevron.BeginAnimation(RotateTransform.AngleProperty, turn);
        }
        else
        {
            AgentsChevron.BeginAnimation(RotateTransform.AngleProperty, null);
            AgentsChevron.Angle = angle;
        }
    }

    /// <summary>Hands the wheel to the flyout when the agents list cannot scroll that way.</summary>
    private void OnAgentsWheel(object sender, MouseWheelEventArgs e)
    {
        var offset = AgentsViewport.VerticalOffset;
        var canScroll = _model.AgentsScroll && !_heights.IsRunning
            && (e.Delta > 0 ? offset > 0.5 : offset < AgentsViewport.ScrollableHeight - 0.5);
        if (canScroll)
        {
            return;
        }

        e.Handled = true;
        Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }

    private void OnOrbToggled(object sender, RoutedEventArgs e) => OrbToggled?.Invoke(this, OrbToggle.IsChecked == true);

    private void OnInfo(object sender, RoutedEventArgs e) => InfoPopup.IsOpen = !InfoPopup.IsOpen;

    private void OnCostInfo(object sender, RoutedEventArgs e) => CostPopup.IsOpen = !CostPopup.IsOpen;

    /// <summary>Speaks <paramref name="text"/> once through UI Automation (a polite notification from the hero's unit).</summary>
    public void Announce(string text)
    {
        var peer = UIElementAutomationPeer.FromElement(HeroUnit) ?? UIElementAutomationPeer.CreatePeerForElement(HeroUnit);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, text, "unitChanged");
    }

    /// <summary>A single click on the hero's unit switches tok/s and money at once (the hero has no double-click action).</summary>
    private void OnHeroUnit(object sender, RoutedEventArgs e) => UnitToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>A new display currency: the money values crossfade (0.2 s, DESIGN 9 Currency).</summary>
    private void CrossfadeMoney()
    {
        var fade = new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.2)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        TodayCostValue.BeginAnimation(OpacityProperty, fade);
        if (_model.HeroIsMoney)
        {
            HeroValue.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>The value and unit arrive from below like a title change (0.3 s); Reduce Motion: a 0.15 s fade (DESIGN 9).</summary>
    private void SlideHeroValue()
    {
        var reduce = _model.ReduceMotion;
        var duration = TimeSpan.FromSeconds(reduce ? 0.15 : 0.3);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HeroValue.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        HeroValueSlide.BeginAnimation(TranslateTransform.YProperty,
            reduce ? null : new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Dismiss();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenSources(object sender, RoutedEventArgs e)
    {
        Dismiss();
        SourcesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceRow row })
        {
            RequestHistory(row.Kind);
        }
    }

    private void OnHistory(object sender, RoutedEventArgs e) => RequestHistory(null);

    private void OnTrafficRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrafficRowItem row })
        {
            Dismiss();
            TrafficHistoryRequested?.Invoke(this, row.Source);
        }
    }

    private void OnTrafficExpand(object sender, RoutedEventArgs e) => _model.ToggleTrafficRows();

    private void OnTrafficHelper(object sender, RoutedEventArgs e)
    {
        Dismiss();
        TrafficSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestHistory(SourceKind? source)
    {
        Dismiss();
        HistoryRequested?.Invoke(this, source);
    }

    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Dismiss();
        UpdateRequested?.Invoke(this, EventArgs.Empty);
    }
}

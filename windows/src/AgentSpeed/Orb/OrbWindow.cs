using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentSpeed.Interop;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Orb;

/// <summary>Window options of one orb (DESIGN 2b, per orb).</summary>
internal readonly record struct OrbWindowOptions(bool AlwaysOnTop, bool ClickThrough, bool FadeWhenIdle, bool Dimmed, bool ReduceMotion);

/// <summary>
/// One floating orb window drawn by WPF, the fallback when Direct3D 11 is unavailable (<see cref="Gpu.GpuOrbWindow"/> is the
/// default): borderless, per-pixel transparent, never activated. Outside the disc it lets clicks through (WS_EX_TRANSPARENT
/// toggled by cursor position). Dragging asks the owner for the assisted target (edges, other orbs, alignment) and follows it
/// through a critically damped spring; positions are physical pixels of the disc center.
/// </summary>
internal sealed class OrbWindow : Window, IOrbWindow
{
    private const double DragSlop = 3;
    private const double DragSettle = 0.12;

    private readonly Func<double> _clock;
    private readonly DispatcherTimer _passThroughPoll;
    private readonly DispatcherTimer _housekeeping;
    private readonly ScaleTransform _appearScale = new(1, 1);
    private readonly System.Windows.Controls.StackPanel _actions = new()
    {
        Width = 1,
        Height = 1,
        Opacity = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private nint _hwnd;
    private OrbWindowOptions _options;
    private bool _passThrough;
    private bool _dragging;
    private bool _dragMoved;
    private POINT _dragStartCursor;
    private PointD _dragStartCenter;
    private PointD _dragTarget;
    private PointD _center;
    private bool _hasCenter;
    private CriticalSpring _followX;
    private CriticalSpring _followY;
    private DampedSpring _moveX;
    private DampedSpring _moveY;
    private PointD _moveTarget;
    private Action? _moveDone;
    private bool _moving;
    private bool _following;
    private MotionFrames _frames;
    private readonly IdleFade _idleFade = new();
    private bool _hovering;
    private bool _discHover;
    private bool _plateHover;
    private DispatcherTimer? _hoverGrace;
    private readonly List<IOrbAttachment> _attachments = [];
    private readonly List<(nint Handle, PixelRect Rect)> _batch = [];

    public OrbWindow(string orbId, Func<double> clock, OrbClock orbClock, uint seed)
    {
        OrbId = orbId;
        _clock = clock;
        Surface = new OrbSurface(orbClock, seed);
        var root = new System.Windows.Controls.Grid();
        root.Children.Add(Surface);
        root.Children.Add(_actions);
        Content = root;
        Title = "Agent Speed Orb";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Topmost = true;
        Focusable = false;
        Style = new Style(typeof(Window));
        UseLayoutRounding = false;
        Cursor = Cursors.SizeAll;
        Left = -32000;
        Top = -32000;
        Surface.RenderTransform = _appearScale;
        ApplySize(Surface.Diameter);

        _passThroughPoll = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => PollPassThrough(), Dispatcher);
        _passThroughPoll.Stop();
        _housekeeping = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Housekeeping(), Dispatcher);

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceAt(_center));
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _housekeeping.Start();
            }
            else
            {
                _housekeeping.Stop();
                _passThroughPoll.Stop();
            }
        };
        Closed += (_, _) =>
        {
            _housekeeping.Stop();
            _passThroughPoll.Stop();
            _hoverGrace?.Stop();
            StopFrames();
        };
    }

    public string OrbId { get; }

    public OrbSurface Surface { get; }

    IOrbSurface IOrbWindow.Surface => Surface;

    /// <summary>Name plates bind to the window's Opacity property directly.</summary>
    event EventHandler? IOrbWindow.OpacityChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public void SetLanguage(XmlLanguage language) => Language = language;

    /// <summary>The WPF fallback has no click target on the unit (the menu, the taskbar and Settings switch it).</summary>
    public void SetUnitHint(string hint)
    {
    }

    /// <summary>The WPF fallback draws no two-line readout, so it has nothing to explain.</summary>
    public void SetDiscHint(string? hint)
    {
    }

    event EventHandler? IOrbWindow.UnitToggleRequested
    {
        add
        {
        }

        remove
        {
        }
    }

    public void Announce(string text) =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(Surface)?.RaiseNotificationEvent(
            System.Windows.Automation.AutomationNotificationKind.ActionCompleted,
            System.Windows.Automation.AutomationNotificationProcessing.MostRecent, text, "unitChanged");

    public void OwnPlate(Window plate) => plate.Owner = this;

    public BitmapSource? Snapshot()
    {
        if (_hwnd == 0 || !User32.GetWindowRect(_hwnd, out var rect) || Surface.ActualWidth <= 0)
        {
            return null;
        }

        var scale = rect.Width / Surface.ActualWidth;
        var bitmap = new RenderTargetBitmap(rect.Width, rect.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(Surface);
        bitmap.Freeze();
        return bitmap;
    }

    public nint Handle => _hwnd;

    /// <summary>Disc center in physical pixels.</summary>
    public PointD Center => _center;

    public bool HasCenter => _hasCenter;

    public bool IsDragging => _dragging;

    public bool IsHovered => _hovering;

    /// <summary>The disc center as drawn: the window's whole-pixel origin plus half its size (physical px).</summary>
    public PointD DrawnCenter { get; private set; }

    /// <summary>The display scale (dpi / 96) at the disc center.</summary>
    public double DisplayScale { get; private set; } = 1;

    /// <summary>The orb's window options (the name plate follows always-on-top and click-through).</summary>
    public OrbWindowOptions Options => _options;

    /// <summary>Dims the orb to 55 % when every source is unavailable (DESIGN 2b).</summary>
    public void SetDimmed(bool dimmed)
    {
        if (_options.Dimmed != dimmed)
        {
            _options = _options with { Dimmed = dimmed };
            ApplyOpacity();
        }
    }

    /// <summary>A window that moves with the orb in the same batch (its name plate, its undo chip).</summary>
    public void Attach(IOrbAttachment attachment)
    {
        if (!_attachments.Contains(attachment))
        {
            _attachments.Add(attachment);
        }
    }

    public void Detach(IOrbAttachment attachment) => _attachments.Remove(attachment);

    /// <summary>The pointer is over the orb's name plate: the plate belongs to the orb, so the orb is hovered.</summary>
    public void SetPlateHover(bool hovering)
    {
        if (_plateHover != hovering)
        {
            _plateHover = hovering;
            ApplyHover();
        }

        if (hovering)
        {
            Restore();
        }
    }

    /// <summary>Left button down on the name plate: a double-click opens the dashboard, else a drag of the orb begins.</summary>
    public void PlatePress(int clickCount)
    {
        Interacted?.Invoke(this, EventArgs.Empty);
        if (clickCount >= 2)
        {
            _dragging = false;
            DashboardRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        BeginDrag();
    }

    /// <summary>The pointer moved while the plate holds the capture.</summary>
    public void PlateMove()
    {
        if (_dragging)
        {
            ContinueDrag();
        }
    }

    /// <summary>The plate's button came up (or it lost the capture).</summary>
    public void PlateRelease()
    {
        if (_dragging)
        {
            EndDrag();
        }
    }

    /// <summary>Right-click on the name plate opens the orb's menu.</summary>
    public void PlateMenu()
    {
        Interacted?.Invoke(this, EventArgs.Empty);
        MenuRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? DashboardRequested;

    /// <summary>Right-click on the disc; the menu opens at the cursor.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Pointer down on the disc (z-order: the last clicked orb is in front).</summary>
    public event EventHandler? Interacted;

    /// <summary>The disc is hovered (true) or left (false).</summary>
    public event EventHandler<bool>? HoverChanged;

    /// <summary>Asks the owner for the assisted drag target of a raw center (physical px).</summary>
    public Func<PointD, PointD>? DragAssist { get; set; }

    /// <summary>Raised when a drag ends with the raw center; the owner springs the orb to its release target.</summary>
    public event EventHandler<PointD>? DragReleased;

    /// <summary>Raised on every drag movement (the owner refreshes guides and the chip).</summary>
    public event EventHandler? DragMoved;

    public void Configure(OrbSurfaceConfig config, OrbWindowOptions options)
    {
        var sizeChanged = Math.Abs(config.Size - Surface.Diameter) > 0.01;
        Surface.Configure(config);
        if (sizeChanged)
        {
            ApplySize(config.Size);
            if (_hasCenter)
            {
                PlaceAt(_center);
            }
        }

        if (Topmost != options.AlwaysOnTop)
        {
            Topmost = options.AlwaysOnTop;
        }

        var clickThroughChanged = options.ClickThrough != _options.ClickThrough || _hwnd == 0;
        _options = options;
        if (!options.FadeWhenIdle)
        {
            _idleFade.Restore();
        }

        ApplyOpacity();
        if (clickThroughChanged)
        {
            SetClickThrough(options.ClickThrough, force: true);
        }
    }

    /// <summary>
    /// The orb's accessibility actions (DESIGN 2b, Accessibility): invisible, non-focusable buttons that UI Automation
    /// clients can invoke (open the dashboard, show the menu, move to the next corner, orb settings).
    /// </summary>
    public void SetActions(IReadOnlyList<(string Name, Action Invoke)> actions)
    {
        _actions.Children.Clear();
        foreach (var (name, invoke) in actions)
        {
            var button = new System.Windows.Controls.Button { Width = 1, Height = 1, Focusable = false, IsTabStop = false };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => invoke();
            _actions.Children.Add(button);
        }
    }

    /// <summary>Moves the disc center immediately (physical pixels).</summary>
    public void MoveTo(PointD center)
    {
        StopMove();
        PlaceAt(center);
    }

    /// <summary>Springs the disc center to <paramref name="center"/> (layout springs; Reduce Motion: immediately).</summary>
    public void AnimateTo(PointD center, double response = 0.35, double damping = 0.8, Action? done = null)
    {
        if (_options.ReduceMotion || !_hasCenter || !IsVisible || (Math.Abs(center.X - _center.X) < 0.5 && Math.Abs(center.Y - _center.Y) < 0.5))
        {
            StopMove();
            PlaceAt(center);
            done?.Invoke();
            return;
        }

        _moveX = new DampedSpring(response, damping, _center.X);
        _moveY = new DampedSpring(response, damping, _center.Y);
        _moveTarget = center;
        _moveDone = done;
        _moving = true;
        StartFrames();
    }

    /// <summary>Springs in from 0.6 × scale and 0 opacity (a new orb), or fades in with Reduce Motion.</summary>
    public void Appear()
    {
        var duration = TimeSpan.FromSeconds(_options.ReduceMotion ? 0.2 : 0.45);
        Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, _options.ReduceMotion ? duration : TimeSpan.FromSeconds(0.2)));
        if (_options.ReduceMotion)
        {
            return;
        }

        var center = Surface.Diameter * 0.75;
        _appearScale.CenterX = center;
        _appearScale.CenterY = center;
        var spring = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut };
        var animation = new DoubleAnimation(0.6, 1, duration) { EasingFunction = spring };
        _appearScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _appearScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>Shrinks to 0.85 and fades out (split original, merge source, removal), then runs <paramref name="done"/>.</summary>
    public void Vanish(Action done, double seconds = 0.2)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        var fade = new DoubleAnimation(Surface.Opacity, 0, duration);
        fade.Completed += (_, _) => done();
        Surface.BeginAnimation(OpacityProperty, fade);
        if (!_options.ReduceMotion)
        {
            var center = Surface.Diameter * 0.75;
            _appearScale.CenterX = center;
            _appearScale.CenterY = center;
            var shrink = new DoubleAnimation(1, 0.85, duration);
            _appearScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _appearScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
    }

    /// <summary>The orb's own idle state drives "fade when idle" (its sources idle for 60 s).</summary>
    public void NoteActivity(bool active)
    {
        if (_idleFade.NoteActivity(active, _clock()))
        {
            ApplyOpacity();
        }
    }

    /// <summary>Brings this orb in front of the others of its level without activating it.</summary>
    public void BringToFront()
    {
        if (_hwnd != 0)
        {
            User32.SetWindowPos(_hwnd, Topmost ? HWND.TOPMOST : HWND.TOP, 0, 0, 0, 0, SWP.NOMOVE | SWP.NOSIZE | SWP.NOACTIVATE);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHover(e.GetPosition(Surface));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging)
        {
            SetDiscHover(false);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            ContinueDrag();
            return;
        }

        UpdateHover(e.GetPosition(Surface));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsInsideDisc(e.GetPosition(Surface)))
        {
            return;
        }

        Interacted?.Invoke(this, EventArgs.Empty);
        if (e.ClickCount >= 2)
        {
            _dragging = false;
            DashboardRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (BeginDrag())
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        EndDrag();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            EndDrag();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (IsInsideDisc(e.GetPosition(Surface)))
        {
            Interacted?.Invoke(this, EventArgs.Empty);
            MenuRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Surface.Paused = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.AddExtendedStyle(_hwnd, WS_EX.TOOLWINDOW | WS_EX.NOACTIVATE);
        SetClickThrough(_options.ClickThrough, force: true);
        if (_hasCenter)
        {
            PlaceAt(_center);
        }
    }

    private void ApplySize(double diameter)
    {
        Width = diameter * 1.5;
        Height = diameter * 1.5;
    }

    private void PlaceAt(PointD center)
    {
        _center = center;
        _hasCenter = true;
        if (_hwnd == 0)
        {
            return;
        }

        var monitor = Monitors.FromPoint(new POINT((int)Math.Round(center.X), (int)Math.Round(center.Y)));
        var scale = monitor?.Scale ?? VisualTreeHelper.GetDpi(this).DpiScaleX;
        var size = (int)Math.Round(Surface.Diameter * 1.5 * scale);
        var left = (int)Math.Round(center.X - size / 2.0);
        var top = (int)Math.Round(center.Y - size / 2.0);
        DrawnCenter = new PointD(left + size / 2.0, top + size / 2.0);
        DisplayScale = scale;
        _batch.Clear();
        _batch.Add((_hwnd, new PixelRect(left, top, size, size)));
        CollectAttachments();
        WindowBatch.Move(_batch);
    }

    private void CollectAttachments()
    {
        foreach (var attachment in _attachments)
        {
            if (attachment.Handle != 0 && attachment.Arrange(DrawnCenter, DisplayScale) is { } rect)
            {
                _batch.Add((attachment.Handle, rect));
            }
        }
    }

    private bool BeginDrag()
    {
        if (!User32.GetCursorPos(out var cursor))
        {
            return false;
        }

        StopMove();
        _dragging = true;
        _dragMoved = false;
        _dragStartCursor = cursor;
        _dragStartCenter = _center;
        _dragTarget = _center;
        return true;
    }

    private void ContinueDrag()
    {
        if (!User32.GetCursorPos(out var cursor))
        {
            return;
        }

        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        if (!_dragMoved && Math.Abs(dx) < DragSlop && Math.Abs(dy) < DragSlop)
        {
            return;
        }

        if (!_dragMoved)
        {
            _dragMoved = true;
            _followX = new CriticalSpring(DragSettle, _center.X);
            _followY = new CriticalSpring(DragSettle, _center.Y);
        }

        var raw = new PointD(_dragStartCenter.X + dx, _dragStartCenter.Y + dy);
        _dragTarget = DragAssist?.Invoke(raw) ?? raw;
        if (_options.ReduceMotion)
        {
            PlaceAt(_dragTarget);
        }
        else
        {
            _following = true;
            StartFrames();
        }

        DragMoved?.Invoke(this, EventArgs.Empty);
    }

    private void EndDrag()
    {
        _dragging = false;
        _following = false;
        if (!_dragMoved)
        {
            StopFrames();
            return;
        }

        PlaceAt(_dragTarget);
        DragReleased?.Invoke(this, _dragTarget);
    }

    private void StartFrames()
    {
        if (_frames.Start(_clock()))
        {
            CompositionTarget.Rendering -= OnMotionFrame;
            CompositionTarget.Rendering += OnMotionFrame;
        }
    }

    private void StopFrames()
    {
        _frames.Stop();
        CompositionTarget.Rendering -= OnMotionFrame;
    }

    private void OnMotionFrame(object? sender, EventArgs e)
    {
        var dt = _frames.Step(_clock());
        if (_following && _dragging)
        {
            _followX.Step(_dragTarget.X, dt);
            _followY.Step(_dragTarget.Y, dt);
            if (_followX.IsSettled(_dragTarget.X, 0.3) && _followY.IsSettled(_dragTarget.Y, 0.3))
            {
                // At rest under a still pointer: no frames until the pointer moves again (ContinueDrag restarts them).
                _followX.Snap(_dragTarget.X);
                _followY.Snap(_dragTarget.Y);
                PlaceAt(_dragTarget);
                StopFrames();
                return;
            }

            PlaceAt(new PointD(_followX.Value, _followY.Value));
            return;
        }

        if (_moving)
        {
            _moveX.Step(_moveTarget.X, dt);
            _moveY.Step(_moveTarget.Y, dt);
            if (_moveX.IsSettled(_moveTarget.X, 0.3) && _moveY.IsSettled(_moveTarget.Y, 0.3))
            {
                var done = _moveDone;
                StopMove();
                PlaceAt(_moveTarget);
                done?.Invoke();
                return;
            }

            PlaceAt(new PointD(_moveX.Value, _moveY.Value));
            return;
        }

        StopFrames();
    }

    private void StopMove()
    {
        if (_moving)
        {
            _moving = false;
            _moveDone = null;
        }

        if (!_dragging)
        {
            StopFrames();
        }
    }

    private bool IsInsideDisc(Point position)
    {
        var c = Surface.Diameter * 0.75;
        var hover = 1 + 0.03 * Math.Max(0, Surface.Scene.Hover);
        return Presentation.Orb.OrbHitShape.Contains(Surface.InstrumentStyle, (position.X - c) / hover, (position.Y - c) / hover, Surface.Diameter, 1 / hover);
    }

    private void UpdateHover(Point position)
    {
        var inside = IsInsideDisc(position);
        SetDiscHover(inside);
        if (inside)
        {
            Restore();
        }
        else if (!_options.ClickThrough)
        {
            SetPassThrough(true);
        }
    }

    private void SetDiscHover(bool hovering)
    {
        if (_discHover != hovering)
        {
            _discHover = hovering;
            ApplyHover();
        }
    }

    /// <summary>
    /// The orb is hovered while the pointer is over its disc or its name plate. Leaving one for the other crosses the
    /// small gap between them, so with a plate attached the hover ends only after a short grace.
    /// </summary>
    private void ApplyHover()
    {
        var hovering = _discHover || _plateHover;
        if (hovering || _attachments.Count == 0 || _dragging)
        {
            _hoverGrace?.Stop();
            SetHover(hovering);
            return;
        }

        if (_hovering && _hoverGrace is not { IsEnabled: true })
        {
            _hoverGrace ??= new DispatcherTimer(TimeSpan.FromSeconds(0.12), DispatcherPriority.Input, (_, _) =>
            {
                _hoverGrace!.Stop();
                SetHover(_discHover || _plateHover);
            }, Dispatcher);
            _hoverGrace.Start();
        }
    }

    private void SetHover(bool hovering)
    {
        if (_hovering == hovering)
        {
            return;
        }

        _hovering = hovering;
        Surface.SetHover(hovering);
        HoverChanged?.Invoke(this, hovering);
    }

    private void PollPassThrough()
    {
        if (!_passThrough || _hwnd == 0 || !User32.GetCursorPos(out var cursor) || !User32.GetWindowRect(_hwnd, out var rect))
        {
            _passThroughPoll.Stop();
            return;
        }

        var insideWindow = cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        if (!insideWindow)
        {
            SetPassThrough(false);
            return;
        }

        var scale = rect.Width / Math.Max(1, ActualWidth);
        var local = new Point((cursor.X - rect.Left) / scale, (cursor.Y - rect.Top) / scale);
        if (IsInsideDisc(local))
        {
            SetPassThrough(false);
            SetDiscHover(true);
            Restore();
        }
    }

    private void SetPassThrough(bool enabled)
    {
        if (_options.ClickThrough || _passThrough == enabled)
        {
            return;
        }

        _passThrough = enabled;
        ApplyTransparentStyle(enabled);
        if (enabled)
        {
            SetDiscHover(false);
            _passThroughPoll.Start();
        }
        else
        {
            _passThroughPoll.Stop();
        }
    }

    private void SetClickThrough(bool enabled, bool force = false)
    {
        if (!force && _options.ClickThrough == enabled)
        {
            return;
        }

        _passThrough = false;
        _passThroughPoll.Stop();
        ApplyTransparentStyle(enabled);
        if (enabled)
        {
            _discHover = false;
            _plateHover = false;
            _hoverGrace?.Stop();
            SetHover(false);
        }
    }

    private void ApplyTransparentStyle(bool transparent)
    {
        if (_hwnd == 0)
        {
            return;
        }

        if (transparent)
        {
            WindowEffects.AddExtendedStyle(_hwnd, WS_EX.TRANSPARENT | WS_EX.LAYERED, frameChanged: false);
        }
        else
        {
            WindowEffects.AddExtendedStyle(_hwnd, 0, WS_EX.TRANSPARENT, frameChanged: false);
        }
    }

    private void Housekeeping()
    {
        Surface.Paused = WindowEffects.IsCloaked(_hwnd);
        if (_idleFade.TryFade(_options.FadeWhenIdle, engaged: Surface.Scene.HoverBlend >= 0.01, _options.ReduceMotion, _clock()))
        {
            ApplyOpacity(animated: true);
        }
    }

    private void Restore()
    {
        if (_idleFade.Restore())
        {
            ApplyOpacity();
        }
    }

    private void ApplyOpacity(bool animated = false)
    {
        var target = (_options.Dimmed ? 0.55 : 1) * (_idleFade.Faded ? IdleFade.Factor : 1);
        if (animated && !_options.ReduceMotion)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(IdleFade.FadeSeconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
        }
    }
}

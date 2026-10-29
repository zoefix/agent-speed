using System.Windows.Automation.Provider;
using System.Windows.Threading;
using AgentSpeed.Boards.Gpu;
using AgentSpeed.Boards.Layers;
using AgentSpeed.Interop;
using AgentSpeed.Orb;
using AgentSpeed.Orb.Gpu;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Live;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Boards;

/// <summary>The window side of a board's settings (DESIGN 2c, Interaction and window).</summary>
internal readonly record struct BoardWindowOptions(bool AlwaysOnTop = true, bool ClickThrough = false, double Opacity = 1, bool ReduceMotion = false);

/// <summary>
/// One floating board (DESIGN 2c, Interaction and window): a composition window like the GPU orbs' (no redirection bitmap,
/// never activated, a tool window), the board's square and a 40 pt margin for its bloom. Clicks pass through the margin and,
/// with click-through on, everywhere. Drag anywhere on the square; a release within 24 pt of a work-area edge snaps to the
/// 12 pt margin. Positions are physical pixels of the square's center.
/// </summary>
internal sealed class GpuBoardWindow : IOrbHostInput, IDisposable
{
    private const double DragSlop = 3;
    private const double DragSettle = 0.12;

    private readonly Func<double> _clock;
    private readonly OrbClock _orbClock;
    private readonly OrbHostWindow _host;
    private readonly BoardSurface _surface;
    private readonly DispatcherTimer _passThroughPoll;
    private readonly DispatcherTimer _housekeeping;
    private readonly MotionClient _mover;
    private readonly BoardAutomation _automation;
    private BoardWindowOptions _options;
    private bool _passThrough;
    private bool _dragging;
    private bool _dragMoved;
    private POINT _dragStartCursor;
    private (int X, int Y) _press;
    private PointD _dragStartCenter;
    private PointD _dragTarget;
    private PointD _center;
    private bool _hasCenter;
    private CriticalSpring _followX;
    private CriticalSpring _followY;
    private DampedSpring _moveX;
    private DampedSpring _moveY;
    private PointD _moveTarget;
    private bool _moving;
    private double _lastFrame;
    private string? _hovered;
    private bool _closed;

    public GpuBoardWindow(string boardId, GpuDevice device, OrbClock orbClock, BoardStyle style, BoardSize size, IBoardLayerPainter painter, uint seed = 1)
    {
        BoardId = boardId;
        _orbClock = orbClock;
        _clock = () => orbClock.Now;
        _host = new OrbHostWindow("Agent Speed Board", this, topmost: true);
        _surface = new BoardSurface(device, orbClock, style, size, painter, seed) { Hwnd = _host.Handle };
        _mover = new MotionClient(this);
        _automation = new BoardAutomation(() => _host.Handle, RowBounds);
        _passThroughPoll = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => PollPassThrough(),
            Dispatcher.CurrentDispatcher);
        _passThroughPoll.Stop();
        _housekeeping = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Housekeeping(), Dispatcher.CurrentDispatcher);
        _housekeeping.Stop();
    }

    public string BoardId { get; }

    public BoardSurface Surface => _surface;

    public nint Handle => _host.Handle;

    public PointD Center => _center;

    public double DisplayScale { get; private set; } = 1;

    public bool IsDragging => _dragging;

    public bool IsVisible => !_closed && _host.IsVisible;

    /// <summary>The drag ended (after snapping): the owner saves the position for the arrangement.</summary>
    public event EventHandler<PointD>? DragReleased;

    public event EventHandler? MenuRequested;

    public event EventHandler? DashboardRequested;

    /// <summary>
    /// What the cursor is over changed (the tooltip or score card, the expansion's interaction): a group or sub row's key, the
    /// hero's 性能 (<see cref="BoardScoreCards.HeroKey"/>), or null.
    /// </summary>
    public event EventHandler<string?>? HoveredRowChanged;

    /// <summary>A click without a drag on a group row (its key): the group's sub-agents expand or collapse.</summary>
    public event EventHandler<string>? RowClicked;

    /// <summary>The board's list element (<c>a11y.board</c>), its row items and its actions.</summary>
    public BoardAutomation Automation => _automation;

    public IRawElementProviderSimple? AutomationProvider => _automation;

    /// <summary>The board's rounded square in screen pixels (the window less its bloom margin), or null.</summary>
    public RECT? SquareRect()
    {
        if (_host.Handle == 0 || !User32.GetWindowRect(_host.Handle, out var window))
        {
            return null;
        }

        var margin = (int)Math.Round(BoardCanvas.Margin * DisplayScale);
        return new RECT(window.Left + margin, window.Top + margin, window.Right - margin, window.Bottom - margin);
    }

    /// <summary>The hero's 性能 as last drawn (glass: the dial; neon: digits and meter; timing: its row) in screen pixels, or null.</summary>
    public RECT? ScoreRect()
    {
        if (_surface.Scene.Gauge.ScoreArea is not { } area || _host.Handle == 0 || !User32.GetWindowRect(_host.Handle, out var window))
        {
            return null;
        }

        return new RECT((int)Math.Round(window.Left + area.Left * DisplayScale), (int)Math.Round(window.Top + area.Top * DisplayScale),
            (int)Math.Round(window.Left + area.Right * DisplayScale), (int)Math.Round(window.Top + area.Bottom * DisplayScale));
    }

    /// <summary>
    /// Hit-tests the cursor again where it rests, so a hover follows the row that the ranking moved under it (called on each
    /// tick while a tooltip or card is up or due).
    /// </summary>
    public void RefreshHover()
    {
        if (_dragging || _closed || _options.ClickThrough || _host.Handle == 0 || !User32.GetCursorPos(out var cursor) ||
            !User32.GetWindowRect(_host.Handle, out var rect))
        {
            return;
        }

        var (x, y) = (cursor.X - rect.Left, cursor.Y - rect.Top);
        if (cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom || !IsOnDisc(x, y))
        {
            SetHovered(null, null);
            return;
        }

        Hover(x, y, atRest: true);
    }

    /// <summary>A shown row's rectangle in screen pixels where it is heading (a moving row: its new place), or null.</summary>
    public RECT? RowRect(string key)
    {
        if (_host.Handle == 0 || !User32.GetWindowRect(_host.Handle, out var window))
        {
            return null;
        }

        var scene = _surface.Scene;
        foreach (var row in scene.Rows)
        {
            if (!row.Leaving && string.Equals(row.Key, key, StringComparison.Ordinal))
            {
                var metrics = scene.Metrics;
                var left = window.Left + (BoardCanvas.Margin + metrics.ListLeft) * DisplayScale;
                var top = window.Top + (BoardCanvas.Margin + metrics.ListTop + row.Target) * DisplayScale;
                return new RECT((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(left + metrics.ListWidth * DisplayScale),
                    (int)Math.Round(top + row.Entry.Height * DisplayScale));
            }
        }

        return null;
    }

    public void Configure(BoardStyle style, BoardSize size, BoardEffectsPolicy policy, BoardWindowOptions options)
    {
        var resized = size != _surface.Scene.Size;
        _options = options;
        _surface.Configure(style, size, policy with { ReduceMotion = policy.ReduceMotion || options.ReduceMotion });
        _surface.Opacity = Math.Clamp(options.Opacity, 0, 1);
        _host.Topmost = options.AlwaysOnTop;
        SetClickThrough(options.ClickThrough);
        if (resized && _hasCenter)
        {
            PlaceAt(_center);
        }
    }

    public void Apply(BoardTick tick) => _surface.Apply(tick);

    public void MoveTo(PointD center)
    {
        StopMove();
        PlaceAt(center);
    }

    public void Show()
    {
        if (_closed)
        {
            return;
        }

        if (_hasCenter)
        {
            PlaceAt(_center);
        }

        _host.Show();
        _surface.Visible = true;
        _housekeeping.Start();
    }

    public void Hide()
    {
        _surface.Visible = false;
        _housekeeping.Stop();
        _host.Hide();
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _housekeeping.Stop();
        _passThroughPoll.Stop();
        _mover.Active = false;
        _surface.Visible = false;
        _surface.Dispose();
        _host.Dispose();
    }

    // ---- Input (IOrbHostInput) ----

    public bool IsOnDisc(int x, int y) => _surface.Scene.Canvas.Contains(x / DisplayScale, y / DisplayScale);

    public bool IsOnUnit(int x, int y) => false;

    public void OnMouseMove(int x, int y)
    {
        if (_dragging)
        {
            ContinueDrag();
            return;
        }

        if (!IsOnDisc(x, y))
        {
            SetHovered(null, null);
            if (!_options.ClickThrough)
            {
                SetPassThrough(true);
            }

            return;
        }

        Hover(x, y);
    }

    public void OnMouseLeave()
    {
        if (!_dragging)
        {
            SetHovered(null, null);
        }
    }

    public void OnLeftDown(int x, int y, bool doubleClick)
    {
        if (!IsOnDisc(x, y))
        {
            return;
        }

        if (doubleClick)
        {
            _dragging = false;
            DashboardRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _press = (x, y);
        if (BeginDrag())
        {
            _host.CaptureMouse();
        }
    }

    public void OnLeftUp()
    {
        if (!_dragging)
        {
            return;
        }

        EndDrag();
        _host.ReleaseMouse();
    }

    public void OnRightUp(int x, int y)
    {
        if (IsOnDisc(x, y))
        {
            MenuRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void OnCaptureLost()
    {
        if (_dragging)
        {
            EndDrag();
        }
    }

    public void OnDpiChanged()
    {
        if (_hasCenter)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceAt(_center));
        }
    }

    // ---- Placement ----

    private void PlaceAt(PointD center)
    {
        _center = center;
        _hasCenter = true;
        if (_host.Handle == 0)
        {
            return;
        }

        var monitor = Monitors.FromPoint(new POINT((int)Math.Round(center.X), (int)Math.Round(center.Y)));
        var scale = monitor?.Scale ?? DisplayScale;
        var size = (int)Math.Round(_surface.Scene.Canvas.Extent * scale);
        var left = (int)Math.Round(center.X - size / 2.0);
        var top = (int)Math.Round(center.Y - size / 2.0);
        DisplayScale = scale;
        User32.SetWindowPos(_host.Handle, 0, left, top, size, size, SWP.NOZORDER | SWP.NOACTIVATE);
        _surface.SetPixels(size, scale);
    }

    // ---- Drag and motion ----

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
            SetHovered(null, null);
        }

        _dragTarget = new PointD(_dragStartCenter.X + dx, _dragStartCenter.Y + dy);
        if (_options.ReduceMotion)
        {
            PlaceAt(_dragTarget);
        }
        else
        {
            StartFrames();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        if (!_dragMoved)
        {
            StopFrames();
            if (_surface.Scene.HitRow(_press.X / DisplayScale, _press.Y / DisplayScale) is { Entry.Kind: BoardEntryKind.Group } row)
            {
                RowClicked?.Invoke(this, row.Key);
            }

            return;
        }

        var monitor = Monitors.FromPoint(new POINT((int)Math.Round(_dragTarget.X), (int)Math.Round(_dragTarget.Y)));
        var snapped = monitor is null ? _dragTarget : BoardPlacement.Release(_dragTarget, _surface.Scene.Size, monitor.WorkRect, monitor.Scale);
        if (_options.ReduceMotion)
        {
            PlaceAt(snapped);
        }
        else
        {
            var from = new PointD(_followX.Value, _followY.Value);
            _moveX = new DampedSpring(0.35, 0.8, from.X);
            _moveY = new DampedSpring(0.35, 0.8, from.Y);
            _moveTarget = snapped;
            _moving = true;
            StartFrames();
        }

        DragReleased?.Invoke(this, snapped);
    }

    private void StartFrames()
    {
        if (!_mover.Active)
        {
            _lastFrame = _clock();
        }

        _mover.Active = true;
    }

    private void StopFrames() => _mover.Active = false;

    private void OnMotionFrame()
    {
        var now = _clock();
        var dt = Math.Clamp(now - _lastFrame, 0, 0.05);
        _lastFrame = now;
        if (_dragging && _dragMoved)
        {
            _followX.Step(_dragTarget.X, dt);
            _followY.Step(_dragTarget.Y, dt);
            if (_followX.IsSettled(_dragTarget.X, 0.3) && _followY.IsSettled(_dragTarget.Y, 0.3))
            {
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
                StopMove();
                PlaceAt(_moveTarget);
                return;
            }

            PlaceAt(new PointD(_moveX.Value, _moveY.Value));
            return;
        }

        StopFrames();
    }

    private void StopMove()
    {
        _moving = false;
        if (!_dragging)
        {
            StopFrames();
        }
    }

    // ---- Hover and click-through ----

    // A window point (pixels): the hero's 性能, a group or sub row (a group row's tint lifts), or nothing; `atRest` tests where the
    // rows are heading.
    private void Hover(int x, int y, bool atRest = false)
    {
        var scene = _surface.Scene;
        var (px, py) = (x / DisplayScale, y / DisplayScale);
        if (scene.HitScore(px, py))
        {
            SetHovered(null, BoardScoreCards.HeroKey);
            return;
        }

        var row = scene.HitRow(px, py, atRest);
        SetHovered(row is { Entry.Kind: BoardEntryKind.Group } ? row.Key : null,
            row is { Entry.Kind: BoardEntryKind.Group or BoardEntryKind.Sub } ? row.Key : null);
    }

    private void SetHovered(string? tint, string? target)
    {
        _surface.SetHovered(tint);
        if (!string.Equals(_hovered, target, StringComparison.Ordinal))
        {
            _hovered = target;
            HoveredRowChanged?.Invoke(this, target);
        }
    }

    private void PollPassThrough()
    {
        if (!_passThrough || _host.Handle == 0 || !User32.GetCursorPos(out var cursor) || !User32.GetWindowRect(_host.Handle, out var rect))
        {
            _passThroughPoll.Stop();
            return;
        }

        var insideWindow = cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        if (!insideWindow || IsOnDisc(cursor.X - rect.Left, cursor.Y - rect.Top))
        {
            SetPassThrough(false);
        }
    }

    private void SetPassThrough(bool enabled)
    {
        if (_options.ClickThrough || _passThrough == enabled)
        {
            return;
        }

        _passThrough = enabled;
        _host.Transparent = enabled;
        if (enabled)
        {
            _passThroughPoll.Start();
        }
        else
        {
            _passThroughPoll.Stop();
        }
    }

    private void SetClickThrough(bool enabled)
    {
        _passThrough = false;
        _passThroughPoll.Stop();
        _host.Transparent = enabled;
        if (enabled)
        {
            SetHovered(null, null);
        }
    }

    private System.Windows.Rect RowBounds(string key) =>
        RowRect(key) is { } r ? new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : System.Windows.Rect.Empty;

    private void Housekeeping() => _surface.Paused = WindowEffects.IsCloaked(_host.Handle);

    /// <summary>Drives drag-follow and the snap on the shared frame clock (no WPF render loop).</summary>
    private sealed class MotionClient(GpuBoardWindow owner) : IOrbFrameClient
    {
        private bool _active;

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value)
                {
                    return;
                }

                _active = value;
                if (value)
                {
                    owner._orbClock.Register(this);
                }
                else
                {
                    owner._orbClock.Unregister(this);
                }
            }
        }

        public bool IsActive => _active;

        public bool IsPreferred => true;

        public FrameClass Demand => _active ? FrameClass.Fps60 : FrameClass.None;

        public double? NextWake => null;

        public FrameClass Cap => FrameClass.Fps60;

        public void Render(double now) => owner.OnMotionFrame();
    }
}

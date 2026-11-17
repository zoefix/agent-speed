using System.Windows;
using System.Windows.Media;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb.Names;

namespace AgentSpeed.Orb.Names;

/// <summary>
/// One plate beside an orb (its name plate, or its undo chip): content, slot and motion (DESIGN 2b, Name plate). The
/// plate is static — a cached drawing in its own window that the orb moves with itself — and runs frames only while it
/// fades, morphs its width or follows the orb's hover scale. Rectangles are in pt relative to the disc center.
/// </summary>
internal sealed class OrbPlate : IOrbAttachment
{
    /// <summary>Room around the capsule inside the window: the shadow (blur 9, y 1) and the motion offsets (≤ 4 pt).</summary>
    private const double Margin = 14;

    private readonly IOrbWindow _orb;
    private readonly Func<double> _clock;
    private readonly NamePlateAnimation _animation = new();
    private PlateInk? _ink;
    private PlateTypography? _type;
    private double _bodyOpacity = 1;
    private NamePlateLayout? _rest;
    private NamePlateLayout? _shown;
    private NamePlateLayout? _chip;
    private bool _expanded;
    private Rect _restRect = Rect.Empty;
    private Rect _area = Rect.Empty;
    private Rect _from = Rect.Empty;
    private Rect _to = Rect.Empty;
    private Pending? _pending;
    private Point _origin;
    private bool _frames;
    private int _calmFrames;
    private double _lastHover;
    private bool _away;

    public OrbPlate(IOrbWindow orb, PlateWindow window, Func<double> clock)
    {
        _orb = orb;
        _clock = clock;
        Window = window;
        Window.View.DisplayDpiChanged += (_, _) => Window.Dispatcher.BeginInvoke(() =>
        {
            Redraw();
            Reposition();
        });

        // The first content may have been set before the window knew its display's DPI.
        Window.View.Loaded += (_, _) => Redraw();
    }

    /// <summary>Raised after the capsule moved inside the window (the undo chip places its action over it).</summary>
    public event EventHandler? Arranged;

    /// <summary>Raised when the plate moved to a new slot (the undo chip stacks beyond it again).</summary>
    public event EventHandler? SlotChanged;

    public PlateWindow Window { get; }

    public nint Handle => Window.Handle;

    public NamePlateSlot Slot { get; private set; }

    /// <summary>The capsule's resting rectangle (pt, relative to the disc center).</summary>
    public Rect RestRect => _restRect;

    /// <summary>The capsule as currently drawn, in the window (DIP).</summary>
    public Rect CapsuleInWindow => Local(CurrentRect());

    /// <summary>The content at rest.</summary>
    public NamePlateLayout? Content => _rest;

    /// <summary>Shown or fading in (not hidden, not fading out).</summary>
    public bool IsShowing => _animation.IsShowing;

    public bool IsVisible => _animation.IsVisible;

    /// <summary>The look, the motion preference and the orb's opacity setting.</summary>
    public void SetStyle(PlateInk ink, PlateTypography type, bool reduceMotion, double bodyOpacity)
    {
        var redraw = !ReferenceEquals(ink, _ink) || !ReferenceEquals(type, _type);
        _ink = ink;
        _type = type;
        _animation.ReduceMotion = reduceMotion;
        _bodyOpacity = bodyOpacity;
        if (redraw)
        {
            Redraw();
        }

        UpdateVisuals();
    }

    /// <summary>
    /// Shows <paramref name="layout"/> in a slot: a hidden plate fades in (after <paramref name="delay"/> s), a new slot
    /// fades out at the old one and in at the new one, a new text crossfades while the width springs.
    /// </summary>
    /// <param name="chip">The source chip that folds into the plate (<see cref="Expand"/>), or null.</param>
    /// <param name="rect">The capsule's rectangle (pt, relative to the disc center).</param>
    /// <param name="area">The usable area (pt, relative to the disc center), which the folded chip stays inside.</param>
    public void Show(NamePlateLayout layout, NamePlateLayout? chip, NamePlateSlot slot, Rect rect, Rect area, double delay = 0)
    {
        var now = _clock();
        var next = new Pending(layout, chip, slot, rect, area);
        if (!_animation.IsShowing)
        {
            var resume = _animation.IsVisible && slot == Slot && rect == _restRect && Same(layout, _rest) && Same(_shown, _rest)
                && Same(chip, _chip);
            if (!resume)
            {
                _pending = null;
                Apply(next);
            }

            _animation.Show(now, delay);
            if (_expanded && ChipTarget() is { } folded)
            {
                MorphTo(folded.Content, folded.Rect, NamePlateMotion.ChipCrossfadeSeconds);
            }

            Reposition();
            StartFrames();
            return;
        }

        if (_pending is not null || slot != Slot)
        {
            if (_animation.Relocate(now))
            {
                _pending = next;
                StartFrames();
                return;
            }

            _pending = null;
            var moved = slot != Slot || rect != _restRect;
            Apply(next);
            Reposition();
            StartFrames();
            if (moved)
            {
                SlotChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var changed = !Same(layout, _rest) || rect != _restRect || !Same(chip, _chip) || area != _area;
        _rest = layout;
        _restRect = rect;
        _area = area;
        _chip = chip;
        if (changed)
        {
            var (content, target) = Target();
            MorphTo(content, target, ReferenceEquals(content, layout) ? NamePlateMotion.NameCrossfadeSeconds : NamePlateMotion.ChipCrossfadeSeconds);
        }
    }

    /// <summary>Fades out over 0.18 s while moving 2 pt toward the disc.</summary>
    public void Hide()
    {
        _pending = null;
        if (!_animation.IsVisible)
        {
            return;
        }

        _animation.Hide(_clock());
        StartFrames();
    }

    /// <summary>
    /// Folds the source chip into the plate (hover, drag, after a change) or returns to the name: the width springs about
    /// its center to the chip's content while the text crossfades. Nothing moves when the content is the same or the
    /// chip has no room.
    /// </summary>
    public void Expand(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        _expanded = expanded;
        if (_rest is null || !_animation.IsShowing || _pending is not null)
        {
            return;
        }

        var (content, target) = Target();
        if (!ReferenceEquals(content, _shown) || target != _to)
        {
            MorphTo(content, target, NamePlateMotion.ChipCrossfadeSeconds);
        }
    }

    /// <summary>The orb's hover changed: the plate follows the disc edge through the hover scale, so the gap stays.</summary>
    public void FollowHover()
    {
        if (_animation.IsVisible)
        {
            _calmFrames = 0;
            StartFrames();
        }
    }

    public PixelRect? Arrange(PointD center, double scale)
    {
        if (!_animation.IsVisible || _rest is null || _restRect.IsEmpty)
        {
            return null;
        }

        // The window also holds the folded chip, so folding it never moves or resizes the window.
        var union = Rect.Union(_restRect, Rect.Union(_from, _to));
        if (ChipTarget() is { } folded)
        {
            union.Union(folded.Rect);
        }

        var plateLeft = (int)Math.Round(center.X + _restRect.X * scale);
        var plateTop = (int)Math.Round(center.Y + _restRect.Y * scale);
        var padLeft = (int)Math.Ceiling((Margin + _restRect.X - union.X) * scale);
        var padTop = (int)Math.Ceiling((Margin + _restRect.Y - union.Y) * scale);
        var right = (int)Math.Ceiling((union.Right - _restRect.X + Margin) * scale);
        var bottom = (int)Math.Ceiling((union.Bottom - _restRect.Y + Margin) * scale);
        var origin = new Point(padLeft / scale, padTop / scale);
        if (origin != _origin)
        {
            _origin = origin;
            UpdateVisuals();
        }

        return new PixelRect(plateLeft - padLeft, plateTop - padTop, padLeft + right, padTop + bottom);
    }

    /// <summary>Its orb left the screen (hide when idle): the window hides, and shows again with the orb if it still should.</summary>
    public void SetAway(bool away)
    {
        if (_away == away)
        {
            return;
        }

        _away = away;
        if (!away)
        {
            Reposition();
        }
        else if (!Window.IsClosed)
        {
            Window.Hide();
        }
    }

    public void Close()
    {
        StopFrames();
        _orb.Detach(this);
        if (!Window.IsClosed)
        {
            Window.Close();
        }
    }

    private void Apply(Pending next)
    {
        _rest = next.Layout;
        _shown = next.Layout;
        _chip = next.Chip;
        Slot = next.Slot;
        _restRect = next.Rect;
        _area = next.Area;
        _from = next.Rect;
        _to = next.Rect;
        _animation.SetWidth(next.Rect.Width, _clock(), animate: false);
        if (_ink is not null && _type is not null)
        {
            Window.View.SetContent(next.Layout, _ink, _type, keepPrevious: false);
        }

        _animation.EndCrossfade();
        if (_expanded && _animation.IsShowing && ChipTarget() is { } folded)
        {
            MorphTo(folded.Content, folded.Rect, NamePlateMotion.ChipCrossfadeSeconds);
        }
    }

    /// <summary>What the plate shows now: the folded chip while expanded (when it differs and has room), else the name.</summary>
    private (NamePlateLayout Content, Rect Rect) Target() =>
        _expanded && ChipTarget() is { } folded ? folded : (_rest!, _restRect);

    private (NamePlateLayout Content, Rect Rect)? ChipTarget() =>
        _chip is { } chip && _rest is { } rest && NamePlateContent.ChipDiffers(rest.Line, chip.Line) && ChipRect(chip) is { } rect
            ? (chip, rect)
            : null;

    private static bool Same(NamePlateLayout? a, NamePlateLayout? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.Line.Equals(b.Line) && a.Width == b.Width);

    private void MorphTo(NamePlateLayout content, Rect target, double crossfade)
    {
        var now = _clock();
        var current = CurrentRect();
        if (!Same(content, _shown))
        {
            if (_ink is not null && _type is not null)
            {
                Window.View.SetContent(content, _ink, _type, keepPrevious: true);
            }

            _animation.Crossfade(now, crossfade);
        }

        _shown = content;
        _from = current;
        _to = target;
        _animation.SetWidth(target.Width, now, animate: true);
        if (_animation.Width == target.Width)
        {
            _from = target;
        }

        Reposition();
        StartFrames();
    }

    /// <summary>The capsule now: between the morph's ends by the width spring's progress.</summary>
    private Rect CurrentRect()
    {
        if (_to.IsEmpty)
        {
            return _restRect;
        }

        if (_from.IsEmpty || Math.Abs(_to.Width - _from.Width) < 0.01)
        {
            return _to;
        }

        var t = (_animation.Width - _from.Width) / (_to.Width - _from.Width);
        return new Rect(
            _from.X + (_to.X - _from.X) * t,
            _from.Y + (_to.Y - _from.Y) * t,
            Math.Max(0, _animation.Width),
            _to.Height);
    }

    /// <summary>
    /// The folded chip's rectangle: about the plate's center below or above the disc (kept inside the usable area), away
    /// from the disc on a side; null when it has no room there.
    /// </summary>
    private Rect? ChipRect(NamePlateLayout chip)
    {
        const double Inset = NamePlatePlacement.AreaInset;
        var w = chip.Width;
        var h = _restRect.Height;
        double x;
        switch (Slot)
        {
            case NamePlateSlot.Left:
                x = _restRect.Right - w;
                if (!_area.IsEmpty && x < _area.X + Inset)
                {
                    return null;
                }

                break;
            case NamePlateSlot.Right:
                x = _restRect.X;
                if (!_area.IsEmpty && x + w > _area.Right - Inset)
                {
                    return null;
                }

                break;
            default:
                x = _restRect.X + (_restRect.Width - w) / 2;
                if (!_area.IsEmpty)
                {
                    if (w > _area.Width - 2 * Inset)
                    {
                        return null;
                    }

                    x = Math.Clamp(x, _area.X + Inset, _area.Right - Inset - w);
                }

                break;
        }

        return new Rect(x, _restRect.Y, w, h);
    }

    private void Redraw()
    {
        if (_shown is not null && _ink is not null && _type is not null)
        {
            Window.View.SetContent(_shown, _ink, _type, keepPrevious: false);
            _animation.EndCrossfade();
        }
    }

    private void Reposition()
    {
        if (_away || !_animation.IsVisible || Window.IsClosed || _orb.Handle == 0 || !_orb.HasCenter)
        {
            return;
        }

        if (Arrange(_orb.DrawnCenter, _orb.DisplayScale) is { } rect)
        {
            Window.Place(rect);
            UpdateVisuals();
        }
    }

    private Rect Local(Rect rect) => _restRect.IsEmpty
        ? Rect.Empty
        : new Rect(_origin.X + rect.X - _restRect.X, _origin.Y + rect.Y - _restRect.Y, rect.Width, rect.Height);

    private void UpdateVisuals()
    {
        if (_ink is null || _restRect.IsEmpty)
        {
            return;
        }

        var view = Window.View;
        view.SetBody(Local(CurrentRect()), _ink);
        view.SetBlend(_animation.ContentBlend);
        var away = Away(Slot);
        var push = NamePlateMotion.HoverGrowth * _orb.Surface.Diameter * _orb.Surface.Scene.Hover - _animation.Inset;
        view.SetMotion(new Vector(away.X * push, away.Y * push), _animation.Scale);
        view.Opacity = _animation.Opacity * _bodyOpacity;
        Arranged?.Invoke(this, EventArgs.Empty);
    }

    private static Vector Away(NamePlateSlot slot) => slot switch
    {
        NamePlateSlot.Above => new Vector(0, -1),
        NamePlateSlot.Left => new Vector(-1, 0),
        NamePlateSlot.Right => new Vector(1, 0),
        _ => new Vector(0, 1),
    };

    private void StartFrames()
    {
        if (_frames)
        {
            return;
        }

        _frames = true;
        _calmFrames = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (_frames)
        {
            _frames = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Window.IsClosed)
        {
            StopFrames();
            return;
        }

        var now = _clock();
        _animation.Step(now);
        if (_animation.SlotSwapDue)
        {
            var moved = _pending is not null;
            if (_pending is { } next)
            {
                _pending = null;
                Apply(next);
            }

            _animation.EnterSlot(now);
            Reposition();
            if (moved)
            {
                SlotChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        UpdateVisuals();

        // The hover spring is stepped by the orb's own frames, which may run slower than ours or pause: it is calm once
        // it reached its target, or stopped moving for 0.2 s.
        var hover = _orb.Surface.Scene.Hover;
        var atTarget = Math.Abs(hover - (_orb.IsHovered ? 1 : 0)) < 0.002;
        _calmFrames = atTarget ? Math.Max(_calmFrames + 1, 12) : Math.Abs(hover - _lastHover) < 0.0005 ? _calmFrames + 1 : 0;
        _lastHover = hover;
        if (!_animation.IsSettled || _calmFrames < 12)
        {
            return;
        }

        StopFrames();
        if (!_animation.IsVisible)
        {
            _from = _to = _restRect;
            if (!Window.IsClosed)
            {
                Window.Hide();
            }

            return;
        }

        if (_from != _to)
        {
            _from = _to;
            Reposition();
        }
    }

    private sealed record Pending(NamePlateLayout Layout, NamePlateLayout? Chip, NamePlateSlot Slot, Rect Rect, Rect Area);
}

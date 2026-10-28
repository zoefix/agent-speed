using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>Timing of the name plates (DESIGN 2b, Name plate, Motion and Chip and undo).</summary>
public static class NamePlateMotion
{
    public const double AppearSeconds = 0.28;
    public const double AppearInset = 4;
    public const double AppearScale = 0.96;
    public const double DisappearSeconds = 0.18;
    public const double DisappearInset = 2;
    public const double NameCrossfadeSeconds = 0.2;
    public const double ChipCrossfadeSeconds = 0.15;
    public const double WidthResponse = 0.3;
    public const double WidthDamping = 0.86;
    public const double SlotFadeOutSeconds = 0.12;
    public const double SlotFadeInSeconds = 0.2;

    /// <summary>Reduce Motion: every fade takes 0.15 s; widths and placements jump.</summary>
    public const double ReducedFadeSeconds = 0.15;

    /// <summary>The source chip folds into the plate after this much hover.</summary>
    public const double ChipHoverDelay = 0.35;

    /// <summary>After a sources change the plate shows the chip for this long.</summary>
    public const double ChipAfterChange = 2;

    /// <summary>New orbs' plates fade in this long after landing (Add, Split, Merge).</summary>
    public const double LandingDelay = 0.28;

    /// <summary>The undo chip stacks this far beyond the plate.</summary>
    public const double UndoGap = 4;

    /// <summary>The hover scale (1.03) moves the disc edge by 1.5 % of S; the plate follows it.</summary>
    public const double HoverGrowth = 0.015;
}

/// <summary>
/// The animated state of one plate: appear, disappear, a fade to a new slot, the width spring and the content
/// crossfade. Pure: the app steps it at 60 fps only while <see cref="IsSettled"/> is false and draws the outputs.
/// </summary>
public sealed class NamePlateAnimation
{
    private Phase _phase = Phase.Hidden;
    private double _start;
    private double _fromOpacity;
    private double _fromInset;
    private double _fromScale = 1;
    private DampedSpring _width = new(NamePlateMotion.WidthResponse, NamePlateMotion.WidthDamping);
    private double _widthTarget;
    private double _fadeStart = double.NegativeInfinity;
    private double _fadeSeconds = 1;
    private double _lastStep;

    private enum Phase
    {
        Hidden,
        Appearing,
        Shown,
        Disappearing,
        SlotOut,
        SlotIn,
    }

    public bool ReduceMotion { get; set; }

    public double Opacity { get; private set; }

    /// <summary>How far (pt) the plate is drawn toward its disc: 4 → 0 while appearing, 0 → 2 while disappearing.</summary>
    public double Inset { get; private set; }

    public double Scale { get; private set; } = 1;

    public double Width => _width.Value;

    public double WidthTarget => _widthTarget;

    /// <summary>The content crossfade: 0 shows the previous content, 1 the current one.</summary>
    public double ContentBlend { get; private set; } = 1;

    /// <summary>Anything is drawn (shown, or fading in or out).</summary>
    public bool IsVisible => _phase != Phase.Hidden;

    /// <summary>The plate is meant to be shown (not hidden and not fading out).</summary>
    public bool IsShowing => _phase is Phase.Appearing or Phase.Shown or Phase.SlotOut or Phase.SlotIn;

    /// <summary>The plate faded out at its old slot: the app moves it to the new one and calls <see cref="EnterSlot"/>.</summary>
    public bool SlotSwapDue { get; private set; }

    public bool IsSettled =>
        _phase is Phase.Hidden or Phase.Shown && ContentBlend >= 1 && _width.IsSettled(_widthTarget, 0.05);

    /// <summary>Fades in (after <paramref name="delay"/> s): opacity 0 → 1, 4 pt from the disc side, scale 0.96 → 1.</summary>
    public void Show(double now, double delay = 0)
    {
        if (IsShowing)
        {
            return;
        }

        var fresh = _phase == Phase.Hidden;
        _fromOpacity = fresh ? 0 : Opacity;
        _fromInset = fresh && !ReduceMotion ? NamePlateMotion.AppearInset : Inset;
        _fromScale = fresh && !ReduceMotion ? NamePlateMotion.AppearScale : Scale;
        Begin(Phase.Appearing, now + Math.Max(0, delay));
        Step(now);
    }

    /// <summary>Fades out over 0.18 s while moving 2 pt toward the disc.</summary>
    public void Hide(double now)
    {
        if (_phase is Phase.Hidden or Phase.Disappearing)
        {
            return;
        }

        if (_phase == Phase.Appearing && now < _start)
        {
            Begin(Phase.Hidden, now);
            Opacity = 0;
            return;
        }

        _fromOpacity = Opacity;
        _fromInset = Inset;
        _fromScale = Scale;
        Begin(Phase.Disappearing, now);
        Step(now);
    }

    /// <summary>
    /// A new slot: true when the plate first fades out at the old slot (then <see cref="SlotSwapDue"/>); false when the
    /// app moves it right away (Reduce Motion, nothing drawn yet, or already fading out).
    /// </summary>
    public bool Relocate(double now)
    {
        if (ReduceMotion || _phase is Phase.Hidden or Phase.Disappearing || Opacity < 0.01)
        {
            return false;
        }

        if (_phase != Phase.SlotOut)
        {
            _fromOpacity = Opacity;
            Begin(Phase.SlotOut, now);
        }

        return true;
    }

    /// <summary>The plate is at its new slot: fades in over 0.2 s.</summary>
    public void EnterSlot(double now)
    {
        SlotSwapDue = false;
        if (_phase != Phase.SlotOut)
        {
            return;
        }

        Inset = 0;
        Scale = 1;
        Begin(Phase.SlotIn, now);
        Step(now);
    }

    /// <summary>The width target; it springs (response 0.3 s, damping 0.86) unless it jumps.</summary>
    public void SetWidth(double width, double now, bool animate)
    {
        if (_width.IsSettled(_widthTarget, 0.05))
        {
            _lastStep = now;
        }

        _widthTarget = width;
        if (!animate || ReduceMotion || !IsVisible || Opacity < 0.01)
        {
            _width.Snap(width);
        }
    }

    /// <summary>Starts a crossfade from the previous content to the current one (Reduce Motion: 0.15 s).</summary>
    public void Crossfade(double now, double seconds)
    {
        if (!IsVisible || Opacity < 0.01)
        {
            ContentBlend = 1;
            return;
        }

        _fadeStart = now;
        _fadeSeconds = ReduceMotion ? NamePlateMotion.ReducedFadeSeconds : Math.Max(0.01, seconds);
        ContentBlend = 0;
    }

    /// <summary>Shows the current content alone (it replaced the previous one while nothing was drawn).</summary>
    public void EndCrossfade() => ContentBlend = 1;

    public void Step(double now)
    {
        var t = now - _start;
        switch (_phase)
        {
            case Phase.Appearing:
                if (t < 0)
                {
                    Opacity = _fromOpacity;
                    Inset = _fromInset;
                    Scale = _fromScale;
                    break;
                }

                var appear = Progress(t, ReduceMotion ? NamePlateMotion.ReducedFadeSeconds : NamePlateMotion.AppearSeconds);
                var e = ReduceMotion ? appear : Easing.OutCubic(appear);
                Opacity = Easing.Lerp(_fromOpacity, 1, e);
                Inset = Easing.Lerp(_fromInset, 0, e);
                Scale = Easing.Lerp(_fromScale, 1, e);
                if (appear >= 1)
                {
                    Begin(Phase.Shown, now);
                }

                break;
            case Phase.Disappearing:
                var gone = Progress(t, ReduceMotion ? NamePlateMotion.ReducedFadeSeconds : NamePlateMotion.DisappearSeconds);
                Opacity = Easing.Lerp(_fromOpacity, 0, ReduceMotion ? gone : Easing.InOutSine(gone));
                Inset = ReduceMotion ? _fromInset : Easing.Lerp(_fromInset, NamePlateMotion.DisappearInset, Easing.OutQuad(gone));
                if (gone >= 1)
                {
                    Begin(Phase.Hidden, now);
                    Opacity = 0;
                    Inset = 0;
                    Scale = 1;
                }

                break;
            case Phase.SlotOut:
                var fadeOut = Progress(t, NamePlateMotion.SlotFadeOutSeconds);
                Opacity = Easing.Lerp(_fromOpacity, 0, fadeOut);
                SlotSwapDue = fadeOut >= 1;
                break;
            case Phase.SlotIn:
                var fadeIn = Progress(t, NamePlateMotion.SlotFadeInSeconds);
                Opacity = Easing.OutCubic(fadeIn);
                if (fadeIn >= 1)
                {
                    Begin(Phase.Shown, now);
                }

                break;
            case Phase.Shown:
                Opacity = 1;
                Inset = 0;
                Scale = 1;
                break;
            default:
                Opacity = 0;
                break;
        }

        if (ContentBlend < 1)
        {
            ContentBlend = Progress(now - _fadeStart, _fadeSeconds);
        }

        if (!_width.IsSettled(_widthTarget, 0.05))
        {
            _width.Step(_widthTarget, Math.Clamp(now - _lastStep, 0, 0.05));
            if (_width.IsSettled(_widthTarget, 0.05))
            {
                _width.Snap(_widthTarget);
            }
        }

        _lastStep = now;
    }

    private void Begin(Phase phase, double now)
    {
        _phase = phase;
        _start = now;
        if (phase != Phase.SlotOut)
        {
            SlotSwapDue = false;
        }
    }

    private static double Progress(double elapsed, double seconds) => Easing.Clamp01(elapsed / Math.Max(0.001, seconds));
}

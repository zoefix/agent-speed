namespace AgentSpeed.Presentation.Motion;

/// <summary>
/// "Hide when idle" (DESIGN 2), per orb: after the same 60 s without activity as <see cref="IdleFade"/>, and never under the
/// pointer, the orb fades to 0 over 0.6 s ease-in-out and its window then leaves the screen (<see cref="IsAway"/>). It comes
/// back at its own opacity over 0.12 s ease-out; Reduce Motion cuts both ways. The window asks <see cref="TryHide"/> on its
/// housekeeping timer, so the two fades are the only times it needs frames.
/// </summary>
public sealed class IdleHide
{
    public const double DelaySeconds = IdleFade.DelaySeconds;
    public const double HideSeconds = IdleFade.FadeSeconds;
    public const double ReturnSeconds = 0.12;

    private double _idleSince = double.NaN;
    private double _from = 1;
    private double _start = double.NegativeInfinity;

    /// <summary>Fading out or off the screen.</summary>
    public bool Hidden { get; private set; }

    /// <summary>An engine tick: activity brings the orb back (true when it was hidden), idle starts the 60 s.</summary>
    public bool NoteActivity(bool active, double now, bool reduceMotion)
    {
        if (active)
        {
            _idleSince = double.NaN;
            return Return(now, reduceMotion);
        }

        if (double.IsNaN(_idleSince))
        {
            _idleSince = now;
        }

        return false;
    }

    /// <summary>Starts the 60 s again from now while idle (the switch changed).</summary>
    public void Rearm(double now)
    {
        if (!double.IsNaN(_idleSince))
        {
            _idleSince = now;
        }
    }

    /// <summary>Starts hiding when switched on, idle for 60 s and neither hovered nor dragged; true when it began.</summary>
    public bool TryHide(bool enabled, bool engaged, bool reduceMotion, double now)
    {
        if (!enabled || engaged || Hidden || double.IsNaN(_idleSince) || now - _idleSince < DelaySeconds)
        {
            return false;
        }

        _from = Value(now);
        Hidden = true;
        _start = reduceMotion ? double.NegativeInfinity : now;
        return true;
    }

    /// <summary>Back to the orb's own opacity from wherever the fade is, over 0.12 s; true when it was hidden.</summary>
    public bool Return(double now, bool reduceMotion)
    {
        if (!Hidden)
        {
            return false;
        }

        _from = Value(now);
        Hidden = false;
        _start = reduceMotion ? double.NegativeInfinity : now;
        return true;
    }

    /// <summary>The factor on the window's opacity: 1 shown, 0 hidden, eased while either fade runs.</summary>
    public double Value(double now)
    {
        var target = Hidden ? 0 : 1;
        if (!IsMoving(now))
        {
            return target;
        }

        var t = (now - _start) / Duration;
        var eased = Hidden ? (t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2) : Easing.OutCubic(t);
        return _from + (target - _from) * eased;
    }

    /// <summary>One of the two fades is running (the window draws until it ends).</summary>
    public bool IsMoving(double now) => double.IsFinite(_start) && now >= _start && now - _start < Duration;

    /// <summary>Faded out completely: the window is off the screen, with no frames and no hit testing.</summary>
    public bool IsAway(double now) => Hidden && !IsMoving(now);

    private double Duration => Hidden ? HideSeconds : ReturnSeconds;
}

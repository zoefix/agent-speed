namespace AgentSpeed.Presentation.Motion;

/// <summary>
/// "Fade when idle" (DESIGN 2), one rule for the orb and board windows: 60 s without activity (the window's tier idle, counted
/// from its first tick) fades it to 45 % of its opacity over 0.6 s ease-in-out, a cut with Reduce Motion; activity, hover, a
/// drag or switching it off brings it back at once. The window asks <see cref="TryFade"/> on its housekeeping timer, so the
/// fade itself is the only time it needs frames.
/// </summary>
public sealed class IdleFade
{
    public const double DelaySeconds = 60;
    public const double Factor = 0.45;
    public const double FadeSeconds = 0.6;

    private double _idleSince = double.NaN;
    private double _from = 1;
    private double _start = double.NegativeInfinity;

    public bool Faded { get; private set; }

    /// <summary>An engine tick: activity restores at once (true when the window was faded), idle starts the 60 s.</summary>
    public bool NoteActivity(bool active, double now)
    {
        if (active)
        {
            _idleSince = double.NaN;
            return Restore();
        }

        if (double.IsNaN(_idleSince))
        {
            _idleSince = now;
        }

        return false;
    }

    /// <summary>Starts the fade when switched on, idle for 60 s and not under the pointer; true when it began.</summary>
    public bool TryFade(bool enabled, bool engaged, bool reduceMotion, double now)
    {
        if (!enabled || engaged || Faded || double.IsNaN(_idleSince) || now - _idleSince < DelaySeconds)
        {
            return false;
        }

        _from = Value(now);
        Faded = true;
        _start = reduceMotion ? double.NegativeInfinity : now;
        return true;
    }

    /// <summary>Back to full opacity at once (activity, hover, a drag, the switch off); true when it was faded.</summary>
    public bool Restore()
    {
        if (!Faded)
        {
            return false;
        }

        Faded = false;
        _start = double.NegativeInfinity;
        return true;
    }

    /// <summary>The factor on the window's opacity: 1, or 0.45 once faded, eased in-out (cubic) while the fade runs.</summary>
    public double Value(double now)
    {
        var target = Faded ? Factor : 1;
        if (!IsFading(now))
        {
            return target;
        }

        var t = (now - _start) / FadeSeconds;
        var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        return _from + (target - _from) * eased;
    }

    /// <summary>The 0.6 s fade is running (the window draws at 30 fps until it ends).</summary>
    public bool IsFading(double now) => double.IsFinite(_start) && now >= _start && now - _start < FadeSeconds;
}

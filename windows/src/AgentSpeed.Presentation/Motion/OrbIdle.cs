namespace AgentSpeed.Presentation.Motion;

/// <summary>
/// The idle rules of one floating orb window (DESIGN 2, 2b): fade when idle (<see cref="IdleFade"/>) and hide when idle
/// (<see cref="IdleHide"/>) on the orb's own idle state; with both switched on, hiding replaces the fade. The window feeds
/// it the engine's activity, its switches and the pointer, asks <see cref="Tick"/> on its housekeeping timer and multiplies
/// its opacity by <see cref="Value"/>.
/// </summary>
public sealed class OrbIdle
{
    private readonly IdleFade _fade = new();
    private readonly IdleHide _hide = new();
    private bool _fadeOn;
    private bool _hideOn;

    public bool Faded => _fade.Faded;

    /// <summary>Fading out or off the screen by hide when idle.</summary>
    public bool Hidden => _hide.Hidden;

    /// <summary>The factor once the running fade settles: 0 hidden, 0.45 faded, else 1.</summary>
    public double Target => _hide.Hidden ? 0 : _fade.Faded ? IdleFade.Factor : 1;

    /// <summary>
    /// The orb's switches. Changing hide when idle starts the 60 s again, and turning it off brings the orb back; a fade
    /// ends at once when its switch is off or hiding replaces it. True when the factor changed.
    /// </summary>
    public bool Configure(bool fadeWhenIdle, bool hideWhenIdle, bool reduceMotion, double now)
    {
        var changed = false;
        if (hideWhenIdle != _hideOn)
        {
            _hideOn = hideWhenIdle;
            _hide.Rearm(now);
            _fade.Rearm(now);
            changed = !hideWhenIdle && _hide.Return(now, reduceMotion);
        }

        _fadeOn = fadeWhenIdle;
        if (!fadeWhenIdle || hideWhenIdle)
        {
            changed |= _fade.Restore();
        }

        return changed;
    }

    /// <summary>An engine tick of the orb's sources: true when activity ended the fade or brought the orb back.</summary>
    public bool NoteActivity(bool active, double now, bool reduceMotion) =>
        _fade.NoteActivity(active, now) | _hide.NoteActivity(active, now, reduceMotion);

    /// <summary>
    /// The pointer reached the orb or its plate: the fade ends at once and an orb still fading out comes back (one off the
    /// screen cannot be reached). True when either happened.
    /// </summary>
    public bool Engage(double now, bool reduceMotion) =>
        _fade.Restore() | (_hide.Hidden && !_hide.IsAway(now) && _hide.Return(now, reduceMotion));

    /// <summary>The housekeeping tick: hides, else fades, once due and not <paramref name="engaged"/>; true when one began.</summary>
    public bool Tick(bool engaged, bool reduceMotion, double now) =>
        _hide.TryHide(_hideOn, engaged, reduceMotion, now) || _fade.TryFade(_fadeOn && !_hideOn, engaged, reduceMotion, now);

    /// <summary>The factor on the window's opacity now.</summary>
    public double Value(double now) => _fade.Value(now) * _hide.Value(now);

    /// <summary>A fade out, the hide or the 0.12 s return is running.</summary>
    public bool IsMoving(double now) => _fade.IsFading(now) || _hide.IsMoving(now);

    /// <summary>The 0.12 s return is running (it draws at 60 fps, the fades out at 30).</summary>
    public bool IsReturning(double now) => !_hide.Hidden && _hide.IsMoving(now);

    /// <summary>Hidden and faded out: the window is off the screen.</summary>
    public bool IsAway(double now) => _hide.IsAway(now);
}

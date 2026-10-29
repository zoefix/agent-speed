namespace AgentSpeed.Presentation.Perf;

/// <summary>What the score card window should do after an event.</summary>
public enum PerfCardAction
{
    None,

    /// <summary>Appear for <see cref="PerfCardHover.Shown"/>: a 0.15 s fade and a 2 DIP slide.</summary>
    Show,

    /// <summary>Another row's card replaces the shown one at once, with a 0.12 s crossfade.</summary>
    Switch,

    Hide,
}

/// <summary>
/// The score card's hover and keyboard timing (DESIGN 12, <i>Placement and timing</i>), free of any UI: it shows after
/// the host's delay, switches at once between rows while shown, hides 0.15 s after the pointer leaves, follows the
/// keyboard focus while shown, and Esc hides it. Times are seconds on one clock; call <see cref="Tick"/> at
/// <see cref="NextDue"/>.
/// </summary>
public sealed class PerfCardHover
{
    private object? _pendingKey;
    private double _pendingAt = double.PositiveInfinity;
    private double _hideAt = double.PositiveInfinity;

    /// <summary>The row whose card is shown, or null.</summary>
    public object? Shown { get; private set; }

    /// <summary>The host of the shown or pending card.</summary>
    public PerfCardHost Host { get; private set; }

    /// <summary>The row under the pointer, or null.</summary>
    public object? Hovered { get; private set; }

    /// <summary>When <see cref="Tick"/> must run next, or null when nothing is waiting.</summary>
    public double? NextDue
    {
        get
        {
            var due = Math.Min(_pendingAt, _hideAt);
            return double.IsPositiveInfinity(due) ? null : due;
        }
    }

    public bool IsShown => Shown is not null;

    /// <summary>The pointer entered a row.</summary>
    public PerfCardAction Hover(object key, PerfCardHost host, double now)
    {
        ArgumentNullException.ThrowIfNull(key);
        Hovered = key;
        _hideAt = double.PositiveInfinity;
        if (Shown is not null)
        {
            CancelPending();
            return Show(key, host);
        }

        if (!Equals(_pendingKey, key))
        {
            _pendingKey = key;
            _pendingAt = now + PerfCardPlacement.Delay(host);
            Host = host;
        }

        return PerfCardAction.None;
    }

    /// <summary>The pointer left a row.</summary>
    public PerfCardAction Leave(object key, double now)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Equals(Hovered, key))
        {
            Hovered = null;
        }

        if (Equals(_pendingKey, key))
        {
            CancelPending();
        }

        if (Shown is not null && Hovered is null)
        {
            _hideAt = now + PerfCardPlacement.HideDelay;
        }

        return PerfCardAction.None;
    }

    /// <summary>Runs what has come due.</summary>
    public PerfCardAction Tick(double now)
    {
        if (now >= _pendingAt && _pendingKey is { } key)
        {
            CancelPending();
            Shown = key;
            return PerfCardAction.Show;
        }

        if (now >= _hideAt)
        {
            return Hide();
        }

        return PerfCardAction.None;
    }

    /// <summary>Space on a focused row: shows its card at once.</summary>
    public PerfCardAction Show(object key, PerfCardHost host)
    {
        ArgumentNullException.ThrowIfNull(key);
        CancelPending();
        _hideAt = double.PositiveInfinity;
        Host = host;
        if (Equals(Shown, key))
        {
            return PerfCardAction.None;
        }

        var action = Shown is null ? PerfCardAction.Show : PerfCardAction.Switch;
        Shown = key;
        return action;
    }

    /// <summary>The keyboard focus moved to a row (null: away from every row); a shown card follows it.</summary>
    public PerfCardAction Focus(object? key, PerfCardHost host)
    {
        if (Shown is null || Hovered is not null)
        {
            return PerfCardAction.None;
        }

        return key is null ? Hide() : Show(key, host);
    }

    /// <summary>Esc, a closing window, or a row that went away.</summary>
    public PerfCardAction Hide()
    {
        CancelPending();
        _hideAt = double.PositiveInfinity;
        if (Shown is null)
        {
            return PerfCardAction.None;
        }

        Shown = null;
        return PerfCardAction.Hide;
    }

    private void CancelPending()
    {
        _pendingKey = null;
        _pendingAt = double.PositiveInfinity;
    }
}

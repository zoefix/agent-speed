namespace AgentSpeed.Presentation.TaskScore;

/// <summary>What the score card window should do after an event.</summary>
public enum TaskCardAction
{
    None,

    /// <summary>Appear for <see cref="TaskCardHover.Shown"/>: a 0.15 s fade and a 2 DIP slide.</summary>
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
public sealed class TaskCardHover
{
    private object? _pendingKey;
    private double _pendingAt = double.PositiveInfinity;
    private double _hideAt = double.PositiveInfinity;

    /// <summary>The row whose card is shown, or null.</summary>
    public object? Shown { get; private set; }

    /// <summary>The host of the shown or pending card.</summary>
    public TaskCardHost Host { get; private set; }

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
    public TaskCardAction Hover(object key, TaskCardHost host, double now)
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
            _pendingAt = now + TaskCardPlacement.Delay(host);
            Host = host;
        }

        return TaskCardAction.None;
    }

    /// <summary>The pointer left a row.</summary>
    public TaskCardAction Leave(object key, double now)
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
            _hideAt = now + TaskCardPlacement.HideDelay;
        }

        return TaskCardAction.None;
    }

    /// <summary>Runs what has come due.</summary>
    public TaskCardAction Tick(double now)
    {
        if (now >= _pendingAt && _pendingKey is { } key)
        {
            CancelPending();
            Shown = key;
            return TaskCardAction.Show;
        }

        if (now >= _hideAt)
        {
            return Hide();
        }

        return TaskCardAction.None;
    }

    /// <summary>Space on a focused row: shows its card at once.</summary>
    public TaskCardAction Show(object key, TaskCardHost host)
    {
        ArgumentNullException.ThrowIfNull(key);
        CancelPending();
        _hideAt = double.PositiveInfinity;
        Host = host;
        if (Equals(Shown, key))
        {
            return TaskCardAction.None;
        }

        var action = Shown is null ? TaskCardAction.Show : TaskCardAction.Switch;
        Shown = key;
        return action;
    }

    /// <summary>The keyboard focus moved to a row (null: away from every row); a shown card follows it.</summary>
    public TaskCardAction Focus(object? key, TaskCardHost host)
    {
        if (Shown is null || Hovered is not null)
        {
            return TaskCardAction.None;
        }

        return key is null ? Hide() : Show(key, host);
    }

    /// <summary>Esc, a closing window, or a row that went away.</summary>
    public TaskCardAction Hide()
    {
        CancelPending();
        _hideAt = double.PositiveInfinity;
        if (Shown is null)
        {
            return TaskCardAction.None;
        }

        Shown = null;
        return TaskCardAction.Hide;
    }

    private void CancelPending()
    {
        _pendingKey = null;
        _pendingAt = double.PositiveInfinity;
    }
}

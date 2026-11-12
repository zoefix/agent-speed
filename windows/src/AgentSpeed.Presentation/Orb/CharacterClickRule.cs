namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// A single click (DESIGN 9, <i>Orb</i>, <i>Switching</i> 1, and CHARACTERS 5, <i>Behavior</i>, <c>click</c>): a press and
/// release within <see cref="MaxMovement"/> pt and <see cref="MaxPress"/> s, fired once the system double-click time
/// passed without a second click; a double-click and the end of a drag never fire. Times in seconds, points in screen
/// points.
/// </summary>
public sealed class CharacterClickRule(double doubleClickInterval)
{
    public const double MaxMovement = 4;
    public const double MaxPress = 0.5;

    private (double Time, double X, double Y)? _press;

    public double DoubleClickInterval { get; set; } = doubleClickInterval;

    /// <summary>The click waiting for the double-click time: when it fires and where it was pressed.</summary>
    public (double FireAt, double X, double Y)? Pending { get; private set; }

    /// <summary>A press; <paramref name="armed"/> when it may start a click (on her figure's orb).</summary>
    public void Down(double time, double x, double y, int clickCount, bool armed = true)
    {
        if (clickCount >= 2)
        {
            _press = null;
            Pending = null;
            return;
        }

        _press = armed ? (time, x, y) : null;
    }

    /// <summary>The release; <paramref name="moved"/> when the press turned into a drag. Returns the delay until the click fires, or null.</summary>
    public double? Up(double time, double x, double y, bool moved)
    {
        if (_press is not { } press)
        {
            return null;
        }

        _press = null;
        var held = time - press.Time;
        var isClick = !moved && double.Hypot(x - press.X, y - press.Y) <= MaxMovement && held <= MaxPress;
        if (!isClick)
        {
            return null;
        }

        var delay = Math.Max(0, DoubleClickInterval - held);
        Pending = (time + delay, press.X, press.Y);
        return delay;
    }

    /// <summary>The pending click once its time came, taken once.</summary>
    public (double X, double Y)? Take(double now)
    {
        if (Pending is not { } pending || !(now >= pending.FireAt - 1e-6))
        {
            return null;
        }

        Pending = null;
        return (pending.X, pending.Y);
    }

    public void Cancel()
    {
        _press = null;
        Pending = null;
    }
}

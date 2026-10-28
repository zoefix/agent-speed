using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Motion;

/// <summary>
/// The agents list opening or closing (DESIGN section 4, item 4, and section 6). The viewport and the flyout window each
/// ride a layout spring with the same response, so from rest they move in lockstep and arrive together; a retarget
/// mid-flight keeps both velocities. The window never passes <see cref="Cap"/>, so it cannot outgrow the screen.
/// </summary>
public sealed class DisclosureMotion
{
    /// <summary>Settled once both heights are within a quarter DIP of their targets and nearly still.</summary>
    public const double SettleEpsilon = 0.25;

    private DampedSpring _viewport = DampedSpring.Layout();
    private DampedSpring _window = DampedSpring.Layout();

    public FlyoutHeights Target { get; private set; }

    /// <summary>Largest window height, in DIPs.</summary>
    public double Cap { get; private set; } = double.PositiveInfinity;

    public FlyoutHeights Current => new(Math.Max(0, _viewport.Value), Math.Clamp(_window.Value, 0, Cap));

    public bool IsSettled =>
        _viewport.IsSettled(Target.Viewport, SettleEpsilon) && _window.IsSettled(Target.Window, SettleEpsilon);

    /// <summary>Starts at rest at <paramref name="from"/>.</summary>
    public void Start(FlyoutHeights from, FlyoutHeights to, double cap)
    {
        _viewport = DampedSpring.Layout(Sanitize(from.Viewport));
        _window = DampedSpring.Layout(Sanitize(from.Window));
        Retarget(to, cap);
    }

    /// <summary>Moves the targets without touching the current heights or velocities.</summary>
    public void Retarget(FlyoutHeights to, double cap)
    {
        Cap = double.IsNaN(cap) ? double.PositiveInfinity : Math.Max(0, cap);
        Target = new FlyoutHeights(Sanitize(to.Viewport), Math.Min(Sanitize(to.Window), Cap));
    }

    /// <summary>Advances by <paramref name="dt"/> seconds; once settled the heights sit exactly on the targets.</summary>
    public FlyoutHeights Step(double dt)
    {
        _viewport.Step(Target.Viewport, dt);
        _window.Step(Target.Window, dt);
        if (IsSettled)
        {
            Snap(Target);
        }

        return Current;
    }

    public void Snap(FlyoutHeights heights)
    {
        Target = new FlyoutHeights(Sanitize(heights.Viewport), Math.Min(Sanitize(heights.Window), Cap));
        _viewport.Snap(Target.Viewport);
        _window.Snap(Target.Window);
    }

    private static double Sanitize(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}

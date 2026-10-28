namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// The needle's underdamped spring (DESIGN 2a.2, Needle dynamics): 1.5 Hz, damping ratio 0.55, integrated in
/// semi-implicit Euler sub-steps of at most 1/480 s, with stops at <see cref="NeedleGeometry.ZeroStop"/> and
/// <see cref="NeedleGeometry.Peg"/> that reverse the velocity × −0.32 on contact (checked every sub-step, so the bounce
/// does not depend on the frame rate). Values are dial positions; velocity is dial units per second.
/// </summary>
public struct NeedleDynamics
{
    public const double Frequency = 1.5;
    public const double DampingRatio = 0.55;
    public const double MaxSubStep = 1.0 / 480;
    public const double Restitution = 0.32;

    private static readonly double Omega = 2 * Math.PI * Frequency;

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public void Step(double target, double dt)
    {
        if (!(dt > 0) || !double.IsFinite(target))
        {
            return;
        }

        var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxSubStep - 1e-9));
        var h = dt / steps;
        var x = Value;
        var v = Velocity;
        var w2 = Omega * Omega;
        var c = 2 * DampingRatio * Omega;
        for (var i = 0; i < steps; i++)
        {
            v += (-w2 * (x - target) - c * v) * h;
            x += v * h;
            if (x > NeedleGeometry.Peg)
            {
                x = NeedleGeometry.Peg;
                if (v > 0)
                {
                    v *= -Restitution;
                }
            }
            else if (x < NeedleGeometry.ZeroStop)
            {
                x = NeedleGeometry.ZeroStop;
                if (v < 0)
                {
                    v *= -Restitution;
                }
            }
        }

        Value = x;
        Velocity = v;
    }

    /// <summary>Rests at <paramref name="value"/> (clamped by the stops) without velocity.</summary>
    public void Snap(double value)
    {
        Value = double.IsFinite(value) ? Math.Clamp(value, NeedleGeometry.ZeroStop, NeedleGeometry.Peg) : 0;
        Velocity = 0;
    }

    public readonly bool IsSettled(double target, double epsilon = 0.001)
    {
        var rest = Math.Clamp(target, NeedleGeometry.ZeroStop, NeedleGeometry.Peg);
        return Math.Abs(Value - rest) < epsilon && Math.Abs(Velocity) < epsilon * 10;
    }
}

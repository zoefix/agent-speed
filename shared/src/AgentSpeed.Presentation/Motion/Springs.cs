namespace AgentSpeed.Presentation.Motion;

/// <summary>Critically damped spring integrated analytically, so any time step is stable.</summary>
public struct CriticalSpring
{
    private const double SettleFactor = 5.8335;

    public CriticalSpring(double settleSeconds, double initial = 0)
    {
        Omega = SettleFactor / Math.Max(settleSeconds, 0.001);
        Value = initial;
        Velocity = 0;
    }

    public double Omega { get; }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public void Step(double target, double dt)
    {
        if (!double.IsFinite(target))
        {
            return;
        }

        if (dt <= 0 || !double.IsFinite(dt))
        {
            return;
        }

        var x0 = Value - target;
        var c = Velocity + Omega * x0;
        var e = Math.Exp(-Omega * dt);
        Value = target + (x0 + c * dt) * e;
        Velocity = (Velocity - Omega * c * dt) * e;
    }

    public void Snap(double value)
    {
        Value = value;
        Velocity = 0;
    }

    public readonly bool IsSettled(double target, double epsilon = 0.001) =>
        Math.Abs(Value - target) < epsilon && Math.Abs(Velocity) < epsilon * 10;
}

/// <summary>
/// Damped harmonic spring parameterised like SwiftUI (response in seconds, damping fraction), integrated analytically.
/// </summary>
public struct DampedSpring
{
    public DampedSpring(double response, double dampingFraction, double initial = 0)
    {
        Omega = 2 * Math.PI / Math.Max(response, 0.001);
        Damping = Math.Max(dampingFraction, 0);
        Value = initial;
        Velocity = 0;
    }

    public static DampedSpring Layout(double initial = 0) => new(0.45, 0.82, initial);

    public double Omega { get; }

    public double Damping { get; }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public void Step(double target, double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt) || !double.IsFinite(target))
        {
            return;
        }

        var x0 = Value - target;
        var v0 = Velocity;
        var w = Omega;
        var z = Damping;
        double x;
        double v;

        if (z < 1)
        {
            var wd = w * Math.Sqrt(1 - z * z);
            var e = Math.Exp(-z * w * dt);
            var cos = Math.Cos(wd * dt);
            var sin = Math.Sin(wd * dt);
            x = e * (x0 * cos + (v0 + z * w * x0) / wd * sin);
            v = e * (v0 * cos - (z * w * v0 + w * w * x0) / wd * sin);
        }
        else if (z == 1)
        {
            var c = v0 + w * x0;
            var e = Math.Exp(-w * dt);
            x = (x0 + c * dt) * e;
            v = (v0 - w * c * dt) * e;
        }
        else
        {
            var root = Math.Sqrt(z * z - 1);
            var r1 = -w * (z - root);
            var r2 = -w * (z + root);
            var c2 = (v0 - r1 * x0) / (r2 - r1);
            var c1 = x0 - c2;
            var e1 = Math.Exp(r1 * dt);
            var e2 = Math.Exp(r2 * dt);
            x = c1 * e1 + c2 * e2;
            v = c1 * r1 * e1 + c2 * r2 * e2;
        }

        Value = target + x;
        Velocity = v;
    }

    public void Snap(double value)
    {
        Value = value;
        Velocity = 0;
    }

    public void Kick(double velocity) => Velocity += velocity;

    public readonly bool IsSettled(double target, double epsilon = 0.001) =>
        Math.Abs(Value - target) < epsilon && Math.Abs(Velocity) < epsilon * 10;
}

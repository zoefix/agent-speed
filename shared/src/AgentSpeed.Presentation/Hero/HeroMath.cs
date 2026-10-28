namespace AgentSpeed.Presentation.Hero;

/// <summary>Easing and spring helpers with the exact formulas of DESIGN 4c (and its motion prototype).</summary>
public static class HeroMath
{
    public static double Clamp01(double t) => double.IsFinite(t) ? Math.Clamp(t, 0, 1) : t > 0 ? 1 : 0;

    public static double Smooth(double t)
    {
        t = Clamp01(t);
        return t * t * (3 - 2 * t);
    }

    public static double EaseInCubic(double t)
    {
        t = Clamp01(t);
        return t * t * t;
    }

    public static double EaseOutCubic(double t)
    {
        t = 1 - Clamp01(t);
        return 1 - t * t * t;
    }

    public static double EaseInOutSine(double t) => 0.5 - 0.5 * Math.Cos(Math.PI * Clamp01(t));

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary><c>bump(a, p, e)</c>: smoothstep from 0 at <paramref name="start"/> to 1 at <paramref name="peak"/>, back to 0 at <paramref name="end"/>.</summary>
    public static double Bump(double t, double start, double peak, double end) =>
        t < start ? 0 : t < peak ? Smooth((t - start) / (peak - start)) : 1 - Smooth((t - peak) / (end - peak));

    /// <summary>JavaScript <c>Math.round</c> (halves round up), so counts match the prototype.</summary>
    public static int RoundHalfUp(double value) => (int)Math.Floor(value + 0.5);

    /// <summary>Underdamped spring (SwiftUI response / damping fraction) evaluated analytically at time t.</summary>
    public static double SpringAt(double t, double from, double to, double velocity, double response, double damping)
    {
        if (t <= 0)
        {
            return from;
        }

        var w0 = 2 * Math.PI / response;
        var wd = w0 * Math.Sqrt(1 - damping * damping);
        var a = from - to;
        var b = (velocity + damping * w0 * a) / wd;
        return to + Math.Exp(-damping * w0 * t) * (a * Math.Cos(wd * t) + b * Math.Sin(wd * t));
    }

    /// <summary>Time derivative of <see cref="SpringAt"/>.</summary>
    public static double SpringVelocityAt(double t, double from, double to, double velocity, double response, double damping)
    {
        if (t <= 0)
        {
            return velocity;
        }

        var w0 = 2 * Math.PI / response;
        var z = damping;
        var wd = w0 * Math.Sqrt(1 - z * z);
        var a = from - to;
        var b = (velocity + z * w0 * a) / wd;
        var e = Math.Exp(-z * w0 * t);
        var cos = Math.Cos(wd * t);
        var sin = Math.Sin(wd * t);
        return e * ((-z * w0 * a + wd * b) * cos + (-z * w0 * b - wd * a) * sin);
    }

    /// <summary>Angle in [0, 2π), keeping large world distances precise before they reach the GPU.</summary>
    public static double WrapRadians(double angle)
    {
        var r = angle % (2 * Math.PI);
        if (r < 0)
        {
            r += 2 * Math.PI;
        }

        return r >= 2 * Math.PI ? 0 : r;
    }
}

/// <summary>
/// The exact critically damped spring of DESIGN 4c ("Speed model"): <c>ω = 6.2 / settle</c>, evaluated in closed form so
/// any frame time is stable. <see cref="Velocity"/> is <c>a = dG/dt</c>.
/// </summary>
public struct SettleSpring
{
    public SettleSpring(double settleSeconds, double initial = 0)
    {
        Omega = 6.2 / Math.Max(settleSeconds, 0.001);
        Value = initial;
        Velocity = 0;
    }

    public double Omega { get; }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public void Step(double target, double dt)
    {
        if (!double.IsFinite(target) || !double.IsFinite(dt) || dt <= 0)
        {
            return;
        }

        var c1 = Value - target;
        var c2 = Velocity + Omega * c1;
        var e = Math.Exp(-Omega * dt);
        Value = target + (c1 + c2 * dt) * e;
        Velocity = (c2 - Omega * (c1 + c2 * dt)) * e;
    }

    public void Snap(double value)
    {
        Value = value;
        Velocity = 0;
    }

    public readonly bool IsSettled(double target, double epsilon) =>
        Math.Abs(Value - target) < epsilon && Math.Abs(Velocity) < epsilon * 10;
}

/// <summary>Deterministic xorshift32 generator (the prototype's <c>rng</c>), so particle runs replay exactly.</summary>
public sealed class HeroRandom
{
    private uint _state;

    public HeroRandom(uint seed)
    {
        _state = seed == 0 ? 1 : seed;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double Next()
    {
        var s = _state;
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        _state = s;
        return s / 4294967296.0;
    }
}

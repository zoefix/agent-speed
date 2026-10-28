using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>Seeded xorshift32 (the prototype's generator); a value type, so a model owns it without allocating.</summary>
internal struct XorShift32
{
    private uint _state;

    public XorShift32(int seed)
    {
        // Scramble the seed so small seeds do not start with tiny outputs.
        var z = unchecked((uint)seed + 0x9E3779B9u);
        z = unchecked((z ^ (z >> 16)) * 0x85EBCA6Bu);
        z = unchecked((z ^ (z >> 13)) * 0xC2B2AE35u);
        z ^= z >> 16;
        _state = z == 0 ? 1u : z;
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

    public double Range(double min, double max) => min + (max - min) * Next();
}

/// <summary>
/// Underdamped spring integrated with semi-implicit Euler in sub-steps of at most 1/480 s (the prototype's
/// <c>Oscillator</c>), plus an exact critically damped step on the same state for Off and Reduce Motion.
/// </summary>
internal struct SubSteppedSpring
{
    public const double MaxStep = 1 / 480.0;
    private const double SettleFactor = 5.8335;

    private readonly double _omega;
    private readonly double _damping;

    public SubSteppedSpring(double frequency, double damping, double initial = 0)
    {
        _omega = 2 * Math.PI * frequency;
        _damping = damping;
        Value = initial;
        Velocity = 0;
    }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public void Step(double target, double dt) => Step(target, target, dt);

    /// <summary>Steps toward a target that moved linearly from <paramref name="fromTarget"/> over the frame.</summary>
    public void Step(double fromTarget, double target, double dt)
    {
        if (!(dt > 0) || !double.IsFinite(dt) || !double.IsFinite(target))
        {
            return;
        }

        if (!double.IsFinite(fromTarget))
        {
            fromTarget = target;
        }

        var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxStep - 1e-9));
        var h = dt / steps;
        var w2 = _omega * _omega;
        var c = 2 * _damping * _omega;
        var x = Value;
        var v = Velocity;
        for (var i = 0; i < steps; i++)
        {
            var goal = fromTarget + (target - fromTarget) * (i + 1) / steps;
            v += (-w2 * (x - goal) - c * v) * h;
            x += v * h;
        }

        Value = x;
        Velocity = v;
    }

    /// <summary>Critically damped step that settles in <paramref name="settleSeconds"/>, keeping the velocity.</summary>
    public void StepCritical(double target, double dt, double settleSeconds)
    {
        if (!(dt > 0) || !double.IsFinite(dt) || !double.IsFinite(target))
        {
            return;
        }

        var w = SettleFactor / settleSeconds;
        var x0 = Value - target;
        var c = Velocity + w * x0;
        var e = Math.Exp(-w * dt);
        Value = target + (x0 + c * dt) * e;
        Velocity = (Velocity - w * c * dt) * e;
    }

    public void Kick(double velocity) => Velocity += velocity;

    public void Snap(double value)
    {
        Value = value;
        Velocity = 0;
    }

    public readonly bool IsSettled(double target, double epsilon = 0.001) =>
        Math.Abs(Value - target) < epsilon && Math.Abs(Velocity) < epsilon * 10;
}

/// <summary>Turns a rate (per second) into whole spawns per frame, carrying the fraction.</summary>
internal struct EmissionDebt
{
    private double _debt;

    public int Take(double rate, double dt)
    {
        if (!(rate > 0) || !(dt > 0))
        {
            _debt = 0;
            return 0;
        }

        _debt += rate * dt;
        var n = (int)Math.Floor(_debt);
        _debt -= n;
        return n;
    }

    public void Reset() => _debt = 0;
}

/// <summary>Colors shared by the energy styles (DESIGN 2a, Conventions).</summary>
internal static class EnergyPalette
{
    public static readonly Rgba WhiteHot = HeatPalette.WhiteHot;
    public static readonly Rgba Gold = HeatPalette.Gold;
    public static readonly Rgba Amber = HeatPalette.LightSparkHot;

    // The conservative light surface of macOS DashboardPalette.legible, so both platforms step identically.
    private static readonly Rgba LightSurface = Rgba.FromHex("#E6E6E6");

    public static Rgba Heat(double heat) => HeatPalette.Sample(heat);

    /// <summary>The label color: dark = heat; light = heat darkened 18 % (OKLab L), stepped to ≥ 4.5:1.</summary>
    public static Rgba Label(double heat, bool dark)
    {
        var color = HeatPalette.Sample(heat);
        if (dark)
        {
            return color;
        }

        var candidate = OkLab.Darken(color, 0.18);
        var lab = OkLab.FromRgb(candidate);
        for (var i = 0; i < 40; i++)
        {
            if (Rgba.ContrastRatio(candidate, LightSurface) >= 4.5)
            {
                return candidate;
            }

            lab = lab with { L = Math.Clamp(lab.L - 0.015, 0, 1) };
            candidate = lab.ToRgb();
        }

        return candidate;
    }

    /// <summary><c>hot</c>: <c>mix(heat(h), #FFF7D6, 0.55 + 0.45 o)</c> dark, <c>mix(label, #FFB020, 0.3)</c> light.</summary>
    public static Rgba Hot(double heat, double overdrive, bool dark) => dark
        ? Mix(HeatPalette.Sample(heat), WhiteHot, 0.55 + 0.45 * overdrive)
        : Mix(Label(heat, false), Amber, 0.3);

    /// <summary>OKLab mix that returns an end color untouched at t ≤ 0 or t ≥ 1 (no round trip).</summary>
    public static Rgba Mix(Rgba a, Rgba b, double t) => t <= 0 ? a : t >= 1 ? b : OkLab.Mix(a, b, t);

    /// <summary>Raises the OKLab lightness by <paramref name="amount"/> (0.25 = the tier flash's +25 %).</summary>
    public static Rgba Brighten(Rgba color, double amount)
    {
        if (!(amount > 0))
        {
            return color;
        }

        var lab = OkLab.FromRgb(color);
        return (lab with { L = Math.Min(1, lab.L * (1 + amount)) }).ToRgb(color.A);
    }

    /// <summary>Rounds a 0..1 value to <paramref name="steps"/> levels (the change keys of the renderers).</summary>
    public static int Bucket(double value, int steps) =>
        (int)Math.Round((double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0) * steps);
}

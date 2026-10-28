namespace AgentSpeed.Presentation.Orb.Styles;

/// <summary>
/// Shared math of the orb styles (DESIGN 2a, Conventions): angles are degrees clockwise from 3 o'clock (screen y down),
/// a dial position <c>n</c> maps to <c>135° + 270° n</c>, lengths are fractions of S from the disc center.
/// </summary>
public static class StyleMath
{
    public const double DialStartDegrees = 135;
    public const double DialSweepDegrees = 270;
    public const double TopDegrees = 270;
    public const double Tau = 2 * Math.PI;
    public const double DegreesPerRadian = 180 / Math.PI;

    /// <summary>The redline-tier threshold on the dial, <c>0.75^0.6</c>.</summary>
    public static readonly double RedlineDial = Math.Pow(0.75, 0.6);

    public static double DialDegrees(double n) => DialStartDegrees + DialSweepDegrees * n;

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        var u = (x - edge0) / (edge1 - edge0);
        u = u <= 0 ? 0 : u >= 1 ? 1 : u;
        return u * u * (3 - 2 * u);
    }

    /// <summary>d/dx of <see cref="SmoothStep"/>.</summary>
    public static double SmoothStepSlope(double edge0, double edge1, double x)
    {
        var u = (x - edge0) / (edge1 - edge0);
        return u is <= 0 or >= 1 ? 0 : 6 * u * (1 - u) / (edge1 - edge0);
    }

    public static double Clamp01(double x) => x <= 0 || double.IsNaN(x) ? 0 : x >= 1 ? 1 : x;

    /// <summary>Advances an accumulated phase (radians) by <c>2π f dt</c>, kept in [0, 2π) (DESIGN 4c resolved detail 3).</summary>
    public static double Advance(double phase, double hertz, double dt)
    {
        phase += Tau * hertz * dt;
        if (phase >= Tau || phase < 0)
        {
            phase %= Tau;
            if (phase < 0)
            {
                phase += Tau;
            }
        }

        return phase;
    }

    /// <summary><c>sin(2π f t)</c> on the shared clock for constant-frequency motion (several orbs stay in step).</summary>
    public static double ClockSine(double time, double hertz)
    {
        var cycles = time * hertz;
        cycles -= Math.Floor(cycles);
        return Math.Sin(Tau * cycles);
    }

    public static (double X, double Y) Polar(double radius, double degrees)
    {
        var radians = degrees / DegreesPerRadian;
        return (radius * Math.Cos(radians), radius * Math.Sin(radians));
    }

    /// <summary>A value in 0..1 quantized to <paramref name="steps"/> buckets (for change keys).</summary>
    public static int Bucket(double value, int steps) => (int)Math.Round(Clamp01(value) * steps);

    public static double Finite(double value, double fallback = 0) => double.IsFinite(value) ? value : fallback;
}

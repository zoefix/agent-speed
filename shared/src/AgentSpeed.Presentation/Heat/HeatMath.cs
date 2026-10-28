using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Heat;

public enum SpeedTier
{
    Idle,
    Cruise,
    Boost,
    Overdrive,
    Redline,
}

/// <summary>Speed to heat mapping (DESIGN section 1).</summary>
public static class HeatMath
{
    /// <summary>The default redline is the slider's maximum (DESIGN 1).</summary>
    public const double DefaultRedline = 2000;

    /// <summary>
    /// The redline of the title table in DESIGN 1 ("at R = 400") and the default before version 2 of the settings,
    /// which migrates once to <see cref="DefaultRedline"/>.
    /// </summary>
    public const double ReferenceRedline = 400;

    /// <summary>The total-throughput default before version 2 of the settings.</summary>
    public const double ReferenceRedlineTotal = 200_000;
    public const double MinRedline = 50;
    public const double MaxRedline = 2000;
    public const double RedlineStep = 50;
    public const double CruiseThreshold = 0.5;
    public const double DefaultRedlineTotal = 2_000_000;
    public const double MinRedlineTotal = 20_000;
    public const double MaxRedlineTotal = 2_000_000;

    public static IReadOnlyList<SpeedTier> ActiveTiers { get; } =
        [SpeedTier.Cruise, SpeedTier.Boost, SpeedTier.Overdrive, SpeedTier.Redline];

    public static double ClampRedline(double redline)
    {
        if (!double.IsFinite(redline))
        {
            return DefaultRedline;
        }

        var stepped = Math.Round(redline / RedlineStep, MidpointRounding.AwayFromZero) * RedlineStep;
        return Math.Clamp(stepped, MinRedline, MaxRedline);
    }

    public static double DefaultRedlineFor(SpeedMetric metric) =>
        metric == SpeedMetric.Total ? DefaultRedlineTotal : DefaultRedline;

    public static double MinRedlineFor(SpeedMetric metric) => metric == SpeedMetric.Total ? MinRedlineTotal : MinRedline;

    public static double MaxRedlineFor(SpeedMetric metric) => metric == SpeedMetric.Total ? MaxRedlineTotal : MaxRedline;

    /// <summary>Each metric has its own redline: output 50–2000 in steps of 50, total 20k–2M (two significant digits).</summary>
    public static double ClampRedline(SpeedMetric metric, double redline) =>
        metric == SpeedMetric.Total ? ClampRedlineTotal(redline) : ClampRedline(redline);

    public static double ClampRedlineTotal(double redline)
    {
        if (!double.IsFinite(redline))
        {
            return DefaultRedlineTotal;
        }

        var clamped = Math.Clamp(redline, MinRedlineTotal, MaxRedlineTotal);
        var step = Math.Pow(10, Math.Floor(Math.Log10(clamped)) - 1);
        var rounded = Math.Round(clamped / step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(rounded, MinRedlineTotal, MaxRedlineTotal);
    }

    public static double Heat(double value, double redline)
    {
        if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(redline) || redline <= 0)
        {
            return 0;
        }

        return Math.Min(1, Math.Pow(value / redline, 0.6));
    }

    public static double Overdrive(double value, double redline)
    {
        if (!double.IsFinite(value) || !double.IsFinite(redline) || redline <= 0)
        {
            return 0;
        }

        return Math.Clamp((value - redline) / redline, 0, 1);
    }

    public static double Threshold(SpeedTier tier, double redline) => tier switch
    {
        SpeedTier.Cruise => CruiseThreshold,
        SpeedTier.Boost => 0.15 * redline,
        SpeedTier.Overdrive => 0.375 * redline,
        SpeedTier.Redline => 0.75 * redline,
        _ => 0,
    };

    public static double HeatAtThreshold(SpeedTier tier, double redline) => Heat(Threshold(tier, redline), redline);

    /// <summary>The highest tier whose entry threshold is reached, without hysteresis.</summary>
    public static SpeedTier RawTier(double value, double redline)
    {
        if (!double.IsFinite(value))
        {
            return SpeedTier.Idle;
        }

        for (var tier = SpeedTier.Redline; tier > SpeedTier.Idle; tier--)
        {
            if (value >= Threshold(tier, redline))
            {
                return tier;
            }
        }

        return SpeedTier.Idle;
    }
}

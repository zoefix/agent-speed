namespace AgentSpeed.Presentation.Hero;

public enum HeroTourScenario
{
    /// <summary>Idle, start-up, every upgrade, redline and overdrive, the downgrades, parking and a second start-up (56 s).</summary>
    Tour,

    /// <summary>A start-up from each kind of parked vehicle (39 s).</summary>
    Startups,

    /// <summary>Nothing running: the parked vehicle, its breathing and the idle light band.</summary>
    Idle,
}

/// <summary>
/// The scripted live values of the hero vehicle's motion prototype (DESIGN 4c), for the <c>--hero-demo</c> development
/// option. Values are for a 400 tok/s redline and scale with the redline, so every title is visited.
/// </summary>
public static class HeroTour
{
    private static readonly (double At, double Value)[] Tour =
    [
        (0, 0), (3, 3), (6, 10), (9.5, 14), (12, 19), (14.5, 27), (17, 37), (19.5, 56), (22, 78), (24.5, 112), (27, 160), (29.5, 235),
        (32, 320), (34.5, 440), (37.5, 700), (41, 230), (44, 26), (46.5, 0), (52, 38), (56, 38),
    ];

    private static readonly (double At, double Value)[] Startups =
    [
        (0, 0), (2, 10), (5, 0), (10.5, 3), (13, 0), (18.5, 450), (22, 0), (27.5, 18), (30.5, 0), (36, 120), (39, 120),
    ];

    public static double Duration(HeroTourScenario scenario) => scenario switch
    {
        HeroTourScenario.Startups => 39,
        HeroTourScenario.Idle => 60,
        _ => 56,
    };

    /// <summary>Value (tok/s) at <paramref name="seconds"/> into the scenario; loops after <see cref="Duration"/>.</summary>
    public static double Value(HeroTourScenario scenario, double seconds, double redline = 400)
    {
        if (scenario == HeroTourScenario.Idle)
        {
            return 0;
        }

        var keys = scenario == HeroTourScenario.Startups ? Startups : Tour;
        var t = double.IsFinite(seconds) && seconds > 0 ? seconds % Duration(scenario) : 0;
        var v = keys[^1].Value;
        for (var i = 0; i < keys.Length - 1; i++)
        {
            if (t < keys[i].At || t >= keys[i + 1].At)
            {
                continue;
            }

            var previous = i > 0 ? keys[i - 1].Value : keys[i].Value;
            var current = keys[i].Value;
            var u = Math.Min(1, (t - keys[i].At) / 0.45);
            var e = u * u * (3 - 2 * u);
            v = previous > 0 && current > 0
                ? Math.Exp(Math.Log(previous) + (Math.Log(current) - Math.Log(previous)) * e)
                : previous + (current - previous) * e;
            break;
        }

        if (v <= 0)
        {
            return 0;
        }

        var noise = 0.035 * Math.Sin(t * 2.1) + 0.025 * Math.Sin(t * 5.3 + 1) + 0.015 * Math.Sin(t * 11.7 + 2);
        var scale = double.IsFinite(redline) && redline > 0 ? redline / 400 : 1;
        return v * (1 + noise) * scale;
    }

    public static bool TryParse(string? text, out HeroTourScenario scenario)
    {
        scenario = HeroTourScenario.Tour;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "tour", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "startups", StringComparison.OrdinalIgnoreCase))
        {
            scenario = HeroTourScenario.Startups;
            return true;
        }

        if (string.Equals(text, "idle", StringComparison.OrdinalIgnoreCase))
        {
            scenario = HeroTourScenario.Idle;
            return true;
        }

        return false;
    }
}

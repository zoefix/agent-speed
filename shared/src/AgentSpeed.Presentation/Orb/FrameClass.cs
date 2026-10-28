namespace AgentSpeed.Presentation.Orb;

/// <summary>How often an orb needs frames (DESIGN 2a, "Frame policy").</summary>
public enum FrameClass
{
    /// <summary>Static: the last frame stays on screen.</summary>
    None,

    /// <summary>About 15 fps.</summary>
    Fps15,

    /// <summary>At most 24 fps (the animated style cards in Settings).</summary>
    Fps24,

    /// <summary>About 30 fps.</summary>
    Fps30,

    /// <summary>About 60 fps (72 on a 144 Hz display), never the display rate above 72.</summary>
    Fps60,
}

/// <summary>Frame divisors on the shared display clock (DESIGN 2a, "Frame classes").</summary>
public static class FramePolicy
{
    /// <summary>With this many orbs in the 60 fps class at once, all but the focused one drop to 30 fps.</summary>
    public const int CrowdedCount = 4;

    /// <summary>
    /// The integer divisor of <paramref name="refreshHz"/> whose rate is closest to the class rate (ties to the higher
    /// rate): 144 Hz → 2 (72 fps) for 60, 5 (28.8 fps) for 30; 120 Hz → 2 / 4; 60 Hz → 1 / 2. Zero for <see cref="FrameClass.None"/>.
    /// </summary>
    public static int Divisor(FrameClass frameClass, double refreshHz)
    {
        var target = frameClass switch
        {
            FrameClass.Fps60 => 60.0,
            FrameClass.Fps30 => 30.0,
            FrameClass.Fps24 => 24.0,
            FrameClass.Fps15 => 15.0,
            _ => 0.0,
        };
        if (target <= 0)
        {
            return 0;
        }

        if (!double.IsFinite(refreshHz) || refreshHz < 1)
        {
            refreshHz = 60;
        }

        if (frameClass == FrameClass.Fps24)
        {
            var divisor = 1;
            while (refreshHz / divisor > 24.5)
            {
                divisor++;
            }

            return divisor;
        }

        var best = 1;
        var bestError = double.MaxValue;
        var maxDivisor = Math.Max(1, (int)Math.Floor(refreshHz));
        for (var divisor = 1; divisor <= maxDivisor; divisor++)
        {
            var rate = refreshHz / divisor;
            var error = Math.Abs(rate - target);
            if (error < bestError - 1e-9)
            {
                best = divisor;
                bestError = error;
            }

            if (rate < target)
            {
                break;
            }
        }

        // The display rate itself is never used above 72 fps.
        while (refreshHz / best > 72.0001)
        {
            best++;
        }

        return best;
    }

    /// <summary>The effective class of one orb when several animate (four or more at 60 fps → 30 fps, except the focused one).</summary>
    public static FrameClass Crowded(FrameClass requested, int sixtyCount, bool focused) =>
        requested == FrameClass.Fps60 && sixtyCount >= CrowdedCount && !focused ? FrameClass.Fps30 : requested;

    public static FrameClass Max(FrameClass a, FrameClass b) => a >= b ? a : b;
}

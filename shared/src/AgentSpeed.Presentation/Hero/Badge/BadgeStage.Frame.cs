using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Hero.Badge;

/// <summary>What one frame of the badge shows (the layers and the transition table of DESIGN 4c, Hero title badge).</summary>
public sealed partial class BadgeStage
{
    public const double RingRadius = 33;
    public const double InnerRingRadius = 30.5;
    public const double PipRingRadius = 37.5;
    public const double PipRadius = 1.05;
    public const double CurrentPipRadius = 1.45;

    /// <summary>The medallion is not drawn inside the title's ink box grown by this much.</summary>
    public const double RingClearance = 2.5;

    /// <summary>The idle light band starts this long into the gauge's idle sweep (DESIGN 4c resolved detail 10).</summary>
    public const double SweepDelay = HeroStage.SweepDelay;

    public const double SweepDuration = 0.9;
    public const double SheenCross = 0.7;

    private double H => HeroMath.Clamp01(Input.Heat);

    private double O => HeroMath.Clamp01(Input.Overdrive);

    /// <summary>Opacity of the idle label: 55 % (dark) / 70 % (light) breathing over 4.8 s; solid in high contrast.</summary>
    public double IdleAlpha => !Decorated ? 1 : (Dark ? 0.55 : 0.7) * (Moving ? 0.86 + 0.14 * Math.Sin(BreathPhase) : 1);

    /// <summary>The titles on screen with their transforms.</summary>
    public BadgeTitleItems Titles
    {
        get
        {
            var steady = new BadgeTitleItems(new BadgeTitleItem(Text, Level, BaseAlpha(Level)), null);
            if (RenameFrom is { } from && Time - RenameStart < RenameDuration)
            {
                var q = HeroMath.Smooth((Time - RenameStart) / RenameDuration);
                var a = BaseAlpha(Level);
                return new BadgeTitleItems(new BadgeTitleItem(from, Level, a * (1 - q)), new BadgeTitleItem(Text, Level, a * q));
            }

            if (Transition is not { } transition || Fx == EffectsIntensity.Off)
            {
                return steady;
            }

            var t = Time - transition.Start;
            if (t >= TransitionLifetime)
            {
                return steady;
            }

            var old = Label(transition.From);
            var kind = transition.Kind;
            if (kind is BadgeTransitionKind.Up or BadgeTransitionKind.Down && string.Equals(old, Text, StringComparison.Ordinal))
            {
                return steady;
            }

            if (Input.Options.ReduceMotion)
            {
                var q = HeroMath.Smooth(t / 0.3);
                return new BadgeTitleItems(new BadgeTitleItem(old, transition.From, BaseAlpha(transition.From) * (1 - q)),
                    new BadgeTitleItem(Text, Level, BaseAlpha(Level) * q));
            }

            switch (kind)
            {
                case BadgeTransitionKind.Up:
                {
                    var t2 = t - 0.08;
                    return new BadgeTitleItems(
                        new BadgeTitleItem(old, transition.From, 1 - HeroMath.Smooth(t / 0.16), 1 + 0.12 * HeroMath.EaseOutCubic(t / 0.22)),
                        new BadgeTitleItem(Text, Level, HeroMath.Smooth(t2 / 0.12), SpringTo1(t2, 0.72, 0.42, 0.6),
                            Boost: t2 > 0 ? 0.8 * Math.Exp(-t2 / 0.25) : 0));
                }

                case BadgeTransitionKind.Down:
                {
                    var t2 = t - 0.12;
                    return new BadgeTitleItems(
                        new BadgeTitleItem(old, transition.From, 1 - HeroMath.Smooth(t / 0.3), Dy: 5 * HeroMath.EaseInOutSine(t / 0.3)),
                        new BadgeTitleItem(Text, Level, HeroMath.Smooth(t2 / 0.35), Dy: -3 * (1 - HeroMath.EaseOutCubic(t2 / 0.35))));
                }

                case BadgeTransitionKind.Startup:
                {
                    var q = HeroMath.Smooth(t / 0.25);
                    return new BadgeTitleItems(new BadgeTitleItem(old, 0, IdleAlpha * (1 - q)),
                        new BadgeTitleItem(Text, Level, q, SpringTo1(t, 0.85, 0.42, 0.6), Boost: 0.5 * Math.Exp(-t / 0.3)));
                }

                default:
                {
                    var q = HeroMath.Smooth(t / 0.4);
                    return new BadgeTitleItems(new BadgeTitleItem(old, transition.From, 1 - q), new BadgeTitleItem(Text, 0, IdleAlpha * q));
                }
            }
        }
    }

    /// <summary>Underdamped spring from <paramref name="from"/> to 1 at time <paramref name="t"/> (from rest).</summary>
    public static double SpringTo1(double t, double from, double response, double damping) =>
        HeroMath.SpringAt(t, from, 1, 0, response, damping);

    /// <summary>How much of the outer ring is drawn, clockwise from 12 o'clock (degrees): it draws on at start-up.</summary>
    public double OuterRingSweep => Transition is { Kind: BadgeTransitionKind.Startup } transition && Moving
        ? 360 * HeroMath.EaseOutCubic((Time - transition.Start) / 0.45)
        : 360;

    /// <summary>Medallion opacity factor: 60 % parked, full awake.</summary>
    public double RingPresence => 0.6 + 0.4 * Wake;

    /// <summary>The level-up shockwave: r 33 → 44 over 0.5 s from <c>τ = 0.06</c>.</summary>
    public BadgeRing? Shockwave
    {
        get
        {
            if (Transition is not { Kind: BadgeTransitionKind.Up } transition || !Moving)
            {
                return null;
            }

            var p = (Time - transition.Start - 0.06) / 0.5;
            if (p is <= 0 or >= 1)
            {
                return null;
            }

            var e = HeroMath.EaseOutCubic(p);
            return new BadgeRing(RingRadius + 11 * e, 1.6 * (1 - p), 0.7 * (1 - e));
        }
    }

    /// <summary>The flash at the center from <c>τ = 0.06</c> (level up; start-up at 60 %).</summary>
    public BadgeFlash? Flash
    {
        get
        {
            if (Transition is not { Kind: BadgeTransitionKind.Up or BadgeTransitionKind.Startup } transition || !Moving || !Decorated)
            {
                return null;
            }

            var p = (Time - transition.Start - 0.06) / 0.35;
            if (p is <= 0 or >= 1)
            {
                return null;
            }

            var soft = Fx == EffectsIntensity.Soft || transition.Kind == BadgeTransitionKind.Startup;
            var alpha = (Dark ? 0.42 : 0.26) * (1 - p) * (1 - p) * Math.Min(1.6, 1 + 0.2 * (transition.Climbed - 1)) * (soft ? 0.6 : 1);
            return new BadgeFlash(8 + 32 * HeroMath.EaseOutCubic(p), alpha);
        }
    }

    /// <summary>Center of rank pip <paramref name="k"/> (1–13): r 37.5 from 216° to 324°, 9° apart.</summary>
    public static PointD PipCenter(int k)
    {
        var angle = (216 + 9 * (k - 1)) * Math.PI / 180;
        return new PointD(50 + PipRingRadius * Math.Cos(angle), 50 + PipRingRadius * Math.Sin(angle));
    }

    /// <summary>Heat of pip <paramref name="k"/>'s threshold, <c>(T_k / R)^0.6</c>.</summary>
    public static double PipHeat(int k, double redline) => SpeedTitles.HeatAt((SpeedTitle)Math.Clamp(k, 1, PipCount), redline);

    /// <summary>How lit pip <paramref name="k"/> is (0–1).</summary>
    public double PipLit(int k) => _pips[k - 1].Lit;

    /// <summary>Radius of lit pip <paramref name="k"/>: the current one is larger, a newly lit one springs in.</summary>
    public double PipRadiusOf(int k)
    {
        ref readonly var pip = ref _pips[k - 1];
        var scale = 1.0;
        if (double.IsFinite(pip.Pop) && Moving && k <= Level)
        {
            var t = Time - pip.Pop;
            scale = t < 0 ? 0 : SpringTo1(t, 0, 0.3, 0.5);
        }

        return (k == Level ? CurrentPipRadius : PipRadius) * Math.Max(scale, 0);
    }

    /// <summary>Orbit motes on screen: <c>2 + 6 h</c> (× 1.5 at Max), capped at 4 / 8 / 12; the last one fades in.</summary>
    public double MoteCount
    {
        get
        {
            if (!Moving || !Decorated || Wake <= 0.01)
            {
                return 0;
            }

            var wanted = 2 + 6 * H;
            return Fx switch
            {
                EffectsIntensity.Soft => Math.Min(4, wanted),
                EffectsIntensity.Standard => Math.Min(8, wanted),
                EffectsIntensity.Max => Math.Min(12, wanted * 1.5),
                _ => 0,
            };
        }
    }

    /// <summary>Opacity of the mote in fill-order slot <paramref name="slot"/> (× wake).</summary>
    public double MoteVisibility(int slot) => HeroMath.Clamp01(MoteCount - slot) * Wake;

    /// <summary>Head angle of the mote in fill-order slot <paramref name="slot"/>, degrees clockwise from 3 o'clock.</summary>
    public double MoteHeadDegrees(int slot) => (MotePhase + MoteOrderTable[slot] / (double)MoteSlots * 2 * Math.PI) * 180 / Math.PI;

    /// <summary>Length of a mote's trail arc: <c>8° + 40° h</c>.</summary>
    public double MoteTrailDegrees => 8 + 40 * H;

    /// <summary>Camera shake of the content at the redline tier (Standard and Max); never the mask.</summary>
    public PointD Shake
    {
        get
        {
            if (Fx < EffectsIntensity.Standard || !Moving || Level == 0 || Input.Tier < SpeedTier.Redline)
            {
                return default;
            }

            var a = (0.2 + 0.5 * O) * (Fx == EffectsIntensity.Max ? 1.4 : 1);
            var t = Time;
            const double tau = 2 * Math.PI;
            return new PointD(
                a * (0.5 * Math.Sin(tau * 23 * t) + 0.3 * Math.Sin(tau * 31 * t + 1) + 0.2 * Math.Sin(tau * 41 * t + 2)),
                a * (0.5 * Math.Sin(tau * 19 * t + 0.5) + 0.3 * Math.Sin(tau * 29 * t + 2) + 0.2 * Math.Sin(tau * 37 * t + 1)));
        }
    }

    /// <summary>The halo's opacity at its center; none with Increase Contrast or in high contrast.</summary>
    public double HaloAlpha
    {
        get
        {
            if (Input.Options.IncreasedContrast || !Decorated || Wake <= 0.01)
            {
                return 0;
            }

            var breathe = Moving ? 1 + 0.1 * Math.Sin(HaloPhase) : 1;
            return (Dark ? 0.10 + 0.22 * H : 0.05 + 0.08 * H) * Wake * breathe;
        }
    }

    /// <summary>The title glow (σ and opacity); <paramref name="boost"/> is a transition's extra glow.</summary>
    public BadgeGlow Glow(double boost)
    {
        if (!Decorated)
        {
            return default;
        }

        var h = H;
        var o = O;
        var sigma = (1.0 + 1.6 * h + 0.6 * o) * (Fx == EffectsIntensity.Max ? 1.2 : 1);
        var strength = Fx == EffectsIntensity.Off
            ? Dark ? 0.30 : 0.20
            : Dark ? 0.30 + 0.45 * h + 0.15 * o : 0.16 + 0.22 * h;
        if (Input.Options.IncreasedContrast)
        {
            strength *= 0.5;
        }

        return new BadgeGlow(sigma, strength * (1 + boost) * Wake);
    }

    /// <summary>Streaks take the accent at the redline (dark).</summary>
    public bool StreakAccent => Dark && H >= 0.84;

    /// <summary>The sheen across a title <paramref name="inkWidth"/> units wide (Standard and Max, <c>h ≥ 0.45</c>), or null.</summary>
    public BadgeBand? Sheen(double inkWidth)
    {
        var h = H;
        if (Fx < EffectsIntensity.Standard || !Moving || !Decorated || h < 0.45 || Level == 0)
        {
            return null;
        }

        var u = SheenPhase % 1 * SheenPeriod(h) / SheenCross;
        if (u >= 1)
        {
            return null;
        }

        var width = inkWidth + 28;
        return new BadgeBand(50 - width / 2 + (-0.2 + 1.4 * u) * width, 18, -0.45, Dark ? 0.4 : 0.5, true);
    }

    /// <summary>The idle light band across the idle label (x −10 → 110 in 0.9 s, 0.63 s into the gauge sweep), or null.</summary>
    public BadgeBand? IdleBand
    {
        get
        {
            if (!Moving || !Decorated || !SweepWindow(0, 0))
            {
                return null;
            }

            var u = SweepProgress() / SweepDuration;
            return new BadgeBand(-10 + 120 * HeroMath.EaseInOutSine(u), 9, 0, Dark ? 0.75 : 0.6, false);
        }
    }

    private double BaseAlpha(int level) => level == 0 ? IdleAlpha : 1;

    /// <summary>Seconds since the idle light band started (it runs 0.9 s); negative before it.</summary>
    private double SweepProgress()
    {
        if (double.IsFinite(Input.SweepOrigin))
        {
            return Time - Input.SweepOrigin - SweepDelay;
        }

        var since = Time - _parkedAt;
        if (!double.IsFinite(since) || since < SweepInterval)
        {
            return double.NegativeInfinity;
        }

        return since % SweepInterval - SweepDelay;
    }

    private bool SweepWindow(double lead, double tail)
    {
        if (Level != 0)
        {
            return false;
        }

        var c = SweepProgress();
        return c >= -lead && c < SweepDuration + tail;
    }
}

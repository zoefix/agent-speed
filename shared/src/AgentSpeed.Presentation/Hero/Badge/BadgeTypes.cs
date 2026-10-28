using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Hero.Badge;

/// <summary>What the title badge reads each frame (DESIGN 4c, Hero title badge).</summary>
/// <param name="Time">Seconds on the orb clock.</param>
/// <param name="Level">0 idle, 1–13 the speed title.</param>
/// <param name="TitleChangedAt">
/// When the shown title changed (the label slide); a transition starts there when that was within the last 0.3 s.
/// </param>
/// <param name="Heat">Smoothed heat <c>h</c>.</param>
/// <param name="Overdrive">Smoothed overdrive <c>o</c>.</param>
/// <param name="SweepOrigin">Start of the gauge's latest idle sweep; NaN keeps the badge's own 6 s cadence.</param>
public readonly record struct BadgeInput(
    double Time,
    int Level,
    double TitleChangedAt,
    double Heat,
    double Overdrive,
    SpeedTier Tier,
    double Redline,
    HeroOptions Options,
    double SweepOrigin = double.NaN);

public enum BadgeTransitionKind
{
    Up,
    Down,

    /// <summary>Idle → title.</summary>
    Startup,

    /// <summary>Title → idle.</summary>
    Park,
}

/// <param name="Start">When the transition began (<c>τ = 0</c>).</param>
/// <param name="Climbed">Titles climbed (at least 1).</param>
public readonly record struct BadgeTransition(int From, int To, double Start, BadgeTransitionKind Kind, int Climbed);

/// <summary>A rank pip: how lit it is and when it pops in (or starts to go dark).</summary>
public struct BadgePip
{
    public double Lit;

    /// <summary>Negative infinity when nothing is pending.</summary>
    public double Pop;
}

/// <summary>A speed streak in canvas units; <see cref="X"/> is its head, the tail trails to the right.</summary>
public struct BadgeStreak
{
    public bool Alive;
    public double X;
    public double Y;
    public double Length;
    public double Age;
    public double Speed;
    public double Alpha;
}

/// <summary>An ember (rising from the lower ring) or a level-up spark, drawn as a streak of velocity × 0.022 s.</summary>
public struct BadgeParticle
{
    public bool Alive;
    public bool Ember;
    public double X;
    public double Y;
    public double VelocityX;
    public double VelocityY;

    /// <summary>Negative while the particle waits to appear.</summary>
    public double Age;

    public double Life;
    public double Drag;
    public double Seed;
}

/// <summary>One title on screen with its transition transform (scale about the block center, then a vertical offset).</summary>
/// <param name="Boost">Extra glow (0.8 = +80 %).</param>
public readonly record struct BadgeTitleItem(string Text, int Level, double Alpha, double Scale = 1, double Dy = 0, double Boost = 0)
{
    public bool IsIdle => Level == 0;
}

/// <summary>The title on screen, and the one leaving during a transition or a rename (drawn first).</summary>
public readonly record struct BadgeTitleItems(BadgeTitleItem First, BadgeTitleItem? Second)
{
    /// <summary>The more visible title: it decides where the medallion is interrupted.</summary>
    public BadgeTitleItem Top => Second is { } second && second.Alpha > First.Alpha ? second : First;
}

/// <summary>A light band across the glyphs: the sheen (diagonal) or the idle light band (vertical).</summary>
/// <param name="X">Center x in canvas units at y = 50.</param>
/// <param name="HalfWidth">Horizontal half width; the band fades linearly to 0 there.</param>
/// <param name="Slope">Horizontal shift per unit of y (dx/dy); −0.45 leans the sheen forward.</param>
/// <param name="Alpha">Peak opacity.</param>
public readonly record struct BadgeBand(double X, double HalfWidth, double Slope, double Alpha, bool Sheen);

/// <summary>The level-up shockwave ring.</summary>
public readonly record struct BadgeRing(double Radius, double Width, double Alpha);

/// <summary>The level-up (or start-up) flash at the center.</summary>
public readonly record struct BadgeFlash(double Radius, double Alpha);

/// <summary>The title glow: blur σ in canvas units and its opacity (may exceed 1 briefly).</summary>
public readonly record struct BadgeGlow(double Sigma, double Strength);

/// <summary>How often the badge needs frames (DESIGN 4c frame policy).</summary>
public enum BadgeFrames
{
    /// <summary>Static: drawn once.</summary>
    None,

    /// <summary>Parked: about 15 fps for the breathing.</summary>
    Parked,

    /// <summary>The idle light band crosses the glyphs: 30 fps.</summary>
    Sweep,

    /// <summary>Moving, a transition or particles: every display frame (up to the hero's moving rate).</summary>
    Full,
}

public static class BadgeFrameRates
{
    public const double Parked = 15;
    public const double Sweep = 30;

    /// <summary>What the badge asks of the orb's frame driver.</summary>
    public static FrameDemand ToFrameDemand(BadgeFrames frames) => frames switch
    {
        BadgeFrames.None => FrameDemand.None,
        BadgeFrames.Parked => FrameDemand.Medium,
        _ => FrameDemand.Full,
    };
}

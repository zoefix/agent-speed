namespace AgentSpeed.Presentation.Heat;

/// <summary>The visible speed label (DESIGN section 1, "Speed titles"); effect tiers stay separate.</summary>
public enum SpeedTitle
{
    Idle,
    Walk,
    Bicycle,
    EBike,
    Motorcycle,
    Car,
    SportsCar,
    BulletTrain,
    RaceCar,
    Maglev,
    Jetliner,
    FighterJet,
    Hypersonic,
    Rocket,
}

/// <param name="Fraction">Entry threshold as a fraction of the redline; null for walk, which starts at an absolute 0.5.</param>
public readonly record struct SpeedTitleStep(SpeedTitle Title, string Key, double? Fraction)
{
    public double Threshold(double redline) =>
        Fraction is { } fraction ? fraction * redline : HeatMath.CruiseThreshold;
}

/// <summary>The 13-step vehicle ladder. Thresholds scale with the redline, so every metric reads the same.</summary>
public static class SpeedTitles
{
    public const string IdleKey = "tier.idle";

    /// <summary>Every title from walk to rocket, in climbing order.</summary>
    public static IReadOnlyList<SpeedTitleStep> Ladder { get; } =
    [
        new(SpeedTitle.Walk, "title.walk", null),
        new(SpeedTitle.Bicycle, "title.bicycle", 0.02),
        new(SpeedTitle.EBike, "title.ebike", 0.03),
        new(SpeedTitle.Motorcycle, "title.motorcycle", 0.04),
        new(SpeedTitle.Car, "title.car", 0.06),
        new(SpeedTitle.SportsCar, "title.sportsCar", 0.08),
        new(SpeedTitle.BulletTrain, "title.bulletTrain", 0.12),
        new(SpeedTitle.RaceCar, "title.raceCar", 0.17),
        new(SpeedTitle.Maglev, "title.maglev", 0.24),
        new(SpeedTitle.Jetliner, "title.jetliner", 0.34),
        new(SpeedTitle.FighterJet, "title.fighterJet", 0.5),
        new(SpeedTitle.Hypersonic, "title.hypersonic", 0.7),
        new(SpeedTitle.Rocket, "title.rocket", 1.0),
    ];

    public static string Key(SpeedTitle title) =>
        title == SpeedTitle.Idle ? IdleKey : Ladder[Index(title)].Key;

    /// <summary>Entry threshold in tok/s for <paramref name="redline"/>; 0 for idle.</summary>
    public static double Threshold(SpeedTitle title, double redline) =>
        title == SpeedTitle.Idle ? 0 : Ladder[Index(title)].Threshold(redline);

    /// <summary>
    /// The rungs whose thresholds scale with the redline (bicycle to rocket): the settings ladder lists these, since
    /// walking starts at a fixed 0.5 tok/s.
    /// </summary>
    public static IReadOnlyList<SpeedTitleStep> Scaled { get; } = Ladder.Where(step => step.Fraction is not null).ToArray();

    /// <summary>
    /// Heat (0..1) at a title's threshold, <c>(t / R)^0.6</c>: where its tick sits on the heat strip. For the scaled
    /// rungs it does not depend on the redline, so the ticks stay put while the values move.
    /// </summary>
    public static double HeatAt(SpeedTitle title, double redline) => HeatMath.Heat(Threshold(title, redline), redline);

    /// <summary>The highest title whose threshold is reached, without hysteresis.</summary>
    public static SpeedTitle Raw(double value, double redline)
    {
        if (!double.IsFinite(value))
        {
            return SpeedTitle.Idle;
        }

        for (var i = Ladder.Count - 1; i >= 0; i--)
        {
            if (value >= Ladder[i].Threshold(redline))
            {
                return Ladder[i].Title;
            }
        }

        return SpeedTitle.Idle;
    }

    private static int Index(SpeedTitle title)
    {
        var index = (int)title - 1;
        if ((uint)index >= (uint)Ladder.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(title), title, null);
        }

        return index;
    }
}

/// <summary>Speed title with the same hysteresis, hold and idle rules as the effect tiers.</summary>
public sealed class TitleTracker
{
    private readonly LadderTracker _ladder;

    public TitleTracker(double holdSeconds = TierTracker.DefaultHoldSeconds, double idleSeconds = TierTracker.DefaultIdleSeconds)
    {
        _ladder = new LadderTracker((int)SpeedTitle.Rocket, (level, redline) => SpeedTitles.Threshold((SpeedTitle)level, redline),
            holdSeconds, idleSeconds);
    }

    public SpeedTitle Displayed => (SpeedTitle)_ladder.Displayed;

    /// <summary>Title after hysteresis, before the hold delay.</summary>
    public SpeedTitle Candidate => (SpeedTitle)_ladder.Candidate;

    public double? NextDeadline => _ladder.NextDeadline;

    public void Reset(SpeedTitle title = SpeedTitle.Idle) => _ladder.Reset((int)title);

    /// <returns>True when <see cref="Displayed"/> changed.</returns>
    public bool Update(double value, double redline, double nowSeconds) => _ladder.Update(value, redline, nowSeconds);
}

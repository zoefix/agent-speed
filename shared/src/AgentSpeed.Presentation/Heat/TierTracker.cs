namespace AgentSpeed.Presentation.Heat;

/// <summary>
/// Effect tier with hysteresis: a tier is entered at its threshold and left at 90 % of it, must hold
/// <see cref="HoldSeconds"/> to take effect, and idle needs the value below the cruise threshold for
/// <see cref="IdleSeconds"/>.
/// </summary>
public sealed class TierTracker
{
    public const double DefaultHoldSeconds = 0.8;
    public const double DefaultIdleSeconds = 3;
    public const double ExitRatio = LadderTracker.ExitRatio;

    private readonly LadderTracker _ladder;

    public TierTracker(double holdSeconds = DefaultHoldSeconds, double idleSeconds = DefaultIdleSeconds)
    {
        _ladder = new LadderTracker((int)SpeedTier.Redline, (level, redline) => HeatMath.Threshold((SpeedTier)level, redline),
            holdSeconds, idleSeconds);
    }

    public double HoldSeconds => _ladder.HoldSeconds;

    public double IdleSeconds => _ladder.IdleSeconds;

    public SpeedTier Displayed => (SpeedTier)_ladder.Displayed;

    /// <summary>Tier after hysteresis, before the hold delay.</summary>
    public SpeedTier Candidate => (SpeedTier)_ladder.Candidate;

    /// <summary>Earliest time at which <see cref="Update"/> could change the displayed tier without new data.</summary>
    public double? NextDeadline => _ladder.NextDeadline;

    public void Reset(SpeedTier tier = SpeedTier.Idle) => _ladder.Reset((int)tier);

    /// <returns>True when <see cref="Displayed"/> changed.</returns>
    public bool Update(double value, double redline, double nowSeconds) => _ladder.Update(value, redline, nowSeconds);
}

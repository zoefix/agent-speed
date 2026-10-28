namespace AgentSpeed.Presentation.Heat;

/// <summary>
/// Hysteresis over an ordered ladder of levels (DESIGN section 1): level 0 is idle, level 1 starts at
/// <see cref="HeatMath.CruiseThreshold"/>, and every level above it at its own threshold. A level is entered at its
/// threshold and left at <see cref="ExitRatio"/> of it, must hold <see cref="HoldSeconds"/> to be shown, and idle
/// needs the value below level 1 for <see cref="IdleSeconds"/>.
/// </summary>
public sealed class LadderTracker
{
    public const double ExitRatio = 0.9;

    private readonly Func<int, double, double> _threshold;
    private double? _belowFirstSince;
    private int? _pending;
    private double _pendingSince;

    /// <param name="top">The highest level.</param>
    /// <param name="threshold">Entry threshold of a level (1..<paramref name="top"/>) for a redline.</param>
    public LadderTracker(int top, Func<int, double, double> threshold, double holdSeconds, double idleSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        ArgumentNullException.ThrowIfNull(threshold);
        Top = top;
        _threshold = threshold;
        HoldSeconds = holdSeconds;
        IdleSeconds = idleSeconds;
    }

    public int Top { get; }

    public double HoldSeconds { get; }

    public double IdleSeconds { get; }

    public int Displayed { get; private set; }

    /// <summary>Level after hysteresis, before the hold delay.</summary>
    public int Candidate { get; private set; }

    /// <summary>Earliest time at which <see cref="Update"/> could change the displayed level without new data.</summary>
    public double? NextDeadline
    {
        get
        {
            double? deadline = null;
            if (_pending is { } pending)
            {
                deadline = _pendingSince + HoldFor(pending);
            }

            if (_belowFirstSince is { } since && Candidate != 0)
            {
                var idleAt = since + IdleSeconds;
                deadline = deadline is null ? idleAt : Math.Min(deadline.Value, idleAt);
            }

            return deadline;
        }
    }

    public void Reset(int level = 0)
    {
        Displayed = Math.Clamp(level, 0, Top);
        Candidate = Displayed;
        _pending = null;
        _belowFirstSince = null;
    }

    /// <summary>The highest level whose entry threshold is reached, without hysteresis.</summary>
    public int Raw(double value, double redline)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        for (var level = Top; level > 0; level--)
        {
            if (value >= _threshold(level, redline))
            {
                return level;
            }
        }

        return 0;
    }

    /// <returns>True when <see cref="Displayed"/> changed.</returns>
    public bool Update(double value, double redline, double nowSeconds)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            value = 0;
        }

        if (value < HeatMath.CruiseThreshold)
        {
            _belowFirstSince ??= nowSeconds;
        }
        else
        {
            _belowFirstSince = null;
        }

        Candidate = ComputeCandidate(value, redline, nowSeconds);

        if (Candidate == Displayed)
        {
            _pending = null;
            return false;
        }

        if (_pending != Candidate)
        {
            _pending = Candidate;
            _pendingSince = nowSeconds;
        }

        if (nowSeconds - _pendingSince + 1e-9 >= HoldFor(Candidate))
        {
            Displayed = Candidate;
            _pending = null;
            return true;
        }

        return false;
    }

    private double HoldFor(int level) => level == 0 ? 0 : HoldSeconds;

    private int ComputeCandidate(double value, double redline, double nowSeconds)
    {
        if (value < HeatMath.CruiseThreshold)
        {
            var idle = _belowFirstSince is { } since && nowSeconds - since + 1e-9 >= IdleSeconds;
            return idle || Candidate == 0 ? 0 : 1;
        }

        var raw = Raw(value, redline);
        if (raw >= Candidate)
        {
            return raw;
        }

        for (var level = Candidate; level > 1; level--)
        {
            if (value >= _threshold(level, redline) * ExitRatio)
            {
                return level;
            }
        }

        return 1;
    }
}

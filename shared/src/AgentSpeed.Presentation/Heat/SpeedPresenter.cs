using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Heat;

/// <summary>
/// Turns hub snapshots into the value, the effect tier and the speed title every surface shows. Time is injected for
/// testability.
/// </summary>
public sealed class SpeedPresenter
{
    private readonly TierTracker _tracker;
    private readonly TitleTracker _titles;

    public SpeedPresenter(TierTracker? tracker = null, TitleTracker? titles = null)
    {
        _tracker = tracker ?? new TierTracker();
        _titles = titles ?? new TitleTracker();
    }

    public SpeedMetric Metric { get; private set; } = SpeedMetric.Output;

    public double Redline { get; private set; } = HeatMath.DefaultRedline;

    public SpeedSnapshot Snapshot { get; private set; } = SpeedSnapshot.Empty;

    public double Value => Sanitize(Snapshot.Live.ValueFor(Metric));

    /// <summary>Effect tier: drives the effects, never shown as text.</summary>
    public SpeedTier Tier => _tracker.Displayed;

    /// <summary>The visible label.</summary>
    public SpeedTitle Title => _titles.Displayed;

    public double Heat => HeatMath.Heat(Value, Redline);

    public double Overdrive => HeatMath.Overdrive(Value, Redline);

    /// <summary>The earlier of the tier's and the title's deadlines.</summary>
    public double? NextDeadline => (_tracker.NextDeadline, _titles.NextDeadline) switch
    {
        ({ } tier, { } title) => Math.Min(tier, title),
        (var tier, var title) => tier ?? title,
    };

    /// <returns>True when the displayed tier or title changed.</returns>
    public bool Update(SpeedSnapshot snapshot, double nowSeconds)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        return Advance(nowSeconds);
    }

    public bool Configure(SpeedMetric metric, double redline, double nowSeconds)
    {
        Metric = metric;
        Redline = HeatMath.ClampRedline(metric, redline);
        return Advance(nowSeconds);
    }

    public bool Tick(double nowSeconds) => Advance(nowSeconds);

    public double ValueFor(Rate rate) => Sanitize(rate.ValueFor(Metric));

    private bool Advance(double nowSeconds)
    {
        var tier = _tracker.Update(Value, Redline, nowSeconds);
        var title = _titles.Update(Value, Redline, nowSeconds);
        return tier || title;
    }

    private static double Sanitize(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}

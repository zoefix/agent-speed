using AgentSpeed.Core;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// One floating orb's view of the data (METRICS 6.8, DESIGN 2b Engine): the live value of its sources in the selected
/// metric, its own effect tier and speed title (hysteresis and hold) with its own redline, its agents, and its own
/// <c>newPeak_A</c>. An all-sources orb gets exactly the all-sources values and uses the engine's <c>NewPeak</c> event.
/// </summary>
public sealed class OrbFeed
{
    private readonly TierTracker _tier = new();
    private readonly TitleTracker _title = new();
    private readonly SubsetPeakTracker _peak = new();
    private bool _configured;
    private long _lastNowMs;

    public OrbFeed(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    public string Id { get; }

    /// <summary>The tracked sources; null for all sources.</summary>
    public SourceSet? Sources { get; private set; }

    public SpeedMetric Metric { get; private set; } = SpeedMetric.Output;

    public double Redline { get; private set; } = HeatMath.DefaultRedline;

    public SubsetReading Reading { get; private set; } = OrbSubset.Reading(SpeedSnapshot.Empty, null);

    public double Value { get; private set; }

    /// <summary>tok/s or the money unit (DESIGN 9); heat, tiers and titles always follow tokens.</summary>
    public OrbUnit Unit { get; set; } = OrbUnit.Tokens;

    /// <summary>The money readout while <see cref="Unit"/> is money and a <see cref="CostDisplay"/> is attached; else null.</summary>
    public LiveMoney? Money { get; private set; }

    /// <summary>Formats the money unit (the orb's sources without subscription sources unless included, METRICS 9.4).</summary>
    public CostDisplay? Cost { get; set; }

    /// <summary>The effects preview has no prices: a money orb shows the demo's output at this nominal price (µUSD per token).</summary>
    public const double PreviewMicroUsdPerToken = 15;

    /// <summary>The effects preview drives the snapshots (see <see cref="PreviewMicroUsdPerToken"/>).</summary>
    public bool Previewing { get; set; }

    public SpeedTier Tier => _tier.Displayed;

    public SpeedTitle Title => _title.Displayed;

    public bool IsUnavailable => Reading.State != SubsetState.Available;

    /// <summary>Today's peak output of the orb's sources (METRICS 6.8 <c>peakToday_A</c>).</summary>
    public double PeakToday => Sources is null ? Reading.MeasuredPeakOutput ?? 0 : _peak.PeakTodayOutput;

    public double? NextDeadline => (_tier.NextDeadline, _title.NextDeadline) switch
    {
        ({ } a, { } b) => Math.Min(a, b),
        (var a, var b) => a ?? b,
    };

    /// <summary>
    /// Sets the sources, metric and redline. A change of the source set while running restarts the orb's peak and
    /// silences its celebration for 60 s (METRICS 6.8); the first configuration (app start) does not.
    /// </summary>
    public void Configure(SourceSet? sources, SpeedMetric metric, double redline, long nowMs, bool created = false)
    {
        var changed = _configured && sources != Sources;
        Sources = sources;
        Metric = metric;
        Redline = HeatMath.ClampRedline(metric, redline);
        if (changed || created)
        {
            _peak.Restart(nowMs);
        }

        _configured = true;
    }

    /// <summary>Applies a snapshot; returns true when this orb's own <c>newPeak_A</c> fires (subset orbs only).</summary>
    public bool Update(SpeedSnapshot snapshot, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Reading = OrbSubset.Reading(snapshot, Sources);
        Money = Unit == OrbUnit.Cost && Cost is { } cost
            ? cost.Live(Previewing ? PreviewReading(Reading.Live.Output) : OrbSubset.Cost(snapshot, Sources, cost.Billable), Reading.Agents.Count)
            : null;
        var value = Reading.Live.ValueFor(Metric);
        Value = double.IsFinite(value) && value > 0 ? value : 0;
        _tier.Update(Value, Redline, nowSeconds);
        _title.Update(Value, Redline, nowSeconds);

        if (Sources is null || snapshot.Timestamp == DateTimeOffset.MinValue)
        {
            return false;
        }

        var nowMs = snapshot.Timestamp.ToUnixTimeMilliseconds();
        if (nowMs <= _lastNowMs)
        {
            return false;
        }

        _lastNowMs = nowMs;
        var dayStart = snapshot.DayStart == DateTimeOffset.MinValue ? 0 : snapshot.DayStart.ToUnixTimeMilliseconds();
        return _peak.Update(Reading.Live.Output, Reading.MeasuredPeakOutput, dayStart, nowMs);
    }

    private static CostReading PreviewReading(double output) => CostReading.Zero with
    {
        MicroUsdPerSecond = output * PreviewMicroUsdPerToken,
        LiveOutput = output,
    };

    /// <summary>Advances the hold timers without new data; true when the tier or title changed.</summary>
    public bool Tick(double nowSeconds)
    {
        var tier = _tier.Update(Value, Redline, nowSeconds);
        var title = _title.Update(Value, Redline, nowSeconds);
        return tier || title;
    }

    /// <summary>Whether an engine event belongs to this orb (completions of its sources; the all-sources new peak).</summary>
    public bool Accepts(SpeedEvent speedEvent) => OrbSubset.Accepts(speedEvent, Sources);
}

using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Boards;

/// <summary>What a board shows after one engine tick (DESIGN 2c, Content).</summary>
/// <param name="Value">The board's live value <c>live_A</c> in the selected metric.</param>
/// <param name="Groups">The groups in their stable ranking, first place first.</param>
/// <param name="Events">The passes of this tick (empty on ticks that only update values).</param>
/// <param name="Generating">Agents of the board's sources in phase generating (<c>agents.context</c>).</param>
/// <param name="Tools">Agents in phase tools.</param>
/// <param name="TodayOutput">Today's output of the board's sources (<c>boards.today</c>).</param>
/// <param name="PeakToday">Today's peak of the board's sources (<c>peakToday_A</c>, <c>boards.peak</c>).</param>
/// <param name="NewPeak">A new <c>peakToday_A</c> on this tick (the gold sheen, <c>boards.newPeak</c> and the ring).</param>
public sealed record BoardData(
    double Value,
    SpeedTier Tier,
    SpeedTitle Title,
    double Redline,
    IReadOnlyList<BoardGroup> Groups,
    IReadOnlyList<BoardRankEvent> Events,
    int Generating,
    int Tools,
    long TodayOutput,
    double PeakToday,
    bool NewPeak)
{
    public static BoardData Empty { get; } = new(0, SpeedTier.Idle, SpeedTitle.Idle, HeatMath.DefaultRedline, [], [], 0, 0, 0, 0, false);

    /// <summary>The live intelligence sums of the board's sources (METRICS 6.9 over <c>A</c>).</summary>
    public Core.Intelligence.IntelligenceSums Intelligence { get; init; }

    /// <summary>The board's 性能 (METRICS 6.9 over <c>A</c>) with its supplying agent and N.</summary>
    public PerfSetReading Score { get; init; } = PerfSetReading.None;

    /// <summary>The live cost of the board's billable sources (METRICS 9.5, 9.4).</summary>
    public CostReading Cost { get; init; } = CostReading.Zero;

    public double Heat => HeatMath.Heat(Value, Redline);

    public double Overdrive => HeatMath.Overdrive(Value, Redline);
}

/// <summary>
/// One board's view of the data (METRICS 6.8 for its sources, DESIGN 2c): the hero's value, tier, title and peak come from
/// an <see cref="OrbFeed"/> of the same sources; the agents are grouped by root and ranked by the stable ranking once per
/// engine tick. The live value's history is kept per second for the hero (60 s sparkline, 30 bars, LED bar).
/// </summary>
public sealed class BoardFeed
{
    /// <summary>Seconds of history the hero keeps (the glass sparkline shows 60).</summary>
    public const int HistorySeconds = 120;

    /// <summary>Snapshots closer than this to the previous ranking tick only update values (engine ticks are 500 ms apart).</summary>
    public const double MinTickSeconds = 0.4;

    private readonly OrbFeed _feed;
    private readonly BoardRanking _ranking = new();
    private readonly double[] _history = new double[HistorySeconds];
    private long _historySecond = long.MinValue;
    private double _lastTick = double.NegativeInfinity;
    private IReadOnlyList<string> _order = [];

    public BoardFeed(string id)
    {
        _feed = new OrbFeed(id);
    }

    public string Id => _feed.Id;

    public SourceSet? Sources => _feed.Sources;

    public SpeedMetric Metric => _feed.Metric;

    public SubsetReading Reading => _feed.Reading;

    public BoardData Data { get; private set; } = BoardData.Empty;

    /// <summary>When the tier or title hold ends without new data (the window asks again then).</summary>
    public double? NextDeadline => _feed.NextDeadline;

    public void Configure(SourceSet? sources, SpeedMetric metric, double redline, long nowMs, bool created = false)
    {
        if (sources != _feed.Sources || metric != _feed.Metric)
        {
            _ranking.Reset();
            _order = [];
        }

        _feed.Configure(sources, metric, redline, nowMs, created);
    }

    /// <summary>
    /// Applies a snapshot: the hero values every time, the ranking at most once per engine tick. A subset board's own new
    /// peak fires here; an all-sources board's comes from the engine's <see cref="SpeedEvent.NewPeak"/> (<see cref="Accepts"/>).
    /// Costs count only the <paramref name="billable"/> sources (every source when null; METRICS 9.4).
    /// </summary>
    public BoardData Update(SpeedSnapshot snapshot, double nowSeconds, SourceSet? billable = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var newPeak = _feed.Update(snapshot, nowSeconds);
        var reading = _feed.Reading;
        var counted = billable ?? SourceSet.Everything;
        var groups = BoardGrouping.Group(reading.Agents, _feed.Metric);
        IReadOnlyList<BoardRankEvent> events = [];
        if (nowSeconds - _lastTick >= MinTickSeconds || _order.Count == 0 && groups.Count > 0)
        {
            _lastTick = nowSeconds;
            var result = _ranking.Tick(groups.Select(g => g.RankInput).ToArray());
            _order = result.Order;
            events = result.Events;
        }

        Record(snapshot.Timestamp, _feed.Value);
        var generating = 0;
        var tools = 0;
        foreach (var agent in reading.Agents)
        {
            generating += agent.Phase == AgentPhase.Generating ? 1 : 0;
            tools += agent.Phase == AgentPhase.Tools ? 1 : 0;
        }

        Data = new BoardData(_feed.Value, _feed.Tier, _feed.Title, _feed.Redline, Arrange(groups, _order), events, generating, tools,
            reading.Today.Output, _feed.PeakToday, newPeak)
        {
            Intelligence = reading.Intelligence,
            Score = Highest(reading.Agents),
            Cost = OrbSubset.Cost(snapshot, _feed.Sources, counted),
        };
        return Data;
    }

    /// <summary>The all-sources new peak (engine event) for a board tracking every source.</summary>
    public bool Accepts(SpeedEvent speedEvent) => _feed.Accepts(speedEvent);

    /// <summary>Advances the tier and title holds without new data; true when either changed.</summary>
    public bool Tick(double nowSeconds)
    {
        if (!_feed.Tick(nowSeconds))
        {
            return false;
        }

        Data = Data with { Tier = _feed.Tier, Title = _feed.Title, Events = [], NewPeak = false };
        return true;
    }

    /// <summary>The live value of the last seconds, oldest first, filling <paramref name="destination"/> (0 where nothing was recorded).</summary>
    public void CopyHistory(Span<double> destination)
    {
        var count = Math.Min(destination.Length, HistorySeconds);
        for (var i = 0; i < count; i++)
        {
            var second = _historySecond - (count - 1 - i);
            destination[destination.Length - count + i] = _historySecond == long.MinValue ? 0 : _history[Slot(second)];
        }
    }

    /// <summary>
    /// The highest performance score among <paramref name="agents"/> (METRICS 6.9, <i>Sets and groups</i>: live before resting, then the
    /// supplying agent's latest line and smallest id), read over the same agents for its supplier and N.
    /// </summary>
    public static PerfSetReading Highest(IReadOnlyList<AgentStatus> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var value = Core.Perf.PerfSetValue.None;
        foreach (var agent in agents)
        {
            value = Core.Perf.PerfSetValue.Merge(value, agent.Intelligence.Score);
        }

        return PerfSetReading.Of(value, agents);
    }

    /// <summary>The groups in the ranking's order; groups the ranking has not seen yet (between ticks) follow by value.</summary>
    internal static IReadOnlyList<BoardGroup> Arrange(IReadOnlyList<BoardGroup> groups, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(order.Count, StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        return groups
            .OrderBy(g => rank.TryGetValue(g.Id, out var r) ? r : int.MaxValue)
            .ThenByDescending(g => g.Live)
            .ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private void Record(DateTimeOffset timestamp, double value)
    {
        if (timestamp == DateTimeOffset.MinValue)
        {
            return;
        }

        var second = timestamp.ToUnixTimeMilliseconds() / 1000;
        if (_historySecond == long.MinValue || second - _historySecond >= HistorySeconds || second < _historySecond)
        {
            Array.Clear(_history);
        }
        else
        {
            for (var s = _historySecond + 1; s < second; s++)
            {
                _history[Slot(s)] = 0;
            }
        }

        _historySecond = second;
        _history[Slot(second)] = value;
    }

    private static int Slot(long second) => (int)(((second % HistorySeconds) + HistorySeconds) % HistorySeconds);
}

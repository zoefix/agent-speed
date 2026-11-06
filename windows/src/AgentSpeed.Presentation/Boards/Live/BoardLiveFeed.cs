using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Settings.Boards;

namespace AgentSpeed.Presentation.Boards.Live;

/// <summary>
/// One live board's ticks (DESIGN 2c Content): the engine snapshot filtered to the board's sources (METRICS 6.8) and
/// ranked by <see cref="BoardFeed"/>, laid out for the board's style and size with the expanded group, and worded by
/// <see cref="BoardTexts"/>. A relayout (style, size, language or expansion changes) re-plans the last data without
/// repeating its passes or its new peak.
/// </summary>
public sealed class BoardLiveFeed
{
    private readonly BoardFeed _feed;
    private readonly BoardThinkingHold _thinking = new();
    private BoardDefinition _board;
    private SpeedSnapshot _snapshot = SpeedSnapshot.Empty;
    private bool _hasData;

    public BoardLiveFeed(BoardDefinition board)
    {
        ArgumentNullException.ThrowIfNull(board);
        _board = board;
        _feed = new BoardFeed(board.Id);
    }

    public string Id => _feed.Id;

    public BoardDefinition Board => _board;

    public BoardFeed Feed => _feed;

    public BoardData Data => _feed.Data;

    /// <summary>The last tick made, without its one-off passes and new peak.</summary>
    public BoardTick? Last { get; private set; }

    /// <summary>When the tier or title hold ends without new data.</summary>
    public double? NextDeadline => _feed.NextDeadline;

    /// <param name="created">The board was just added or its sources changed: no new peak is celebrated for 60 s.</param>
    public void Configure(BoardDefinition board, SpeedMetric metric, double redline, long nowMs, bool created = false)
    {
        ArgumentNullException.ThrowIfNull(board);
        _board = board;
        _feed.Configure(board.TrackedSources, metric, redline, nowMs, created, board.TrackedHosts);
    }

    /// <summary>Applies an engine snapshot: the hero every time, the ranking at most once per engine tick.</summary>
    public BoardTick Update(SpeedSnapshot snapshot, double nowSeconds, BoardTexts texts, string? expanded = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(texts);
        _snapshot = snapshot;
        _hasData = true;
        var data = _feed.Update(snapshot, nowSeconds, texts.Billable);
        return Remember(Build(data, texts, expanded, nowSeconds));
    }

    /// <summary>The last data laid out again (null before the first snapshot); no passes, no new peak.</summary>
    public BoardTick? Relayout(BoardTexts texts, string? expanded = null) =>
        _hasData ? Remember(Build(_feed.Data with { Events = [], NewPeak = false }, texts, expanded, null)) : null;

    /// <summary>Advances the tier and title holds without new data; the new tick when either changed.</summary>
    public BoardTick? Tick(double nowSeconds, BoardTexts texts, string? expanded = null) =>
        _feed.Tick(nowSeconds) ? Relayout(texts, expanded) : null;

    /// <summary>The engine's all-sources new peak for a board tracking every source: the last tick once more, with its new peak.</summary>
    public BoardTick? EnginePeak(SpeedEvent speedEvent) =>
        speedEvent is SpeedEvent.NewPeak && _feed.Sources is null && _feed.Accepts(speedEvent) && Last is { } last
            ? last with { NewPeak = true }
            : null;

    private BoardTick Remember(BoardTick tick)
    {
        Last = tick.Events.Count == 0 && !tick.NewPeak ? tick : tick with { Events = [], NewPeak = false };
        return tick;
    }

    /// <param name="time">The tick's time for the thinking hold; null re-lays out the last data (the hold only notes a switch change).</param>
    private BoardTick Build(BoardData data, BoardTexts texts, string? expanded, double? time)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var board = _board;
        var metric = _feed.Metric;
        var readouts = board.Readouts;
        var open = expanded is not null && data.Groups.Any(g => string.Equals(g.Id, expanded, StringComparison.Ordinal)) ? expanded : null;
        var entries = BoardReadoutRows.Word(BoardLayoutPlan.Arrange(data.Groups, BoardSizes.Limits(board.Size),
            BoardMetrics.For(board.Style, board.Size, readouts), metric, open), readouts, texts);
        var gauge = texts.Hero(data.Intelligence, data.Score, data.Cost);
        if (time is { } now)
        {
            _thinking.Update(readouts, gauge, now);
        }
        else if (!readouts.Thinking || !(readouts.Intelligence || readouts.Cost))
        {
            _thinking.Reset();
        }

        var history = new double[BoardTick.HistorySeconds];
        _feed.CopyHistory(history);
        // Every selected host offline (DESIGN 13): the hero says so in place of its title, and its value rests at a dash.
        var offline = _feed.Reading.State == SubsetState.HostsOffline;
        return new BoardTick(data.Value, offline ? "–" : SpeedFormat.Speed(data.Value), data.Tier, data.Redline, metric, data.Generating > 0,
            entries, data.Events, data.NewPeak)
        {
            Title = offline ? texts.Localizer["remote.orb.offline"] : texts.Title(data.Title),
            GeneratingCount = data.Generating,
            ToolsCount = data.Tools,
            History = history,
            PeakToday = data.PeakToday,
            TodayOutput = data.TodayOutput,
            TodayText = texts.Today(data.TodayOutput),
            CostText = texts.TodayCost(board.TrackedHosts is { } hosts ? OrbSubset.Restricted(_snapshot, hosts) : _snapshot, board.TrackedSources),
            PeakText = texts.Peak(data.PeakToday),
            Expanded = open,
            Readouts = readouts,
            Gauge = gauge,
            ThinkingHidden = _thinking.Hidden,
        };
    }
}

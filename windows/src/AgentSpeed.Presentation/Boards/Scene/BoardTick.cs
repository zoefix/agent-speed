using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>
/// What one engine tick tells a board's scene (DESIGN 2c): the hero's live value and its formatted text, heat, tier and
/// redline, whether anything generates, the rows of the layout plan at their resting places, the passes of the tick and a
/// new <c>peakToday_A</c>. The live feed (<see cref="BoardFeed"/>) and the demo (<see cref="Demo.BoardDemoFeed"/>) both make it.
/// </summary>
public sealed record BoardTick(
    double Value,
    string ValueText,
    SpeedTier Tier,
    double Redline,
    SpeedMetric Metric,
    bool Generating,
    IReadOnlyList<BoardEntry> Entries,
    IReadOnlyList<BoardRankEvent> Events,
    bool NewPeak)
{
    /// <summary>Seconds of <see cref="History"/> a tick carries (the glass sparkline's 60 s).</summary>
    public const int HistorySeconds = 60;

    /// <summary>The speed title in the General title theme (DESIGN 1), as the hero draws it.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Agents of the board's sources in phase generating and in tools (<c>agents.context</c>).</summary>
    public int GeneratingCount { get; init; }

    public int ToolsCount { get; init; }

    /// <summary><c>live_A</c> once per second, oldest first, at most <see cref="HistorySeconds"/> (0 where nothing was recorded).</summary>
    public IReadOnlyList<double> History { get; init; } = [];

    /// <summary>Today's peak of the board's sources (<c>peakToday_A</c>, neon's LED bar marks it).</summary>
    public double PeakToday { get; init; }

    /// <summary>Today's output of the board's sources (tokens).</summary>
    public long TodayOutput { get; init; }

    /// <summary>Footer texts, already formatted: <c>boards.today</c>, today's cost (null when unknown, DESIGN 9) and <c>boards.peak</c>.</summary>
    public string TodayText { get; init; } = string.Empty;

    public string? CostText { get; init; }

    public string PeakText { get; init; } = string.Empty;

    /// <summary>The group whose sub-agents are expanded (DESIGN 2c, Layout per size), null when none is.</summary>
    public string? Expanded { get; init; }

    /// <summary>The board's live metrics switches (DESIGN 2c, Live metrics); the entries were laid out for them.</summary>
    public BoardReadouts Readouts { get; init; } = BoardReadouts.None;

    /// <summary>The hero's 性能, 思考 and 費用 as shown.</summary>
    public BoardHeroReadouts Gauge { get; init; } = BoardHeroReadouts.Empty;

    /// <summary>思考 has read 0 for 3 s under a live value while another hero readout shows: it is hidden (DESIGN 2c).</summary>
    public bool ThinkingHidden { get; init; }

    public double Heat => HeatMath.Heat(Value, Redline);

    public double Overdrive => HeatMath.Overdrive(Value, Redline);

    /// <summary>The value a row shows: the group speed, a sub-agent's live value, or a more line's sum.</summary>
    public double LiveOf(BoardEntry entry) => entry.Kind switch
    {
        BoardEntryKind.Group => entry.Group.Live,
        BoardEntryKind.Sub => entry.Agent?.Live.ValueFor(Metric) ?? 0,
        _ => entry.RestLive,
    };
}

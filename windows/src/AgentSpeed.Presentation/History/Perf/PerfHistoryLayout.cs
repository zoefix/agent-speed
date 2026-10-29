namespace AgentSpeed.Presentation.History.Perf;

/// <summary>The section's three layouts, by its content width (DESIGN 12, <i>Usage History: performance</i>, Widths).</summary>
public enum PerfHistoryWidth
{
    /// <summary>Below 600: the table becomes a list of rows.</summary>
    Narrow,

    /// <summary>600–860 (the window's minimum lands here): the seven component columns become one 構成 column.</summary>
    Medium,

    /// <summary>From 860: every column.</summary>
    Wide,
}

/// <summary>Sizes of the 性能表現 section (DESIGN 12; the prototype where DESIGN gives no value).</summary>
public static class PerfHistoryLayout
{
    public const double WideFrom = 860;
    public const double MediumFrom = 600;

    public const double TitleSize = 15;
    public const double InfoWidth = 340;
    public const double TrendTitleSize = 12;
    public const double LegendSize = 11;

    /// <summary>The chart's height at each width.</summary>
    public static double ChartHeight(PerfHistoryWidth width) => width switch
    {
        PerfHistoryWidth.Wide => 150,
        PerfHistoryWidth.Medium => 132,
        _ => 120,
    };

    public const double AxisSize = 9.5;
    public const double LineThickness = 2;
    public const double Dot = 4.5;
    public const double LatestDot = 6;
    public const double DotRing = 1.5;

    /// <summary>The area under the line: the source color from this opacity at the line down to 0.</summary>
    public const double AreaOpacity = 0.12;

    /// <summary>The usual line: 1 DIP, dashed 4 · 3, the source color at 60 %.</summary>
    public const double UsualOpacity = 0.6;

    /// <summary>The 1000 line: the label color at 35 %, 1 DIP.</summary>
    public const double ReferenceOpacity = 0.35;

    /// <summary>A single-day range: that day's slot in the label color at 5 %.</summary>
    public const double DayTint = 0.05;

    public const double RowHeight = 30;
    public const double ModelSize = 12.5;
    public const double SourceSize = 11;
    public const double ScoreSize = 13;
    public const double CellSize = 12;
    public const double MeterWidth = 36;
    public const double MeterHeight = 4;
    public const double SourceDot = 7;

    /// <summary>The medium layout's 構成 column: seven bars, 5 wide, 2 apart, 20 tall.</summary>
    public const double PartsWidth = 64;

    public const double PartWidth = 5;
    public const double PartGap = 2;
    public const double PartHeight = 20;

    public const double FootnoteSize = 11;

    public static PerfHistoryWidth WidthOf(double contentWidth) =>
        contentWidth >= WideFrom ? PerfHistoryWidth.Wide : contentWidth >= MediumFrom ? PerfHistoryWidth.Medium : PerfHistoryWidth.Narrow;
}

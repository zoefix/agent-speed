namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>
/// The numbers of one style's sub-gauge at one size (DESIGN 2c, Live metrics: Sub-gauge, glass / timing / neon), in pt from
/// the sub-gauge's top. Baselines were rounded to 0.5 pt from the prototype's macOS fonts; Windows keeps them (Segoe UI
/// Variable and Cascadia Mono sit within half a point of them at these sizes).
/// </summary>
/// <param name="Nominal">The sub-gauge's nominal width (timing: the data box's).</param>
/// <param name="LabelSize">性能's label (glass, neon) or the tab labels (timing).</param>
/// <param name="LabelTracking">Letter spacing of 性能's label (timing: of the tab labels), in em.</param>
/// <param name="MainSize">性能's number.</param>
/// <param name="TableSize">The numbers of 思考 and 費用.</param>
/// <param name="TableLabelSize">The table's labels (glass, neon).</param>
/// <param name="UnitSize">The table's units.</param>
/// <param name="LabelBaseline">性能's label (glass, neon).</param>
/// <param name="MainBaseline">性能's number (glass, neon; timing: the 性能 row's).</param>
/// <param name="TableTop">The table's first row with 性能 above it (glass: 3 under the dial box; neon: the gap under the meter).</param>
/// <param name="RowBaseline">A table row's baseline under its top (timing: under the top of a row with its separator).</param>
/// <param name="RowPitch">From one table row to the next (glass and neon: 15 tall and the gap; timing: the small row height).</param>
/// <param name="MainHeight">Glass: the dial box (D × 0.742 D); neon: the meter's bottom; timing: the 性能 row.</param>
/// <param name="LabelPad">Glass and neon: after the label cell (S 3, else 4); timing: the tab's horizontal padding.</param>
/// <param name="UnitGap">Before the unit (S 2, else 3).</param>
/// <param name="RightPad">Timing: the values' right padding.</param>
/// <param name="Dial">Glass: the dial's D; neon: the meter's width.</param>
/// <param name="MeterTop">Neon: the meter's top.</param>
public sealed record BoardGaugeSpec(
    double Nominal,
    double LabelSize,
    double LabelTracking,
    double MainSize,
    double TableSize,
    double TableLabelSize,
    double UnitSize,
    double LabelBaseline,
    double MainBaseline,
    double TableTop,
    double RowBaseline,
    double RowPitch,
    double MainHeight,
    double LabelPad,
    double UnitGap,
    double RightPad,
    double Dial,
    double MeterTop)
{
    /// <summary>Glass and neon table rows are 15 tall.</summary>
    public const double RowHeight = 15;

    /// <summary>Neon's meter: 12 segments, 3 tall, 2 apart.</summary>
    public const int Segments = 12;

    public const double SegmentHeight = 3;

    public const double SegmentGap = 2;

    /// <summary>The space after <c>≥</c> in its reserved cell.</summary>
    public const double AtLeastGap = 1;

    /// <summary><c>≥</c> at 0.85 × the number's size (the orb's 60 % would fall under 7 pt here).</summary>
    public const double AtLeastScale = 0.85;

    /// <summary>The subscription tag at 0.8 × the number's size.</summary>
    public const double TagScale = 0.8;

    /// <summary>Glass's arc: radius 0.439 D, stroke 0.0455 D, tip 0.035 D, 240° from 150° (lower left) clockwise.</summary>
    public double ArcRadius => 0.439 * Dial;

    public double ArcStroke => 0.0455 * Dial;

    public double TipRadius => 0.035 * Dial;

    public const double ArcStart = 150;

    public const double ArcSweep = 240;

    /// <summary>The range the arc and the meter span: the performance score, 0 to 100 (METRICS 6.9).</summary>
    public const double IndexFloor = 0;

    public const double IndexSpan = 100;

    /// <summary>Timing's 性能 row height and the small rows' (with their 1 pt separator).</summary>
    public double TimingMainRow => MainHeight;

    public static BoardGaugeSpec For(BoardStyle style, BoardSize size)
    {
        var i = size switch
        {
            BoardSize.S => 0,
            BoardSize.L => 2,
            _ => 1,
        };

        return style switch
        {
            BoardStyle.Timing => Timing[i],
            BoardStyle.Neon => Neon[i],
            _ => Glass[i],
        };
    }

    // Glass: dial D 54 / 66 / 74 in a 40 / 49 / 55 box, the table 3 under it.
    private static readonly BoardGaugeSpec[] Glass =
    [
        new(92, 7.5, 0.1, 16, 9.5, 7.5, 7.5, 19, 35.5, 43, 8, 17, 40, 3, 2, 0, 54, 0),
        new(112, 8.5, 0.1, 19, 11, 8.5, 8.5, 22, 42.5, 52, 10, 17, 49, 4, 3, 0, 66, 0),
        new(128, 8.5, 0.1, 21, 12, 9, 9, 24, 46.5, 58, 11, 17, 55, 4, 3, 0, 74, 0),
    ];

    // Timing's data box: rows 21 / 24 / 27 and 15 / 17 / 19; baselines 15.5, 32, 47 / 17.5, 36.5, 53.5 / 20, 41, 60.
    private static readonly BoardGaugeSpec[] Timing =
    [
        new(110, 8, 0, 15, 10.5, 8, 7, 0, 15.5, 21, 11, 15, 21, 4, 2, 5, 0, 0),
        new(134, 9, 0.06, 17, 11.5, 9, 8, 0, 17.5, 24, 12.5, 17, 24, 6, 3, 7, 0, 0),
        new(152, 9, 0.06, 19, 12.5, 9, 8, 0, 20, 27, 14, 19, 27, 6, 3, 7, 0, 0),
    ];

    // Neon: label baseline 6 / 7 / 7, digits 28.5 / 32.5 / 35.5, meter 62 / 74 / 84 wide at 36.5 / 41 / 45.5, gap 3 / 6 / 6,
    // rows 2 / 3 / 3 apart.
    private static readonly BoardGaugeSpec[] Neon =
    [
        new(92, 7.5, 0.18, 21, 9.5, 7.5, 7.5, 6, 28.5, 42.5, 8, 17, 39.5, 3, 2, 0, 62, 36.5),
        new(112, 8.5, 0.24, 24, 10.5, 8.5, 8, 7, 32.5, 50, 9, 18, 44, 4, 3, 0, 74, 41),
        new(128, 8.5, 0.24, 28, 11.5, 8.5, 8, 7, 35.5, 54.5, 10, 18, 48.5, 4, 3, 0, 84, 45.5),
    ];
}

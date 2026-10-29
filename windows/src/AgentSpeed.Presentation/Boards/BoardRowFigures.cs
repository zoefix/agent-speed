namespace AgentSpeed.Presentation.Boards;

/// <summary>
/// The figures of a two-line group row (DESIGN 2c, Ranking rows: The second line): the 性能 cell and the value leave line 1,
/// 2 pt larger than in a one-line row, and share one baseline that puts the cap-height centre of their digits on the row's
/// centre line (19 under the top at M, 20 at L) — the mean of their two cap heights, so each lies within a quarter of their
/// difference of it; timing's interval is centred on its own baseline at its own size. The platform measures the fonts:
/// <see cref="Intelligence"/> and <see cref="Value"/> are the widest figures at the new sizes (<c>8,888</c>; the widest value
/// METRICS 7 writes), which the cells widen to fit, and the caps are the digits' cap heights.
/// </summary>
/// <param name="Intelligence">The width of <see cref="IntelligenceSample"/> in a two-line group row's 性能 font.</param>
/// <param name="Value">The widest of <see cref="ValueSamples"/> in a two-line group row's value font.</param>
/// <param name="IntelligenceCap">The cap height of that 性能 font.</param>
/// <param name="ValueCap">The cap height of that value font.</param>
/// <param name="IntervalCap">The cap height of timing's interval font.</param>
public readonly record struct BoardRowFigures(double Intelligence, double Value, double IntelligenceCap, double ValueCap, double IntervalCap)
{
    /// <summary>A two-line group row's figures are this much larger than a one-line row's.</summary>
    public const double Growth = 2;

    /// <summary>The 性能 cell fits this: the widest compact score (DESIGN 12, <i>Number formats</i>).</summary>
    public const string IntelligenceSample = Perf.PerfNumber.WidestCompact;

    /// <summary>Timing's interval digits (<c>.gap</c>), unchanged in size.</summary>
    public const double IntervalSize = 10.5;

    /// <summary>Before the platform has measured (the placeholder painter, tests): no widening; cap heights at 0.7 em.</summary>
    public const double NominalCapShare = 0.7;

    /// <summary>The values METRICS 7 writes for a row, at their widest: three digits and the compact forms with a suffix.</summary>
    public static IReadOnlyList<string> ValueSamples { get; } = ["888", "8.88k", "88.8k", "888k"];

    public static BoardRowFigures Nominal { get; } = new(0, 0, NominalCapShare * BoardRowColumns.IntelligenceSize(BoardStyle.Glass, false, true),
        NominalCapShare * Scene.BoardValueGlyphs.Size(BoardStyle.Glass, false, true), NominalCapShare * IntervalSize);

    /// <summary>The row's centre line under its top: half the two-line row's height.</summary>
    public static double Centre(double rowHeight) => rowHeight / 2;

    /// <summary>
    /// The figures' shared baseline under the row's top: the value's cap centred on the centre line, or, with the 性能 cell, the
    /// mean of both caps.
    /// </summary>
    public double Baseline(double rowHeight, bool intelligence) =>
        Centre(rowHeight) + (intelligence ? (IntelligenceCap + ValueCap) / 2 : ValueCap) / 2;

    /// <summary>Timing's interval: its own cap centred on the centre line (on the shared baseline it would sit off it).</summary>
    public double IntervalBaseline(double rowHeight) => Centre(rowHeight) + IntervalCap / 2;

    /// <summary>How far a figure of <paramref name="cap"/> on the shared baseline has its cap centre from the centre line.</summary>
    public double Offset(double rowHeight, bool intelligence, double cap) => Baseline(rowHeight, intelligence) - cap / 2 - Centre(rowHeight);

    /// <summary>
    /// A figure's width as the scene composes it from glyphs: the advances of its characters and <paramref name="tracking"/>
    /// between them.
    /// </summary>
    public static double Width(string text, Func<char, double> advance, double tracking)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(advance);
        var width = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            width += advance(text[i]) + (i > 0 ? tracking : 0);
        }

        return width;
    }

    /// <summary>The widest of <see cref="ValueSamples"/>.</summary>
    public static double WidestValue(Func<char, double> advance, double tracking) => ValueSamples.Max(s => Width(s, advance, tracking));
}

namespace AgentSpeed.Presentation.Boards;

/// <summary>
/// The right-hand columns of a ranking row, measured from the row's right end in pt (DESIGN 2c, Ranking rows): the value, the
/// 性能 cell while the column shows, timing's interval (none at S), and where the capsule column ends. With 性能 off the rows
/// take the columns of the boards without metrics (value 46; timing's interval 38). At M and L, whose group rows centre their
/// figures 2 pt larger (<see cref="BoardRowFigures"/>), the 性能 cell and the value widen to fit the measured widest figures;
/// the name block gives way.
/// </summary>
/// <param name="Padding">The row's own right padding (glass and neon 6, timing 4).</param>
/// <param name="Gap">Between columns (glass and neon 8, timing 7).</param>
/// <param name="Value">The value column's width.</param>
/// <param name="Intelligence">The 性能 cell's width; 0 without the column.</param>
/// <param name="Interval">Timing's interval column; 0 for the other styles and at S.</param>
public readonly record struct BoardRowColumns(double Padding, double Gap, double Value, double Intelligence, double Interval)
{
    /// <summary>The value's right edge from the row's right end.</summary>
    public double ValueRight => Padding;

    /// <summary>The 性能 cell's right edge from the row's right end.</summary>
    public double IntelligenceRight => Padding + Value + Gap;

    /// <summary>Timing's interval's right edge from the row's right end.</summary>
    public double IntervalRight => IntelligenceRight + (Intelligence > 0 ? Intelligence + Gap : 0);

    /// <summary>The capsule column's right edge from the row's right end.</summary>
    public double CapsuleRight => IntervalRight + (Interval > 0 ? Interval + Gap : 0);

    /// <summary>The first figure column's left edge from the row's right end (timing's interval, the 性能 cell or the value).</summary>
    public double FiguresLeft => CapsuleRight - Gap;

    /// <summary>
    /// Where a two-line group row's detail ends in a row <paramref name="width"/> wide: under the capsule column up to the first
    /// figure column's left edge, since the figures, centred on the row, hold their columns on both lines (DESIGN 2c, Ranking
    /// rows: The second line).
    /// </summary>
    public double DetailRight(double width) => width - FiguresLeft;

    /// <summary>The 性能 cell's number size: 11 on group rows (a two-line row's 13); sub rows glass 10.5, timing and neon 10.</summary>
    public static double IntelligenceSize(BoardStyle style, bool sub, bool twoLine = false) =>
        sub ? style == BoardStyle.Glass ? 10.5 : 10 : 11 + (twoLine ? BoardRowFigures.Growth : 0);

    /// <param name="figures">The measured figures; the cells widen to fit them at M and L (null: the table's widths).</param>
    public static BoardRowColumns For(BoardStyle style, BoardSize size, BoardReadouts readouts, BoardRowFigures? figures = null)
    {
        var columns = Table(style, size, readouts);
        if (size == BoardSize.S || figures is not { } f)
        {
            return columns;
        }

        return columns with
        {
            Value = Math.Max(columns.Value, Math.Ceiling(f.Value)),
            Intelligence = columns.Intelligence > 0 ? Math.Max(columns.Intelligence, Math.Ceiling(f.Intelligence)) : 0,
        };
    }

    private static BoardRowColumns Table(BoardStyle style, BoardSize size, BoardReadouts readouts)
    {
        var i = size switch
        {
            BoardSize.S => 0,
            BoardSize.L => 2,
            _ => 1,
        };

        var column = readouts.Intelligence;
        if (style == BoardStyle.Timing)
        {
            // The plain tower: position, stripe, name, capsule, interval 38, value 46 (S: value 44, no interval).
            return column
                ? new BoardRowColumns(4, 7, size == BoardSize.S ? 40 : 42, Cell(style, i), size == BoardSize.S ? 0 : 34)
                : new BoardRowColumns(4, 7, size == BoardSize.S ? 44 : 46, 0, size == BoardSize.S ? 0 : 38);
        }

        return column ? new BoardRowColumns(6, 8, 44, Cell(style, i), 0) : new BoardRowColumns(6, 8, 46, 0, 0);
    }

    /// <summary>
    /// The 性能 cell: as wide as <c>8,888</c>, the widest compact score — one-line rows glass 33, timing and neon 34; the two-line
    /// group rows of M and L glass 39, timing and neon 40 (DESIGN 2c, Ranking rows). Measured figures widen it further.
    /// </summary>
    private static double Cell(BoardStyle style, int size)
    {
        var mono = style != BoardStyle.Glass;
        return size == 0 ? (mono ? 34 : 33) : (mono ? 40 : 39);
    }
}

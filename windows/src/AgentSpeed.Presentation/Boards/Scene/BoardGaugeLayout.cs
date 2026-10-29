namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>
/// The widths of the sub-gauge's fixed table cells (DESIGN 2c, Table cells), measured when the language, currency, font or
/// switches change, never per value: the widest switched-on label, <c>≥</c> at 0.85 × the number's size, the number cell (as
/// wide as <c>0.00088</c>, or the wider value held for 6 s) and the widest unit. Timing uses the label (its tab) and number
/// widths of the small rows and its own unit.
/// </summary>
public readonly record struct BoardGaugeCells(double Label, double AtLeast, double Number, double Unit);

/// <summary>
/// Where the sub-gauge's parts lie at one moment, in hero coordinates (pt from the hero's top-left corner): its box, the
/// glass dial or the neon meter, 性能's label and number, and each table row's baseline (timing: its row box). Thinking hidden
/// at zero glides every part between the stack with 思考 and the one without it.
/// </summary>
public readonly record struct BoardGaugePlacement
{
    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    /// <summary>The centre line every glass and neon part is centred on.</summary>
    public double CenterX => Left + Width / 2;

    /// <summary>Glass: the arc's centre (the dial box's centre); neon: unused.</summary>
    public double DialCenterY { get; init; }

    public double MainLabelBaseline { get; init; }

    public double MainBaseline { get; init; }

    /// <summary>Neon: the meter's top.</summary>
    public double MeterTop { get; init; }

    public double ThinkingBaseline { get; init; }

    public double CostBaseline { get; init; }

    /// <summary>The 思考 row's opacity (it fades in place while it hides).</summary>
    public double ThinkingAlpha { get; init; }

    /// <summary>Glass and neon: the table's left edge, where its labels start.</summary>
    public double TableLeft { get; init; }

    /// <summary>Where <c>≥</c> starts.</summary>
    public double AtLeastLeft { get; init; }

    /// <summary>The numbers' right edge (timing: of the small rows; 性能's ends a unit gap further left).</summary>
    public double NumberRight { get; init; }

    /// <summary>Where the units start.</summary>
    public double UnitLeft { get; init; }

    /// <summary>Timing: the tab column's width.</summary>
    public double TabWidth { get; init; }

    /// <summary>Timing: each row's top and height (a row below another starts with its 1 pt separator).</summary>
    public double MainTop { get; init; }

    public double MainRow { get; init; }

    public double ThinkingTop { get; init; }

    public double CostTop { get; init; }

    public double SmallRow { get; init; }
}

/// <summary>
/// The sub-gauge's layout (DESIGN 2c, Live metrics: Hero, Sub-gauge per style, Folding, Thinking hidden at zero): right-aligned at
/// the hero's side inset, centred vertically in the band (neon: the band less its bottom 17), as wide as its nominal width or
/// its widest line, growing to the left. Switched-off readouts take no room and the rest closes up and re-centres.
/// </summary>
public static class BoardGaugeLayout
{
    /// <summary>Neon centres the sub-gauge in the band less the LED bar's 17 pt.</summary>
    public const double NeonBarRoom = 17;

    /// <summary>The table's width (glass, neon) from its cells.</summary>
    public static double TableWidth(BoardGaugeSpec spec, BoardGaugeCells cells)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return cells.Label + spec.LabelPad + cells.AtLeast + BoardGaugeSpec.AtLeastGap + cells.Number + spec.UnitGap + cells.Unit;
    }

    /// <summary>
    /// Timing's data box width: the tab (widest label and its padding), then the value column — its left padding, <c>≥</c>, the
    /// number, the unit gap, the unit and the right padding — at least the nominal width.
    /// </summary>
    public static double TimingWidth(BoardGaugeSpec spec, BoardGaugeCells cells)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var tab = cells.Label + 2 * spec.LabelPad;
        var values = spec.LabelPad + cells.AtLeast + BoardGaugeSpec.AtLeastGap + cells.Number + spec.UnitGap + cells.Unit + spec.RightPad;
        return Math.Max(spec.Nominal, tab + values);
    }

    /// <summary>The sub-gauge's width: its nominal width or its widest line, whichever is more.</summary>
    public static double Width(BoardStyle style, BoardGaugeSpec spec, BoardGaugeCells cells, BoardReadouts readouts)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (style == BoardStyle.Timing)
        {
            return TimingWidth(spec, cells);
        }

        var table = readouts.Thinking || readouts.Cost ? TableWidth(spec, cells) : 0;
        return Math.Max(spec.Nominal, table);
    }

    /// <summary>
    /// The sub-gauge's height with 思考 shown or hidden (Folding; Thinking hidden at zero: glass 17 shorter, neon 17 / 18 / 18,
    /// timing 15 / 17 / 19). Only 性能: the dial (glass), the digits and meter (neon) or the 性能 row (timing).
    /// </summary>
    public static double Height(BoardStyle style, BoardGaugeSpec spec, BoardReadouts readouts, bool thinkingShown)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var rows = (readouts.Thinking && thinkingShown ? 1 : 0) + (readouts.Cost ? 1 : 0);
        if (style == BoardStyle.Timing)
        {
            return (readouts.Intelligence ? spec.MainHeight : 0) + rows * spec.RowPitch;
        }

        if (rows == 0)
        {
            return readouts.Intelligence ? spec.MainHeight : 0;
        }

        var table = BoardGaugeSpec.RowHeight + (rows - 1) * spec.RowPitch;
        return readouts.Intelligence ? spec.TableTop + table : table;
    }

    /// <summary>
    /// The placement at <paramref name="glide"/> (0 with 思考 shown, 1 hidden; eased) and with 思考 at
    /// <paramref name="thinkingAlpha"/>, in a hero <paramref name="heroWidth"/> × <paramref name="band"/> pt.
    /// </summary>
    public static BoardGaugePlacement Place(BoardStyle style, BoardSize size, BoardReadouts readouts, BoardGaugeCells cells, double heroWidth,
        double band, double glide, double thinkingAlpha)
    {
        var spec = BoardGaugeSpec.For(style, size);
        var width = Width(style, spec, cells, readouts);
        var room = style == BoardStyle.Neon ? band - NeonBarRoom : band;
        var shown = Height(style, spec, readouts, true);
        var hidden = Height(style, spec, readouts, false);
        var g = readouts.Thinking ? Math.Clamp(glide, 0, 1) : 0;
        var height = shown + (hidden - shown) * g;
        var top = (room - height) / 2;
        var left = heroWidth - width;
        var placement = new BoardGaugePlacement
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            ThinkingAlpha = readouts.Thinking ? Math.Clamp(thinkingAlpha, 0, 1) : 0,
        };

        return style == BoardStyle.Timing ? Timing(placement, spec, readouts, cells, g) : Table(placement, style, spec, readouts, cells, g);
    }

    // Glass and neon: 性能 (dial or digits and meter) on top, then the table; 費用 slides up into 思考's place as it hides.
    private static BoardGaugePlacement Table(BoardGaugePlacement p, BoardStyle style, BoardGaugeSpec spec, BoardReadouts readouts,
        BoardGaugeCells cells, double glide)
    {
        var tableTop = p.Top + (readouts.Intelligence ? spec.TableTop : 0);
        var thinking = tableTop + spec.RowBaseline;
        var costShown = thinking + (readouts.Thinking ? spec.RowPitch : 0);
        var cost = costShown + (thinking - costShown) * glide;
        var table = TableWidth(spec, cells);
        var left = p.CenterX - table / 2;
        var atLeast = left + cells.Label + spec.LabelPad;
        var numberRight = atLeast + cells.AtLeast + BoardGaugeSpec.AtLeastGap + cells.Number;
        return p with
        {
            DialCenterY = p.Top + spec.Dial / 2,
            MainLabelBaseline = p.Top + spec.LabelBaseline,
            MainBaseline = p.Top + spec.MainBaseline,
            MeterTop = p.Top + (style == BoardStyle.Neon ? spec.MeterTop : 0),
            ThinkingBaseline = thinking,
            CostBaseline = cost,
            TableLeft = left,
            AtLeastLeft = atLeast,
            NumberRight = numberRight,
            UnitLeft = numberRight + spec.UnitGap,
        };
    }

    // Timing: rows from the box's top, 性能 first; 思考 leaves the box and 費用 takes its place.
    private static BoardGaugePlacement Timing(BoardGaugePlacement p, BoardGaugeSpec spec, BoardReadouts readouts, BoardGaugeCells cells,
        double glide)
    {
        var main = readouts.Intelligence ? spec.MainHeight : 0;
        var thinkingTop = p.Top + main;
        var costShown = thinkingTop + (readouts.Thinking ? spec.RowPitch : 0);
        var costTop = costShown + (thinkingTop - costShown) * glide;
        // A first small row has no separator: its number box centres in the whole row, half a point higher.
        double Baseline(double rowTop) => rowTop + spec.RowBaseline - (Math.Abs(rowTop - p.Top) < 0.01 ? 0.5 : 0);
        var right = p.Left + p.Width - spec.RightPad;
        var numberRight = right - cells.Unit - spec.UnitGap;
        return p with
        {
            TabWidth = cells.Label + 2 * spec.LabelPad,
            MainTop = p.Top,
            MainRow = main,
            MainBaseline = p.Top + spec.MainBaseline,
            ThinkingTop = thinkingTop,
            CostTop = costTop,
            SmallRow = spec.RowPitch,
            ThinkingBaseline = Baseline(thinkingTop),
            CostBaseline = Baseline(costTop),
            NumberRight = numberRight,
            AtLeastLeft = numberRight - cells.Number - BoardGaugeSpec.AtLeastGap - cells.AtLeast,
            UnitLeft = right - cells.Unit,
        };
    }
}

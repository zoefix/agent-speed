namespace AgentSpeed.Presentation.Boards;

/// <summary>
/// The geometry of one style at one size in pt/DIP (DESIGN 2c; the numbers of <c>spec/prototypes/board/board-*.html</c>,
/// normative where 2c gives none): the rounded square, the hero, the ranking list and its row heights, and the footer.
/// Coordinates are from the board's top-left corner.
/// </summary>
/// <param name="Side">The square's side.</param>
/// <param name="Radius">Corner radius of the rounded square.</param>
/// <param name="Pad">Inset of the hero and the footer.</param>
/// <param name="HeroHeight">Height of the hero block, which starts at <c>Pad - 2</c> (timing <c>Pad + 2</c>).</param>
/// <param name="ValueSize">Font size of the hero number.</param>
/// <param name="ListTop">Top of the ranking list.</param>
/// <param name="ListInset">Left and right inset of the ranking list.</param>
/// <param name="ListBottom">Distance of the list's bottom from the square's bottom.</param>
/// <param name="Room">Height the layout plan may fill (from the prototypes' target sizes, never a measured one).</param>
/// <param name="StartY">Offset of the first row inside the list.</param>
/// <param name="GroupHeight">Height a group row takes, its gap included.</param>
/// <param name="SubHeight">Height of a sub row.</param>
/// <param name="MoreHeight">Height of a <c>boards.moreSubs</c> or <c>boards.moreGroups</c> line.</param>
/// <param name="FooterTop">Top of the footer line.</param>
/// <param name="FooterHeight">Height of the footer line.</param>
public sealed record BoardMetrics(
    double Side,
    double Radius,
    double Pad,
    double HeroHeight,
    double ValueSize,
    double ListTop,
    double ListInset,
    double ListBottom,
    double Room,
    double StartY,
    double GroupHeight,
    double SubHeight,
    double MoreHeight,
    double FooterTop,
    double FooterHeight)
{
    /// <summary>The list's frame inside the board (rows are clipped to it).</summary>
    public double ListLeft => ListInset;

    public double ListWidth => Side - 2 * ListInset;

    public double ListHeight => Side - ListTop - ListBottom;

    /// <summary>Top of the hero block.</summary>
    public double HeroTop { get; init; }

    /// <summary>The live metrics switches these numbers are for (DESIGN 2c, Live metrics).</summary>
    public BoardReadouts Readouts { get; init; } = BoardReadouts.None;

    /// <summary>The hero is layout B: its right side is the sub-gauge (glass and timing draw their history as a strip).</summary>
    public bool Gauge => Readouts.Hero;

    /// <summary>The rows show the 性能 column and the captions line above the ranking.</summary>
    public bool IntelligenceColumn => Readouts.Intelligence;

    /// <summary>Group rows have a second line with the model (at M and L, whatever the switches; never at S).</summary>
    public bool TwoLineRows => Size != BoardSize.S;

    /// <summary>The board's size.</summary>
    public BoardSize Size { get; init; } = BoardSize.M;

    /// <summary>The captions line at the top of the ranking (12 tall, timing 14); 0 without the 性能 column.</summary>
    public double CaptionsHeight { get; init; }

    /// <summary>The history strip under a layout B hero (glass, timing), in board coordinates; empty otherwise.</summary>
    public double StripTop { get; init; }

    public double StripHeight { get; init; }

    /// <summary>The geometry without the live metrics (every switch off): the hero as before them, two-line group rows at M and L.</summary>
    public static BoardMetrics For(BoardStyle style, BoardSize size) => For(style, size, BoardReadouts.None);

    /// <summary>
    /// The geometry with the live metrics switched as <paramref name="readouts"/> (DESIGN 2c, Live metrics: Hero, Ranking rows):
    /// a layout B hero moves glass's and timing's history to a strip under it and makes neon's hero taller; the 性能 column adds
    /// the captions line above the first row; at M and L the second line makes group rows 8 taller (glass and neon then leave
    /// out their 2 pt between group rows).
    /// </summary>
    public static BoardMetrics For(BoardStyle style, BoardSize size, BoardReadouts readouts)
    {
        var plain = Plain(style, size) with { Size = size, Readouts = readouts };
        var i = size switch
        {
            BoardSize.S => 0,
            BoardSize.L => 2,
            _ => 1,
        };

        var row = new[] { 30.0, 30, 32 }[i];
        var group = size != BoardSize.S ? row + 8 : plain.GroupHeight;
        if (!readouts.Hero)
        {
            // The original hero; at M and L the rows' second line still makes group rows taller.
            return plain with { GroupHeight = group };
        }

        var side = plain.Side;
        double heroTop, hero, listTop, listBottom, stripTop = 0, strip = 0;
        switch (style)
        {
            case BoardStyle.Timing:
                heroTop = new[] { 14.0, 16, 18 }[i];
                hero = new[] { 72.0, 80, 88 }[i];
                stripTop = heroTop + hero + 8;
                strip = new[] { 10.0, 12, 12 }[i];
                listTop = stripTop + strip + 5;
                listBottom = 32;
                break;
            case BoardStyle.Neon:
                heroTop = new[] { 12.0, 14, 16 }[i];
                hero = new[] { 92.0, 100, 108 }[i];
                listTop = heroTop + hero + 4;
                listBottom = 36;
                break;
            default:
                heroTop = new[] { 12.0, 14, 16 }[i];
                hero = new[] { 78.0, 86, 94 }[i];
                stripTop = heroTop + hero;
                strip = new[] { 14.0, 16, 18 }[i];
                listTop = stripTop + strip;
                listBottom = 38;
                break;
        }

        var captions = readouts.Intelligence ? style == BoardStyle.Timing ? 14 : 12 : 0;
        var startY = readouts.Intelligence ? captions + 2 : plain.StartY;
        var height = side - listTop - listBottom;
        return plain with
        {
            HeroTop = heroTop,
            HeroHeight = hero,
            ListTop = listTop,
            ListBottom = listBottom,
            StartY = startY,
            Room = height - startY,
            GroupHeight = group,
            CaptionsHeight = captions,
            StripTop = stripTop,
            StripHeight = strip,
        };
    }

    private static BoardMetrics Plain(BoardStyle style, BoardSize size)
    {
        var side = BoardSizes.Side(size);
        var i = size switch
        {
            BoardSize.S => 0,
            BoardSize.L => 2,
            _ => 1,
        };

        switch (style)
        {
            case BoardStyle.Timing:
            {
                double[] pad = [12, 14, 16], hero = [70, 78, 86], value = [30, 36, 42], row = [30, 30, 32], sub = [21, 21, 22];
                var top = pad[i] + hero[i] + 8;
                return new BoardMetrics(side, 20, pad[i], hero[i], value[i], top, pad[i] - 4, 32, side - top - 32 - 6, 0, row[i], sub[i], 19,
                    side - 28, 28) { HeroTop = pad[i] + 2 };
            }

            case BoardStyle.Neon:
            {
                // The neon hero is taller (LED bar), but its plan keeps the glass room, as in the prototype.
                double[] pad = [14, 16, 18], hero = [82, 92, 100], planHero = [78, 86, 94], value = [34, 40, 46], row = [32, 32, 34],
                    sub = [22, 22, 23];
                return new BoardMetrics(side, 22, pad[i], hero[i], value[i], pad[i] + hero[i] + 2, pad[i] - 6, 36,
                    side - (pad[i] + planHero[i] + 4) - 38 - 8, 6, row[i], sub[i], 20, side - 11 - 18, 18) { HeroTop = pad[i] - 2 };
            }

            default:
            {
                double[] pad = [14, 16, 18], hero = [78, 86, 94], value = [34, 40, 46], row = [32, 32, 34], sub = [22, 22, 23];
                var top = pad[i] + hero[i] + 4;
                return new BoardMetrics(side, 26, pad[i], hero[i], value[i], top, pad[i] - 6, 38, side - top - 38 - 8, 6, row[i], sub[i], 20,
                    side - 12 - 18, 18) { HeroTop = pad[i] - 2 };
            }
        }
    }
}

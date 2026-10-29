using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Modes;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>The glyph sets of the numbers a row shows.</summary>
public enum BoardValueSet
{
    /// <summary>A group row's value.</summary>
    Group,

    /// <summary>A sub row's value.</summary>
    Sub,

    /// <summary>Timing's interval to the group above (<c>.gap</c>).</summary>
    Interval,

    /// <summary>A group row's live 性能 (neon: baked <c>led</c> with its glow).</summary>
    IntelGroup,

    /// <summary>A sub row's live 性能.</summary>
    IntelSub,

    /// <summary>A group row's resting 性能 or <c>–</c> (the quieter weight; tinted <c>ink-3</c>).</summary>
    IntelGroupRest,

    IntelSubRest,

    /// <summary>The sub-gauge's 性能 number (neon: baked LED digits with their glow).</summary>
    GaugeMain,

    /// <summary>The sub-gauge's 思考 and 費用 numbers (neon: baked with their glow).</summary>
    GaugeTable,

    /// <summary>Neon's plain 性能 digits: quiet values in <c>ink-3</c> and the ghost <c>8</c>s.</summary>
    GaugeMainPlain,

    /// <summary>Neon's plain table digits for quiet values.</summary>
    GaugeTablePlain,
}

/// <summary>
/// The numbers of the rows (the prototypes' <c>.speed</c> and timing's <c>.gap</c>) composed from cached glyph layers, like the
/// hero number: they change on every tick, so they are never rasterized then (DESIGN 2c Two layers). Each set has one white
/// glyph layer per character; a row tints its glyphs to its color, its warm flash or its dim. A value below 0.05 shows an en
/// dash in the dim weight.
/// </summary>
public static class BoardValueGlyphs
{
    public const string Glyphs = "0123456789.kMB–−+,<";

    /// <summary>The en dash of a value below 0.05 (METRICS 7: never shown as a number).</summary>
    public const char Dash = '–';

    /// <summary>Room around a glyph's advance box in its layer.</summary>
    public const double Pad = 2;

    /// <summary>The characters a set draws (a score: digits, the grouping comma and the compact form's <c>.</c>, <c>k</c>, <c>M</c>, or <c>–</c>; a rate also has its other separators and suffixes).</summary>
    public static string Alphabet(BoardValueSet set) => set switch
    {
        BoardValueSet.IntelGroup or BoardValueSet.IntelSub or BoardValueSet.IntelGroupRest or BoardValueSet.IntelSubRest or BoardValueSet.GaugeMain
            or BoardValueSet.GaugeMainPlain => "0123456789,.kM–",
        BoardValueSet.GaugeTable or BoardValueSet.GaugeTablePlain => "0123456789.,kMB–<",
        _ => "0123456789.kMB–−+",
    };

    /// <summary>Room around a set's glyphs: neon bakes the glow of its lit sets into their layers.</summary>
    public static double PadOf(BoardValueSet set) => set switch
    {
        BoardValueSet.GaugeMain => 10,
        BoardValueSet.GaugeTable or BoardValueSet.IntelGroup or BoardValueSet.IntelSub => 8,
        _ => Pad,
    };

    /// <summary>The right edge of timing's interval column, from the row's right end (M and L; S has no interval).</summary>
    public const double IntervalRight = 57;

    private static readonly string[][] SetKeys = Enum.GetValues<BoardValueSet>()
        .Select(set => Glyphs.Select(c => "#v" + (int)set + c).ToArray())
        .ToArray();

    /// <summary>The glyph keys of a set, in <see cref="Glyphs"/> order.</summary>
    public static IReadOnlyList<string> Keys(BoardValueSet set) => SetKeys[(int)set];

    public static string? KeyOf(char c, BoardValueSet set)
    {
        var i = Glyphs.IndexOf(c, StringComparison.Ordinal);
        return i < 0 ? null : SetKeys[(int)set][i];
    }

    /// <summary>The sets a style draws (only timing has intervals).</summary>
    public static int Sets(BoardStyle style) => style == BoardStyle.Timing ? 3 : 2;

    /// <summary>The sets a board draws with its live metrics switched as <paramref name="metrics"/> says (DESIGN 2c, Live metrics).</summary>
    public static IReadOnlyList<BoardValueSet> SetsFor(BoardStyle style, BoardMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var sets = new List<BoardValueSet>(12) { BoardValueSet.Group, BoardValueSet.Sub };
        if (style == BoardStyle.Timing)
        {
            sets.Add(BoardValueSet.Interval);
        }

        var readouts = metrics.Readouts;
        if (readouts.Intelligence)
        {
            sets.AddRange([BoardValueSet.IntelGroup, BoardValueSet.IntelSub, BoardValueSet.IntelGroupRest, BoardValueSet.IntelSubRest]);
        }

        if (readouts.Hero && readouts.Intelligence)
        {
            sets.Add(BoardValueSet.GaugeMain);
            if (style == BoardStyle.Neon)
            {
                sets.Add(BoardValueSet.GaugeMainPlain);
            }
        }

        if (readouts.Thinking || readouts.Cost)
        {
            sets.Add(BoardValueSet.GaugeTable);
            if (style == BoardStyle.Neon)
            {
                sets.Add(BoardValueSet.GaugeTablePlain);
            }
        }

        return sets;
    }

    /// <summary>The width of <paramref name="text"/> in a set, <paramref name="tracking"/> between glyphs (0 while glyphs are missing).</summary>
    public static double Measure(IBoardLayers layers, ReadOnlySpan<char> text, BoardValueSet set, double tracking)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var width = 0.0;
        var count = 0;
        var pad = PadOf(set);
        for (var i = 0; i < text.Length; i++)
        {
            if (TryGlyph(text[i], set, layers, out var slot))
            {
                width += slot.Width - 2 * pad + (count++ > 0 ? tracking : 0);
            }
        }

        return width;
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its baseline at <paramref name="baseline"/>, starting at, centred on or ending at
    /// <paramref name="x"/> (<paramref name="align"/> −1, 0, 1), tinted <paramref name="tint"/> by <paramref name="amount"/> (0
    /// keeps a baked color); <paramref name="dy"/> per glyph is not used here (rolls use <see cref="BoardReadoutNumber"/>).
    /// Returns the drawn width.
    /// </summary>
    public static double EmitOnBaseline(BoardDrawList list, IBoardLayers layers, ReadOnlySpan<char> text, BoardValueSet set, double x, int align, double baseline,
        double tracking, Rgba tint, double amount, double alpha, RectBounds clip = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        var width = Measure(layers, text, set, tracking);
        if (alpha <= 0.002)
        {
            return width;
        }

        var at = align < 0 ? x : align == 0 ? x - width / 2 : x - width;
        var color = tint.WithAlpha(1);
        var opacity = alpha * (amount > 0 ? tint.A : 1);
        var pad = PadOf(set);
        for (var i = 0; i < text.Length; i++)
        {
            if (!TryGlyph(text[i], set, layers, out var slot))
            {
                continue;
            }

            list.AddLayer(BoardLayerInstance.Create(slot, at - pad, baseline - Baseline(slot), opacity, color, amount, clip));
            at += slot.Width - 2 * pad + tracking;
        }

        return width;
    }

    /// <summary>The glyph's baseline in its layer (the middle of the layer when the painter gave none).</summary>
    public static double Baseline(in BoardLayerSlot slot) => double.IsFinite(slot.Baseline) ? slot.Baseline : slot.Height / 2;

    /// <summary>A glyph's slot, false while it has not been rasterized.</summary>
    public static bool TryGet(char c, BoardValueSet set, IBoardLayers layers, out BoardLayerSlot slot) => TryGlyph(c, set, layers, out slot);

    /// <summary>What a row with <paramref name="live"/> shows.</summary>
    public static string Text(double live) => live > 0.05 ? SpeedFormat.Speed(live) : Dash.ToString();

    /// <summary>Timing's interval: the gap to the group above (−29 when behind by 29, +20 when ahead inside the 10 % band).</summary>
    public static string Interval(double above, double live) => (above - live >= 0 ? "−" : "+") + SpeedFormat.Speed(Math.Abs(above - live));

    /// <summary>The value's font size: 13 pt (a two-line group row's 15), a sub row's 11 pt (glass 11.5).</summary>
    public static double Size(BoardStyle style, bool sub, bool twoLine = false) =>
        sub ? style == BoardStyle.Glass ? 11.5 : 11 : 13 + (twoLine ? BoardRowFigures.Growth : 0);

    /// <summary>The CSS letter spacing of −0.02 em between glyphs.</summary>
    public static double Tracking(BoardStyle style, bool sub, bool twoLine = false) => -0.02 * Size(style, sub, twoLine);

    /// <summary>The value's right edge inside the row (the row's right padding).</summary>
    public static double Inset(BoardStyle style) => style == BoardStyle.Timing ? 4 : 6;

    /// <summary>
    /// The value's color: dim below 0.05; glass and neon color a hot value (heat above 0.45) by its heat, glass on group rows
    /// only; otherwise the primary ink, a sub row's secondary.
    /// </summary>
    public static Rgba Color(BoardStyle style, BoardPalette palette, bool sub, double live, double redline)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!(live > 0.05))
        {
            return palette.Ink3;
        }

        var heat = HeatMath.Heat(live, redline);
        var hot = heat > 0.45 && !palette.HighContrast && style != BoardStyle.Timing && (!sub || style == BoardStyle.Neon);
        return hot ? EffortCapsulePalette.Heat(heat, palette.Dark) : sub ? palette.Ink2 : palette.Ink;
    }

    /// <summary>
    /// Draws <paramref name="text"/> ending at <paramref name="right"/>, its line centered on <paramref name="centerY"/> (canvas
    /// points), in <paramref name="color"/>; glyphs still missing are skipped.
    /// </summary>
    public static void Emit(BoardDrawList list, IBoardLayers layers, string text, BoardValueSet set, double right, double centerY, double tracking,
        Rgba color, double alpha, RectBounds clip)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(text);
        if (alpha * color.A <= 0.002)
        {
            return;
        }

        var width = 0.0;
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (TryGlyph(text[i], set, layers, out var slot))
            {
                width += slot.Width - 2 * Pad + (count++ > 0 ? tracking : 0);
            }
        }

        var x = right - width;
        var tint = color.WithAlpha(1);
        for (var i = 0; i < text.Length; i++)
        {
            if (!TryGlyph(text[i], set, layers, out var slot))
            {
                continue;
            }

            list.AddLayer(BoardLayerInstance.Create(slot, x - Pad, centerY - slot.Height / 2, alpha * color.A, tint, 1, clip));
            x += slot.Width - 2 * Pad + tracking;
        }
    }

    private static bool TryGlyph(char c, BoardValueSet set, IBoardLayers layers, out BoardLayerSlot slot)
    {
        slot = default;
        return KeyOf(c, set) is { } key && layers.TryGet(key, out slot);
    }
}

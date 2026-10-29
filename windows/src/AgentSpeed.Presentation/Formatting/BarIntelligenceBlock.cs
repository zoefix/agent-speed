using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>The taskbar intelligence block's cells (DIP), left to right: label, number, unit.</summary>
public readonly record struct BarIntelligenceCells(double Label, double Number, double Unit)
{
    public const double LabelGap = 3;
    public const double UnitGap = 3;

    /// <summary>The block's width without the gap before it.</summary>
    public double Width => Label + LabelGap + Number + UnitGap + Unit;
}

/// <summary>One dot of the provisional mark, its box on the device pixel grid (DIP).</summary>
public readonly record struct BarDot(double X, double Y, double Diameter);

/// <summary>The color a part of the intelligence block takes (DESIGN 10, Menu bar and taskbar; DESIGN 12, Platform details).</summary>
public enum BarIntelligenceInk
{
    /// <summary>A live number: the label color at <see cref="BarIntelligenceBlock.LiveOpacity"/>.</summary>
    Number,

    /// <summary>Labels, the unit, a resting score, <c>–</c> and a zero rate: the secondary color.</summary>
    Secondary,

    /// <summary>High contrast, a live number: the theme's full text color.</summary>
    Text,

    /// <summary>High contrast, everything else: the theme's gray-text color, as the orb readout's glyphs and units.</summary>
    GrayText,
}

/// <summary>
/// The taskbar 性能 block (DESIGN 10, Menu bar and taskbar): after the value, the money block and the network block,
/// <c>性能 1,286</c> over <c>思考 128 tok/s</c>. 性能 is the highest performance score among the agents of all enabled
/// sources, live before resting (DESIGN 12; METRICS 6.9), in compact form: live in the label color at 85 %, resting and
/// <c>–</c> in the secondary color, on the dotted provisional mark while provisional. Labels sit in a column, both numbers
/// right-aligned in one cell as wide as the wider of <c>8.88k</c> and <c>8,888</c>, so the block's width depends on the
/// language only. It never hides while chosen: with nothing generated in the window the thinking number reads <c>–</c> and
/// <c>tok/s</c> stays, while 性能 keeps its own form. Once the thinking
/// rate has read 0 for 3 s under a score, line 1 stands alone, centred in the same cells. Every change replaces characters
/// in place, a drop included: the widget is one image. It needs neither network measuring nor the helper.
/// </summary>
/// <param name="Form">性能's form: live, resting (pale) or none (<c>–</c>).</param>
/// <param name="ThinkingDim">The thinking number takes the secondary color: <c>–</c>, or a rate that reads 0.</param>
/// <param name="Spoken">
/// <c>a11y.orb.intel</c> with the score words, <c>a11y.orb.intel.only</c> while line 2 is hidden or reads <c>–</c>; while
/// none <c>intel.none</c> takes the 性能 words' place. Read after the value and in the tooltip's first line.
/// </param>
public sealed record BarIntelligenceBlock(
    string Label, string ThinkingLabel, string Index, string Thinking, string Unit, PerfForm Form, bool ThinkingDim, string Spoken)
{
    public const string Dash = PerfNumber.Dash;

    /// <summary>The widest METRICS 7 form below 1 M tok/s.</summary>
    public const string WidestNumber = "8.88k";

    /// <summary>The widest compact score below a million (DESIGN 12, <i>Number formats</i>).</summary>
    public const string WidestScore = PerfNumber.WidestCompact;

    /// <summary>A live score in the label color at this share (high contrast: the full label color, DESIGN 12 <i>Platform details</i>).</summary>
    public const double LiveOpacity = 0.85;

    /// <summary>The language whose UI face draws the labels.</summary>
    public string LanguageCode { get; init; } = LanguageCodes.ZhHant;

    /// <summary>The supplying agent's score is provisional: the dotted mark goes under 性能 (DESIGN 12).</summary>
    public bool Provisional { get; init; }

    /// <summary>The tooltip's second line, <c>perf.tooltip.top</c>: the score and its supplying agent; null while none.</summary>
    public string? Top { get; init; }

    /// <summary>Line 2 is not drawn: its rate has read 0 for 3 s under a score (DESIGN 10, Thinking hidden at zero).</summary>
    public bool ThinkingHidden { get; init; }

    /// <summary>The words for line 1 alone (<c>a11y.orb.intel.only</c>, or <c>intel.none</c>).</summary>
    public string SpokenAlone { get; init; } = string.Empty;

    /// <summary>
    /// The color of a part: a number (<paramref name="number"/>) that is not <paramref name="dim"/> takes the number color,
    /// everything else the secondary color. In high contrast every part follows the orb readout's rule (DESIGN 10): live
    /// numbers in the text color; labels, the unit, a resting score, <c>–</c> and a zero rate in the gray-text color, so live
    /// and resting differ by more than a pale level and no label is as loud as the number.
    /// </summary>
    public static BarIntelligenceInk InkOf(bool number, bool dim, bool highContrast) => (number && !dim, highContrast) switch
    {
        (true, true) => BarIntelligenceInk.Text,
        (true, false) => BarIntelligenceInk.Number,
        (false, true) => BarIntelligenceInk.GrayText,
        _ => BarIntelligenceInk.Secondary,
    };

    /// <summary>性能 has a value, live or resting.</summary>
    public bool HasScore => Form != PerfForm.None;

    /// <summary>性能's number takes the secondary color: resting or <c>–</c>.</summary>
    public bool IndexDim => Form != PerfForm.Live;

    /// <summary>The thinking rate reads 0 (or nothing was generated) under a score: line 2 hides after its hold.</summary>
    public bool ThinkingZero => HasScore && ThinkingDim;

    /// <summary>Null unless <c>settings.bar.intelligence</c> is on and the widget shows a value (not glyph only).</summary>
    public static BarIntelligenceBlock? From(AppSettings settings, SpeedSnapshot snapshot, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!settings.BarIntelligence || settings.BarMode == BarDisplayMode.GlyphOnly)
        {
            return null;
        }

        var reading = OrbSubset.Reading(snapshot, null);
        return Of(reading.Intelligence, reading.Agents, localizer);
    }

    /// <summary>
    /// The block with the thinking line's hold applied at <paramref name="time"/> (seconds): line 2 hidden once its rate has
    /// read 0 for 3 s under a score. Feed every update through the same <paramref name="hold"/>.
    /// </summary>
    public static BarIntelligenceBlock? From(AppSettings settings, SpeedSnapshot snapshot, Localizer localizer, ThinkingZeroHold hold,
        double time)
    {
        ArgumentNullException.ThrowIfNull(hold);
        var block = From(settings, snapshot, localizer);
        return hold.Hides(block?.ThinkingZero == true, time) ? block!.HidingThinking() : block;
    }

    public static BarIntelligenceBlock Of(IntelligenceSums sums, Localizer localizer) => Of(sums, [], localizer);

    /// <summary>The block for the window <paramref name="sums"/> of all enabled sources and their <paramref name="agents"/>.</summary>
    public static BarIntelligenceBlock Of(IntelligenceSums sums, IEnumerable<AgentStatus> agents, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return Of(PerfSetReading.Of(sums, agents), sums, localizer);
    }

    public static BarIntelligenceBlock Of(PerfSetReading score, IntelligenceSums sums, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(localizer);

        // Nothing generated in the window (Σ w = 0): the thinking number is `–` (DESIGN 10, Quiet state).
        var zero = ThinkingZeroHold.ReadsZero(sums.LiveThinking);
        var thinking = !sums.IsActive ? Dash : zero ? "0" : SpeedFormat.Speed(sums.LiveThinking);
        var alone = score.Sentence(null, localizer);
        return new BarIntelligenceBlock(localizer["intel.glyph"], localizer["intel.thinking.glyph"], score.Compact, thinking,
            localizer["unit.tokS"], score.Form, thinking == Dash || zero, thinking == Dash ? alone : score.Sentence(thinking, localizer))
        {
            LanguageCode = localizer.LanguageCode,
            Provisional = score.IsProvisional,
            Top = score.Tooltip(localizer),
            SpokenAlone = alone,
        };
    }

    /// <summary>Line 1 alone: line 2 not drawn and the label reads its words alone; a block whose rate is not 0 stays.</summary>
    public BarIntelligenceBlock HidingThinking() => ThinkingZero ? this with { ThinkingHidden = true, Spoken = SpokenAlone } : this;

    /// <summary>
    /// Line 1's baseline in a block centred on <paramref name="centerY"/> with lines <paramref name="pitch"/> apart (line 2
    /// one pitch below it): alone it sits halfway between the two baselines, its cap height centred on the item's text centre.
    /// </summary>
    public double FirstBaseline(double centerY, double capHeight, double pitch) =>
        ThinkingHidden ? centerY + capHeight / 2 : centerY - pitch / 2 + capHeight / 2;

    /// <summary>
    /// The cells for a measure of text and of tabular numbers: the number cell is as wide as the wider of
    /// <see cref="WidestNumber"/> and <see cref="WidestScore"/> unless a number is wider (1 M tok/s or a million points or
    /// more), so it never changes with the values below that.
    /// </summary>
    public BarIntelligenceCells Cells(Func<string, double> text, Func<string, double> number)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(number);
        return new BarIntelligenceCells(
            Math.Max(text(Label), text(ThinkingLabel)),
            Math.Max(Math.Max(number(WidestNumber), number(WidestScore)), Math.Max(number(Index), number(Thinking))),
            text(Unit));
    }

    /// <summary>
    /// The provisional mark under line 1's number (DESIGN 12, <i>Provisional mark</i>), its advance <paramref name="width"/> starting at
    /// <paramref name="left"/> on <paramref name="baseline"/> at text size <paramref name="size"/>: dots centred 0.17 × the size
    /// below the baseline (the room line 2 leaves), each box on the device pixel grid so the 1 px dots stay round and even.
    /// Empty unless provisional.
    /// </summary>
    public IReadOnlyList<BarDot> ProvisionalDots(double left, double width, double baseline, double size, double pixelsPerDip,
        bool highContrast)
    {
        if (!Provisional)
        {
            return [];
        }

        var mark = PerfProvisionalMark.Underline(size, width, pixelsPerDip, barBlock: true, highContrast);
        var scale = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        var d = mark.Diameter;
        var top = Snap(baseline + mark.Below - d / 2, scale);
        return [.. mark.Centers.Select(center => new BarDot(Snap(left + center - d / 2, scale), top, d))];
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}

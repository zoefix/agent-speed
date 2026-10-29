using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Perf;

/// <summary>The usual chip's look (DESIGN 12, <i>Chips</i>).</summary>
public enum PerfChipTone
{
    /// <summary>Above usual: mint.</summary>
    Mint,

    /// <summary>At or below usual: secondary text on the label color at 6 %.</summary>
    Neutral,

    /// <summary>No baseline, or the provisional chip: no fill, a dotted outline.</summary>
    Outlined,
}

/// <summary>A chip's text and look.</summary>
public sealed record PerfChip(string Text, PerfChipTone Tone);

/// <summary>The score's words on every surface (DESIGN 12, <i>Score card</i>, <i>Accessibility</i>) and the card's value formats.</summary>
public static class PerfText
{
    /// <summary>The card's rows in their order: 速度 (T, L, R), then 效率 (N, U, S, H).</summary>
    public static IReadOnlyList<PerfComponentId> SpeedRows { get; } = [PerfComponentId.T, PerfComponentId.L, PerfComponentId.R];

    public static IReadOnlyList<PerfComponentId> EfficiencyRows { get; } =
        [PerfComponentId.N, PerfComponentId.U, PerfComponentId.S, PerfComponentId.H];

    public static IReadOnlyList<PerfComponentId> CardOrder { get; } = [.. SpeedRows, .. EfficiencyRows];

    /// <summary>The <c>perf.comp.*</c> key of a component (its label; <c>.desc</c> its meaning).</summary>
    public static string ComponentKey(PerfComponentId id) => id switch
    {
        PerfComponentId.T => "perf.comp.throughput",
        PerfComponentId.L => "perf.comp.latency",
        PerfComponentId.R => "perf.comp.reasoning",
        PerfComponentId.N => "perf.comp.pace",
        PerfComponentId.U => "perf.comp.toolTime",
        PerfComponentId.S => "perf.comp.toolSuccess",
        _ => "perf.comp.cache",
    };

    /// <summary>The History table's short column name (<c>history.perf.column.*</c>).</summary>
    public static string ColumnKey(PerfComponentId id) => id switch
    {
        PerfComponentId.T => "history.perf.column.throughput",
        PerfComponentId.L => "history.perf.column.latency",
        PerfComponentId.R => "history.perf.column.reasoning",
        PerfComponentId.N => "history.perf.column.pace",
        PerfComponentId.U => "history.perf.column.toolTime",
        PerfComponentId.S => "history.perf.column.toolSuccess",
        _ => "history.perf.column.cache",
    };

    /// <summary>The tier word of a shown score (card only: it never colors or drives anything).</summary>
    public static string TierKey(int shown) => shown switch
    {
        < 700 => "perf.tier.slow",
        < 1300 => "perf.tier.standard",
        < 2000 => "perf.tier.fast",
        _ => "perf.tier.veryFast",
    };

    /// <summary><c>v / 1000</c> with two decimals (<c>1.29</c>, <c>12.84</c>).</summary>
    public static string Ratio(int shown) =>
        Math.Round(shown / 1000m, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// A component's measure as the card and History show it (DESIGN 12, <i>Components</i>, values): tok/s per METRICS 7,
    /// times in ms / s / min, the pace per minute, shares as whole percents that never round up to 100 %.
    /// </summary>
    public static string Measure(PerfComponentId id, double measure, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return id switch
        {
            PerfComponentId.T or PerfComponentId.R => SpeedFormat.Speed(measure) + " " + loc["unit.tokS"],
            PerfComponentId.L or PerfComponentId.U => Duration(measure, loc),
            PerfComponentId.N => loc.Format("perf.unit.perMin", OneDecimalBelowTen(measure)),
            _ => Percent(measure),
        };
    }

    /// <summary>
    /// A time from milliseconds, each rounding taken once, half away from zero, the unit following the rounded value:
    /// ms below 1 s, seconds with one decimal below 10 s and whole up to 60 s, minutes with one decimal below 10 min.
    /// </summary>
    public static string Duration(double ms, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var whole = Math.Round(Math.Max(0, ms), MidpointRounding.AwayFromZero);
        if (whole < 1000)
        {
            return loc.Format("perf.unit.ms", whole.ToString("0", CultureInfo.InvariantCulture));
        }

        var seconds = ms / 1000;
        var tenth = Math.Round(seconds, 1, MidpointRounding.AwayFromZero);
        if (tenth < 10)
        {
            return loc.Format("perf.unit.s", tenth.ToString("0.0", CultureInfo.InvariantCulture));
        }

        var secondsWhole = Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (secondsWhole < 60)
        {
            return loc.Format("perf.unit.s", secondsWhole.ToString("0", CultureInfo.InvariantCulture));
        }

        return loc.Format("perf.unit.min", OneDecimalBelowTen(ms / 60_000));
    }

    /// <summary>A share as a whole percent, half away from zero; below 100 % it never shows <c>100%</c>.</summary>
    public static string Percent(double share)
    {
        var percent = Math.Round(Math.Max(0, share) * 100, MidpointRounding.AwayFromZero);
        if (percent >= 100 && share < 1)
        {
            percent = 99;
        }

        return percent.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>A sub-score in full form.</summary>
    public static string SubScore(double? subScore) => subScore is { } s ? PerfNumber.Full(PerfFormula.Round(s)) : PerfNumber.Dash;

    /// <summary>The usual chip: <c>比平常 +8%</c> (mint), <c>±0%</c> / <c>−12%</c> (neutral), or no usual yet (outlined).</summary>
    public static PerfChip Usual(int? vsUsualPercent, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return vsUsualPercent is not { } percent
            ? new PerfChip(loc["perf.usual.none"], PerfChipTone.Outlined)
            : new PerfChip(loc.Format("perf.usual", SignedPercent(percent)), percent > 0 ? PerfChipTone.Mint : PerfChipTone.Neutral);
    }

    /// <summary><c>+8%</c>, <c>±0%</c> or <c>−12%</c> (U+2212).</summary>
    public static string SignedPercent(int percent) => percent switch
    {
        > 0 => "+" + percent.ToString(CultureInfo.InvariantCulture) + "%",
        0 => "±0%",
        _ => "−" + (-(long)percent).ToString(CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>
    /// The score words (DESIGN 12, <i>Accessibility</i>): the full integer, wrapped for a provisional value, for a resting
    /// value and for a set of <paramref name="count"/> ≥ 2 scored agents — <c>1,540，閒置中，2 個 agent 中最高</c>.
    /// </summary>
    public static string Words(int shown, bool provisional, PerfForm form, int count, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var words = PerfNumber.Spoken(shown);
        if (provisional)
        {
            words = loc.Format("a11y.perf.provisional", words);
        }

        if (form == PerfForm.Resting)
        {
            words = loc.Format("a11y.perf.idle", words);
        }

        return count >= 2 ? loc.Format("a11y.perf.top", words, count) : words;
    }

    /// <summary>An agent row's addition to its label (<c>a11y.agent.intel</c>); empty while none.</summary>
    public static string AgentLabel(PerfSetReading reading, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Words(loc) is { } words ? loc.Format("a11y.agent.intel", words) : string.Empty;
    }

    private static string OneDecimalBelowTen(double value)
    {
        var v = double.IsFinite(value) ? Math.Max(0, value) : 0;
        var tenth = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        return tenth < 10
            ? tenth.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }
}

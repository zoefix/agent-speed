using AgentSpeed.Core;

namespace AgentSpeed.Presentation.History;

/// <summary>Evenly spaced axis values from 0 to <see cref="Max"/>.</summary>
public readonly record struct AxisTicks(double Step, int Intervals)
{
    public double Max => Step * Intervals;

    public double Value(int index) => Step * index;
}

/// <summary>Horizontal geometry of one bar per day.</summary>
/// <param name="Slot">Width of each day.</param>
/// <param name="BarWidth">Width of the bar inside its slot.</param>
public readonly record struct BarSlots(double Slot, double BarWidth, int Count)
{
    public double Left(int index) => index * Slot + (Slot - BarWidth) / 2;

    public double Center(int index) => index * Slot + Slot / 2;

    /// <summary>The day under a horizontal position, clamped to the range.</summary>
    public int IndexAt(double x)
    {
        if (Count <= 0 || Slot <= 0 || !double.IsFinite(x))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(x / Slot), 0, Count - 1);
    }
}

public enum AxisLabelKind
{
    /// <summary>A month boundary (or the month the range starts in).</summary>
    Month,

    /// <summary>A month boundary that also starts a new year.</summary>
    Year,

    /// <summary>A day of the month under its bar.</summary>
    Day,
}

public readonly record struct AxisLabel(int Index, DateOnly Day, AxisLabelKind Kind);

/// <summary>Month rules and labels of the daily chart.</summary>
/// <param name="Rules">Indices of the first day of each month after the range start (a faint vertical rule).</param>
public sealed record AxisMarks(IReadOnlyList<int> Rules, IReadOnlyList<AxisLabel> Months, IReadOnlyList<AxisLabel> Days);

/// <summary>Scales, ticks and layout of the Usage History chart (DESIGN 4b, item 3).</summary>
public static class HistoryChartMath
{
    private static readonly double[] NiceSteps = [1, 2, 2.5, 5, 10];
    private static readonly int[] DayLabelSteps = [1, 2, 7, 14];
    private static readonly int[] MonthLabelSteps = [1, 2, 3, 6, 12];

    /// <summary>Smallest 1/2/2.5/5 × 10ⁿ step that is at least <paramref name="value"/>.</summary>
    public static double NiceStep(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var nice in NiceSteps)
        {
            var step = nice * magnitude;
            if (step >= value * (1 - 1e-9))
            {
                return step;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>Ticks for 0..<paramref name="dataMax"/> with at most <paramref name="maxIntervals"/> intervals.</summary>
    public static AxisTicks Ticks(double dataMax, int maxIntervals = 4)
    {
        maxIntervals = Math.Max(1, maxIntervals);
        if (!double.IsFinite(dataMax) || dataMax <= 0)
        {
            return new AxisTicks(1, maxIntervals);
        }

        var step = NiceStep(dataMax / maxIntervals);
        var intervals = Math.Clamp((int)Math.Ceiling(dataMax / step - 1e-9), 1, maxIntervals);
        return new AxisTicks(step, intervals);
    }

    /// <summary>
    /// Ticks for a secondary axis that share the primary axis' grid lines: exactly <paramref name="intervals"/>
    /// intervals with the smallest nice step that fits <paramref name="dataMax"/>.
    /// </summary>
    public static AxisTicks AlignedTicks(double dataMax, int intervals)
    {
        intervals = Math.Max(1, intervals);
        if (!double.IsFinite(dataMax) || dataMax <= 0)
        {
            return new AxisTicks(1, intervals);
        }

        return new AxisTicks(NiceStep(dataMax / intervals), intervals);
    }

    /// <summary>
    /// Ticks of the mirrored traffic chart (DESIGN 10): one step for both sides, about four intervals in all; the side
    /// below the axis (upload, usually far smaller) takes the smallest fraction of a step that holds it while it needs
    /// no more than one, so an empty band does not take a quarter of the chart. <c>Below</c> is null without upload.
    /// </summary>
    public static (AxisTicks Above, AxisTicks? Below) MirroredTicks(double above, double below)
    {
        above = double.IsFinite(above) ? Math.Max(0, above) : 0;
        below = double.IsFinite(below) ? Math.Max(0, below) : 0;
        var step = NiceStep(Math.Max(above + below, 1) * 1.04 / 4);
        var upper = new AxisTicks(step, Math.Max(1, (int)Math.Ceiling(above * 1.04 / step - 1e-9)));
        var intervals = (int)Math.Ceiling(below * 1.04 / step - 1e-9);
        if (intervals <= 0)
        {
            return (upper, null);
        }

        var lowerStep = step;
        if (intervals == 1)
        {
            foreach (var fraction in (ReadOnlySpan<double>)[0.2, 0.25, 0.5, 1])
            {
                if (fraction * step >= below * 1.04)
                {
                    lowerStep = fraction * step;
                    break;
                }
            }
        }

        return (upper, new AxisTicks(lowerStep, intervals));
    }

    /// <summary>Stacking order: the source with the most output (or cost) in the range first (drawn at the bottom).</summary>
    public static IReadOnlyList<SourceKind> StackOrder(IEnumerable<DaySummary> days, HistoryChartMetric metric = HistoryChartMetric.Output)
    {
        ArgumentNullException.ThrowIfNull(days);
        var totals = new Dictionary<SourceKind, long>();
        foreach (var day in days)
        {
            foreach (var entry in day.Sources)
            {
                totals[entry.Source] = totals.GetValueOrDefault(entry.Source)
                    + (metric == HistoryChartMetric.Cost ? entry.Cost.MicroUsd : entry.Output);
            }
        }

        return totals.Where(t => t.Value > 0).OrderByDescending(t => t.Value).ThenBy(t => t.Key).Select(t => t.Key).ToArray();
    }

    /// <summary>
    /// One slot per day across <paramref name="width"/>. Bars keep a gap of about a quarter of the slot (at least one
    /// pixel once there is room for it) and never grow wider than <paramref name="maxBarWidth"/>.
    /// </summary>
    public static BarSlots Slots(int count, double width, double maxBarWidth = 28, double gapRatio = 0.28, double minGap = 1)
    {
        if (count <= 0 || !double.IsFinite(width) || width <= 0)
        {
            return new BarSlots(0, 0, Math.Max(count, 0));
        }

        var slot = width / count;
        var bar = slot * (1 - gapRatio);
        if (slot >= 2 * minGap + 1)
        {
            bar = Math.Min(bar, slot - minGap);
        }
        else
        {
            bar = slot;
        }

        var narrowest = Math.Min(slot, 1);
        var widest = double.IsFinite(maxBarWidth) ? Math.Max(maxBarWidth, narrowest) : double.MaxValue;
        return new BarSlots(slot, Math.Clamp(bar, narrowest, widest), count);
    }

    /// <summary>Cumulative tops (in pixels above the baseline) of stacked <paramref name="values"/>.</summary>
    public static double[] Stack(IReadOnlyList<double> values, double scaleMax, double plotHeight)
    {
        ArgumentNullException.ThrowIfNull(values);
        var tops = new double[values.Count];
        if (scaleMax <= 0 || !double.IsFinite(scaleMax) || plotHeight <= 0)
        {
            return tops;
        }

        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            sum += double.IsFinite(value) && value > 0 ? value : 0;
            tops[i] = Math.Min(sum / scaleMax, 1) * plotHeight;
        }

        return tops;
    }

    /// <summary>Runs of consecutive days that have a value; the speed line breaks between runs.</summary>
    public static IReadOnlyList<(int Start, int Count)> Runs(IReadOnlyList<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var runs = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= values.Count; i++)
        {
            var present = i < values.Count && values[i] is { } v && double.IsFinite(v);
            if (present && start < 0)
            {
                start = i;
            }
            else if (!present && start >= 0)
            {
                runs.Add((start, i - start));
                start = -1;
            }
        }

        return runs;
    }

    /// <summary>
    /// Month rules and labels plus day labels for a range drawn with <paramref name="slot"/> pixels per day. Labels
    /// are thinned so neighbours stay at least <paramref name="minSpacing"/> apart; the last day is always labelled
    /// when day labels fit at all.
    /// </summary>
    public static AxisMarks Marks(DayRange range, double slot, double minSpacing = 44)
    {
        var count = range.DayCount;
        if (count <= 0 || !double.IsFinite(slot) || slot <= 0)
        {
            return new AxisMarks([], [], []);
        }

        var boundaries = new List<int>();
        for (var i = 1; i < count; i++)
        {
            if (range.DayAt(i).Day == 1)
            {
                boundaries.Add(i);
            }
        }

        var monthStep = MonthLabelSteps.FirstOrDefault(step => 30.4 * step * slot >= minSpacing, 12);
        var labelled = boundaries.Where(i => (range.DayAt(i).Month - 1) % monthStep == 0).ToList();
        var months = labelled
            .Select(i => new AxisLabel(i, range.DayAt(i), range.DayAt(i).Month == 1 ? AxisLabelKind.Year : AxisLabelKind.Month))
            .ToList();
        if (months.Count == 0 || months[0].Index * slot >= minSpacing * 1.25)
        {
            months.Insert(0, new AxisLabel(0, range.Start, AxisLabelKind.Month));
        }

        var rules = monthStep == 1 || 30.4 * slot >= 6 ? boundaries : labelled;

        var days = new List<AxisLabel>();
        var dayStep = DayLabelSteps.FirstOrDefault(step => step * slot >= minSpacing * 0.75, 0);
        if (dayStep > 0)
        {
            for (var i = count - 1; i >= 0; i -= dayStep)
            {
                days.Add(new AxisLabel(i, range.DayAt(i), AxisLabelKind.Day));
            }

            days.Reverse();
        }

        return new AxisMarks(rules, months, days);
    }

    /// <summary>Linear blend of two equally long value arrays (a missing value counts as 0).</summary>
    public static double[] Blend(IReadOnlyList<double> from, IReadOnlyList<double> to, double t)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var result = new double[to.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var a = i < from.Count ? from[i] : 0;
            result[i] = a + (to[i] - a) * t;
        }

        return result;
    }
}

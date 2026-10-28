using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Presentation.History;

/// <summary>
/// What the daily chart draws: one stack of per-source output (or cost in display units) per day of a range (bottom
/// first) and the day's average speed (none for cost). Traffic is mirrored: download stacks above the axis, upload
/// below it, with this computer's total as an outline. Frames are keyed by date and source, so a transition can morph
/// one range, filter or metric into another.
/// </summary>
public sealed class HistoryBars
{
    private readonly double[][] _values;
    private readonly double?[] _speeds;
    private readonly bool[]? _present;
    private readonly double[][]? _below;
    private readonly TrafficPair[]? _outline;

    /// <param name="present">Which days have data (a day without a bar can still have data); null: the days with a bar.</param>
    /// <param name="below">Per day and source (in <paramref name="order"/>), the part drawn below the axis (upload).</param>
    /// <param name="outline">Per day, the outline drawn behind the bars (this computer's traffic).</param>
    /// <param name="notRecordedDays">How many days at the start of the range were not recorded (traffic before measuring began).</param>
    public HistoryBars(DayRange range, IReadOnlyList<SourceKind> order, double[][] values, double?[] speeds,
        HistoryChartMetric metric = HistoryChartMetric.Output, bool[]? present = null, double[][]? below = null,
        TrafficPair[]? outline = null, int notRecordedDays = 0)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(speeds);
        if (values.Length != range.DayCount || speeds.Length != range.DayCount || (present is not null && present.Length != range.DayCount)
            || (below is not null && below.Length != range.DayCount) || (outline is not null && outline.Length != range.DayCount))
        {
            throw new ArgumentException("One value set and one speed per day of the range are required.", nameof(values));
        }

        Range = range;
        Order = order;
        Metric = metric;
        _values = values;
        _speeds = speeds;
        _present = present;
        _below = below;
        _outline = outline;
        NotRecordedDays = Math.Clamp(notRecordedDays, 0, range.DayCount);
        var max = 0.0;
        var maxBelow = 0.0;
        var maxSpeed = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            max = Math.Max(max, Math.Max(Total(i), Outline(i).Above));
            maxBelow = Math.Max(maxBelow, Math.Max(TotalBelow(i), Outline(i).Below));
            if (speeds[i] is { } speed && double.IsFinite(speed))
            {
                maxSpeed = Math.Max(maxSpeed, speed);
            }
        }

        Max = max;
        MaxBelow = maxBelow;
        MaxSpeed = maxSpeed;
    }

    /// <summary>Bytes above (download) and below (upload) the axis.</summary>
    public readonly record struct TrafficPair(double Above, double Below);

    /// <summary>Traffic: values above the axis, <see cref="Below"/> under it.</summary>
    public bool IsMirrored => _below is not null;

    /// <summary>Largest daily total below the axis (with the outline).</summary>
    public double MaxBelow { get; }

    /// <summary>Days at the start of the range before traffic was recorded (a hatched band).</summary>
    public int NotRecordedDays { get; }

    public bool HasOutline => _outline is not null;

    /// <summary>Upload per source of day <paramref name="index"/>, in <see cref="Order"/>; empty when not mirrored.</summary>
    public IReadOnlyList<double> Below(int index) => _below?[index] ?? [];

    public double TotalBelow(int index)
    {
        var sum = 0.0;
        if (_below is not null)
        {
            foreach (var value in _below[index])
            {
                sum += value;
            }
        }

        return sum;
    }

    /// <summary>This computer's download and upload on day <paramref name="index"/> (0 without an outline).</summary>
    public TrafficPair Outline(int index) => _outline is not null && index >= 0 && index < _outline.Length ? _outline[index] : default;

    public DayRange Range { get; }

    /// <summary>Output tokens with the speed line, or cost in the display currency without it.</summary>
    public HistoryChartMetric Metric { get; }

    /// <summary>Stacking order, bottom first.</summary>
    public IReadOnlyList<SourceKind> Order { get; }

    public int Count => _values.Length;

    /// <summary>Largest daily total.</summary>
    public double Max { get; }

    /// <summary>Largest daily average speed (0 without measurements).</summary>
    public double MaxSpeed { get; }

    public bool HasSpeed => MaxSpeed > 0;

    /// <summary>Output per source of day <paramref name="index"/>, in <see cref="Order"/>.</summary>
    public IReadOnlyList<double> Values(int index) => _values[index];

    public double? Speed(int index) => _speeds[index];

    public IReadOnlyList<double?> Speeds => _speeds;

    public double Total(int index)
    {
        var sum = 0.0;
        foreach (var value in _values[index])
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>Day <paramref name="index"/> has data (even when its bar is empty, as an unpriced day in the cost chart).</summary>
    public bool HasData(int index) => index >= 0 && index < _values.Length && (_present?[index] ?? Total(index) > 0);

    public double ValueOf(DateOnly day, SourceKind source) => Find(_values, day, source);

    public double BelowOf(DateOnly day, SourceKind source) => _below is null ? 0 : Find(_below, day, source);

    public TrafficPair OutlineOf(DateOnly day) => Range.Contains(day) ? Outline(Range.IndexOf(day)) : default;

    private double Find(double[][] rows, DateOnly day, SourceKind source)
    {
        if (!Range.Contains(day))
        {
            return 0;
        }

        for (var k = 0; k < Order.Count; k++)
        {
            if (Order[k] == source)
            {
                var row = rows[Range.IndexOf(day)];
                return k < row.Length ? row[k] : 0;
            }
        }

        return 0;
    }

    public static HistoryBars Empty(DayRange range, HistoryChartMetric metric = HistoryChartMetric.Output) =>
        new(range, [], Enumerable.Range(0, range.DayCount).Select(_ => Array.Empty<double>()).ToArray(), new double?[range.DayCount], metric);

    /// <param name="days">Days with data (any order); days outside <paramref name="range"/> are ignored.</param>
    /// <param name="source">The filter: one bar of that source, or (null) a stack of every source.</param>
    /// <param name="money">Converts the cost metric to display units; required for <see cref="HistoryChartMetric.Cost"/>.</param>
    public static HistoryBars From(IReadOnlyList<DaySummary> days, DayRange range, SourceKind? source,
        HistoryChartMetric metric = HistoryChartMetric.Output, HistoryMoney? money = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        var cost = metric == HistoryChartMetric.Cost;
        if (cost && money is null)
        {
            throw new ArgumentNullException(nameof(money), "The cost metric needs the display currency.");
        }

        var inRange = days.Where(d => range.Contains(d.Day)).ToArray();
        IReadOnlyList<SourceKind> order = source is { } only ? [only] : HistoryChartMath.StackOrder(inRange, metric);
        var values = new double[range.DayCount][];
        var speeds = new double?[range.DayCount];
        var present = new bool[range.DayCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new double[order.Count];
        }

        foreach (var day in inRange)
        {
            var index = range.IndexOf(day.Day);
            present[index] = true;
            for (var k = 0; k < order.Count; k++)
            {
                values[index][k] = cost
                    ? money!.Value(source is null ? day.CostMicroUsdOf(order[k]) : day.Cost.MicroUsd)
                    : source is null ? day.OutputOf(order[k]) : day.Output;
            }

            speeds[index] = cost ? null : day.AverageSpeed;
        }

        return new HistoryBars(range, order, values, speeds, metric, present);
    }

    /// <summary>
    /// Mirrored daily traffic (DESIGN 10, Usage history): download above the axis stacked by source (most traffic of the
    /// range at the bottom), upload below it; with every source this computer's total as the outline. Days before
    /// <paramref name="since"/> are not recorded; days with usage or traffic are the ones the pointer visits.
    /// </summary>
    public static HistoryBars Traffic(IReadOnlyList<DaySummary> days, DayRange range, SourceKind? source,
        IReadOnlyDictionary<DateOnly, DayTraffic> traffic, DateOnly? since)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(traffic);
        var totals = new Dictionary<SourceKind, long>();
        foreach (var (day, value) in traffic)
        {
            if (!range.Contains(day))
            {
                continue;
            }

            foreach (var (kind, bytes) in value.BySource)
            {
                totals[kind] = totals.GetValueOrDefault(kind) + bytes.Sum;
            }
        }

        IReadOnlyList<SourceKind> order = source is { } only
            ? [only]
            : totals.Where(t => t.Value > 0).OrderByDescending(t => t.Value).ThenBy(t => t.Key).Select(t => t.Key).ToArray();
        var count = range.DayCount;
        var values = new double[count][];
        var below = new double[count][];
        var present = new bool[count];
        TrafficPair[]? outline = source is null ? new TrafficPair[count] : null;
        for (var i = 0; i < count; i++)
        {
            values[i] = new double[order.Count];
            below[i] = new double[order.Count];
            if (!traffic.TryGetValue(range.DayAt(i), out var value))
            {
                continue;
            }

            for (var k = 0; k < order.Count; k++)
            {
                var bytes = value.BySource.GetValueOrDefault(order[k]);
                values[i][k] = bytes.Rx;
                below[i][k] = bytes.Tx;
            }

            if (outline is not null && value.Machine is { } machine)
            {
                outline[i] = new TrafficPair(machine.Rx, machine.Tx);
            }
        }

        foreach (var day in days)
        {
            if (range.Contains(day.Day))
            {
                present[range.IndexOf(day.Day)] = true;
            }
        }

        foreach (var day in traffic.Keys)
        {
            if (range.Contains(day))
            {
                present[range.IndexOf(day)] = true;
            }
        }

        var notRecorded = since is { } first ? Math.Clamp(first.DayNumber - range.Start.DayNumber, 0, count) : count;
        return new HistoryBars(range, order, values, new double?[count], HistoryChartMetric.Traffic, present, below, outline, notRecorded);
    }

    /// <summary>
    /// Every value times <paramref name="factor"/>: a frame of one metric expressed on another metric's scale, so a switch
    /// between output and cost morphs the bars' heights rather than their units.
    /// </summary>
    public HistoryBars Scaled(double factor)
    {
        var f = double.IsFinite(factor) && factor > 0 ? factor : 0;
        var values = _values.Select(row => row.Select(v => v * f).ToArray()).ToArray();
        var below = _below?.Select(row => row.Select(v => v * f).ToArray()).ToArray();
        var outline = _outline?.Select(o => new TrafficPair(o.Above * f, o.Below * f)).ToArray();
        return new HistoryBars(Range, Order, values, (double?[])_speeds.Clone(), Metric, (bool[]?)_present?.Clone(), below, outline,
            NotRecordedDays);
    }

    /// <summary>
    /// The frame <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>: the target's days
    /// and speeds, every source of both frames (the leaving ones on top, shrinking away), values matched by date.
    /// <paramref name="t"/> may overshoot 1 (springs); values never go below 0. Upload and outline blend alike while either
    /// frame is mirrored.
    /// </summary>
    public static HistoryBars Blend(HistoryBars from, HistoryBars to, double t)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var order = to.Order.Concat(from.Order.Where(s => !to.Order.Contains(s))).ToArray();
        var mirrored = from.IsMirrored || to.IsMirrored;
        var outlined = from.HasOutline || to.HasOutline;
        var values = new double[to.Count][];
        var below = mirrored ? new double[to.Count][] : null;
        var outline = outlined ? new TrafficPair[to.Count] : null;
        for (var i = 0; i < values.Length; i++)
        {
            var day = to.Range.DayAt(i);
            var row = new double[order.Length];
            var under = mirrored ? new double[order.Length] : null;
            for (var k = 0; k < order.Length; k++)
            {
                row[k] = Lerp(from.ValueOf(day, order[k]), k < to.Order.Count ? to._values[i][k] : 0, t);
                if (under is not null)
                {
                    under[k] = Lerp(from.BelowOf(day, order[k]), k < to.Order.Count && to._below is { } b ? b[i][k] : 0, t);
                }
            }

            values[i] = row;
            if (below is not null)
            {
                below[i] = under!;
            }

            if (outline is not null)
            {
                var a = from.OutlineOf(day);
                var b = to.Outline(i);
                outline[i] = new TrafficPair(Lerp(a.Above, b.Above, t), Lerp(a.Below, b.Below, t));
            }
        }

        return new HistoryBars(to.Range, order, values, (double?[])to._speeds.Clone(), to.Metric, (bool[]?)to._present?.Clone(), below,
            outline, to.NotRecordedDays);

        static double Lerp(double a, double b, double t) => Math.Max(0, a + (b - a) * t);
    }
}

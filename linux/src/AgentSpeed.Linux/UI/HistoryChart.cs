using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AgentSpeed.Linux.UI;

internal readonly record struct HistoryChartSegment(int DayIndex, int SourceIndex, bool Below, Rect Bounds);

internal readonly record struct HistoryChartOutline(int DayIndex, Rect Bounds);
internal readonly record struct HistorySpeedPoint(int DayIndex, Point Point, bool ConnectToPrevious);

/// <summary>Finite, testable geometry used by the native Avalonia history chart.</summary>
internal sealed record HistoryChartLayout(
    Rect Plot,
    BarSlots Slots,
    double ZeroY,
    double ScaleAbove,
    double ScaleBelow,
    AxisTicks AboveTicks,
    AxisTicks? BelowTicks,
    AxisTicks? SpeedTicks,
    IReadOnlyList<bool> PresentDays,
    IReadOnlyList<HistoryChartSegment> Segments,
    IReadOnlyList<Rect> ZeroMarkers,
    IReadOnlyList<HistoryChartOutline> Outlines,
    Rect NotRecordedBand,
    bool ShowSpeed,
    IReadOnlyList<HistorySpeedPoint> SpeedPoints);

/// <summary>
/// Daily stacked usage bars with an average-speed line, or mirrored network traffic bars. Data and date-range rules
/// come from the shared HistoryBars model so gaps, source order, currency scaling and machine traffic stay consistent.
/// </summary>
internal sealed class HistoryChart : Control
{
    private static readonly Typeface Face = new("Inter");
    private static readonly IBrush DefaultGridBrush = new SolidColorBrush(Color.Parse("#263246"));
    private static readonly IBrush DefaultMutedBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush DefaultLineBrush = new SolidColorBrush(Color.Parse("#222222"));

    private HistoryBars _bars = HistoryBars.Empty(DayRange.Single(DateOnly.FromDateTime(DateTime.Today)));
    private int _hoverIndex = -1;
    private string? _tooltipText;

    public HistoryBars Bars
    {
        get => _bars;
        set
        {
            _bars = value ?? throw new ArgumentNullException(nameof(value));
            InvalidateVisual();
        }
    }

    public IBrush GridBrush { get; set; } = DefaultGridBrush;

    public IBrush MutedBrush { get; set; } = DefaultMutedBrush;

    /// <summary>Brush of the average-speed line.</summary>
    public IBrush LineBrush { get; set; } = DefaultLineBrush;

    public string? EmptyText { get; set; }

    public Func<double, string>? TickFormat { get; set; }

    /// <summary>Unit shown with the value axis; speed labels use tok/s for output charts.</summary>
    public string? Unit { get; set; }

    /// <summary>Optional localized, exact tooltip text for a day.</summary>
    public Func<DateOnly, string>? TooltipForDay { get; set; }

    /// <summary>Whether any date in the selected range was recorded, including recorded zero-value days.</summary>
    public bool HasData
    {
        get
        {
            for (var i = 0; i < Bars.Count; i++)
            {
                if (Bars.HasData(i)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Builds the exact bar geometry used by rendering. Each range day gets a slot; absent days do not get segments, and
    /// mirrored traffic uses separate positive and negative scales around its zero line.
    /// </summary>
    internal static HistoryChartLayout CalculateLayout(HistoryBars bars, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(bars);
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;

        var showSpeed = bars.Metric == HistoryChartMetric.Output && bars.HasSpeed;
        var left = Math.Min(width * 0.18, Math.Clamp(width * 0.12, 30, 54));
        var right = showSpeed ? Math.Min(width * 0.18, Math.Clamp(width * 0.12, 30, 54)) : Math.Min(12, width * 0.06);
        var top = Math.Min(24, height * 0.2);
        var bottom = Math.Min(28, height * 0.22);
        var plot = new Rect(left, top, Math.Max(1, width - left - right), Math.Max(1, height - top - bottom));
        var slots = HistoryChartMath.Slots(bars.Count, plot.Width);

        var aboveMax = FinitePositive(bars.Max);
        var aboveTicks = HistoryChartMath.Ticks(aboveMax);
        var scaleAbove = PositiveScale(aboveTicks.Max);
        AxisTicks? belowTicks = null;
        AxisTicks? speedTicks = null;
        var scaleBelow = 0d;
        if (bars.IsMirrored)
        {
            var mirrored = HistoryChartMath.MirroredTicks(aboveMax, FinitePositive(bars.MaxBelow));
            aboveTicks = mirrored.Above;
            belowTicks = mirrored.Below;
            scaleAbove = PositiveScale(aboveTicks.Max);
            scaleBelow = belowTicks is { } below ? PositiveScale(below.Max) : 0;
        }
        else if (showSpeed)
        {
            speedTicks = HistoryChartMath.AlignedTicks(FinitePositive(bars.MaxSpeed), aboveTicks.Intervals);
        }

        var lowerHeight = scaleBelow > 0 ? plot.Height * scaleBelow / (scaleAbove + scaleBelow) : 0;
        var upperHeight = plot.Height - lowerHeight;
        var zeroY = plot.Top + upperHeight;
        var present = new bool[bars.Count];
        var segments = new List<HistoryChartSegment>();
        var zeroMarkers = new List<Rect>();
        var outlines = new List<HistoryChartOutline>();

        for (var day = 0; day < bars.Count; day++)
        {
            present[day] = bars.HasData(day);
            if (!present[day]) continue;

            var x = plot.Left + slots.Left(day);
            var barWidth = Math.Max(0, slots.BarWidth);
            var positive = bars.Values(day);
            var positiveTotal = 0d;
            for (var source = 0; source < positive.Count; source++)
            {
                var value = FinitePositive(positive[source]);
                if (value <= 0) continue;
                var previous = positiveTotal;
                positiveTotal = Math.Min(scaleAbove, positiveTotal + value);
                var yTop = zeroY - upperHeight * positiveTotal / scaleAbove;
                var yBottom = zeroY - upperHeight * previous / scaleAbove;
                if (yBottom - yTop > 0)
                {
                    segments.Add(new HistoryChartSegment(day, source, false,
                        new Rect(x, yTop, barWidth, yBottom - yTop)));
                }
            }

            var negative = bars.Below(day);
            var negativeTotal = 0d;
            if (scaleBelow > 0)
            {
                for (var source = 0; source < negative.Count; source++)
                {
                    var value = FinitePositive(negative[source]);
                    if (value <= 0) continue;
                    var previous = negativeTotal;
                    negativeTotal = Math.Min(scaleBelow, negativeTotal + value);
                    var yTop = zeroY + lowerHeight * previous / scaleBelow;
                    var yBottom = zeroY + lowerHeight * negativeTotal / scaleBelow;
                    if (yBottom - yTop > 0)
                    {
                        segments.Add(new HistoryChartSegment(day, source, true,
                            new Rect(x, yTop, barWidth, yBottom - yTop)));
                    }
                }
            }

            if (positiveTotal <= 0 && negativeTotal <= 0)
            {
                var markerY = Math.Clamp(zeroY - 1, plot.Top, Math.Max(plot.Top, plot.Bottom - 2));
                zeroMarkers.Add(new Rect(plot.Left + slots.Center(day) - Math.Min(3, slots.BarWidth / 2), markerY,
                    Math.Min(6, Math.Max(1, slots.BarWidth)), Math.Min(2, plot.Height)));
            }

            if (bars.HasOutline)
            {
                var outline = bars.Outline(day);
                var outAbove = FinitePositive(outline.Above);
                var outBelow = FinitePositive(outline.Below);
                if (outAbove > 0 || outBelow > 0)
                {
                    var outlineTop = outAbove > 0 ? zeroY - upperHeight * Math.Min(outAbove / scaleAbove, 1) : zeroY;
                    var outlineBottom = outBelow > 0 && scaleBelow > 0
                        ? zeroY + lowerHeight * Math.Min(outBelow / scaleBelow, 1)
                        : zeroY;
                    var outlineWidth = slots.BarWidth + Math.Min(4, Math.Max(2, slots.BarWidth * 0.25));
                    outlines.Add(new HistoryChartOutline(day, new Rect(
                        plot.Left + slots.Center(day) - outlineWidth / 2,
                        outlineTop,
                        outlineWidth,
                        Math.Max(1, outlineBottom - outlineTop))));
                }
            }
        }

        var bandWidth = plot.Width * Math.Clamp(bars.NotRecordedDays, 0, bars.Count) / Math.Max(1, bars.Count);
        var notRecordedBand = new Rect(plot.Left, plot.Top, bandWidth, plot.Height);
        var speedPoints = new List<HistorySpeedPoint>();
        if (speedTicks is { Max: > 0 } axis)
        {
            var previous = false;
            for (var day = 0; day < bars.Count; day++)
            {
                if (bars.Speed(day) is not { } value || !double.IsFinite(value) || value < 0)
                { previous = false; continue; }
                var point = new Point(plot.Left + slots.Center(day),
                    plot.Bottom - Math.Clamp(value / axis.Max, 0, 1) * plot.Height);
                speedPoints.Add(new HistorySpeedPoint(day, point, previous));
                previous = true;
            }
        }
        return new HistoryChartLayout(plot, slots, zeroY, scaleAbove, scaleBelow, aboveTicks, belowTicks, speedTicks,
            present, segments, zeroMarkers, outlines, notRecordedBand, showSpeed, speedPoints);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 8 || height <= 8) return;

        var bars = Bars;
        var layout = CalculateLayout(bars, width, height);
        DrawNotRecorded(context, bars, layout);
        DrawAxes(context, bars, layout, width);
        DrawMonthMarks(context, bars, layout);
        DrawOutlines(context, layout);
        DrawBars(context, bars, layout);
        if (layout.ShowSpeed) DrawSpeed(context, bars, layout);

        if (!HasData && !string.IsNullOrWhiteSpace(EmptyText))
        {
            DrawLabel(context, EmptyText!, new Point(layout.Plot.Center.X, layout.Plot.Center.Y), 12,
                MutedBrush, centerHorizontally: true, centerVertically: true);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var layout = CalculateLayout(Bars, Bounds.Width, Bounds.Height);
        if (point.X < layout.Plot.Left || point.X > layout.Plot.Right || point.Y < layout.Plot.Top || point.Y > layout.Plot.Bottom)
        {
            ClearTooltip();
            return;
        }

        var index = layout.Slots.IndexAt(point.X - layout.Plot.Left);
        if (index < 0 || index >= Bars.Count || !Bars.HasData(index))
        {
            ClearTooltip();
            return;
        }

        var day = Bars.Range.DayAt(index);
        var tooltip = TooltipForDay?.Invoke(day) ?? DefaultTooltip(Bars, index);
        if (_hoverIndex != index || !string.Equals(_tooltipText, tooltip, StringComparison.Ordinal))
        {
            _hoverIndex = index;
            _tooltipText = tooltip;
            ToolTip.SetTip(this, tooltip);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearTooltip();
    }

    private void DrawAxes(DrawingContext context, HistoryBars bars, HistoryChartLayout layout, double width)
    {
        var gridPen = new Pen(GridBrush, 1);
        var baselinePen = new Pen(MutedBrush, 1.2);
        var intervals = Math.Max(1, layout.AboveTicks.Intervals);
        for (var i = 0; i <= intervals; i++)
        {
            var ratio = i / (double)intervals;
            var y = layout.ZeroY - ratio * (layout.ZeroY - layout.Plot.Top);
            context.DrawLine(gridPen, new Point(layout.Plot.Left, y), new Point(layout.Plot.Right, y));
            var value = layout.AboveTicks.Value(i);
            DrawLabel(context, bars.IsMirrored && i > 0 ? "+" + FormatTick(value) : FormatTick(value),
                new Point(layout.Plot.Left - 7, y), 10, MutedBrush, rightAlign: true, centerVertically: true);

            if (layout.SpeedTicks is { } speed && i <= speed.Intervals)
            {
                DrawLabel(context, FormatSpeedTick(speed.Value(i)), new Point(layout.Plot.Right + 7, y), 10,
                    MutedBrush, centerVertically: true);
            }
        }

        if (layout.BelowTicks is { } below && layout.ScaleBelow > 0)
        {
            for (var i = 1; i <= below.Intervals; i++)
            {
                var y = layout.ZeroY + (layout.Plot.Bottom - layout.ZeroY) * i / below.Intervals;
                context.DrawLine(gridPen, new Point(layout.Plot.Left, y), new Point(layout.Plot.Right, y));
                DrawLabel(context, "−" + FormatTick(below.Value(i)), new Point(layout.Plot.Left - 7, y), 10,
                    MutedBrush, rightAlign: true, centerVertically: true);
            }
        }

        context.DrawLine(baselinePen, new Point(layout.Plot.Left, layout.ZeroY), new Point(layout.Plot.Right, layout.ZeroY));
        if (!string.IsNullOrWhiteSpace(Unit))
        {
            DrawLabel(context, Unit!, new Point(2, 1), 10, MutedBrush);
        }
        if (layout.ShowSpeed)
        {
            DrawLabel(context, "tok/s", new Point(width, 1), 10, MutedBrush, rightAlign: true);
        }
    }

    private string FormatTick(double value) => TickFormat is { } format
        ? format(value)
        : value.ToString("0.##", CultureInfo.CurrentUICulture);

    internal static string FormatSpeedTick(double value) => SpeedFormat.Speed(value);

    private void DrawMonthMarks(DrawingContext context, HistoryBars bars, HistoryChartLayout layout)
    {
        if (layout.Slots.Slot <= 0 || bars.Count == 0) return;
        var marks = HistoryChartMath.Marks(bars.Range, layout.Slots.Slot);
        var pen = new Pen(GridBrush, 1, lineCap: PenLineCap.Round, dashStyle: new DashStyle([2, 3], 0));
        foreach (var index in marks.Rules)
        {
            var x = layout.Plot.Left + index * layout.Slots.Slot;
            context.DrawLine(pen, new Point(x, layout.Plot.Top), new Point(x, layout.Plot.Bottom));
        }

        foreach (var mark in marks.Months)
        {
            var month = mark.Day.ToString(mark.Kind == AxisLabelKind.Year ? "MMM yyyy" : "MMM", CultureInfo.CurrentUICulture);
            var x = layout.Plot.Left + mark.Index * layout.Slots.Slot + (mark.Index == 0 ? 0 : 4);
            if (x <= layout.Plot.Right) DrawLabel(context, month, new Point(x, 2), 10, MutedBrush);
        }

        var lastRight = double.NegativeInfinity;
        foreach (var mark in marks.Days)
        {
            var label = bars.Count == 1 || layout.Slots.Slot >= 44
                ? mark.Day.ToString("M/d", CultureInfo.CurrentUICulture)
                : mark.Day.Day.ToString(CultureInfo.CurrentUICulture);
            var measured = Measure(label, 10);
            var x = Math.Clamp(layout.Plot.Left + layout.Slots.Center(mark.Index) - measured.Width / 2,
                0, Math.Max(0, Bounds.Width - measured.Width));
            if (x < lastRight + 4) continue;
            DrawLabel(context, label, new Point(x, layout.Plot.Bottom + 5), 10, MutedBrush);
            lastRight = x + measured.Width;
        }
    }

    private void DrawNotRecorded(DrawingContext context, HistoryBars bars, HistoryChartLayout layout)
    {
        var rect = layout.NotRecordedBand;
        if (!bars.IsMirrored || bars.NotRecordedDays <= 0 || rect.Width <= 0.5) return;
        using (context.PushOpacity(0.05))
        {
            context.DrawRectangle(MutedBrush, null, rect, 0, 0, default);
        }
        using (context.PushClip(rect))
        using (context.PushOpacity(0.18))
        {
            var pen = new Pen(MutedBrush, 1);
            for (var offset = -rect.Height; offset < rect.Width; offset += 7)
            {
                context.DrawLine(pen, new Point(rect.Left + offset, rect.Bottom), new Point(rect.Left + offset + rect.Height, rect.Top));
            }
        }
    }

    private void DrawOutlines(DrawingContext context, HistoryChartLayout layout)
    {
        if (layout.Outlines.Count == 0) return;
        using (context.PushOpacity(0.48))
        {
            var pen = new Pen(MutedBrush, 1);
            foreach (var outline in layout.Outlines)
            {
                var rect = outline.Bounds;
                var radius = Math.Min(4, rect.Width / 3);
                context.DrawRectangle(null, pen, rect, radius, radius, default);
            }
        }
    }

    private void DrawBars(DrawingContext context, HistoryBars bars, HistoryChartLayout layout)
    {
        foreach (var segment in layout.Segments)
        {
            var color = segment.SourceIndex < bars.Order.Count
                ? HeatPalette.SourceColor(bars.Order[segment.SourceIndex])
                : HeatPalette.Neutral;
            var brush = new SolidColorBrush(Color.FromArgb(color.A8, color.R8, color.G8, color.B8));
            var rect = segment.Bounds;
            if (segment.Below)
            {
                using (context.PushOpacity(0.68))
                {
                    context.DrawRectangle(brush, null, rect, 0, 0, default);
                }
            }
            else
            {
                context.DrawRectangle(brush, null, rect, 0, 0, default);
            }
        }

        foreach (var marker in layout.ZeroMarkers)
        {
            context.DrawRectangle(MutedBrush, null, marker, marker.Height / 2, marker.Height / 2, default);
        }
    }

    private void DrawSpeed(DrawingContext context, HistoryBars bars, HistoryChartLayout layout)
    {
        var linePen = new Pen(LineBrush, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Point? previous = null;
        foreach (var sample in layout.SpeedPoints)
        {
            if (sample.ConnectToPrevious && previous is { } from) context.DrawLine(linePen, from, sample.Point);
            context.DrawEllipse(LineBrush, null, sample.Point, 3, 3);
            previous = sample.Point;
        }
    }

    private void ClearTooltip()
    {
        _hoverIndex = -1;
        _tooltipText = null;
        ToolTip.SetTip(this, null);
    }

    private string DefaultTooltip(HistoryBars bars, int index)
    {
        var day = bars.Range.DayAt(index).ToString("D", CultureInfo.CurrentUICulture);
        var unit = string.IsNullOrWhiteSpace(Unit) ? string.Empty : " " + Unit;
        if (bars.IsMirrored)
        {
            return $"{day}\n↓ {bars.Total(index).ToString("N0", CultureInfo.CurrentUICulture)}{unit}\n↑ {bars.TotalBelow(index).ToString("N0", CultureInfo.CurrentUICulture)}{unit}";
        }

        var amountFormat = bars.Metric == HistoryChartMetric.Cost ? "N2" : "N0";
        var amount = bars.Total(index).ToString(amountFormat, CultureInfo.CurrentUICulture);
        var text = $"{day}\n{amount}{unit}";
        if (bars.Metric == HistoryChartMetric.Output && bars.Speed(index) is { } speed && double.IsFinite(speed) && speed >= 0)
        {
            text += $"\n{SpeedFormat.Speed(speed)} tok/s";
        }
        return text;
    }

    private static double FinitePositive(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    private static double PositiveScale(double value) => double.IsFinite(value) && value > 0 ? value : 1;

    private static FormattedText Measure(string text, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, Brushes.Black);

    private static void DrawLabel(DrawingContext context, string text, Point origin, double size, IBrush brush,
        bool rightAlign = false, bool centerHorizontally = false, bool centerVertically = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush);
        var x = rightAlign ? origin.X - label.WidthIncludingTrailingWhitespace : origin.X;
        if (centerHorizontally) x -= label.WidthIncludingTrailingWhitespace / 2;
        var y = centerVertically ? origin.Y - label.Height / 2 : origin.Y;
        context.DrawText(label, new Point(x, y));
    }
}

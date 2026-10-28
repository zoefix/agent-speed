using System.Globalization;
using AgentSpeed.Presentation.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AgentSpeed.Linux.UI;

/// <summary>
/// The live and history line chart. It keeps the real one-second line (no smoothing), uses the shared nice-tick rules and
/// labels dates or clock ranges. Empty, all-zero and non-finite series still render a stable frame instead of NaNs.
/// </summary>
internal sealed class LineChart : Control
{
    private static readonly Typeface Face = new("Inter");

    public IReadOnlyList<double> Values { get; set; } = [];

    /// <summary>Compact summary series keep missing dates as gaps, with no axes or reserved axis gutter.</summary>
    public bool Sparkline { get; set; }

    public IBrush LineBrush { get; set; } = new SolidColorBrush(Color.Parse("#2DD4BF"));

    public IBrush GridBrush { get; set; } = new SolidColorBrush(Color.Parse("#263246"));

    public IBrush MutedBrush { get; set; } = new SolidColorBrush(Color.Parse("#93A0B4"));

    public double FillAlpha { get; set; } = .22;

    public string Unit { get; set; } = "";

    public string? StartLabel { get; set; }

    public string? EndLabel { get; set; }

    public string? EmptyText { get; set; }

    public Func<double, string>? TickFormat { get; set; }

    /// <summary>The largest nice-tick top for a finite series; a flat or empty series keeps a 0..1 frame.</summary>
    public static double SafeScaleMax(IEnumerable<double>? values)
    {
        var max = 0.0;
        if (values is not null)
        {
            foreach (var value in values)
                if (double.IsFinite(value) && value > max) max = value;
        }
        return max <= 0 ? 1 : HistoryChartMath.Ticks(max, 3).Max;
    }

    /// <summary>Non-finite and negative samples become zero while keeping the x spacing of the real series.</summary>
    public static double[] Sanitize(IEnumerable<double>? values)
    {
        if (values is null) return [];
        var list = values as IReadOnlyList<double> ?? values.ToArray();
        var clean = new double[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var value = list[i];
            clean[i] = double.IsFinite(value) && value > 0 ? value : 0;
        }
        return clean;
    }

    internal static IReadOnlyList<Point[]> SparklineSegments(IReadOnlyList<double> values, Size size)
    {
        if (values.Count == 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)
            || size.Width < 4 || size.Height < 4) return [];
        var samples = values.Select(v => double.IsFinite(v) && v >= 0 ? (double?)v : null).ToArray();
        var max = SafeScaleMax(values);
        var plot = new Rect(2, 2, size.Width - 4, size.Height - 4);
        return HistoryChartMath.Runs(samples).Select(run => Enumerable.Range(run.Start, run.Count)
            .Select(index => new Point(values.Count == 1 ? plot.Center.X : plot.Left + plot.Width * index / (values.Count - 1),
                plot.Bottom - Math.Clamp(values[index] / max, 0, 1) * plot.Height)).ToArray()).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 8 || height <= 8) return;

        if (Sparkline)
        {
            foreach (var segment in SparklineSegments(Values, Bounds.Size))
            {
                if (segment.Length == 1) { context.DrawEllipse(LineBrush, null, segment[0], 1.5, 1.5); continue; }
                var sparkGeometry = new StreamGeometry();
                using (var path = sparkGeometry.Open())
                {
                    path.BeginFigure(segment[0], false);
                    foreach (var point in segment.Skip(1)) path.LineTo(point);
                    path.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(LineBrush, 1.5), sparkGeometry);
            }
            return;
        }

        var values = Sanitize(Values);
        var dataMax = values.Length == 0 ? 0 : values.Max();
        var scaleMax = SafeScaleMax(values);
        var ticks = HistoryChartMath.Ticks(dataMax <= 0 ? 0 : dataMax, 3);
        if (dataMax <= 0) ticks = new AxisTicks(scaleMax, 3);
        var gutter = Math.Clamp(width * .12, 34, 58);
        var plot = new Rect(2, 5, Math.Max(1, width - gutter - 2), Math.Max(1, height - 22));
        var gridPen = new Pen(GridBrush, 1);
        var intervalCount = Math.Max(1, ticks.Intervals);
        for (var i = 0; i <= intervalCount; i++)
        {
            var y = plot.Bottom - plot.Height * i / intervalCount;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = ticks.Step * i;
            var text = TickFormat is { } format ? format(value) : value.ToString("0.#", CultureInfo.CurrentUICulture);
            DrawLabel(context, text, new Point(plot.Right + 7, y), 9.5, MutedBrush, centerVertically: true);
        }

        if (!string.IsNullOrEmpty(Unit))
            DrawLabel(context, Unit, new Point(width, 0), 9.5, MutedBrush, rightAlign: true);

        var floorY = plot.Bottom;
        if (dataMax <= 0 || values.Length < 2)
        {
            if (!string.IsNullOrEmpty(EmptyText))
                DrawLabel(context, EmptyText, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2),
                    11, MutedBrush, centerHorizontally: true, centerVertically: true);
            DrawRange(context, plot);
            return;
        }

        var points = new Point[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var x = plot.Left + plot.Width * i / (values.Length - 1);
            var y = floorY - Math.Clamp(values[i] / scaleMax, 0, 1) * plot.Height;
            points[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(points[0], false);
            for (var i = 1; i < points.Length; i++) path.LineTo(points[i]);
            path.EndFigure(false);
        }

        var lineColor = LineBrush is ISolidColorBrush solid ? solid.Color : Color.Parse("#2DD4BF");
        if (LineBrush is ISolidColorBrush)
        {
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(DesktopStyles.Alpha(lineColor, (byte)Math.Clamp(FillAlpha * 255, 0, 255)), 0),
                    new GradientStop(DesktopStyles.Alpha(lineColor, 0), 1),
                },
            };
            var area = new StreamGeometry();
            using (var path = area.Open())
            {
                path.BeginFigure(new Point(points[0].X, floorY), true);
                foreach (var point in points) path.LineTo(point);
                path.LineTo(new Point(points[^1].X, floorY));
                path.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }

        context.DrawGeometry(null, new Pen(LineBrush, 2), line);
        var latest = points[^1];
        context.DrawEllipse(new SolidColorBrush(DesktopStyles.Alpha(lineColor, 70)), null, latest, 6, 6);
        context.DrawEllipse(LineBrush, null, latest, 2.6, 2.6);
        DrawRange(context, plot);
    }

    private void DrawRange(DrawingContext context, Rect plot)
    {
        if (!string.IsNullOrEmpty(StartLabel))
            DrawLabel(context, StartLabel, new Point(plot.Left, plot.Bottom + 5), 9.5, MutedBrush);
        if (!string.IsNullOrEmpty(EndLabel))
            DrawLabel(context, EndLabel, new Point(plot.Right, plot.Bottom + 5), 9.5, MutedBrush, rightAlign: true);
    }

    private static void DrawLabel(DrawingContext context, string text, Point origin, double size, IBrush brush,
        bool rightAlign = false, bool centerHorizontally = false, bool centerVertically = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, size, brush);
        var x = rightAlign ? origin.X - label.WidthIncludingTrailingWhitespace : origin.X;
        if (centerHorizontally) x -= label.WidthIncludingTrailingWhitespace / 2;
        var y = centerVertically ? origin.Y - label.Height / 2 : origin.Y;
        context.DrawText(label, new Point(x, y));
    }
}

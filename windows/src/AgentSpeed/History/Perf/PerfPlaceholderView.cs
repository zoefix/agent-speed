using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using AgentSpeed.Presentation.History.Perf;

namespace AgentSpeed.History.Perf;

/// <summary>
/// The re-analysing state's static placeholders (DESIGN 12, <i>States</i>): the chart's gridlines and four table rows in the
/// track color, where the real ones will appear, so nothing jumps when the records fill in.
/// </summary>
internal sealed class PerfPlaceholderView : FrameworkElement
{
    public static readonly DependencyProperty ChartHeightProperty = DependencyProperty.Register(
        nameof(ChartHeight), typeof(double), typeof(PerfPlaceholderView),
        new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private const int Rows = 4;
    private const double TableGap = 12;

    public PerfPlaceholderView()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public double ChartHeight
    {
        get => (double)GetValue(ChartHeightProperty);
        set => SetValue(ChartHeightProperty, value);
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, ChartHeight + TableGap + Rows * PerfHistoryLayout.RowHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var brush = PerfHistoryAppearance.Brush(PerfHistoryAppearance.Track(this));
        var plotTop = PerfTrendChart.MonthBand;
        var plotBottom = ChartHeight - PerfTrendChart.DayBand;
        for (var i = 0; i < 4; i++)
        {
            var y = Math.Round(plotTop + (plotBottom - plotTop) * i / 3) + 0.5;
            dc.DrawRectangle(brush, null, new Rect(28, y - 0.5, width - 32, 1));
            dc.DrawRoundedRectangle(brush, null, new Rect(0, y - 3, 20, 6), 3, 3);
        }

        var top = ChartHeight + TableGap;
        for (var row = 0; row < Rows; row++)
        {
            var y = top + row * PerfHistoryLayout.RowHeight + PerfHistoryLayout.RowHeight / 2;
            dc.DrawEllipse(brush, null, new Point(10, y), 3.5, 3.5);
            dc.DrawRoundedRectangle(brush, null, new Rect(20, y - 4, Math.Min(180, width * 0.3) - row * 14, 8), 4, 4);
            for (var c = 0; c < 4; c++)
            {
                var right = width - 8 - c * Math.Min(90, width * 0.13);
                dc.DrawRoundedRectangle(brush, null, new Rect(right - 40, y - 4, 40, 8), 4, 4);
            }
        }
    }
}

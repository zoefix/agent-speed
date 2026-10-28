using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using AgentSpeed.Linux.UI;
using Avalonia;

namespace AgentSpeed.Linux.Tests;

public sealed class HistoryChartTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);

    [Fact]
    public void SummarySparklineLeavesMissingDatesEmptyInsteadOfInventingAZero()
    {
        var segments = LineChart.SparklineSegments([10, 0, double.NaN, 20], new Size(100, 30));
        Assert.Equal(2, segments.Count);
        Assert.Equal(2, segments[0].Length);
        Assert.Single(segments[1]);
        Assert.Equal(28, segments[0][1].Y); // The recorded zero is real data at the baseline.
        Assert.Equal(98, segments[1][0].X); // Missing dates still occupy their original x slot.
        Assert.True(segments[1][0].X - segments[0][1].X > 32);
    }

    [Fact]
    public void SummarySparklineShowsOneRecordedDayAndKeepsAnEntirelyMissingSeriesEmpty()
    {
        var only = Assert.Single(Assert.Single(LineChart.SparklineSegments([12], new Size(100, 30))));
        Assert.Equal(50, only.X);
        Assert.Empty(LineChart.SparklineSegments([double.NaN, double.PositiveInfinity], new Size(100, 30)));
    }

    [Fact]
    public void ZeroMeasuredSpeedStaysOnTheLineButMissingMeasurementBreaksIt()
    {
        var bars = new HistoryBars(new DayRange(Start, Start.AddDays(3)), [SourceKind.Codex],
            [[20d], [0d], [0d], [30d]], [.4, 0, null, .2], present: [true, true, false, true]);
        var layout = HistoryChart.CalculateLayout(bars, 460, 240);
        Assert.Equal(new[] { 0, 1, 3 }, layout.SpeedPoints.Select(p => p.DayIndex));
        Assert.Equal(layout.Plot.Bottom, layout.SpeedPoints[1].Point.Y);
        Assert.True(layout.SpeedPoints[1].ConnectToPrevious);
        Assert.False(layout.SpeedPoints[2].ConnectToPrevious);
    }

    [Theory]
    [InlineData(.2, "0.2")]
    [InlineData(.4, "0.4")]
    [InlineData(0, "0")]
    public void SpeedAxisKeepsFractionalRatesInsteadOfUsingTheIntegerTokenAxis(double value, string label)
        => Assert.Equal(label, HistoryChart.FormatSpeedTick(value));

    [Fact]
    public void OneDayOutputIsAVisibleCenteredBarEvenWithoutASecondSample()
    {
        var bars = new HistoryBars(DayRange.Single(Start), [SourceKind.Codex], [[794_000d]], [null]);
        var layout = HistoryChart.CalculateLayout(bars, 420, 230);

        var segment = Assert.Single(layout.Segments);
        Assert.Equal(0, segment.DayIndex);
        Assert.Equal(0, segment.SourceIndex);
        Assert.True(segment.Bounds.Width > 0);
        Assert.True(segment.Bounds.Height > 0);
        Assert.Equal(layout.Plot.Center.X, segment.Bounds.Center.X, 8);
        Assert.True(segment.Bounds.Top < layout.ZeroY);
        AssertFinite(segment.Bounds);
    }

    [Fact]
    public void MissingDatesKeepTheirSlotsButHaveNoBarSegments()
    {
        var range = new DayRange(Start, Start.AddDays(2));
        var bars = new HistoryBars(range, [SourceKind.Codex], [[100d], [0d], [200d]], [null, null, null],
            present: [true, false, true]);
        var layout = HistoryChart.CalculateLayout(bars, 460, 240);

        Assert.Equal(3, layout.Slots.Count);
        Assert.Equal(new[] { 0, 2 }, layout.Segments.Select(segment => segment.DayIndex));
        Assert.False(layout.PresentDays[1]);
        Assert.True(layout.Segments[0].Bounds.Right < layout.Segments[1].Bounds.Left);
    }

    [Fact]
    public void RecordedZeroIsPresentAndGetsAZeroMarkerInsteadOfEmptyState()
    {
        var bars = new HistoryBars(DayRange.Single(Start), [SourceKind.Codex], [[0d]], [null], present: [true]);
        var chart = new HistoryChart { Bars = bars, EmptyText = "No data" };
        var layout = HistoryChart.CalculateLayout(bars, 360, 210);

        Assert.True(chart.HasData);
        Assert.True(layout.PresentDays[0]);
        Assert.Empty(layout.Segments);
        AssertFinite(Assert.Single(layout.ZeroMarkers));
    }

    [Fact]
    public void SourceStacksUseSharedOrderAndRemainContinuousAtTheirBoundary()
    {
        var bars = new HistoryBars(DayRange.Single(Start), [SourceKind.Codex, SourceKind.ClaudeCode],
            [[100d, 300d]], [25d]);
        var layout = HistoryChart.CalculateLayout(bars, 420, 230);

        Assert.Equal(2, layout.Segments.Count);
        Assert.Equal(0, layout.Segments[0].SourceIndex);
        Assert.Equal(1, layout.Segments[1].SourceIndex);
        Assert.Equal(layout.Segments[0].Bounds.Top, layout.Segments[1].Bounds.Bottom, 8);
        Assert.True(layout.ShowSpeed);
        Assert.NotNull(layout.SpeedTicks);
        Assert.All(layout.Segments, segment => AssertFinite(segment.Bounds));
    }

    [Fact]
    public void TrafficKeepsDownloadAboveUploadBelowAndMachineOutlineAroundBoth()
    {
        var bars = new HistoryBars(DayRange.Single(Start), [SourceKind.Codex, SourceKind.ClaudeCode],
            [[200d, 100d]], [null], HistoryChartMetric.Traffic, present: [true], below: [[20d, 10d]],
            outline: [new HistoryBars.TrafficPair(340, 45)]);
        var layout = HistoryChart.CalculateLayout(bars, 420, 240);

        var downloads = layout.Segments.Where(segment => !segment.Below).ToArray();
        var uploads = layout.Segments.Where(segment => segment.Below).ToArray();
        var outline = Assert.Single(layout.Outlines);
        Assert.Equal(2, downloads.Length);
        Assert.Equal(2, uploads.Length);
        Assert.All(downloads, segment => Assert.True(segment.Bounds.Bottom <= layout.ZeroY));
        Assert.All(uploads, segment => Assert.True(segment.Bounds.Top >= layout.ZeroY));
        Assert.True(outline.Bounds.Top < layout.ZeroY);
        Assert.True(outline.Bounds.Bottom > layout.ZeroY);
        Assert.All(downloads, segment => AssertFinite(segment.Bounds));
        Assert.All(uploads, segment => AssertFinite(segment.Bounds));
        AssertFinite(outline.Bounds);
    }

    [Fact]
    public void EmptyRangeHasFinitePlotAndNoInventedGeometry()
    {
        var bars = HistoryBars.Empty(new DayRange(Start, Start.AddDays(3)));
        var layout = HistoryChart.CalculateLayout(bars, 320, 180);

        Assert.False(new HistoryChart { Bars = bars }.HasData);
        Assert.Equal(4, layout.PresentDays.Count);
        Assert.Empty(layout.Segments);
        Assert.Empty(layout.ZeroMarkers);
        Assert.Empty(layout.Outlines);
        AssertFinite(layout.Plot);
        Assert.True(layout.Plot.Width > 0);
        Assert.True(layout.Plot.Height > 0);
        Assert.True(double.IsFinite(layout.ZeroY));
        Assert.True(double.IsFinite(layout.ScaleAbove));
    }

    private static void AssertFinite(Rect rect)
    {
        Assert.True(double.IsFinite(rect.X));
        Assert.True(double.IsFinite(rect.Y));
        Assert.True(double.IsFinite(rect.Width));
        Assert.True(double.IsFinite(rect.Height));
        Assert.True(rect.Width >= 0);
        Assert.True(rect.Height >= 0);
    }
}

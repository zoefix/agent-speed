using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Tests;

public class GaugeLayoutTests
{
    private const int N = GaugeLayout.SegmentCount;

    [Fact]
    public void ArcIsMadeOf36SegmentsSpanning270Degrees()
    {
        Assert.Equal(36, N);
        Assert.Equal(270, GaugeLayout.SegmentSpanDegrees * N + GaugeLayout.GapDegrees * (N - 1), 9);
        Assert.Equal(135, GaugeLayout.Segment(0).StartDegrees, 12);
        Assert.Equal(405, GaugeLayout.Segment(N - 1).EndDegrees, 9);
    }

    [Fact]
    public void SegmentsAreEvenlySpacedWithDesignGap()
    {
        for (var i = 0; i < N; i++)
        {
            var segment = GaugeLayout.Segment(i);
            Assert.Equal(i, segment.Index);
            Assert.Equal(GaugeLayout.SegmentSpanDegrees, segment.EndDegrees - segment.StartDegrees, 9);
            Assert.Equal(GaugeLayout.InnerRadius, segment.InnerRadius);
            Assert.Equal(GaugeLayout.OuterRadius, segment.OuterRadius);
            Assert.Equal(GaugeLayout.Radius, segment.MidRadius, 12);
            Assert.Equal((segment.StartDegrees + segment.EndDegrees) / 2, segment.MidDegrees, 12);
            if (i > 0)
            {
                Assert.Equal(2.2, segment.StartDegrees - GaugeLayout.Segment(i - 1).EndDegrees, 9);
            }
        }

        Assert.Equal(GaugeLayout.SegmentLength, GaugeLayout.OuterRadius - GaugeLayout.InnerRadius, 12);
        Assert.Equal(0.41, GaugeLayout.Radius);
        Assert.Equal(0.045, GaugeLayout.SegmentLength);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(N)]
    [InlineData(int.MinValue)]
    public void SegmentIndexOutOfRangeThrows(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GaugeLayout.Segment(index));
    }

    [Fact]
    public void SegmentHeatSpansThePalette()
    {
        Assert.Equal(0, GaugeLayout.SegmentHeat(0));
        Assert.Equal(1, GaugeLayout.SegmentHeat(N - 1));
        Assert.Equal(10 / 35.0, GaugeLayout.SegmentHeat(10), 12);
    }

    [Fact]
    public void SegmentFillIsFractionalAtTheLeadingEdge()
    {
        var heat = 10.25 / N;
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(1, GaugeLayout.SegmentFill(heat, i), 9);
        }

        Assert.Equal(0.25, GaugeLayout.SegmentFill(heat, 10), 9);
        for (var i = 11; i < N; i++)
        {
            Assert.Equal(0, GaugeLayout.SegmentFill(heat, i));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.013)]
    [InlineData(0.37)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    [InlineData(1)]
    public void TotalFillEqualsHeatTimesSegmentCount(double heat)
    {
        var total = Enumerable.Range(0, N).Sum(i => GaugeLayout.SegmentFill(heat, i));
        Assert.Equal(heat * N, total, 9);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(-2, 0)]
    [InlineData(3, 1)]
    public void SegmentFillClampsHeat(double heat, double expectedEveryFill)
    {
        for (var i = 0; i < N; i++)
        {
            Assert.Equal(expectedEveryFill, GaugeLayout.SegmentFill(heat, i));
        }
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0.001, -1)]
    [InlineData(double.NaN, -1)]
    [InlineData(1, 35)]
    [InlineData(5, 35)]
    [InlineData(0.5, 17)]
    [InlineData(10.25 / N, 10)]
    [InlineData(1.0 / N, 0)]
    public void LeadingIndexIsLastLitSegment(double heat, int expected)
    {
        Assert.Equal(expected, GaugeLayout.LeadingIndex(heat));
    }

    [Fact]
    public void LeadingIndexHasPositiveFill()
    {
        for (var i = 1; i <= 1000; i++)
        {
            var heat = i / 1000.0;
            var leading = GaugeLayout.LeadingIndex(heat);
            if (leading >= 0)
            {
                Assert.True(GaugeLayout.SegmentFill(heat, leading) > 0);
                if (leading + 1 < N)
                {
                    Assert.Equal(0, GaugeLayout.SegmentFill(heat, leading + 1), 9);
                }
            }
        }
    }

    [Theory]
    [InlineData(400)]
    [InlineData(100)]
    [InlineData(2000)]
    public void TicksMarkTierThresholdsInIncreasingOrder(double redline)
    {
        var ticks = GaugeLayout.Ticks(redline);
        Assert.Equal(HeatMath.ActiveTiers, ticks.Select(t => t.Tier));
        for (var i = 0; i < ticks.Count; i++)
        {
            Assert.True(ticks[i].Fraction > 0 && ticks[i].Fraction <= 1, $"Tick {i} out of (0,1]: {ticks[i].Fraction}");
            Assert.Equal(HeatMath.Heat(HeatMath.Threshold(ticks[i].Tier, redline), redline), ticks[i].Fraction, 12);
            if (i > 0)
            {
                Assert.True(ticks[i].Fraction > ticks[i - 1].Fraction);
            }
        }
    }

    [Fact]
    public void TicksForDefaultRedlineHaveExpectedFractions()
    {
        var ticks = GaugeLayout.Ticks(400);
        Assert.Equal(Math.Pow(0.15, 0.6), ticks[1].Fraction, 12);
        Assert.Equal(Math.Pow(0.375, 0.6), ticks[2].Fraction, 12);
        Assert.Equal(Math.Pow(0.75, 0.6), ticks[3].Fraction, 12);
    }

    [Theory]
    [InlineData(0, 135)]
    [InlineData(0.5, 270)]
    [InlineData(1, 405)]
    [InlineData(-1, 135)]
    [InlineData(2, 405)]
    public void AngleForFractionClamps(double fraction, double expected)
    {
        Assert.Equal(expected, GaugeLayout.AngleForFraction(fraction), 12);
    }

    [Fact]
    public void PolarUsesScreenCoordinates()
    {
        var top = GaugeLayout.Polar(1, 270);
        Assert.Equal(0, top.X, 9);
        Assert.Equal(-1, top.Y, 9);

        var right = GaugeLayout.Polar(2, 0);
        Assert.Equal(2, right.X, 9);
        Assert.Equal(0, right.Y, 9);

        var bottom = GaugeLayout.Polar(1, 90);
        Assert.Equal(1, bottom.Y, 9);

        var start = GaugeLayout.Polar(1, GaugeLayout.StartDegrees);
        Assert.True(start.X < 0 && start.Y > 0, "135 degrees is bottom-left");

        var end = GaugeLayout.Polar(1, GaugeLayout.StartDegrees + GaugeLayout.SweepDegrees);
        Assert.True(end.X > 0 && end.Y > 0, "405 degrees is bottom-right");

        Assert.Equal(0.41, GaugeLayout.Polar(0.41, 123).Length, 12);
    }

    [Fact]
    public void GapCenterIsAtTheBottom()
    {
        var gap = GaugeLayout.Polar(1, GaugeLayout.GapCenterDegrees);
        Assert.Equal(0, gap.X, 9);
        Assert.Equal(1, gap.Y, 9);
    }

    [Fact]
    public void RingRadiiAreOrderedInsideTheDisc()
    {
        Assert.True(GaugeLayout.OuterRadius < GaugeLayout.TickRadius);
        Assert.True(GaugeLayout.TickRadius < GaugeLayout.CometRadius);
        Assert.True(GaugeLayout.CometRadius < GaugeLayout.DiscRadius);
    }
}

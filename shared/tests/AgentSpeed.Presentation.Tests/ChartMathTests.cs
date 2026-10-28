using AgentSpeed.Presentation.Chart;
using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Tests;

public class ChartMathTests
{
    private static ChartPoint[] Points(params double[] ys) =>
        ys.Select((y, i) => new ChartPoint(i * 10, y)).ToArray();

    [Fact]
    public void CatmullRomPassesThroughEveryPoint()
    {
        var points = Points(0, 5, 3, 8, 8, 2, 0, 0, 10, 4);
        var segments = ChartMath.CatmullRom(points);

        Assert.Equal(points.Length - 1, segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal(points[i + 1], segments[i].End);
            Assert.Equal(points[i], ChartMath.EvaluateBezier(points[i], segments[i], 0));
            var end = ChartMath.EvaluateBezier(points[i], segments[i], 1);
            Assert.Equal(points[i + 1].X, end.X, 9);
            Assert.Equal(points[i + 1].Y, end.Y, 9);
        }
    }

    [Fact]
    public void CatmullRomNeverOvershootsSegmentEndpoints()
    {
        var random = new Random(77);
        for (var trial = 0; trial < 50; trial++)
        {
            var ys = Enumerable.Range(0, 30).Select(_ => random.NextDouble() < 0.3 ? 0 : random.NextDouble() * 500).ToArray();
            var points = Points(ys);
            var segments = ChartMath.CatmullRom(points);

            for (var i = 0; i < segments.Count; i++)
            {
                var lo = Math.Min(points[i].Y, points[i + 1].Y);
                var hi = Math.Max(points[i].Y, points[i + 1].Y);
                var previousX = points[i].X;
                for (var s = 0; s <= 100; s++)
                {
                    var p = ChartMath.EvaluateBezier(points[i], segments[i], s / 100.0);
                    Assert.InRange(p.Y, lo - 1e-9, hi + 1e-9);
                    Assert.True(p.X >= previousX - 1e-9, "x must advance monotonically");
                    previousX = p.X;
                }
            }
        }
    }

    [Fact]
    public void CatmullRomNeverDipsBelowZeroAroundASpike()
    {
        var points = Points(0, 0, 0, 100, 0, 0, 0);
        var segments = ChartMath.CatmullRom(points);
        for (var i = 0; i < segments.Count; i++)
        {
            for (var s = 0; s <= 200; s++)
            {
                var p = ChartMath.EvaluateBezier(points[i], segments[i], s / 200.0);
                Assert.InRange(p.Y, 0, 100);
            }
        }
    }

    [Fact]
    public void CatmullRomNeedsAtLeastTwoPoints()
    {
        Assert.Empty(ChartMath.CatmullRom([]));
        Assert.Empty(ChartMath.CatmullRom([new ChartPoint(1, 2)]));
        Assert.Single(ChartMath.CatmullRom(Points(1, 2)));
        Assert.Throws<ArgumentNullException>(() => ChartMath.CatmullRom(null!));
    }

    [Fact]
    public void FlatDataStaysFlat()
    {
        var points = Points(7, 7, 7, 7);
        foreach (var segment in ChartMath.CatmullRom(points))
        {
            Assert.Equal(7, segment.Control1.Y);
            Assert.Equal(7, segment.Control2.Y);
        }
    }

    [Theory]
    [InlineData(0.7, 1)]
    [InlineData(1, 1)]
    [InlineData(1.3, 2)]
    [InlineData(2, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(7, 10)]
    [InlineData(10, 10)]
    [InlineData(12, 20)]
    [InlineData(115, 200)]
    [InlineData(251, 500)]
    [InlineData(0.03, 0.05)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void NiceCeilingRoundsUpToNiceSteps(double value, double expected)
    {
        Assert.Equal(expected, ChartMath.NiceCeiling(value), 9);
    }

    [Fact]
    public void NiceCeilingIsNeverBelowItsInput()
    {
        for (var v = 0.01; v < 1e6; v *= 1.37)
        {
            Assert.True(ChartMath.NiceCeiling(v) >= v * (1 - 1e-12), $"NiceCeiling({v}) too small");
        }
    }

    [Fact]
    public void ScaleIncludesModeratePeak()
    {
        var scale = ChartMath.ComputeScale(dataMax: 100, peak: 150);
        Assert.True(scale.PeakInRange);
        Assert.True(scale.MaxValue >= 150);
        Assert.Equal(200, scale.MaxValue);
    }

    [Fact]
    public void ScaleExcludesPeakMoreThanTwoAndAHalfTimesTheData()
    {
        var scale = ChartMath.ComputeScale(dataMax: 100, peak: 300);
        Assert.False(scale.PeakInRange);
        Assert.Equal(200, scale.MaxValue);
        Assert.True(scale.MaxValue < 300);
    }

    [Fact]
    public void ScaleHasHeadroomAndAMinimum()
    {
        Assert.Equal(new ChartScale(10, false), ChartMath.ComputeScale(0, 0));
        Assert.Equal(new ChartScale(10, false), ChartMath.ComputeScale(double.NaN, double.NaN));
        Assert.Equal(new ChartScale(20, true), ChartMath.ComputeScale(0, 15));
        Assert.Equal(new ChartScale(10, false), ChartMath.ComputeScale(-50, -1));
        Assert.True(ChartMath.ComputeScale(87, 0).MaxValue >= 87 * 1.15);
    }

    [Fact]
    public void ScaleAlwaysContainsTheData()
    {
        var random = new Random(5);
        for (var i = 0; i < 1000; i++)
        {
            var data = random.NextDouble() * 3000;
            var peak = random.NextDouble() * 6000;
            var scale = ChartMath.ComputeScale(data, peak);
            Assert.True(scale.MaxValue >= data);
            if (scale.PeakInRange)
            {
                Assert.True(scale.MaxValue >= peak, $"Peak {peak} flagged in range but above max {scale.MaxValue}");
            }
        }
    }

    [Fact]
    public void ProjectMapsZeroToBottomAndMaxToTop()
    {
        var points = ChartMath.Project([0, 50, 100], width: 200, height: 96, maxValue: 100);
        Assert.Equal(new ChartPoint(0, 96), points[0]);
        Assert.Equal(new ChartPoint(100, 48), points[1]);
        Assert.Equal(new ChartPoint(200, 0), points[2]);
    }

    [Fact]
    public void ProjectClampsOutOfRangeValues()
    {
        var points = ChartMath.Project([-5, 250, double.NaN, double.PositiveInfinity], 300, 96, 100);
        Assert.Equal(96, points[0].Y);
        Assert.Equal(0, points[1].Y);
        Assert.Equal(96, points[2].Y);
        Assert.Equal(96, points[3].Y);
    }

    [Fact]
    public void ProjectHandlesDegenerateInput()
    {
        Assert.Empty(ChartMath.Project([], 100, 96, 10));
        Assert.Equal(new ChartPoint(0, 48), Assert.Single(ChartMath.Project([5], 100, 96, 10)));
        Assert.All(ChartMath.Project([1, 2, 3], 100, 96, 0), p => Assert.Equal(96, p.Y));
        Assert.Throws<ArgumentNullException>(() => ChartMath.Project(null!, 1, 1, 1));
    }

    [Theory]
    [InlineData(0, 200, 10, 0)]
    [InlineData(200, 200, 10, 9)]
    [InlineData(100, 200, 11, 5)]
    [InlineData(-50, 200, 10, 0)]
    [InlineData(500, 200, 10, 9)]
    [InlineData(10, 200, 0, 0)]
    [InlineData(10, 200, 1, 0)]
    [InlineData(10, 0, 10, 0)]
    [InlineData(double.NaN, 200, 10, 0)]
    public void IndexAtClamps(double x, double width, int count, int expected)
    {
        Assert.Equal(expected, ChartMath.IndexAt(x, width, count));
    }

    [Theory]
    [InlineData(300, 120, 180, 120)]
    [InlineData(300, 300, 0, 300)]
    [InlineData(50, 120, 0, 50)]
    [InlineData(0, 60, 0, 0)]
    [InlineData(300, -5, 300, 0)]
    public void WindowTakesTheLastBuckets(int total, int seconds, int start, int count)
    {
        Assert.Equal((start, count), ChartMath.Window(total, seconds));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    [InlineData(77, 120)]
    [InlineData(0, 120)]
    [InlineData(-60, 120)]
    public void NormalizeRangeAcceptsOnlyOptions(int seconds, int expected)
    {
        Assert.Equal(expected, ChartMath.NormalizeRange(seconds));
    }

    [Fact]
    public void HeatGradientRunsFromMaxHeatAtTopToZeroAtBottom()
    {
        var stops = ChartMath.HeatGradient(maxValue: 500, redline: 400, steps: 8);
        Assert.Equal(9, stops.Count);
        Assert.Equal(0, stops[0].Offset);
        Assert.Equal(1, stops[^1].Offset);
        Assert.Equal(HeatMath.Heat(500, 400), stops[0].Heat);
        Assert.Equal(0, stops[^1].Heat);
        for (var i = 1; i < stops.Count; i++)
        {
            Assert.True(stops[i].Offset > stops[i - 1].Offset);
            Assert.True(stops[i].Heat <= stops[i - 1].Heat);
        }
    }
}

using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Tests.Perf;

/// <summary>The score's full and compact forms and its log scale (DESIGN 12, <i>Number formats</i>, <i>Log scale</i>).</summary>
public sealed class PerfNumberTests
{
    [Theory]
    [InlineData(0, "0", "0")]
    [InlineData(986, "986", "986")]
    [InlineData(1286, "1,286", "1,286")]
    [InlineData(9999, "9,999", "9,999")]
    [InlineData(10_000, "10,000", "10.0k")]
    [InlineData(12_840, "12,840", "12.8k")]
    [InlineData(99_960, "99,960", "100k")]
    [InlineData(99_999, "99,999", "100k")]
    [InlineData(128_400, "128k", "128k")]
    [InlineData(999_500, "1.00M", "1.00M")]
    [InlineData(1_284_000, "1.28M", "1.28M")]
    [InlineData(12_840_000, "12.8M", "12.8M")]
    public void FullAndCompact(int value, string full, string compact)
    {
        Assert.Equal(full, PerfNumber.Full(value));
        Assert.Equal(compact, PerfNumber.Compact(value));
    }

    [Fact]
    public void NoneIsTheEnDashNeverZero()
    {
        Assert.Equal("–", PerfNumber.Full(null));
        Assert.Equal("–", PerfNumber.Compact(null));
        Assert.Equal("12,840", PerfNumber.Spoken(12_840));
    }

    [Theory]
    [InlineData(250, 0)]
    [InlineData(500, 0.25)]
    [InlineData(1000, 0.5)]
    [InlineData(2000, 0.75)]
    [InlineData(4000, 1)]
    [InlineData(12_840, 1)]
    [InlineData(100, 0)]
    public void TheLogScaleHasTheReferenceAtItsMiddle(double value, double fraction) =>
        Assert.Equal(fraction, PerfScale.Fraction(value), 12);

    [Theory]
    [InlineData(250, 0)]
    [InlineData(1000, 6)]
    [InlineData(1213, 7)]
    [InlineData(4000, 12)]
    [InlineData(12_840, 12)]
    public void NeonLightsRoundFTimesTwelve(int value, int lit) => Assert.Equal(lit, PerfScale.LitSegments(value));

    [Fact]
    public void TheChartTopDoublesPastFourThousand()
    {
        Assert.Equal(4000, PerfScale.ChartTop(3999));
        Assert.Equal(8000, PerfScale.ChartTop(5000));
        Assert.Equal(16000, PerfScale.ChartTop(12_840));
        Assert.Equal([500, 1000, 2000, 4000], PerfScale.Gridlines(4000));
        Assert.Equal(0.5, PerfScale.ChartFraction(1000, 4000), 12);
    }
}

using AgentSpeed.Presentation.Orb.Styles;

namespace AgentSpeed.Presentation.Tests;

public class MinimalArcsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, 0)]
    [InlineData(0.05, 1)]
    [InlineData(7.5, 1)]
    [InlineData(7.6, 2)]
    [InlineData(180, 24)]
    [InlineData(360, MinimalArcs.LapSegments)]
    public void SegmentCountCoversTheSweep(double sweep, int count)
    {
        Assert.Equal(count, MinimalArcs.SegmentCount(sweep));
    }

    [Theory]
    [InlineData(3.2)]
    [InlineData(100)]
    [InlineData(360)]
    public void SegmentsAreContiguousOnAFixedGrid(double sweep)
    {
        var count = MinimalArcs.SegmentCount(sweep);
        var previousEnd = 270.0;
        var previousFraction = -1.0;
        for (var i = 0; i < count; i++)
        {
            var segment = MinimalArcs.Segment(270, sweep, i);
            Assert.Equal(previousEnd, segment.FromDegrees, 9);
            Assert.Equal(270 + i * MinimalArcs.StepDegrees, segment.FromDegrees, 9);
            Assert.True(segment.ToDegrees > segment.FromDegrees);
            Assert.True(segment.ToDegrees - segment.FromDegrees <= MinimalArcs.StepDegrees + 1e-9);
            Assert.Equal(i < count - 1 || sweep % MinimalArcs.StepDegrees == 0, segment.Full);
            Assert.InRange(segment.Fraction, 0, 1);
            Assert.True(segment.Fraction > previousFraction);
            var middle = (segment.FromDegrees + segment.ToDegrees) / 2 - 270;
            Assert.InRange(segment.Fraction, middle / sweep - 1e-9, middle / sweep + 1e-9);
            previousEnd = segment.ToDegrees;
            previousFraction = segment.Fraction;
        }

        Assert.Equal(270 + sweep, previousEnd, 9);
    }
}
